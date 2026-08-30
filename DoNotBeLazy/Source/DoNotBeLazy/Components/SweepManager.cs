using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using DoNotBeLazy.Core;
using DoNotBeLazy.Utility;

namespace DoNotBeLazy.Components
{
    // Holds everything needed to keep chaining a pawn through a sweep once
    // they've been assigned into one. WorkGiverDef stays fixed per order;
    // SharedPool is the live, shared candidate list for the whole sweep -
    // every pawn in the same sweep call (BeginSweep) points at the same
    // List instance, so claiming a target for one pawn removes it for
    // everyone else. Workstation orders get an empty pool - there is
    // nothing to fan out across, only one station - and WorkstationTarget
    // is the whole order: AssignNextTask re-asks that same station for the
    // next bill, for the haul-off it wants done first, and again when the
    // pawn comes back from a need-pause.
    public class SweepOrder
    {
        public WorkGiverDef WorkGiverDef { get; }
        public List<LocalTargetInfo> SharedPool { get; }
        public Thing WorkstationTarget { get; }

        // Where the pool was scanned from, kept so the order can build a
        // fresh one when a pawn runs out. Fires were the first case that
        // needed this - they spread, so a pool frozen at BeginSweep time is
        // stale almost immediately - but "until done" means more than "until
        // this list is empty" for every sweep type, and a pawn resuming from
        // a need-pause into a pool the others drained is the case that made
        // that obvious. So the Rescannable flag is gone and AssignNextTask
        // rescans for everything.
        public IntVec3 ScanCenter { get; }
        public int ScanRadius { get; }

        // Which ordering rule NextTargetIndex uses for this order: true
        // ranks by distance from ScanCenter, false by distance from the
        // requesting pawn. Read from settings once, at BeginAreaSweep, and
        // fixed for the life of the order - flipping the setting mid-sweep
        // deliberately doesn't re-order a sweep already under way. Inert on
        // workstation orders; their pool is empty and nothing ranks it.
        public bool CenterOut { get; }

        // Targets already put back in the pool once after the WorkGiver
        // answered with blocker-clearing work instead of the task itself.
        // Capped at one re-queue each: a cell that can never be satisfied
        // would otherwise hand out the same preparatory job forever.
        public HashSet<LocalTargetInfo> Requeued { get; } = new HashSet<LocalTargetInfo>();

        public SweepOrder(WorkGiverDef workGiverDef, List<LocalTargetInfo> sharedPool, Thing workstationTarget = null,
            IntVec3 scanCenter = default(IntVec3), int scanRadius = 0, bool centerOut = true)
        {
            WorkGiverDef = workGiverDef;
            SharedPool = sharedPool;
            WorkstationTarget = workstationTarget;
            ScanCenter = scanCenter;
            ScanRadius = scanRadius;
            CenterOut = centerOut;
        }
    }

    // MapComponent tracking active area sweeps. Job-to-job chaining is
    // event driven (JobTrackerPatch's postfix on EndCurrentJob calls
    // Notify_JobEnded when a sweep pawn's job wraps up) rather than
    // polled here - avoids reassigning on a delay and avoids doing the
    // same completion check twice. Tick() is only for the things nothing
    // else observes: a pawn going down, dying, drafting, or leaving the
    // map mid-sweep (architecture doc section 4 edge cases).
    //
    // Need interrupts pause rather than cancel (per explicit user request -
    // this reverses the original "don't auto-resume" design): NeedMonitor
    // calls PauseForNeed instead of RemoveSweep, and Notify_JobEnded checks
    // pausedForNeed first, so once the pawn's own eat/sleep/joy job wraps up
    // they're handed the next sweep task (or, for a workstation order, sent
    // back to the same station) automatically.
    //
    // No ExposeData - per architecture doc 4, sweeps are cleared on
    // load rather than persisted. Simplest option for v1 and avoids
    // re-deriving stale reservations against a changed map state.
    public class SweepManager : MapComponent
    {
        private const int StateCheckIntervalTicks = 60;

        // A sweep survives individual targets failing (see Notify_JobEnded),
        // but not endlessly. Each failure re-enters AssignNextTask from
        // inside EndCurrentJob, so an unbounded retry is also unbounded
        // recursion if every target fails on the tick it's handed out. Eight
        // consecutive failures with no successful task in between reads as
        // "this sweep isn't going to work" rather than "unlucky target".
        private const int MaxConsecutiveFailures = 8;

        // A pawn whose need never climbs back over the threshold - a mood
        // that just stays low - would otherwise hold a paused sweep, and its
        // share of the shared pool, forever. Half an in-game day.
        private const int MaxPauseTicks = 30000;

        // How long to leave a workstation order alone after the station had
        // no job to give. Vanilla parks a bill for
        // WorkGiver_DoBill.ReCheckFailedBillTicksRange (500-600 ticks,
        // confirmed as a static IntRange on that class in this build) after
        // any failed ingredient search, and a teammate standing on the stack
        // for a moment is enough to cause one - so the first null answer
        // usually means "ask again in ten seconds", not "this bench is
        // finished". Just past the top of that range; MaxConsecutiveFailures
        // caps it at eight tries, about eighty seconds.
        private const int WorkstationRetryTicks = 600;

        private readonly Dictionary<Pawn, SweepOrder> activeSweeps = new Dictionary<Pawn, SweepOrder>();

        // Per-pawn, not per-order: a SweepOrder is shared across everyone in
        // a group sweep, and one pawn hitting bad targets shouldn't count
        // against the others. Reset on any successful task.
        private readonly Dictionary<Pawn, int> consecutiveFailures = new Dictionary<Pawn, int>();

        // pawns pulled out of their sweep job for a critical need but still
        // tracked in activeSweeps - NeedMonitor adds them here via
        // PauseForNeed instead of RemoveSweep, so the sweep can resume once
        // the need is actually dealt with. Value is the tick they were
        // paused on, for the MaxPauseTicks give-up.
        //
        // (was a HashSet, and resumption was "first job end after a pause
        // wins". That's the bug - see Notify_JobEnded.)
        private readonly Dictionary<Pawn, int> pausedForNeed = new Dictionary<Pawn, int>();

        // Workstation orders whose station answered null, and the tick to ask
        // it again on. A workstation order has no pool, so nothing else would
        // ever bring the pawn back: before this existed the first null answer
        // ended the order outright, which is half of "they don't return to
        // the bench". While an entry is here the pawn is working for vanilla,
        // so Notify_JobEnded ignores their job ends - otherwise the next
        // thing they finished would re-ask the station immediately and burn
        // the whole retry budget in a few seconds.
        private readonly Dictionary<Pawn, int> workstationRetryAt = new Dictionary<Pawn, int>();

        // TryTakeOrderedJob interrupts whatever the pawn is doing right now,
        // and that fires EndCurrentJob(InterruptForced) -> JobTrackerPatch's
        // postfix -> Notify_JobEnded -> RemoveSweep. So handing a pawn a
        // sweep job was cancelling the sweep that handed it out. Verified
        // against the DLL: TryTakeOrderedJob -> StartJob -> EndCurrentJob.
        // This flag lets JobTrackerPatch ignore the job end it caused itself.
        public static bool AssigningJob;

        public SweepManager(Map map) : base(map)
        {
        }

        // every TryTakeOrderedJob in the mod goes through here - see AssigningJob
        public static void GiveJob(Pawn pawn, Job job)
        {
            AssigningJob = true;
            try
            {
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            finally
            {
                AssigningJob = false;
            }
        }

        public override void MapComponentTick()
        {
            if (activeSweeps.Count == 0 || Find.TickManager.TicksGame % StateCheckIntervalTicks != 0)
            {
                return;
            }

            // snapshot keys - RemoveSweep mutates activeSweeps mid-loop otherwise
            var pawns = new List<Pawn>(activeSweeps.Keys);
            foreach (Pawn pawn in pawns)
            {
                if (pawn.Dead || pawn.Downed || pawn.InMentalState || pawn.Drafted || pawn.Map != map)
                {
                    RemoveSweep(pawn);
                    continue;
                }

                TryWorkstationRetry(pawn);
            }
        }

        // The one piece of chaining that is not event-driven, and it has to
        // be: a workstation order waiting out a bill's ingredient cooldown
        // has no job of its own to end, so there is no event to hang it on.
        private void TryWorkstationRetry(Pawn pawn)
        {
            if (!workstationRetryAt.TryGetValue(pawn, out int dueAt))
            {
                return;
            }

            // a need outranks a retry, and the resume path in
            // Notify_JobEnded calls AssignNextTask itself, which re-arms the
            // retry if the station still has nothing. Holding both at once
            // deadlocks the pawn: Notify_JobEnded returns early while a retry
            // is pending, so the pause would never be cleared. Checked ahead
            // of the clock so the window is one tick pass, not ten seconds.
            if (pausedForNeed.ContainsKey(pawn))
            {
                workstationRetryAt.Remove(pawn);
                return;
            }

            if (Find.TickManager.TicksGame < dueAt || !activeSweeps.TryGetValue(pawn, out SweepOrder order))
            {
                return;
            }

            workstationRetryAt.Remove(pawn);
            Logger.Message($"{pawn.LabelShort}: asking {order.WorkstationTarget?.LabelShort} for work again ({order.WorkGiverDef.defName})");
            AssignNextTask(pawn, order);
        }

        public bool TryGetActiveSweep(Pawn pawn, out SweepOrder order)
        {
            return activeSweeps.TryGetValue(pawn, out order);
        }

        // copy, not the live key collection - NeedMonitor calls RemoveSweep
        // while walking this and was throwing "collection was modified"
        public IEnumerable<Pawn> GetSweptPawns()
        {
            return new List<Pawn>(activeSweeps.Keys);
        }

        public void RemoveSweep(Pawn pawn)
        {
            activeSweeps.Remove(pawn);
            pausedForNeed.Remove(pawn);
            consecutiveFailures.Remove(pawn);
            workstationRetryAt.Remove(pawn);
        }

        public bool IsPaused(Pawn pawn)
        {
            return pausedForNeed.ContainsKey(pawn);
        }

        // Called by NeedMonitor when a swept pawn's need goes critical.
        // Keeps the sweep order alive (does NOT RemoveSweep) so
        // Notify_JobEnded can resume it once the pawn's own need-driven job
        // (eat/sleep/joy, picked by vanilla AI once we let go) finishes on
        // its own. Uses the same self-caused-job-end suppression as
        // GiveJob - without it, this EndCurrentJob call would trip
        // JobTrackerPatch's postfix immediately and read as "sweep task
        // ended", removing the sweep before the pawn even gets to eat.
        public void PauseForNeed(Pawn pawn)
        {
            if (!activeSweeps.ContainsKey(pawn))
            {
                return;
            }

            pausedForNeed[pawn] = Find.TickManager.TicksGame;

            if (pawn.jobs?.curJob == null)
            {
                return;
            }

            AssigningJob = true;
            try
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
            finally
            {
                AssigningJob = false;
            }
        }

        // Entry point from FloatMenuPatch. eligible pawns only - caller has
        // already run them through PawnValidator. Workstation WorkGivers
        // (WorkGiver_DoBill) get single-pawn best-of selection per
        // architecture doc 2; everything else fans the group out across
        // targets found within sweepRadius of clickedTarget.
        public void BeginSweep(List<Pawn> eligiblePawns, LocalTargetInfo clickedTarget, WorkGiverDef workGiverDef)
        {
            if (map == null || eligiblePawns == null || eligiblePawns.Count == 0)
            {
                return;
            }

            if (!(workGiverDef?.Worker is WorkGiver_Scanner scanner))
            {
                return;
            }

            if (scanner is WorkGiver_DoBill)
            {
                BeginWorkstationSweep(eligiblePawns, clickedTarget.Thing, workGiverDef, scanner);
            }
            else
            {
                BeginAreaSweep(eligiblePawns, clickedTarget.Cell, workGiverDef, scanner);
            }
        }

        private void BeginWorkstationSweep(List<Pawn> eligiblePawns, Thing billGiver, WorkGiverDef workGiverDef, WorkGiver_Scanner scanner)
        {
            if (billGiver == null)
            {
                return;
            }

            // was: single PickBestWorkstationPawn call, then bail if
            // JobOnThing came back null. Problem is the best pawn can fail
            // for reasons that don't apply to the others (can't reach the
            // station, bill has a skill floor they miss) and the whole
            // command then silently did nothing. Rank them and take the
            // first that actually gets a job.
            List<Pawn> ranked = RankForWorkstation(eligiblePawns, workGiverDef);

            foreach (Pawn pawn in ranked)
            {
                Job job = scanner.JobOnThing(pawn, billGiver, true);
                if (job == null)
                {
                    Logger.Message($"BeginSweep {workGiverDef.defName}: no job on {billGiver.LabelShort} for {pawn.LabelShort}, trying next");
                    continue;
                }

                // empty pool - see SweepOrder comment. billGiver is the
                // order: every job after this one comes from AssignNextTask
                // re-asking this same station. A fresh assignment always
                // clears leftover pause and retry state from a previous order
                pausedForNeed.Remove(pawn);
                workstationRetryAt.Remove(pawn);
                activeSweeps[pawn] = new SweepOrder(workGiverDef, new List<LocalTargetInfo>(), billGiver);

                // whole workstation path used to emit nothing at all - a
                // bill order's only trace was one "job ended" line with no
                // context, which is why the 08-22 log couldn't say whether
                // an order had even started. job.def matters here: DoBill
                // means the bill itself, anything else means the WorkGiver
                // wants a haul-off or a refuel first.
                Logger.Message($"BeginSweep {workGiverDef.defName} at {billGiver.LabelShort}: {pawn.LabelShort} of {ranked.Count} ranked, first job {job.def.defName}");
                GiveJob(pawn, job);
                return;
            }

            Logger.Message($"BeginSweep {workGiverDef.defName}: no job on {billGiver.LabelShort} for any of {ranked.Count} pawns, no sweep started");
        }

        // Skill level on the WorkGiver's work type first, WorkSpeedGlobal to
        // break ties, MoveSpeed after that. Doc calls out a specific stat
        // per trade (SmithingSpeed etc.) for the second tiebreak - using the
        // global stat here instead since resolving "the specific stat for
        // this work type" generically isn't a straight lookup. Good enough
        // for picking a winner in the common case; revisit if it matters.
        private static List<Pawn> RankForWorkstation(List<Pawn> eligiblePawns, WorkGiverDef workGiverDef)
        {
            SkillDef relevantSkill = null;
            if (workGiverDef.workType?.relevantSkills != null && workGiverDef.workType.relevantSkills.Count > 0)
            {
                relevantSkill = workGiverDef.workType.relevantSkills[0];
            }

            var ranked = new List<Pawn>(eligiblePawns);
            ranked.Sort((a, b) =>
            {
                int cmp = SkillLevelOf(b, relevantSkill).CompareTo(SkillLevelOf(a, relevantSkill));
                if (cmp != 0)
                {
                    return cmp;
                }
                cmp = b.GetStatValue(StatDefOf.WorkSpeedGlobal).CompareTo(a.GetStatValue(StatDefOf.WorkSpeedGlobal));
                if (cmp != 0)
                {
                    return cmp;
                }
                return b.GetStatValue(StatDefOf.MoveSpeed).CompareTo(a.GetStatValue(StatDefOf.MoveSpeed));
            });

            return ranked;
        }

        private static int SkillLevelOf(Pawn pawn, SkillDef skillDef)
        {
            if (skillDef == null || pawn.skills == null)
            {
                return 0;
            }
            SkillRecord record = pawn.skills.GetSkill(skillDef);
            return record == null || record.TotallyDisabled ? 0 : record.Level;
        }

        private void BeginAreaSweep(List<Pawn> eligiblePawns, IntVec3 clickCell, WorkGiverDef workGiverDef, WorkGiver_Scanner scanner)
        {
            // TaskScanner is pawn-scoped (PotentialWorkThingsGlobal takes a
            // pawn) so we build the shared pool off whichever eligible pawn
            // happens to be first - fine since the checks it applies
            // (forbidden, reservable, radius) don't vary by which pawn asked
            Pawn driver = eligiblePawns[0];
            int radius = DoNotBeLazyMod.Settings.sweepRadius;

            // read once, here, and stamped onto the order below - see
            // SweepOrder.CenterOut
            bool centerOut = DoNotBeLazyMod.Settings.centerOutOrder;
            List<LocalTargetInfo> pool = TaskScanner.FindTargets(clickCell, radius, map, workGiverDef, driver);
            if (pool.Count == 0)
            {
                // the clicked target had a job or the option wouldn't have
                // been offered, so an empty pool here means the radius scan
                // disagreed with the click - reservations, reachability,
                // allowed area. Silence on this used to look identical to
                // "the mod is broken"; the float menu is already gone by
                // now, so a message is the only channel left.
                Logger.Message($"BeginSweep {workGiverDef.defName}: scan found nothing, no sweep started");
                string what = workGiverDef.label.NullOrEmpty() ? workGiverDef.defName : workGiverDef.label.CapitalizeFirst();
                Messages.Message(
                    "* " + what + ": nothing to do within " + radius + " tiles.",
                    new TargetInfo(clickCell, map),
                    MessageTypeDefOf.RejectInput,
                    false);
                return;
            }

            Logger.Message($"BeginSweep {workGiverDef.defName}: {pool.Count} targets, {eligiblePawns.Count} pawns, order {(centerOut ? "centre-out" : "pawn-nearest")}");

            // every area sweep rescans when a pawn runs the pool dry now, not
            // just fire
            var order = new SweepOrder(workGiverDef, pool, null, clickCell, radius, centerOut);

            // was: break out of this loop the moment the pool emptied, which
            // is why "* haul until done" with 36 selected sent exactly one
            // pawn - a 1-target pool dropped the other 35 without a word.
            // AssignNextTask rescans now, so let every pawn ask; the ones
            // with genuinely nothing to do drop out there instead.
            foreach (Pawn pawn in eligiblePawns)
            {
                pausedForNeed.Remove(pawn);
                activeSweeps[pawn] = order;
                AssignNextTask(pawn, order);
            }
        }

        public void Notify_JobEnded(Pawn pawn, JobCondition condition)
        {
            if (!activeSweeps.TryGetValue(pawn, out SweepOrder order))
            {
                return;
            }

            // waiting out a workstation cooldown. Whatever just ended is
            // vanilla's work rather than ours, and TryWorkstationRetry owns
            // this pawn until the clock runs out.
            if (workstationRetryAt.ContainsKey(pawn))
            {
                return;
            }

            // the condition is what decides continue-vs-stop, and "the pawn
            // wandered off" reports are almost always answered by this line
            Logger.Message($"{pawn.LabelShort}: job ended {condition} ({order.WorkGiverDef.defName})");

            if (pausedForNeed.TryGetValue(pawn, out int pausedAt))
            {
                // This is NOT a sweep task ending - the pawn is off dealing
                // with a need. Used to resume right here, on the first job
                // end after the pause, whatever that job was. Which is
                // wrong twice over: EndCurrentJob starts a replacement job
                // immediately, so the first thing to end is usually that
                // replacement rather than a meal, and the pawn got dragged
                // back mid-break; and once the flag was gone the next real
                // interrupt fell through to the InterruptForced branch
                // below and killed the sweep for good. That's the "takes a
                // break and never comes back" report.
                //
                // A job end is only a prompt to look again now.
                if (!NeedMonitor.NeedsSatisfied(pawn))
                {
                    if (Find.TickManager.TicksGame - pausedAt > MaxPauseTicks)
                    {
                        // need isn't recovering (a mood that just stays
                        // low). Don't hold the pool hostage over it.
                        Logger.Message($"{pawn.LabelShort}: still under threshold after {MaxPauseTicks} ticks paused, ending sweep.");
                        RemoveSweep(pawn);
                    }
                    return;
                }

                pausedForNeed.Remove(pawn);
                Logger.Message($"{pawn.LabelShort}: needs satisfied, resuming sweep ({order.WorkGiverDef.defName})");
                AssignNextTask(pawn, order);
                return;
            }

            // Workstation orders used to end here unconditionally, on the
            // reasoning that JobTrackerPatch had already re-asked the bill
            // giver and come up empty. It hadn't: that continuation only
            // fired for a job whose def was DoBill, and the job that actually
            // ends a bill at a bench with product still on it is the
            // HaulToCell the WorkGiver asks for first. So every bill sweep
            // died on its first job end. They take the same path as
            // everything else now - AssignNextTask re-asks the station
            // instead of drawing from a pool.
            if (condition == JobCondition.Succeeded)
            {
                consecutiveFailures.Remove(pawn);
                AssignNextTask(pawn, order);
                return;
            }

            // Used to end the sweep on any non-Succeeded condition, which
            // meant a single bad target killed an entire "until done" order
            // and the pawn silently wandered off to whatever vanilla's think
            // tree picked next. That's what made the GrowerSow plantDefToSow
            // bug look like "sowing does nothing" rather than "one cell got
            // skipped". A failed *target* is not a failed *sweep*: drop that
            // target and hand out the next one.
            if (!TargetFailureIsRecoverable(condition))
            {
                RemoveSweep(pawn);
                return;
            }

            consecutiveFailures.TryGetValue(pawn, out int failures);
            failures++;
            if (failures >= MaxConsecutiveFailures)
            {
                Logger.Message($"{pawn.LabelShort}: {failures} sweep tasks failed in a row ({condition}), ending sweep.");
                RemoveSweep(pawn);
                return;
            }

            consecutiveFailures[pawn] = failures;
            AssignNextTask(pawn, order);
        }

        // Split JobCondition into "that target didn't work out" (keep the
        // sweep, try the next one) and "something took this pawn away from
        // us" (stop - continuing would fight the player or the AI).
        //
        // Deliberately conservative on the interrupt conditions: both
        // InterruptForced and InterruptOptional mean something else decided
        // this pawn should be doing something different - a manual order,
        // drafting, a mental break, another mod. Retrying there would have
        // the sweep tug-of-war with whatever interrupted it. Errored means
        // a genuine exception in the job system, where retrying risks a
        // loop rather than a recovery.
        private static bool TargetFailureIsRecoverable(JobCondition condition)
        {
            return condition == JobCondition.Incompletable      // target no longer workable
                || condition == JobCondition.QueuedNoLongerValid // target invalidated before we got there
                || condition == JobCondition.ErroredPather;      // couldn't path to this one target
        }

        private void AssignNextTask(Pawn pawn, SweepOrder order)
        {
            if (!(order.WorkGiverDef.Worker is WorkGiver_Scanner scanner))
            {
                RemoveSweep(pawn);
                return;
            }

            // state can change between the tick check and here (downed by a
            // roof collapse mid-mining sweep is the obvious one)
            if (!PawnValidator.CanSweep(pawn, order.WorkGiverDef) || !pawn.Spawned || pawn.Map != map)
            {
                RemoveSweep(pawn);
                return;
            }

            if (order.WorkstationTarget != null)
            {
                // resuming (or continuing) a workstation order - always the
                // same station, never a pool to draw from
                if (order.WorkstationTarget.Destroyed)
                {
                    RemoveSweep(pawn);
                    return;
                }

                // whatever the pawn just finished, ask the station what it
                // wants next - the bill itself, the haul-off it insists on
                // first, or a refuel. The old code read a null here as the
                // end of the order; see WorkstationHadNoJob for why it
                // usually isn't one.
                Job resumeJob = scanner.JobOnThing(pawn, order.WorkstationTarget, true);
                if (resumeJob == null)
                {
                    WorkstationHadNoJob(pawn, order);
                    return;
                }

                consecutiveFailures.Remove(pawn);
                workstationRetryAt.Remove(pawn);
                Logger.Message($"{pawn.LabelShort}: {resumeJob.def.defName} at {order.WorkstationTarget.LabelShort} ({order.WorkGiverDef.defName})");
                GiveJob(pawn, resumeJob);
                return;
            }

            bool firefighting = FireCompat.IsFirefighting(order.WorkGiverDef);
            bool rescanned = false;

            // Targets this pawn has already been refused for on THIS call.
            // They stay in the shared pool now (see the RemoveAt note below),
            // so without this we'd hand the same rejected target straight
            // back to the same pawn and spin.
            var refused = new HashSet<LocalTargetInfo>();

            while (true)
            {
                // was: RemoveAt(i) up here, before asking for a job, so a
                // target that merely wasn't workable *by this pawn right
                // now* got destroyed for the whole group. With 9 pawns
                // hauling into one stockpile that's most of the pool - one
                // log had 37 discards against 32 assignments, and a meal one
                // pawn was refused was hauled fine by another a minute
                // later. Only claim a target once we actually have a job for
                // it; everything else is skipped, not consumed.
                int i = NextTargetIndex(order.ScanCenter, pawn.Position, order.SharedPool, refused, order.CenterOut);
                if (i < 0)
                {
                    // nothing left that this pawn hasn't already been refused.
                    // Rescan once per call: the pool is a snapshot from click
                    // time and a sweep runs for a long while, so work that
                    // appeared since - or that freed up while this pawn was
                    // paused for a need - is invisible otherwise. Used to be
                    // firefighting-only because fires spread. Turns out every
                    // sweep type needs it, most obviously a pawn coming back
                    // from a break into a pool the others already emptied.
                    if (rescanned)
                    {
                        break;
                    }

                    rescanned = true;
                    AddNewTargets(order, TaskScanner.FindTargets(order.ScanCenter, order.ScanRadius, map, order.WorkGiverDef, pawn));
                    continue;
                }

                LocalTargetInfo target = order.SharedPool[i];

                // gone for good (hauled by someone else, mined out, filth
                // swept up by another pawn's batch job) vs merely not
                // available to this pawn this second (reserved, forbidden,
                // outside their allowed area, unreachable). Only the first
                // kind leaves the pool - conflating the two is what the
                // discard bug was.
                if (TargetIsGone(target))
                {
                    order.SharedPool.RemoveAt(i);
                    continue;
                }

                string refusal = TargetRefusalReason(pawn, target, scanner, firefighting);
                if (refusal != null)
                {
                    Logger.Message($"{pawn.LabelShort}: skipping {target} ({order.WorkGiverDef.defName}) - {refusal}");
                    refused.Add(target);
                    continue;
                }

                // must precede JobOnCell - this is the call that actually
                // bakes plantDefToSow into the job, so a stale value here is
                // what sends pawns to sow the wrong crop (or unzoned dirt)
                // and then fails them out on the walk over
                GrowerCompat.ResetWantedPlantDef(scanner);

                Job job = target.HasThing ? scanner.JobOnThing(pawn, target.Thing, true) : scanner.JobOnCell(pawn, target.Cell, true);
                if (job == null)
                {
                    // transient nearly every time - WorkGiver_Haul comes back
                    // null when every destination cell is reserved by a
                    // teammate. Leave it for whoever's free next.
                    Logger.Message($"{pawn.LabelShort}: no job for {target} ({order.WorkGiverDef.defName}), left in pool, {order.SharedPool.Count} total");
                    refused.Add(target);
                    continue;
                }

                order.SharedPool.RemoveAt(i);

                // plantDefToSow is the field the whole GrowerSow static-state
                // bug turned on, so name it explicitly - "which crop did we
                // actually tell them to plant, on which cell" is the single
                // most useful line in a sow trace
                Logger.Message($"{pawn.LabelShort}: {job.def.defName} on {target}"
                    + (job.plantDefToSow != null ? $" plant={job.plantDefToSow.defName}" : "")
                    + $" ({order.SharedPool.Count} left)");

                // WorkGiver answered "clear this blocker first" rather than
                // the work asked for (GrowerSow returns CutPlant/HaulAside).
                // Put the target back so the real work still happens once the
                // blocker's gone, instead of dropping the cell we just cleared.
                if (GrowerCompat.IsPreparatoryJob(job, target) && order.Requeued.Add(target))
                {
                    order.SharedPool.Add(target);
                }

                GiveJob(pawn, job);
                return;
            }

            // Pool's empty even after a rescan - this pawn is done. Said
            // nothing at all until now, so a sweep ending looked exactly
            // like a pawn wandering off for no reason; seven pawns in the
            // 08-22 log ended here with a bare "job ended Succeeded" as
            // their last line.
            Logger.Message($"{pawn.LabelShort}: nothing left within {order.ScanRadius} of {order.ScanCenter}, ending sweep ({order.WorkGiverDef.defName})");
            RemoveSweep(pawn);
        }

        // A workstation order has no pool to fall back on, so a null answer
        // from the station used to end it on the spot. Most nulls are
        // temporary - see WorkstationRetryTicks - so ask again shortly
        // instead, unless the bench genuinely has no work queued, which is
        // the ending "until the bills are done" is supposed to have.
        //
        // BillStack.AnyShouldDoNow is vanilla's own "is anything on this
        // bench worth doing" test (verified as a no-argument property in this
        // build). It accounts for suspended bills and for a repeat count
        // already met, and it is deliberately not affected by the ingredient
        // cooldown - which is exactly the distinction needed here.
        private void WorkstationHadNoJob(Pawn pawn, SweepOrder order)
        {
            string station = order.WorkstationTarget.LabelShort;

            if (!(order.WorkstationTarget is IBillGiver billGiver)
                || billGiver.BillStack == null
                || !billGiver.BillStack.AnyShouldDoNow)
            {
                Logger.Message($"{pawn.LabelShort}: no bills left at {station}, ending sweep ({order.WorkGiverDef.defName})");
                RemoveSweep(pawn);
                return;
            }

            consecutiveFailures.TryGetValue(pawn, out int failures);
            failures++;
            if (failures >= MaxConsecutiveFailures)
            {
                Logger.Message($"{pawn.LabelShort}: {station} gave no job {failures} times running, ending sweep ({order.WorkGiverDef.defName})");
                RemoveSweep(pawn);
                return;
            }

            consecutiveFailures[pawn] = failures;
            workstationRetryAt[pawn] = Find.TickManager.TicksGame + WorkstationRetryTicks;
            Logger.Message($"{pawn.LabelShort}: no job at {station} (try {failures}), asking again in {WorkstationRetryTicks} ticks");
        }

        // The pool is built once, against one driver pawn, at sweep start.
        // Everything re-checked here is something that can differ per pawn
        // (allowed area, reachability) or drift while the sweep runs (the
        // target getting destroyed, forbidden, reserved, or its zone's sow
        // toggle being switched off mid-sweep).
        // Returns null when the pawn can work this target, otherwise the
        // name of the check that said no.
        //
        // (was a bare bool. Refusals used to consume the target, so a wrong
        // one only cost you that target and nothing was traced; now they
        // leave it in the pool for someone else, and "nobody will take this
        // and no line says why" is a worse hole than the noise. Constant
        // strings - nothing allocates on the happy path.)
        private string TargetRefusalReason(Pawn pawn, LocalTargetInfo target, WorkGiver_Scanner scanner, bool firefighting)
        {
            Area allowed = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap;

            // checked for both target kinds up front: a fire that starts
            // after the pool was built is exactly the case the scan-time
            // filter can't catch, and a sweep can run for a long while.
            // Obviously exempt when the fire is the point.
            if (!firefighting && TaskScanner.TargetIsBurning(target, map))
            {
                return "burning";
            }

            if (target.HasThing)
            {
                Thing thing = target.Thing;
                if (thing == null || thing.Destroyed || thing.Map != map)
                {
                    return "gone";
                }
                if (thing.IsForbidden(pawn))
                {
                    return "forbidden";
                }
                if (allowed != null && !allowed[thing.Position])
                {
                    return "outside allowed area";
                }
                if (!GrowerCompat.CanReachTarget(pawn, thing, scanner))
                {
                    return "unreachable";
                }
                return map.reservationManager.CanReserve(pawn, thing) ? null : "reserved";
            }

            // cell target (e.g. an empty tile waiting to be sown) - no
            // Thing to check Destroyed/forbidden on, just bounds + area +
            // sow gates + reachability + reservation
            IntVec3 cell = target.Cell;
            if (!cell.InBounds(map))
            {
                return "out of bounds";
            }
            if (allowed != null && !allowed[cell])
            {
                return "outside allowed area";
            }
            if (!GrowerCompat.SowSettingsAllow(scanner, cell, map))
            {
                return "sow settings";
            }
            if (!GrowerCompat.CanReachTarget(pawn, cell, scanner))
            {
                return "unreachable";
            }
            return map.reservationManager.CanReserve(pawn, target) ? null : "reserved";
        }

        // Only the target actually ceasing to exist. Everything else that
        // can stop a pawn working a target - reserved, forbidden, out of
        // area, unreachable, sow toggle flipped - can come back, and lives
        // in TargetRefusalReason instead. Split out when the pool stopped
        // being consumed on failure: something has to still prune the
        // corpses or a long sweep walks past dead entries forever.
        private bool TargetIsGone(LocalTargetInfo target)
        {
            if (!target.HasThing)
            {
                return !target.Cell.InBounds(map);
            }

            Thing thing = target.Thing;
            return thing == null || thing.Destroyed || !thing.Spawned || thing.Map != map;
        }

        // Rescans used to be append-only, which was safe when a target left
        // the pool the moment it was handed out. It isn't now - a rescan
        // finds everything still sitting there and would double it every
        // time. Only take what we don't already have.
        private static void AddNewTargets(SweepOrder order, List<LocalTargetInfo> found)
        {
            if (found == null || found.Count == 0)
            {
                return;
            }

            var have = new HashSet<LocalTargetInfo>(order.SharedPool);
            foreach (LocalTargetInfo target in found)
            {
                if (have.Add(target))
                {
                    order.SharedPool.Add(target);
                }
            }
        }

        // Centre-out: the target nearest the cell the player clicked wins,
        // and the pawn's own position only separates targets that tie on
        // that. Was nearest-to-pawn until 2026-08-27, which meant a group
        // sweep dissolved into each pawn tidying whatever was under its own
        // feet and the pile the player actually pointed at got cleared
        // whenever. The pool empties in rings now.
        //
        // Ties are common rather than exotic - a radial scan produces plenty
        // of cells the same distance from centre - so the tie-break earns
        // its keep: it stops two pawns racing for the same ring cell when
        // one of them is standing next to a different one.
        //
        // Known cost, accepted deliberately (see architecture doc section 0,
        // TOP PRIORITY item A): a pawn can walk past a target beside them to
        // reach one closer to the click. sweepRadius bounds it - trivial at
        // the default 16, visible at the maximum 50.
        //
        // skip = targets this pawn has already been refused this call. -1
        // means the pool holds nothing left for them.
        // The only place sweep order is decided. centerOut ranks by distance
        // from the clicked cell (the pawn only breaks ties); otherwise it's
        // the pre-08-27 rule, nearest to the pawn, ignoring the click. Which
        // one applies is stamped on the order at click time - see
        // SweepOrder.CenterOut.
        //
        // Same loop for both rather than two: the pawn distance is needed in
        // either case, so the modes differ only in which value ranks and
        // which one is the tie-break.
        private static int NextTargetIndex(IntVec3 center, IntVec3 pawnPos, List<LocalTargetInfo> pool, HashSet<LocalTargetInfo> skip, bool centerOut)
        {
            // LengthHorizontalSquared is int on IntVec3, so these compare
            // exactly and the tie-break branch is a real equality, not a
            // float one that never fires
            int best = -1;
            int bestPrimary = 0;
            int bestSecondary = 0;

            for (int i = 0; i < pool.Count; i++)
            {
                if (skip.Contains(pool[i]))
                {
                    continue;
                }

                IntVec3 cell = pool[i].Cell;
                int fromCenter = (cell - center).LengthHorizontalSquared;
                int fromPawn = (cell - pawnPos).LengthHorizontalSquared;

                int primary = centerOut ? fromCenter : fromPawn;
                int secondary = centerOut ? fromPawn : fromCenter;

                if (best < 0
                    || primary < bestPrimary
                    || (primary == bestPrimary && secondary < bestSecondary))
                {
                    best = i;
                    bestPrimary = primary;
                    bestSecondary = secondary;
                }
            }

            return best;
        }
    }
}
