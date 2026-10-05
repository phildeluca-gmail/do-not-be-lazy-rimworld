using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using DoNotBeLazy.Comps;
using DoNotBeLazy.Core;
using DoNotBeLazy.Jobs;
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

        // The player's actual order, when the WorkGiver serves more than
        // one. Null on every sweep that needs no narrowing. Fixed at click
        // time and applied to the first scan AND to every rescan, or a
        // chop-wood sweep would quietly turn into a cut-plants sweep the
        // moment the pool ran dry. See PlantCompat.
        public Predicate<Thing> TargetFilter { get; }

        // Targets already put back in the pool once after the WorkGiver
        // answered with blocker-clearing work instead of the task itself.
        // Capped at one re-queue each: a cell that can never be satisfied
        // would otherwise hand out the same preparatory job forever.
        public HashSet<LocalTargetInfo> Requeued { get; } = new HashSet<LocalTargetInfo>();

        // How many times each target has been handed out and come straight
        // back as a recoverable failure. Added 2026-09-07.
        //
        // WHY. A target is removed from the pool when its job is issued, so
        // one failure never repeated. The rescan put it back:
        // AssignNextTask rescans when a pawn runs the pool dry, and a
        // blueprint that cannot accept a delivery is found again every time,
        // handed out again, and fails again. On 2026-09-07 one wall
        // blueprint was issued FORTY times - 37 Incompletable, 1 Errored, 1
        // Succeeded - Inga took five different blueprints inside a single
        // tick, and four pawns tripped RimWorld's own "started 10 jobs in 10
        // ticks" guard. Colony-wide that session: 1386 Incompletable against
        // 4466 Succeeded.
        //
        // The count is per ORDER, not per pawn, on purpose. A target that
        // one pawn cannot use is usually one no pawn can use, and the
        // failures were spread across the whole sweep rather than
        // concentrated on one colonist.
        public Dictionary<LocalTargetInfo, int> TargetFailures { get; } = new Dictionary<LocalTargetInfo, int>();

        // Scanner orders only: the highest daysWorkingSinceLastFinding seen
        // on this station so far. A find resets that field to zero, so the
        // number going down is the completion condition - see
        // ScannerCompat.FoundSomething. Inert on every other order type.
        public float MaxScanDays;

        // The player's order in words - PlantCompat.LabelFor of the clicked
        // thing - or null when the def serves only one order. Exists only so
        // a repeated click can be recognised as the same order: TargetFilter
        // cannot be compared, because PlantCompat.FilterFor builds a new
        // closure on every click. Added 2026-09-13, architecture section 12.
        public string OrderKind { get; }

        // The area wait line is written at most once per order per
        // AreaRetryTicks, and the waits in between are counted rather than
        // written. Added 2026-09-13: 13 pawns on one pool wrote that line
        // about 130 times in five minutes. Architecture section 12.
        public int NextWaitLogTick;
        public int WaitsNotLogged;

        public SweepOrder(WorkGiverDef workGiverDef, List<LocalTargetInfo> sharedPool, Thing workstationTarget = null,
            IntVec3 scanCenter = default(IntVec3), int scanRadius = 0, bool centerOut = true,
            Predicate<Thing> targetFilter = null, string orderKind = null)
        {
            WorkGiverDef = workGiverDef;
            SharedPool = sharedPool;
            WorkstationTarget = workstationTarget;
            ScanCenter = scanCenter;
            ScanRadius = scanRadius;
            CenterOut = centerOut;
            TargetFilter = targetFilter;
            OrderKind = orderKind;
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

        // How many recoverable failures one target may cost the sweep before
        // it is dropped for good. Added 2026-09-07 - see
        // SweepOrder.TargetFailures. Three rather than one because a
        // genuinely transient failure exists: a blueprint whose cell is
        // briefly occupied, a haul destination a teammate is standing on.
        // Three consecutive is not transient.
        private const int MaxTargetFailures = 3;

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

        // The area equivalent, and the same reasoning one level out. A pool
        // full of targets whose destinations are all reserved by teammates
        // is the normal state of a group haul for a few seconds at a time -
        // WorkGiver_Haul answers null for every one of them until somebody
        // finishes a delivery and frees a cell. Ten seconds is roughly one
        // haul leg, so that is when it is worth asking again.
        private const int AreaRetryTicks = 600;

        // How soon a pawn whose job start threw is asked again - short, so
        // the reservation storm that caused it has settled but the pawn has
        // not wandered off. Added 2026-10-05.
        private const int GiveJobRetryTicks = 30;

        // ...but a stockpile that is genuinely full stays full, and each
        // retry walks the whole pool. Ten tries is about a hundred seconds
        // of patience before the sweep is called finished for that pawn.
        private const int MaxAreaRetries = 10;

        // How close two area orders' centres must be, in tiles, for a repeat
        // click to count as the same order. Added 2026-09-13 - see SameOrder.
        private const int DuplicateCentreTiles = 5;

        private readonly Dictionary<Pawn, SweepOrder> activeSweeps = new Dictionary<Pawn, SweepOrder>();

        // The orders waiting behind each pawn's active one, oldest first.
        // Added 2026-09-13 on the user's order that * orders queue rather
        // than replace each other - architecture section 12.
        //
        // A queue exists only while its pawn has an active order. Nothing is
        // queued for a pawn with no order - it starts instead - and every
        // path that removes the active order either starts the next one
        // (RemoveSweep) or drops the lot (ClearSweeps). That is why
        // NeedMonitor, JobTrackerPatch and the tick pass needed no change:
        // a pawn with a queue is always already in activeSweeps.
        private readonly Dictionary<Pawn, List<SweepOrder>> queuedOrders = new Dictionary<Pawn, List<SweepOrder>>();

        // Per-pawn, not per-order: a SweepOrder is shared across everyone in
        // a group sweep, and one pawn hitting bad targets shouldn't count
        // against the others. Reset on any successful task.
        private readonly Dictionary<Pawn, int> consecutiveFailures = new Dictionary<Pawn, int>();

        // The pooled target each pawn was last sent to. Notify_JobEnded is
        // handed a pawn and a JobCondition and nothing else, so without this
        // there is no way to charge a failure to the target that caused it.
        private readonly Dictionary<Pawn, LocalTargetInfo> lastAssignedTarget =
            new Dictionary<Pawn, LocalTargetInfo>();

        // pawns pulled out of their sweep job for a critical need but still
        // tracked in activeSweeps - NeedMonitor adds them here via
        // PauseForNeed instead of RemoveSweep, so the sweep can resume once
        // the need is actually dealt with. Value is the tick they were
        // paused on, for the MaxPauseTicks give-up.
        //
        // (was a HashSet, and resumption was "first job end after a pause
        // wins". That's the bug - see Notify_JobEnded.)
        // Tick the pause started, and which need caused it. Both travel
        // together in one entry on purpose: a second dictionary keyed by pawn
        // would have to be cleared at all seven sites that drop a pause, and
        // one missed site is a leak nobody would notice. Added 2026-09-07 -
        // see NeedMonitor.CriticalNeedLabel for why the need is recorded.
        private struct PauseInfo
        {
            public int tick;
            public string need;

            // Whether the long-pause line has already been emitted for this
            // pause. Added 2026-09-07 with the give-up removal: the check now
            // runs every StateCheckIntervalTicks for as long as the pause
            // lasts, and a line per check is exactly the shape the standing
            // rule forbids.
            public bool warned;

            // TryForceRestIfStuck's own throttle - added 2026-09-27. Set to
            // `tick` at pause start so the first attempt still waits out
            // ForceRestRetryTicks rather than firing on the very next
            // GameComponentTick, and advanced on every attempt after that
            // (successful or not) so a bed search runs at most once per
            // window instead of every 60 ticks.
            public int lastForceCheckTick;

            // One "no bed" decline logged per pause rather than one per
            // retry - same shape as `warned` above.
            public bool declineLogged;

            // Section 21 - one "still not eating" line per pause from
            // WarnIfFoodStuck, same shape as declineLogged above.
            public bool foodStuckWarned;

            // Section 21, added 2026-09-28 review fix: TryForceEatIfStuck's
            // own "no food could be forced" decline, logged once per pause
            // rather than once per retry - same shape as declineLogged
            // above, but the reason text travels with it so a change in
            // WHY still gets its own line, and a later successful retry
            // clears both (TryForceEatNow).
            public bool foodDeclineLogged;
            public string foodDeclineReason;

            // Added 2026-10-02. The last reason TryForceEatIfStuck returned
            // early, so each distinct reason is written once per pause and a
            // silent non-retry can be explained; and whether the "Food is
            // already satisfied" line has been written for this pause.
            public string earlyReturnReason;
            public bool foodSatisfiedLogged;

            // Change B, 2026-10-02: the "held by Recreation/mood only" line
            // has been written for this pause.
            public bool otherNeedLogged;

            // Change C: the last reason TryForceJoyIfStuck declined, so each
            // distinct reason is written once per pause.
            public string joyDeclineReason;

            // Change D: the last reason TryForceRestIfStuck returned at a
            // silent gate, written once per distinct reason per pause.
            public string restDeclineReason;

            // 2026-10-04 (Van: Food pause, Food satisfied, Rest 10% held it,
            // never sent to bed). The last tick TryForceRestIfStuck acted
            // while the pause was held by Rest but did not START on Rest; 0
            // means never. Separate from lastForceCheckTick, which is set to
            // the pause's start and would make a pawn that finished eating
            // wait out a whole ForceRestRetryTicks first. And the last busy
            // job already written down, once per distinct job def per pause.
            public int restHeldCheckTick;
            public string restBusyJob;

            // 2026-10-04: the same two for TryForceJoyIfStuck - the last tick
            // it acted on a pause that did not START on Recreation (0 means
            // never), and the last busy job already written down.
            public int joyHeldCheckTick;
            public string joyBusyJob;
        }

        private readonly Dictionary<Pawn, PauseInfo> pausedForNeed = new Dictionary<Pawn, PauseInfo>();

        // Workstation orders whose station answered null, and the tick to ask
        // it again on. A workstation order has no pool, so nothing else would
        // ever bring the pawn back: before this existed the first null answer
        // ended the order outright, which is half of "they don't return to
        // the bench". While an entry is here the pawn is working for vanilla,
        // so Notify_JobEnded ignores their job ends - otherwise the next
        // thing they finished would re-ask the station immediately and burn
        // the whole retry budget in a few seconds.
        private readonly Dictionary<Pawn, int> workstationRetryAt = new Dictionary<Pawn, int>();

        // Area orders whose pool still holds targets but where every one of
        // them was refused for this pawn right now, and the tick to look
        // again on. This is the difference between "nothing left to do" and
        // "nothing I can do this second", which AssignNextTask used to
        // collapse into a single RemoveSweep - see the ending there. Same
        // shape as workstationRetryAt, and for the same reason: while an
        // entry is here the pawn is working for vanilla, so job ends are
        // ignored rather than burning the budget in a few seconds.
        private readonly Dictionary<Pawn, int> areaRetryAt = new Dictionary<Pawn, int>();

        // How many times in a row this pawn has found the pool inert.
        // Cleared by any successful assignment.
        private readonly Dictionary<Pawn, int> areaRetries = new Dictionary<Pawn, int>();

        // How long one pawn stays quiet after a line saying a job end was
        // thrown away because AssigningJob was set. 600 ticks, the same round
        // as AreaRetryTicks. Added 2026-09-18 - architecture section 15.
        private const int DiscardedEndQuietTicks = 600;

        // The last job this mod handed the pawn. Read by one thing only: the
        // tick check that asks whether something else has taken the pawn
        // over. A reference comparison against pawn.CurJob, which is exact
        // and costs nothing - the job object is never reused. Added
        // 2026-09-18 - architecture section 15.
        private readonly Dictionary<Pawn, Job> ourJob = new Dictionary<Pawn, Job>();

        // The last foreign (vanilla-driven, not player-forced) job named in
        // a "we still hold the order" line for this pawn, so the same job
        // running on for minutes writes one line and not one per 60-tick
        // poll. Re-added 2026-09-27, simplified from the 2026-09-18
        // MaxForeignJobReports/ForeignJobInfo pair (removed earlier the same
        // day, when the first version of this fix ended the sweep instead of
        // reporting it - reinstated once that fix was narrowed). See
        // TryReportForeignJob.
        private readonly Dictionary<Pawn, Job> lastReportedForeignJob = new Dictionary<Pawn, Job>();

        // Job ends thrown away because this pawn was being handed a job at
        // the moment its own prior job ended. The tick to write the next
        // line on, and how many were thrown away and not written since the
        // last one.
        private struct DiscardedEndInfo
        {
            public int nextLogTick;
            public int suppressed;
        }

        private readonly Dictionary<Pawn, DiscardedEndInfo> discardedEnds = new Dictionary<Pawn, DiscardedEndInfo>();

        // Pawns already given their one "passed over" line on the sweep
        // they are on now. Cleared in EndOrder. Added 2026-10-02 so a pawn
        // the sweep keeps finding nothing for (Timea, idle ten minutes with
        // no line at all) says why once, with the counts.
        private readonly HashSet<Pawn> passedOverLogged = new HashSet<Pawn>();

        // Change A, 2026-10-02: the tick before which a pawn is not sent to
        // unload its stuffed cargo again, and the pawns already told they
        // are overloaded with their own items. Both cleared in EndOrder.
        private const int CargoUnloadQuietTicks = 600;
        private readonly Dictionary<Pawn, int> cargoUnloadAt = new Dictionary<Pawn, int>();
        private readonly HashSet<Pawn> overloadedOwnItemsLogged = new HashSet<Pawn>();

        // A non-sweep job ending on a sweep pawn, deliberately left alone by
        // the ourJob gate in Notify_JobEnded. Same shape as DiscardedEndInfo
        // above - the tick to write the next line on, and how many were
        // left alone without a line since the last one. Added 2026-09-28,
        // section 22 ("bUILD IT").
        private const int NonSweepJobEndQuietTicks = 600;

        private struct NonSweepJobEndInfo
        {
            public int nextLogTick;
            public int suppressed;
        }

        private readonly Dictionary<Pawn, NonSweepJobEndInfo> nonSweepJobEnds = new Dictionary<Pawn, NonSweepJobEndInfo>();

        // How long one pawn stays quiet after a line saying its target was
        // taken by another job and it was retargeted to the next-closest
        // one. Same round number as DiscardedEndQuietTicks, for the same
        // reason - a pawn whose resource keeps getting stolen over a long
        // order would otherwise write one line per incident. Added
        // 2026-09-19 - architecture section 14, the open question answered.
        private const int RetargetQuietTicks = 600;

        // A retarget line already written for this pawn, and how many more
        // happened since - same shape as DiscardedEndInfo, for the same
        // reason.
        private struct RetargetInfo
        {
            public int nextLogTick;
            public int suppressed;
        }

        private readonly Dictionary<Pawn, RetargetInfo> retargets = new Dictionary<Pawn, RetargetInfo>();

        // TryTakeOrderedJob interrupts whatever the pawn is doing right now,
        // and that fires EndCurrentJob(InterruptForced) -> JobTrackerPatch's
        // postfix -> Notify_JobEnded -> RemoveSweep. So handing a pawn a
        // sweep job was cancelling the sweep that handed it out. Verified
        // against the DLL: TryTakeOrderedJob -> StartJob -> EndCurrentJob.
        // This lets JobTrackerPatch ignore the job end it caused itself.
        //
        // Narrowed from one static bool for the whole game to one entry per
        // pawn, 2026-09-19 - architecture section 14, "Seen while diagnosing,
        // not changed" and section 15. The old single flag silenced every
        // OTHER pawn's job ending too while any one pawn was being handed a
        // job, which is what lost a tamed kangaroo on 2026-09-19: it stayed
        // registered on a sweep order after its own job ended for an
        // unrelated reason, because the ignore window belonged to whichever
        // pawn was being assigned at that moment, not to the kangaroo.
        // Confirmed live the same day: a player-forced job (every sweep job
        // is one, see section 14) really does take another pawn's
        // reservation and end that pawn's current job with
        // JobCondition.InterruptForced - verified from the IL of
        // ReservationManager.Reserve and Pawn_JobTracker.TryTakeOrderedJob.
        // That ending is real and now reaches JobTrackerPatch's normal path
        // instead of being thrown away.
        //
        // The value is a depth, not a bool, so a pawn's own assignment can
        // nest - PauseForNeed can end a job while GiveJob is already
        // suppressing for the same pawn - without the inner call's finally
        // block clearing the outer call's suppression early. A pawn absent
        // from the dictionary, or present with 0, is not being assigned.
        private static readonly Dictionary<Pawn, int> assigningJobDepth = new Dictionary<Pawn, int>();

        // Read by JobTrackerPatch to decide whether THIS pawn's job ending
        // is the one this mod is causing right now, rather than some other
        // pawn's job ending for an unrelated reason during the same window.
        public static bool IsBeingAssigned(Pawn pawn)
        {
            return pawn != null && assigningJobDepth.TryGetValue(pawn, out int depth) && depth > 0;
        }

        // Marks pawn as "being assigned" for the duration of the caller's
        // try block. Always paired with EndAssigningJob in a finally, so the
        // marker is cleared even if the assignment throws - a leaked entry
        // would silence every future job ending for that pawn forever, which
        // is the same bug this change removes, just scoped to one pawn
        // instead of the whole map.
        private static void BeginAssigningJob(Pawn pawn)
        {
            assigningJobDepth.TryGetValue(pawn, out int depth);
            assigningJobDepth[pawn] = depth + 1;
        }

        private static void EndAssigningJob(Pawn pawn)
        {
            if (!assigningJobDepth.TryGetValue(pawn, out int depth))
            {
                return;
            }

            if (depth <= 1)
            {
                assigningJobDepth.Remove(pawn);
            }
            else
            {
                assigningJobDepth[pawn] = depth - 1;
            }
        }

        public SweepManager(Map map) : base(map)
        {
        }

        // Throttle for GiveJobFailedThrottled below - one line per pawn per
        // window rather than one per reentrant crash, same shape as
        // JobDriver_StuffAndHaul's skip-log tables. 2026-09-26.
        private const int GiveJobFailedQuietTicks = 250;
        private static readonly Dictionary<Pawn, int> giveJobFailedLogExpiry = new Dictionary<Pawn, int>();

        // Throttle for the missing-sweep-state warning below - same shape as
        // GiveJobFailedQuietTicks/giveJobFailedLogExpiry above, and caused by
        // the same class of reentrancy. Added 2026-09-27.
        private const int MissingSweepStateQuietTicks = 250;
        private static readonly Dictionary<Pawn, int> missingSweepStateLogExpiry = new Dictionary<Pawn, int>();

        // Throttle for MapComponentTickThrottled below - same shape again.
        // Added 2026-09-27 per user order: "Don't end all pawn's activities
        // because one pawn stopped." Before this, an unhandled exception
        // anywhere in one pawn's turn through this loop (ClearSweeps,
        // TryResumeFromNeed, TryScannerWatchdog, TryWorkstationRetry,
        // TryAreaRetry, TryReportForeignJob) aborted the rest of THIS tick's
        // pass for every pawn still to come - the same silent-for-everyone-
        // else failure mode the missing-sweep-state guard above was written
        // for, just one level higher up the call stack.
        private const int MapComponentTickExceptionQuietTicks = 250;
        private static readonly Dictionary<Pawn, int> mapComponentTickExceptionLogExpiry = new Dictionary<Pawn, int>();

        private static void MapComponentTickExceptionThrottled(Pawn pawn, Exception ex)
        {
            int now = Find.TickManager.TicksGame;
            if (mapComponentTickExceptionLogExpiry.TryGetValue(pawn, out int expiry) && now < expiry)
            {
                return;
            }

            mapComponentTickExceptionLogExpiry[pawn] = now + MapComponentTickExceptionQuietTicks;
            Logger.Warning($"{pawn.LabelShort}: exception during this pawn's turn in the sweep tick pass - skipping {pawn.LabelShort} this tick and continuing with the rest of the pass: {ex}");
        }

        // Not throttled: unlike the tick-pass guard above, this fires at most
        // once per pawn per group-order click, never per tick, so there is no
        // message-cap risk. Added 2026-09-27, same order: a group order is
        // meant to drop the pawns who cannot and keep the pawns who can
        // (architecture doc section 9) - an unhandled exception asking one
        // pawn's own answer must not stop the rest of the group from being
        // asked in turn.
        private static void GroupOrderPawnExceptionCaught(Pawn pawn, string workGiverDefName, Exception ex)
        {
            Logger.Warning($"{pawn.LabelShort}: exception while asking for a job on {workGiverDefName} - {pawn.LabelShort} does not join this order, rest of the group is still asked: {ex}");
        }

        // Fixed 2026-09-26 - seen live at 21:53:48: Noob's own job ending
        // called vanilla's EnrouteManager.InterruptEnroutePawns (a
        // HaulToContainer job's destination container was full), which ended
        // a DIFFERENT pawn's job while Noob's own GiveJob call was still on
        // the stack (Job.TryMakePreToilReservations, called from inside
        // Pawn_JobTracker.TryTakeOrderedJob's own pre-check). Architecture
        // section 14 already accepts that an interrupted pawn reaches
        // Notify_JobEnded and gets reassigned reentrantly rather than being
        // thrown away - that part is by design. What is not by design: the
        // reassigned pawn's own StartJob then re-entered the SAME vanilla
        // enroute machinery a second time, and a NullReferenceException in
        // JobDriver_HaulToContainer.UpdateTracker (verified by decompiling
        // Assembly-CSharp.dll with ilspycmd - get_ThingToCarry reads
        // job.targetA fresh on every call, so a second read mid-reservation
        // found it gone) unwound uncaught through vanilla's own
        // EndCurrentJob and InterruptEnroutePawns and errored NOOB'S job -
        // an unrelated pawn three frames further up the same stack.
        // Pawn_JobTracker.StartJob has already set curJob/curDriver for the
        // reassigned pawn by the point it throws (verified: the assignment
        // at line ~291 precedes TryMakePreToilReservations at line ~296),
        // leaving that pawn on a job whose driver has no toils set up.
        // Nothing needs cleaning up for that by hand: JobDriver.DriverTick
        // finds CurToil null forever and ends the job with
        // JobCondition.Succeeded on its own very next tick, and
        // Notify_JobEnded picks the pawn back up normally from there.
        // Catching here, at the one place every sweep job is issued, keeps a
        // reentrant failure to the pawn it actually belongs to instead of
        // crashing whichever pawn's call chain it happened to unwind through.
        private static void GiveJobFailedThrottled(Pawn pawn, Job job, Exception ex)
        {
            int now = Find.TickManager.TicksGame;
            if (giveJobFailedLogExpiry.TryGetValue(pawn, out int expiry) && now < expiry)
            {
                return;
            }

            giveJobFailedLogExpiry[pawn] = now + GiveJobFailedQuietTicks;
            // Full exception now written to log instead of just the type name - 2026-09-26
            Logger.Warning($"{pawn.LabelShort}: declined {DescribeJob(job)} - exception starting it reentrantly: {ex.ToString()}; vanilla will end the half-started job on its own next tick and we'll pick {pawn.LabelShort} back up from there");
        }

        // every TryTakeOrderedJob in the mod goes through here - see
        // BeginAssigningJob / EndAssigningJob above
        //
        // appendBehindCurrentJob is TryTakeOrderedJob's own requestQueueing
        // parameter, passed through. Added 2026-09-21. Verified against
        // Pawn_JobTracker.TryTakeOrderedJob in this build by decompiling
        // Assembly-CSharp.dll: when requestQueueing is true and the pawn is
        // NOT idle, the method skips the branch that ends the pawn's current
        // job and instead calls jobQueue.EnqueueLast(job, tag) - the job is
        // appended behind the pawn's current job and whatever else is
        // already in its queue, and nothing is interrupted. When the pawn
        // IS idle (no current job, or an idle job), it starts the job right
        // away regardless of this flag - there is nothing to queue behind.
        //
        // Wrapped in a catch since 2026-09-26 - see GiveJobFailedThrottled
        // above. A reentrant call here (this pawn was interrupted by
        // vanilla's own job machinery while ANOTHER pawn was being handed a
        // job) can throw mid-StartJob; left uncaught it unwinds out through
        // vanilla's own call chain and errors an unrelated pawn's job.
        //
        // forcedKind (2026-10-02): non-null for a job that is its own order
        // rather than a step of a sweep - a forced meal, a forced rest - and
        // gives it its own ORDER lines. Null for a sweep step, which writes
        // the sweep order's "started" line the first time one is taken.
        //
        // Since 2026-10-05 it returns whether the job was taken, and sets
        // LastGiveJobThrew when TryTakeOrderedJob threw - see
        // AssignNextTask's recovery for why the caller needs to know.
        public static bool LastGiveJobThrew;
        public static string LastGiveJobExceptionType;

        // Consecutive GiveJob throws per pawn, cleared on a successful take.
        // After MaxGiveJobThrowsInARow the pawn is taken off its order.
        // Added 2026-10-05 on the coordinator's order.
        private const int MaxGiveJobThrowsInARow = 5;
        private static readonly Dictionary<Pawn, int> giveJobThrowStreak = new Dictionary<Pawn, int>();

        public static bool GiveJob(Pawn pawn, Job job, bool appendBehindCurrentJob = false, string forcedKind = null)
        {
            bool took = false;
            LastGiveJobThrew = false;
            BeginAssigningJob(pawn);
            try
            {
                took = pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, appendBehindCurrentJob);
            }
            catch (Exception ex)
            {
                LastGiveJobThrew = true;
                LastGiveJobExceptionType = ex.GetType().Name;
                GiveJobFailedThrottled(pawn, job, ex);
            }
            finally
            {
                EndAssigningJob(pawn);
            }

            if (took)
            {
                giveJobThrowStreak.Remove(pawn);
            }

            if (forcedKind != null)
            {
                TrackForcedOrder(pawn, forcedKind, job, took);
            }
            else if (took && orderIds.TryGetValue(pawn, out string sweepOrderId) && orderStarted.Add(pawn))
            {
                Logger.Order($"ORDER {sweepOrderId} started: {pawn.LabelShort} {DescribeJob(job)} {job.targetA}");
            }

            return took;
        }

        // ORDER lines, ordered 2026-10-02 - one id per order per pawn, three
        // kinds of line a script can match:
        //   ORDER DNBL-n issued: <pawn> <order kind> <detail>
        //   ORDER DNBL-n started: <pawn> <job def> <target>
        //   ORDER DNBL-n ended (<reason>): <pawn> <detail>
        // A sweep order is issued when the pawn joins it (NoteOrderIssued),
        // started when the first sweep job is taken (GiveJob) and ended in
        // EndOrder, the one place every sweep ending passes through. Forced
        // meals, forced rests and Drop everything are orders of their own.
        // Each line fires once per order per pawn, never per tick or target.
        private static int orderCounter;
        private static readonly string launchStamp = DateTime.Now.ToString("HHmm");

        // The input behind the click that is starting orders right now, set
        // by FloatMenuPatch around BeginSweep so NoteOrderIssued can name
        // it. Null when an order starts without a click (a queued order
        // taking over, StartNextQueued).
        public static string LastClickKeys;

        private static readonly Dictionary<Pawn, string> orderIds = new Dictionary<Pawn, string>();
        private static readonly HashSet<Pawn> orderStarted = new HashSet<Pawn>();

        // The pawn's current sweep order id, or "DNBL-?" when it has none
        // (a pause with no sweep behind it should not happen).
        private static string OrderIdFor(Pawn pawn)
        {
            return orderIds.TryGetValue(pawn, out string id) ? id : "DNBL-?";
        }

        public static string NextOrderId()
        {
            // 2026-10-04: the counter restarts at 1 on every launch, so the
            // same id named different orders across sessions in one log. The
            // launch stamp (HHmm, no spaces) makes it unique: DNBL-2243-24.
            return "DNBL-" + launchStamp + "-" + (++orderCounter);
        }

        private static void NoteOrderIssued(Pawn pawn, SweepOrder order)
        {
            string id = NextOrderId();
            orderIds[pawn] = id;
            orderStarted.Remove(pawn);
            Logger.Order($"ORDER {id} issued: {pawn.LabelShort} sweep {order.WorkGiverDef.defName} {DescribeWhere(order)} ({LastClickKeys ?? "keys: none - a queued order taking over"})");
        }

        // The words for how a driver's job ended. Job.playerInterruptedForced
        // is still intact while JobDriver.Cleanup runs finish actions - the
        // job is only pooled (and cleared) afterwards.
        public static string DescribeEnding(Pawn pawn, JobCondition condition, Job job)
        {
            if (pawn.Dead) return "pawn dead";
            if (pawn.Downed) return "pawn downed";
            switch (condition)
            {
                case JobCondition.Succeeded:
                    return "finished";
                case JobCondition.InterruptForced:
                    return job != null && job.playerInterruptedForced ? "player gave a different order" : "interrupted by another job";
                case JobCondition.Incompletable:
                    return "target gone or unreachable";
                default:
                    return condition.ToString();
            }
        }

        // For a job that is an order by itself. Started and ended lines both
        // come from the pawn's own JobDriver: AddFinishAction is public and
        // JobDriver.Cleanup runs it for every ending with the condition, so
        // no patch is needed.
        public static void TrackForcedOrder(Pawn pawn, string kind, Job job, bool took)
        {
            string name = pawn.LabelShort;
            string def = job?.def?.defName ?? "no job";
            string target = job != null ? job.targetA.ToString() : "-";
            string id = NextOrderId();
            Logger.Order($"ORDER {id} issued: {name} {kind} {def} {target} (keys: none - issued by the mod's own check, not a click)");

            Job cur = pawn.jobs?.curJob;
            JobDriver driver = pawn.jobs?.curDriver;
            if (!took)
            {
                Logger.Order($"ORDER {id} ended (failed to start: the game refused the order): {name} {def} {target}");
                return;
            }

            if (cur == null || !ReferenceEquals(cur, job) || driver == null)
            {
                Logger.Order($"ORDER {id} ended (failed to start: accepted but not running, pawn is on {DescribeJob(cur)}): {name} {def} {target}");
                return;
            }

            Logger.Order($"ORDER {id} started: {name} {def} {target}");
            driver.AddFinishAction(condition =>
            {
                Logger.Order($"ORDER {id} ended ({DescribeEnding(pawn, condition, job)}): {name} {def} {target} ({condition})");
            });
        }

        // "DoBill Make_ComponentIndustrial", "HaulToCell", or "no job".
        //
        // job.def alone was what every assignment and job-ended line carried,
        // and for a bench order that is the word "DoBill" and nothing else -
        // which recipe the pawn was actually making was never written down.
        // On 2026-09-17 that was the one question the evening's log could not
        // answer. job.bill and Bill.recipe are both plain fields (read off
        // lib\Assembly-CSharp.dll, not remembered), so this cannot throw and
        // allocates only when there is a bill. Added 2026-09-18.
        private static string DescribeJob(Job job)
        {
            if (job?.def == null)
            {
                return "no job";
            }

            RecipeDef recipe = job.bill?.recipe;
            return recipe == null ? job.def.defName : $"{job.def.defName} {recipe.defName}";
        }

        // "JobGiver_Work [Assembly-CSharp]" - the ThinkNode that issued a job
        // and the assembly that node came from, which is what names the mod
        // responsible when it is not vanilla. Same idea as JobSourcePatch,
        // read off Job.jobGiver, which is a plain ThinkNode field on Job.
        // Added 2026-09-18.
        private static string DescribeJobSource(Job job)
        {
            ThinkNode giver = job?.jobGiver;
            if (giver == null)
            {
                return job != null && job.playerForced ? "no job giver (player-forced)" : "no job giver";
            }

            Type type = giver.GetType();
            return $"{type.Name} [{type.Assembly.GetName().Name}]";
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
                // Wrapped per-pawn 2026-09-27 per user order: "Don't end all
                // pawn's activities because one pawn stopped." Before this,
                // an exception anywhere below - ClearSweeps, TryResumeFromNeed,
                // the three retries, TryReportForeignJob - aborted the rest of
                // THIS tick's pass for every other swept pawn on the map, not
                // just the one that threw. See MapComponentTickExceptionThrottled
                // above.
                try
                {
                    if (pawn.Dead || pawn.Downed || pawn.InMentalState || pawn.Drafted || pawn.Map != map)
                    {
                        // the pawn is out of the work, so its queued orders go
                        // too - architecture section 12, decision 4
                        ClearSweeps(pawn,
                            pawn.Dead ? "dead"
                            : pawn.Downed ? "downed"
                            : pawn.InMentalState ? "mental break"
                            : pawn.Drafted ? "drafted"
                            : "left the map");
                        continue;
                    }

                    // scanner orders never arm a retry (their JobOnThing has no
                    // null path) and bench orders are never scanner work, so
                    // these two are exclusive - the watchdog just returns on
                    // anything that isn't a scanner.
                    // Resuming used to happen ONLY on a job end. A pawn whose
                    // need recovered without ending a job we would hear about -
                    // or whose replacement jobs all ended while still under
                    // threshold - sat paused with nothing left to prompt a
                    // second look. Polled here for the same reason the three
                    // retries below are: there is no event to hang it on.
                    // Crashed live 2026-09-27 09:30:39: activeSweeps[pawn] threw
                    // KeyNotFoundException (Dictionary.get_Item) here, taking
                    // down the rest of this tick's pass for every other swept
                    // pawn on the map. `pawns` is a snapshot of activeSweeps.Keys
                    // taken at the top of this call, so every entry existed at
                    // that instant - but an earlier pawn's own turn in THIS SAME
                    // pass can remove a LATER pawn's entry before its turn comes
                    // up: AssignNextTask -> GiveJob can reenter vanilla's own job
                    // machinery (the same reentrancy GiveJobFailedThrottled
                    // above exists for, 2026-09-26) and end a different pawn's
                    // job, which can reach RemoveSweep/ClearSweeps for that pawn
                    // through the ordinary (non-suppressed) path. No matching
                    // "sweep ended" line was found in the log for this crash,
                    // so the exact removal could not be pinned to a pawn or a
                    // call site from the log alone - guarded defensively rather
                    // than left to crash the rest of the pass.
                    if (!activeSweeps.TryGetValue(pawn, out SweepOrder activeOrder))
                    {
                        int now = Find.TickManager.TicksGame;
                        if (!missingSweepStateLogExpiry.TryGetValue(pawn, out int expiry) || now >= expiry)
                        {
                            missingSweepStateLogExpiry[pawn] = now + MissingSweepStateQuietTicks;
                            Logger.Warning($"{pawn.LabelShort}: sweep state vanished mid-tick (no activeSweeps entry left by the time this pass reached it) - skipping this pawn this tick rather than crash the rest of the pass");
                        }

                        continue;
                    }

                    if (TryResumeFromNeed(pawn, activeOrder))
                    {
                        continue;
                    }

                    TryScannerWatchdog(pawn);
                    TryWorkstationRetry(pawn);
                    TryAreaRetry(pawn);
                    TryResumeIdle(pawn, activeOrder);

                    // last, because the three above can end the order or hand
                    // the pawn a fresh job in this same pass, and either answers
                    // the question this one asks
                    TryReportForeignJob(pawn);
                }
                catch (Exception ex)
                {
                    MapComponentTickExceptionThrottled(pawn, ex);
                }
            }
        }

        // End the sweep when a genuine external order has taken the pawn
        // over; report, without ending, when vanilla is merely driving the
        // pawn on its own. Added 2026-09-18 - architecture section 15.
        // Rewritten 2026-09-27 - architecture section 16.
        //
        // This is the case that cost the evening of 2026-09-17: Pelican was
        // on a "* fabricate things until done" order, stopped fabricating,
        // was handed flak jackets by vanilla instead, and the mod went on
        // believing it held him with not one line written about any of it.
        // The 2026-09-18 fix wrote the line but never ended the sweep, which
        // is a second, separate bug from the one that prompted it: a direct
        // player order to a swept pawn was reported as a takeover here and
        // then reclaimed anyway the next time any job of the pawn's ended,
        // because Notify_JobEnded's default for an unexplained job end is
        // "my own task finished, hand out the next one." Confirmed
        // 2026-09-27 against a live log: Kiriko and Bax both kept
        // re-registering on their HaulGeneral order after being given direct
        // orders, and the only thing that ever released a pawn all session
        // was a fresh sweep order replacing the old one outright (ClearSweeps'
        // own "replaced by a new ... order" path). Notify_JobEnded's
        // playerInterruptedForced check (2026-09-26) never once fired in
        // that log either - JobTrackerPatch's postfix discards an ended job
        // as self-caused whenever this mod is mid-hand-off for the same
        // pawn, and vanilla's TryTakeOrderedJob sets that same flag on ANY
        // job it replaces, including this mod's own - so the flag cannot
        // tell the two apart at that check.
        //
        // A first version of this fix (2026-09-27, same day) ended the
        // sweep on ANY curJob that was not the job this mod handed out. Too
        // broad - caught in review before being run in a game. Verified by
        // decompiling lib\Assembly-CSharp.dll: Pawn_JobTracker.StartJob, when
        // replacing an existing job, ends the old one through
        // CleanupCurrentJob directly (line ~269) rather than through
        // EndCurrentJob - so a job vanilla starts this way never touches this
        // mod's Harmony patch at all, and the pawn can be well into it long
        // before this 60-tick poll ever sees the mismatch. That path is how
        // a LOT of ordinary vanilla driving works: Verse.AI.Toils_Recipe.
        // FinishRecipeAndStartStoringProduct calls
        // actor.jobs.StartJob(HaulAIUtility.HaulToCellStorageJob(...), ...)
        // directly to haul a finished bill's product to the best stockpile
        // (line ~277) - every "make X until done" sweep would have ended
        // after its first product. Pawn_JobTracker.EndCurrentJob's own
        // opportunistic-job and Wait_MaintainPosture fallbacks (~408-420),
        // and CheckForJobOverride_NewTemp's higher-priority think-tree job
        // (~543, used for a fire, a hostile, self-preservation, a mental
        // break or a prison break - CLAUDE.md section 16's allowed
        // interrupts all arrive this way) all go through StartJob the same
        // way, as does TryFindAndStartJob picking the pawn's own next
        // ordinary job (eating, sleeping, joy).
        //
        // The discriminator: Verse.AI.Job.playerForced is set to true in
        // exactly one place in the whole decompiled assembly -
        // Pawn_JobTracker.TryTakeOrderedJob, line 858, unconditionally on
        // every call - and none of the StartJob-only paths above ever touch
        // it, so it stays false on whatever they hand the pawn. It survives
        // onto whatever job eventually runs even when the pawn was idle at
        // the moment of the order: TryTakeOrderedJob's own idle branch
        // enqueues the SAME Job object and starts it through
        // CheckForJobOverride_NewTemp -> StartJob, and StartJob never resets
        // the flag. This mod's own GiveJob goes through TryTakeOrderedJob
        // too and gets the flag as well, which is why it is not read alone -
        // only `playerForced && not the job this mod handed out` means a
        // genuine external order (the player's, or another mod's own
        // TryTakeOrderedJob call - Be Lazy's pending gizmo work would be
        // one). Checked before the paused/retry guards below, not after, so
        // a direct order given while this mod has let go of the pawn for a
        // moment - a need pause, a workstation retry, an area retry - still
        // ends the sweep instead of being missed until the gap closes.
        //
        // Why a tick check and not a hook on the job starting.
        // Pawn_JobTracker.EndCurrentJob calls TryFindAndStartJob inside its
        // own body, so vanilla has already started a replacement job by the
        // time this mod's postfix on EndCurrentJob runs and hands out the
        // next sweep job. Asking the question as the job starts would
        // therefore be true on every ordinary handover, which is once per
        // target. Asking it 60 ticks later asks it of a settled pawn, and
        // the answer is a real disagreement rather than a moment in the
        // handover.
        private void TryReportForeignJob(Pawn pawn)
        {
            if (!activeSweeps.TryGetValue(pawn, out SweepOrder order))
            {
                return;
            }

            Job current = pawn.jobs?.curJob;
            ourJob.TryGetValue(pawn, out Job mine);

            if (current != null && current.playerForced && !ReferenceEquals(current, mine))
            {
                ClearSweeps(pawn, $"the player gave this pawn a different order (now on {DescribeJob(current)} from {DescribeJobSource(current)})");
                lastReportedForeignJob.Remove(pawn);
                return;
            }

            // A need pause, a workstation retry and an area retry all mean
            // vanilla is meant to be driving for now - not a takeover, and
            // already covered above if it turns out to be one. Scanner
            // orders are left to TryScannerWatchdog: a scanner job's
            // 1500-tick expiry swaps the Job object for an identical one
            // without the pawn moving, and the reference comparison below
            // would misread that as vanilla having taken over.
            if (pausedForNeed.ContainsKey(pawn)
                || workstationRetryAt.ContainsKey(pawn)
                || areaRetryAt.ContainsKey(pawn)
                || ScannerCompat.IsScannerWork(order.WorkGiverDef))
            {
                return;
            }

            if (current != null && ReferenceEquals(current, mine))
            {
                lastReportedForeignJob.Remove(pawn);
                return;
            }

            // Vanilla driving the pawn on its own without an order behind
            // it - see the cases named above. Reported, not ended, same as
            // before this fix. One line per distinct foreign job, not one
            // per 60-tick poll: the same job running on for minutes writes
            // once.
            lastReportedForeignJob.TryGetValue(pawn, out Job lastReported);
            if (current != null && ReferenceEquals(current, lastReported))
            {
                return;
            }

            lastReportedForeignJob[pawn] = current;
            string now = current == null
                ? "has no job at all"
                : $"is on {DescribeJob(current)} from {DescribeJobSource(current)}";
            Logger.Message($"{pawn.LabelShort}: we still hold the {order.WorkGiverDef.defName} order"
                + (order.WorkstationTarget != null ? $" at {order.WorkstationTarget.LabelShort}" : "")
                + $", but the pawn {now}"
                + (mine == null ? " - we have given it no job yet" : $" - the last job we gave was {DescribeJob(mine)}"));
        }

        // A job end was thrown away because this pawn's own assignment
        // caused it. Added 2026-09-18 - architecture section 15. Narrowed
        // 2026-09-19 - architecture section 14 - when AssigningJob became
        // per-pawn instead of one flag for the whole game.
        //
        // Before the narrowing, JobTrackerPatch called this for ANY pawn
        // whose job ended while this mod was handing ANY pawn a job, so the
        // text here used to have to say whether the job that ended belonged
        // to the pawn being assigned or to some other pawn caught in the
        // same window - that was the loss that cost a tamed kangaroo its
        // hauling order on 2026-09-19 (and Pelican's fabrication order on
        // 2026-09-17). Now JobTrackerPatch only calls this when the ended
        // job's own pawn is the one SweepManager.IsBeingAssigned says is
        // being handed a job right now, so it is always this pawn's own
        // assignment ending its own prior job - the legitimate case GiveJob,
        // EndSweepAndJob and PauseForNeed all rely on. A job ending for an
        // unrelated pawn no longer reaches here at all; it goes to
        // Notify_JobEnded like any other ending.
        //
        // Volume: nothing at all unless the pawn whose job ended is one we
        // hold, and then at most one line per pawn per DiscardedEndQuietTicks
        // - the ones in between are counted and the count rides on the next
        // line, the same way the area wait line collapses its repeats. This
        // should now be close to the number of assignments made, not the
        // number of pawns handed a job anywhere on the map at the same time.
        public void Notify_JobEndDiscarded(Pawn pawn, Job endedJob, JobCondition condition)
        {
            if (!activeSweeps.TryGetValue(pawn, out SweepOrder order))
            {
                return;
            }

            int now = Find.TickManager.TicksGame;
            discardedEnds.TryGetValue(pawn, out DiscardedEndInfo info);
            if (now < info.nextLogTick)
            {
                info.suppressed++;
                discardedEnds[pawn] = info;
                return;
            }

            string alsoLost = info.suppressed == 0
                ? ""
                : $" (and {info.suppressed} more job end{(info.suppressed == 1 ? "" : "s")} of this pawn's thrown away since the last such line)";

            Logger.Message($"{pawn.LabelShort}: job end {condition} on {DescribeJob(endedJob)} was THROWN AWAY"
                + " - we were handing this pawn a job at the time"
                + $"; {pawn.LabelShort} is still registered on the {order.WorkGiverDef.defName} order"
                + (order.WorkstationTarget != null ? $" at {order.WorkstationTarget.LabelShort}" : "")
                + alsoLost);

            info.suppressed = 0;
            info.nextLogTick = now + DiscardedEndQuietTicks;
            discardedEnds[pawn] = info;
        }

        // The scanner equivalent of TryWorkstationRetry, and polled for the
        // same reason: there is no event to hang it on.
        // JobDriver_OperateScanner's work toil is ToilCompleteMode.Never, so
        // a scanner job does not end when the scan finds something - it does
        // not end at all - and the completion condition has to be watched
        // for rather than waited on. ScannerCompat carries the detail.
        //
        // Three endings, in the order worth checking:
        //   a find     - the ending the order was given for
        //   unusable   - power cut, roofed over, forbidden mid-scan
        //   pawn gone  - they are doing something else now
        private void TryScannerWatchdog(Pawn pawn)
        {
            if (!activeSweeps.TryGetValue(pawn, out SweepOrder order)
                || !ScannerCompat.IsScannerWork(order.WorkGiverDef))
            {
                return;
            }

            // paused pawns are off eating or sleeping and are not expected
            // to be at the station - Notify_JobEnded owns the resume, same
            // as every other order type
            if (pausedForNeed.ContainsKey(pawn))
            {
                return;
            }

            CompScanner comp = ScannerCompat.ScannerOn(order.WorkstationTarget);
            if (comp == null || order.WorkstationTarget.Destroyed)
            {
                RemoveSweep(pawn, order.WorkstationTarget.Destroyed
                    ? "the scanner was destroyed"
                    : "the scanner is no longer a scanner");
                return;
            }

            if (ScannerCompat.FoundSomething(comp, ref order.MaxScanDays))
            {
                EndSweepAndJob(pawn, $"{order.WorkstationTarget.LabelShort} found something");
                return;
            }

            if (!ScannerCompat.CanUseNow(comp))
            {
                // the job carries CanUseNow as its own fail condition, so it
                // is already dying - just stop owning the pawn
                RemoveSweep(pawn, $"{order.WorkstationTarget.LabelShort} can't be used now");
                return;
            }

            // Still on it? The 1500-tick expiry override swaps the job
            // object for an identical one without moving the pawn an inch,
            // and that is what Notify_JobEnded deliberately declines to
            // judge. This is the check it defers to.
            Job current = pawn.jobs?.curJob;
            if (current != null
                && current.def == JobDefOf.OperateScanner
                && current.targetA.Thing == order.WorkstationTarget)
            {
                return;
            }

            RemoveSweep(pawn, $"no longer working {order.WorkstationTarget.LabelShort}");
        }

        // Drop the order AND stop the pawn. RemoveSweep on its own only
        // forgets the order - it never touches the job - which is right
        // everywhere else, because everywhere else the job has already
        // ended. A scanner job never ends on its own, so a completed scanner
        // order that only called RemoveSweep would leave the pawn scanning a
        // finished order forever.
        //
        // Order matters since 2026-09-13: the next queued order is started
        // only AFTER the scanner job is ended. Starting it first, as a plain
        // RemoveSweep now would, hands the pawn the new order's first job and
        // then ends that job instead of the scan. Architecture section 12.
        private void EndSweepAndJob(Pawn pawn, string reason)
        {
            EndOrder(pawn, reason);

            if (pawn.jobs?.curJob != null)
            {
                // same self-caused-job-end suppression as GiveJob and
                // PauseForNeed; without it JobTrackerPatch's postfix re-enters
                // on our own EndCurrentJob call
                BeginAssigningJob(pawn);
                try
                {
                    pawn.jobs.EndCurrentJob(JobCondition.Succeeded);
                }
                finally
                {
                    EndAssigningJob(pawn);
                }
            }

            StartNextQueued(pawn);
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

        // The area-order twin of TryWorkstationRetry. Polled for the same
        // reason - the thing being waited on (a teammate freeing a storage
        // cell) fires no event we can hang off.
        private void TryAreaRetry(Pawn pawn)
        {
            if (!areaRetryAt.TryGetValue(pawn, out int dueAt))
            {
                return;
            }

            // a need outranks a retry, exactly as it does for a workstation:
            // holding both at once deadlocks the pawn, because
            // Notify_JobEnded returns early while a retry is pending and the
            // pause would never clear
            if (pausedForNeed.ContainsKey(pawn))
            {
                areaRetryAt.Remove(pawn);
                return;
            }

            if (Find.TickManager.TicksGame < dueAt || !activeSweeps.TryGetValue(pawn, out SweepOrder order))
            {
                return;
            }

            areaRetryAt.Remove(pawn);

            // The resume is named on whichever assignment line comes out of
            // this call, rather than getting a line of its own: an area wait
            // resumes up to MaxAreaRetries times per pawn per order, and a
            // line each is exactly the volume the wait line itself was cut
            // down from on 2026-09-13. Architecture section 15.
            AssignNextTask(pawn, order, "an area wait");
        }

        // Fallback resume for a pawn Notify_JobEnded's queue gate left
        // alone. Added 2026-09-28, section 22 ("bUILD IT"). That gate now
        // advances a non-sweep Succeeded job's ending immediately whenever
        // the pawn's own job queue is already empty - so most of the time
        // (a firefight, a social fight, any other job with nothing queued
        // behind it) the sweep is taken back the moment it ends, same as
        // before this fix existed. This only has work to do for the case
        // the gate leaves alone: a non-Succeeded ending, or one with the
        // queue still non-empty at the time - the sweep is not resumed
        // there and then, and needs this poll to notice once the pawn
        // finally goes idle with nothing left queued. Polled here for the
        // same reason TryWorkstationRetry and TryAreaRetry are: there is no
        // job-end event to hang a resume on for a pawn that is simply idle.
        //
        // Deliberately narrow. Idle means no job at all, or one of the same
        // IdleJobDefs TryForceRestIfStuck/TryForceEatIfStuck already treat
        // as idle, and never player-forced - a job the player gave by hand
        // is never overridden here, matching section 16. Only acts once the
        // pawn's own job QUEUE is empty too: a non-empty queue is already
        // being served by ThinkNode_QueuedJob ahead of any ordinary work
        // (the same fact section 22's Drop Everything fix relies on), so
        // touching the pawn before it drains would race that queue instead
        // of waiting for it. Left alone by design: a paused pawn
        // (TryResumeFromNeed owns it), a pawn already waiting out a
        // workstation or area retry (each re-arms itself on its own clock
        // if still inert), and scanner orders (the long-running scan job IS
        // the sweep; a departure from it is TryScannerWatchdog's to catch,
        // same exclusion TryReportForeignJob already makes).
        //
        // No extra throttle needed: AssignNextTask's own outcome - a job
        // issued, a retry armed, or the order ended - changes the pawn's
        // state on every call, so the next 60-tick poll either sees a pawn
        // that is no longer idle or is caught by one of the exclusions
        // above instead of repeating.
        private void TryResumeIdle(Pawn pawn, SweepOrder order)
        {
            if (pausedForNeed.ContainsKey(pawn) || workstationRetryAt.ContainsKey(pawn) || areaRetryAt.ContainsKey(pawn)
                || ScannerCompat.IsScannerWork(order.WorkGiverDef))
            {
                return;
            }

            Job curJob = pawn.jobs?.curJob;
            bool idle = curJob == null || (IdleJobDefs.Contains(curJob.def) && !curJob.playerForced);
            if (!idle || (pawn.jobs?.jobQueue != null && pawn.jobs.jobQueue.Count > 0))
            {
                return;
            }

            AssignNextTask(pawn, order, "its own job ending");
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

        // EVERY ending goes through here and EVERY ending says why.
        //
        // Seven of the seventeen call sites used to pass through silently -
        // and they were, precisely, the unexpected ones. The expected
        // endings ("no bills left", "pool still inert", "nothing left within
        // radius") all logged; a pawn yanked out by an InterruptForced, or
        // failing CanSweep on the next assignment, left no trace at all.
        // That is backwards, and it cost a session on 2026-09-05: three
        // butchers stopped with animals still queued and the log had nothing
        // to say about any of them.
        //
        // The reason is mandatory rather than optional so the compiler finds
        // a missed site instead of a player finding it. One line per sweep
        // ENDING - not per target, per def or per click - so this does not
        // reopen the message-cap problem that has cost five sessions.
        //
        // Since 2026-09-13 this is the ending that MOVES ON: the order is
        // finished for this pawn, so its next queued order starts. The
        // ending that drops the whole queue is ClearSweeps. Architecture
        // section 12 lists which call site is which.
        public void RemoveSweep(Pawn pawn, string reason)
        {
            EndOrder(pawn, reason);
            StartNextQueued(pawn);
        }

        // The pawn is out of action or something took it - dead, downed,
        // broken, drafted, off the map, or a job ended by an interrupt. The
        // active order ends and every queued order is dropped with it, in
        // the one ending line. Added 2026-09-13, architecture section 12,
        // decision 4.
        public void ClearSweeps(Pawn pawn, string reason)
        {
            int dropped = queuedOrders.TryGetValue(pawn, out List<SweepOrder> queue) ? queue.Count : 0;
            queuedOrders.Remove(pawn);

            EndOrder(pawn, dropped == 0
                ? reason
                : $"{reason}, {dropped} queued order{(dropped == 1 ? "" : "s")} dropped");
        }

        // What RemoveSweep did in full before 2026-09-13: forget the active
        // order and every piece of per-pawn state that goes with it. Touches
        // neither the job nor the queue.
        private void EndOrder(Pawn pawn, string reason)
        {
            // only speak if there was actually a sweep to end; RemoveSweep is
            // called defensively in places where there may be nothing to do
            if (activeSweeps.TryGetValue(pawn, out SweepOrder ending))
            {
                // What the pawn is doing at the moment we let go of it, but
                // only when it is not the job we gave it. That is the whole
                // disagreement stated in one place: we are releasing a pawn
                // that vanilla has already been driving. Costs no extra line
                // and says nothing at all in the ordinary case. Added
                // 2026-09-18 - architecture section 15.
                Job current = pawn.jobs?.curJob;
                ourJob.TryGetValue(pawn, out Job mine);
                string doingNow = ReferenceEquals(current, mine)
                    ? ""
                    : $", the pawn is now on {DescribeJob(current)}";

                Logger.Message($"{pawn.LabelShort}: sweep ended - {reason} ({ending.WorkGiverDef.defName}){doingNow}");

                if (orderIds.TryGetValue(pawn, out string endedId))
                {
                    string how = orderStarted.Contains(pawn) ? reason : "failed to start: " + reason;
                    Logger.Order($"ORDER {endedId} ended ({how}): {pawn.LabelShort} sweep {ending.WorkGiverDef.defName} {DescribeWhere(ending)}");
                    orderIds.Remove(pawn);
                    orderStarted.Remove(pawn);
                }
            }

            activeSweeps.Remove(pawn);
            pausedForNeed.Remove(pawn);
            consecutiveFailures.Remove(pawn);
            lastAssignedTarget.Remove(pawn);
            workstationRetryAt.Remove(pawn);
            areaRetryAt.Remove(pawn);
            areaRetries.Remove(pawn);
            ourJob.Remove(pawn);
            lastReportedForeignJob.Remove(pawn);
            discardedEnds.Remove(pawn);
            retargets.Remove(pawn);
            nonSweepJobEnds.Remove(pawn);
            passedOverLogged.Remove(pawn);
            cargoUnloadAt.Remove(pawn);
            overloadedOwnItemsLogged.Remove(pawn);
        }

        // Put a pawn that can do a new order onto it. Added 2026-09-13,
        // architecture section 12. Given a shift/replace choice 2026-09-20,
        // architecture section 16 ("Queueing and replacing a forced order").
        //
        // Returns true when the pawn has no active order - the CALLER starts
        // it, through the code each entry point already had, so a fresh
        // start is unchanged. It also returns true when a non-shift click
        // just REPLACED the pawn's active order: ClearSweeps has already
        // ended that order and dropped the pawn's queue below, so the
        // caller's fresh-start code is exactly what should run for the new
        // order too. Otherwise returns false after either appending the
        // order to the pawn's queue or, when the pawn already has the same
        // order active or queued, doing nothing. `ahead` is how many orders
        // stand in front of it: -1 for a duplicate, 0 for a start or a
        // replace.
        //
        // queueOrder is the shift state captured at the click, not read
        // here - by the time this runs the click's GUI event may be long
        // gone. See BeginSweep.
        //
        // appendBehindCurrentJob answers a question this method used to
        // ignore: a pawn with no active DNBL order still has whatever
        // vanilla job it was already doing, and until 2026-09-21 that job
        // was force-interrupted even when the player held shift, because
        // this method returned "start now" without ever looking at
        // queueOrder. With the whole colony selected, most pawns are on an
        // ordinary job, so shift-click behaved exactly like a plain click.
        // The order still starts now - there is nothing of ours to queue it
        // behind - but when true, the caller must hand the FIRST job to
        // GiveJob with appendBehindCurrentJob so it queues behind the
        // pawn's current job (TryTakeOrderedJob's requestQueueing) instead
        // of interrupting it.
        private bool JoinOrQueue(Pawn pawn, SweepOrder order, bool queueOrder, out int ahead, out bool appendBehindCurrentJob)
        {
            ahead = 0;
            appendBehindCurrentJob = false;
            if (!activeSweeps.TryGetValue(pawn, out SweepOrder current))
            {
                appendBehindCurrentJob = queueOrder;
                return true;
            }

            if (SameOrder(current, order))
            {
                // A repeat of the pawn's own active order never restarts
                // it - merge by ignoring the repeat (section 12, decision
                // 5). Whether shift was held makes no difference: nothing
                // is being replaced, so a non-shift click has nothing to
                // destroy.
                ahead = -1;
                return false;
            }

            if (!queueOrder)
            {
                // Settled 2026-09-20, section 16: a click without shift
                // ends this pawn's active order and destroys whatever it
                // had queued behind it, and the new order takes over now.
                // ClearSweeps touches only this pawn's own entries - the
                // old order's shared pool and every other pawn still on it
                // are untouched, so a group order loses only the one pawn
                // being redirected.
                ClearSweeps(pawn, $"replaced by a new {order.WorkGiverDef.defName} order");
                return true;
            }

            // Shift held - stack behind whatever the pawn is already
            // running or already has queued, a forced order and an
            // ordinary order alike (section 16, decisions 2 and 3).
            queuedOrders.TryGetValue(pawn, out List<SweepOrder> queue);
            if (queue != null)
            {
                foreach (SweepOrder queued in queue)
                {
                    if (SameOrder(queued, order))
                    {
                        ahead = -1;
                        return false;
                    }
                }
            }
            else
            {
                queue = new List<SweepOrder>();
                queuedOrders[pawn] = queue;
            }

            ahead = 1 + queue.Count;
            queue.Add(order);
            return false;
        }

        // A repeat click is the same order when it is the same WorkGiverDef
        // and either the same station or vehicle, or - for an area order -
        // the same player order with a centre within DuplicateCentreTiles.
        // Section 12, decision 5.
        private static bool SameOrder(SweepOrder a, SweepOrder b)
        {
            if (a.WorkGiverDef != b.WorkGiverDef)
            {
                return false;
            }

            if (a.WorkstationTarget != null || b.WorkstationTarget != null)
            {
                return a.WorkstationTarget == b.WorkstationTarget;
            }

            return a.OrderKind == b.OrderKind
                && (a.ScanCenter - b.ScanCenter).LengthHorizontalSquared <= DuplicateCentreTiles * DuplicateCentreTiles;
        }

        // Start the order at the front of this pawn's queue, if there is one.
        // Only ever called once the active order has gone.
        //
        // The start does what a fresh start does - clear leftover retry
        // state, and for a scanner re-read the station now - and then
        // applies the usual break rules before any work: a pawn already
        // under a need threshold joins paused, on its current job, exactly as
        // BeginAreaSweep does. Otherwise AssignNextTask, which is the
        // re-validation of a pool that may be long stale: it drops gone
        // targets, asks this pawn about the rest, rescans for this pawn when
        // nothing is left, and ends the order through its normal ending if
        // there is still nothing - which calls back in here for the order
        // after. Each step takes one order off the queue, so the depth is
        // bounded by the queue's length.
        private void StartNextQueued(Pawn pawn)
        {
            if (activeSweeps.ContainsKey(pawn) || !queuedOrders.TryGetValue(pawn, out List<SweepOrder> queue))
            {
                return;
            }

            SweepOrder next = queue[0];
            queue.RemoveAt(0);
            if (queue.Count == 0)
            {
                queuedOrders.Remove(pawn);
            }

            // one line per pawn per move - never per target or per tick
            Logger.Message($"{pawn.LabelShort}: starting queued order {next.WorkGiverDef.defName} at {DescribeWhere(next)}, {queue.Count} still queued");

            activeSweeps[pawn] = next;
            NoteOrderIssued(pawn, next);
            workstationRetryAt.Remove(pawn);
            areaRetryAt.Remove(pawn);
            areaRetries.Remove(pawn);
            consecutiveFailures.Remove(pawn);
            pausedForNeed.Remove(pawn);

            // the same seeding BeginWorkstationSweep does, but read now: the
            // station may have been worked since this order was queued
            CompScanner scannerComp = ScannerCompat.ScannerOn(next.WorkstationTarget);
            if (scannerComp != null)
            {
                next.MaxScanDays = 0f;
                ScannerCompat.FoundSomething(scannerComp, ref next.MaxScanDays);
            }

            string need = NeedMonitor.CriticalNeedLabelFor(pawn);
            if (need != null)
            {
                PauseForNeed(pawn, need, false);
                Logger.Message($"{pawn.LabelShort} joined the sweep paused: {need} ({next.WorkGiverDef.defName}) - left on its current job");
                return;
            }

            AssignNextTask(pawn, next);
        }

        private static string DescribeWhere(SweepOrder order)
        {
            return order.WorkstationTarget != null
                ? order.WorkstationTarget.LabelShort
                : order.ScanCenter.ToString();
        }

        // ", queued for 10 of 12 pawns behind 1-3 orders, 1 already had it",
        // or "" when nobody queued and nobody had it already. Folded into
        // each entry point's one summary line - section 12, Logging.
        private static string QueueSummary(int queued, int of, int minAhead, int maxAhead, int duplicates)
        {
            string text = "";
            if (queued > 0)
            {
                string behind = minAhead == maxAhead ? minAhead.ToString() : $"{minAhead}-{maxAhead}";
                text += $", queued for {queued} of {of} pawns behind {behind} order{(maxAhead == 1 ? "" : "s")}";
            }
            if (duplicates > 0)
            {
                text += $", {duplicates} already had it";
            }
            return text;
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
        // endCurrentJob distinguishes the two callers, and getting it wrong
        // froze pawns on 2026-09-07.
        //
        // NeedMonitor calls this MID-SWEEP: the pawn is running a job WE gave
        // it, and that job has to be ended or the pawn keeps hauling while
        // starving. True.
        //
        // BeginAreaSweep calls it at RECRUITMENT: the pawn has not been given
        // a sweep job at all, and pawn.jobs.curJob is whatever vanilla had it
        // doing - very possibly walking to a meal, which is exactly the thing
        // that would clear the pause. Ending it there cancels the cure. Both
        // m Bacon and Ildiko accepted a * order and then stood still, because
        // the order cancelled the job they were already on and the pause then
        // waited for a need that nothing was left to satisfy. False.
        public void PauseForNeed(Pawn pawn, string need, bool endCurrentJob = true)
        {
            if (!activeSweeps.ContainsKey(pawn))
            {
                return;
            }

            int startTick = Find.TickManager.TicksGame;
            pausedForNeed[pawn] = new PauseInfo
            {
                tick = startTick,
                need = need,
                lastForceCheckTick = startTick
            };

            // Change D, 2026-10-02: the need pause as an ORDER event on the
            // pawn's sweep order id, so an order that joined paused does not
            // read as lost. endCurrentJob false is the join-paused path; true
            // is NeedMonitor pausing a pawn mid-sweep. Logging only.
            Logger.Order($"ORDER {OrderIdFor(pawn)} paused: {pawn.LabelShort} ({need}, {(endCurrentJob ? "paused mid-sweep" : "joined paused")})");

            if (endCurrentJob && pawn.jobs?.curJob != null)
            {
                BeginAssigningJob(pawn);
                try
                {
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                finally
                {
                    EndAssigningJob(pawn);
                }

                // 2026-10-04: ourJob still held the sweep job just ended. The
                // job pool reuses Job objects, so a later vanilla Ingest could
                // be the same instance and Notify_JobEnded logged it as "forced
                // meal ended" although we never forced it (Van, 23:29:45).
                // TryForceEatNow below sets it again for a meal we do force.
                ourJob.Remove(pawn);
            }

            // Section 21 go-eat backstop, revised same day it was ordered:
            // "If a pawn pauses for food and doesn't eat, that is a
            // different problem. it should be sent for food as soon as it
            // pauses." No more waiting on NeedMonitor's poll - act here,
            // synchronously, the instant the pause is recorded.
            if (need != null && need.StartsWith("Food"))
            {
                TryForceEatNow(pawn);
            }
        }

        // Section 21. Skips a pawn already on an Ingest job - EndCurrentJob
        // just above can itself hand the pawn a new job synchronously
        // (vanilla's think tree runs before EndCurrentJob returns), and if
        // that job is already Ingest, vanilla is already feeding this pawn
        // and there is nothing to override. Otherwise picks the
        // highest-mood food this pawn may eat (NeedMonitor.TryFindBestFoodJob)
        // and forces it - "best mood buff to lowest", the verbatim ask.
        // isRetry only changes the log wording - added 2026-09-28 for
        // TryForceEatIfStuck below, which calls back in here. Its own
        // gating already excludes an Ingest curJob before calling, so the
        // skip below only ever fires for the first, non-retry call from
        // PauseForNeed and so only ever needs to log once.
        //
        // Review 2026-09-28: Becky's food pause left no trail at all -
        // TryForceEatNow deferred to a vanilla Ingest job that later ended
        // without clearing Food, and this skip logged nothing, so the log
        // could not even show that a decision was made here. One line
        // fixes it.
        private void TryForceEatNow(Pawn pawn, bool isRetry = false)
        {
            Job curJob = pawn.jobs?.curJob;
            if (curJob != null && curJob.def == JobDefOf.Ingest)
            {
                if (!isRetry)
                {
                    Logger.Message($"{pawn.LabelShort}: paused for Food but already on {DescribeJob(curJob)} - leaving vanilla to feed it");
                }
                return;
            }

            string tag = isRetry ? "retry: " : "";
            Job job = NeedMonitor.TryFindBestFoodJob(pawn, out int candidateCount, out float moodEffect, out string declineReason);
            if (job == null)
            {
                LogFoodDecline(pawn, tag, declineReason, candidateCount);
                return;
            }

            // Recorded before GiveJob, same order every other call site
            // uses. Review finding 2026-09-27: GiveJob's TryTakeOrderedJob
            // always sets Job.playerForced, so without this,
            // TryReportForeignJob's periodic poll (its player-order check
            // runs BEFORE the pausedForNeed skip) reads a forced meal it
            // does not recognise as "ours" and ends the whole sweep with
            // "the player gave this pawn a different order."
            ourJob[pawn] = job;
            GiveJob(pawn, job, false, "forced meal");
            Thing food = job.targetA.Thing;
            Logger.Message($"{pawn.LabelShort}: {tag}forced to eat {food?.LabelShort ?? job.def.defName} (mood effect {moodEffect:F0}, {candidateCount} candidate{(candidateCount == 1 ? "" : "s")} considered) - sweep stays paused until Food is satisfied");

            // A forced meal succeeded - clear the decline suppression below
            // so a fresh decline later in the same pause (if the pawn gets
            // stuck again) is not silently swallowed as "already said."
            if (pausedForNeed.TryGetValue(pawn, out PauseInfo resumed))
            {
                resumed.foodDeclineLogged = false;
                resumed.foodDeclineReason = null;
                pausedForNeed[pawn] = resumed;
            }
        }

        // Review 2026-09-28: a pawn with no eligible food logged this
        // warning every ForceEatRetryTicks for as long as the pause
        // lasted. One line per pause per distinct reason now - same shape
        // as TryForceRestIfStuck's declineLogged, but keyed on the reason
        // text too, so a change in WHY (say, the one edible meal got
        // hauled away) still gets its own line instead of being swallowed
        // by the first decline.
        private void LogFoodDecline(Pawn pawn, string tag, string declineReason, int candidateCount)
        {
            if (pausedForNeed.TryGetValue(pawn, out PauseInfo paused))
            {
                if (paused.foodDeclineLogged && paused.foodDeclineReason == declineReason)
                {
                    return;
                }

                paused.foodDeclineLogged = true;
                paused.foodDeclineReason = declineReason;
                pausedForNeed[pawn] = paused;
            }

            Logger.Warning($"{pawn.LabelShort}: {tag}paused for Food but no food could be forced - {declineReason} ({candidateCount} candidates considered)");
        }

        // Section 21, revised 2026-09-28, verbatim "3,4 yes" (Lerb, forced
        // to eat at 21:34:15, went idle when the meal ended, vanilla chose
        // LayDown over another meal at 21:36:17, and Food sat at 0% until
        // this diagnostic fired at 2500 ticks - the one-shot rule below is
        // superseded): TryForceEatIfStuck now retries instead of only
        // diagnosing. This still fires as a last-resort line covering
        // whatever TryForceEatIfStuck's own retries did not catch.
        //
        // Original comment, now historical: "not another forced job every
        // check, which is the retry loop the ask explicitly ruled out."
        // That rule was for TryForceEatNow's own one-shot trigger; the
        // 2026-09-28 order deliberately adds a throttled retry alongside it.
        private const int FoodStuckWarnTicks = 2500; // one in-game hour - same round number as ForceRestRetryTicks

        public void WarnIfFoodStuck(Pawn pawn)
        {
            if (!pausedForNeed.TryGetValue(pawn, out PauseInfo paused)
                || !paused.need.StartsWith("Food")
                || paused.foodStuckWarned)
            {
                return;
            }

            if (Find.TickManager.TicksGame - paused.tick < FoodStuckWarnTicks)
            {
                return;
            }

            Job curJob = pawn.jobs?.curJob;
            if (curJob != null && curJob.def == JobDefOf.Ingest)
            {
                return;
            }

            paused.foodStuckWarned = true;
            pausedForNeed[pawn] = paused;

            // Current levels, not the need stored when the pause began
            // (2026-10-02): "Food 10%" in this line was the level at the
            // start of the pause and read as the current one.
            string held = NeedMonitor.UnsatisfiedNeedsText(pawn);
            if (NeedMonitor.FoodSatisfied(pawn))
            {
                // 2026-10-04: Abi was already in bed (LayDown from
                // JobGiver_GetRest) when this warned that Rest held the
                // pause - misleading, nothing was wrong. When the current
                // job already serves the need holding the pause (LayDown
                // for Rest; a job with joyKind set for Recreation), say so
                // in a plain line instead. Still logged once per pause
                // (foodStuckWarned is set above). Nothing is forced here;
                // TryForceRestIfStuck only acts on an idle pawn, so a
                // LayDown in progress is never replaced.
                bool restHeld = !NeedMonitor.RestSatisfied(pawn);
                bool joyHeld = !NeedMonitor.JoySatisfied(pawn);
                bool resting = curJob != null && curJob.def == JobDefOf.LayDown;
                bool playing = curJob != null && curJob.def.joyKind != null;
                // 2026-10-04, "Not a nap, but the work yes": a pawn napping
                // is never interrupted for Recreation (TryForceJoyIfStuck
                // logs "not interrupting a nap"), so a LayDown while
                // Recreation holds the pause, alone or with Rest, is
                // acceptable too and must not warn.
                bool napAccepted = resting && joyHeld;
                if ((restHeld && resting && (!joyHeld || playing)) || (!restHeld && joyHeld && playing) || napAccepted)
                {
                    Logger.Message($"{pawn.LabelShort}: {FoodStuckWarnTicks} ticks into a Food pause, Food is now satisfied and the pause is held by: {held}; the pawn is already on {curJob.def.defName}, which serves it ({NeedMonitor.LevelsText(pawn)})");
                    return;
                }

                Logger.Warning($"{pawn.LabelShort}: paused {FoodStuckWarnTicks} ticks after a Food pause began, but Food is now satisfied ({NeedMonitor.LevelsText(pawn)}) - the pause is held by: {held}");
                ScreenNote(pawn, $"{pawn.LabelShort}: paused a long time - Food is fine, still held by: {held}");
            }
            else
            {
                Logger.Warning($"{pawn.LabelShort}: still paused and not eating {FoodStuckWarnTicks} ticks after the pause began - the forced Ingest job did not take, or something replaced it. Now: {NeedMonitor.LevelsText(pawn)}; still under threshold: {held}");
                ScreenNote(pawn, $"{pawn.LabelShort}: paused a long time and not eating - still under: {held}");
            }
        }

        // Section 21, added 2026-09-28 per user order, verbatim "3,4 yes":
        // "When a pawn paused for Food is idle (or on a non-player-forced
        // job that is not Ingest - use judgement, mirror
        // TryForceRestIfStuck's gating) and not eating, re-issue
        // TryForceEatNow, throttled to at most one attempt per a few
        // hundred ticks per pawn - same shape as TryForceRestIfStuck and
        // its ForceRestRetryTicks." Reason: Lerb's forced meal ended and
        // vanilla put him on LayDown (JobGiver_GetRest) rather than another
        // meal - not player-forced, not Ingest.
        //
        // Narrowed in review 2026-09-28, the same day: the first cut
        // treated ANY non-player-forced, non-Ingest job as eligible, which
        // also overrode firefighting, doctoring, rescuing, fleeing and a
        // lord's own duty jobs. Mirrors TryForceRestIfStuck properly now:
        // eligible only when the pawn is genuinely idle (no job, or one of
        // IdleJobDefs and not playerForced) or on a non-player-forced
        // JobDefOf.LayDown - the exact Lerb case, and the only non-idle
        // job worth overriding here. Never overrides a job the player gave
        // by hand, and never acts on a pawn that cannot meaningfully be
        // fed right now: downed, drafted, mid mental break, a wild man, or
        // roped - same exclusions TryForceRestIfStuck uses, verified
        // against Assembly-CSharp.dll's JobGiver_GetRest.TryGiveJob. Food's
        // own equivalent of that method's AllowRestingInBed check is
        // LordToil.AllowSatisfyLongNeeds (also read by GetPriority there),
        // so a lord whose toil disallows it is excluded too.
        //
        // Reuses PauseInfo.lastForceCheckTick for the throttle - a pause is
        // Food or Rest, never both, so the field is never shared between
        // two live timers. Called from NeedMonitor.GameComponentTick
        // alongside TryForceRestIfStuck and WarnIfFoodStuck.
        private const int ForceEatRetryTicks = 300; // a few in-game minutes

        public void TryForceEatIfStuck(Pawn pawn)
        {
            if (!pausedForNeed.TryGetValue(pawn, out PauseInfo paused) || !paused.need.StartsWith("Food"))
            {
                return;
            }

            Job curJob = pawn.jobs?.curJob;
            bool idle = curJob == null || (IdleJobDefs.Contains(curJob.def) && !curJob.playerForced);
            bool onNonForcedRestJob = curJob != null && !curJob.playerForced && curJob.def == JobDefOf.LayDown;
            if (!idle && !onNonForcedRestJob)
            {
                return;
            }

            // Change B: with Food already satisfied this method does nothing
            // but say so once. It must return BEFORE the throttle below,
            // which shares lastForceCheckTick with TryForceRestIfStuck's
            // longer window - advancing it here every 300 ticks would keep
            // that one from ever firing.
            if (NeedMonitor.FoodSatisfied(pawn))
            {
                if (!paused.foodSatisfiedLogged)
                {
                    paused.foodSatisfiedLogged = true;
                    pausedForNeed[pawn] = paused;
                    Logger.Message($"{pawn.LabelShort}: no forced meal - Food is no longer the problem ({NeedMonitor.LevelsText(pawn)}); the pause is held by: {NeedMonitor.UnsatisfiedNeedsText(pawn)}");
                }

                return;
            }

            int now = Find.TickManager.TicksGame;
            if (now - paused.lastForceCheckTick < ForceEatRetryTicks)
            {
                return;
            }

            paused.lastForceCheckTick = now;
            pausedForNeed[pawn] = paused;

            if (pawn.Downed || pawn.Drafted || pawn.InMentalState || pawn.IsWildMan())
            {
                LogEatRetrySkipped(pawn, pawn.Downed ? "downed" : pawn.Drafted ? "drafted" : pawn.InMentalState ? "in a mental state" : "a wild man");
                return;
            }

            if (pawn.roping != null && pawn.roping.IsRoped)
            {
                LogEatRetrySkipped(pawn, "roped");
                return;
            }

            Lord lord = pawn.GetLord();
            if (lord != null && lord.CurLordToil != null && !lord.CurLordToil.AllowSatisfyLongNeeds)
            {
                LogEatRetrySkipped(pawn, $"under a lord whose current duty ({lord.CurLordToil.GetType().Name}) does not allow satisfying long needs");
                return;
            }

            // (The current-Food check, 2026-10-02, now sits above the
            // throttle - see change B.)

            // One line per retry (already throttled to ForceEatRetryTicks).
            Logger.Message($"{pawn.LabelShort}: retrying a forced meal - {NeedMonitor.LevelsText(pawn)}");
            TryForceEatNow(pawn, isRetry: true);
        }

        // Why TryForceEatIfStuck did not retry, once per distinct reason
        // per pause - added 2026-10-02 so a silent non-retry is explainable.
        private void LogEatRetrySkipped(Pawn pawn, string reason)
        {
            if (!pausedForNeed.TryGetValue(pawn, out PauseInfo paused) || paused.earlyReturnReason == reason)
            {
                return;
            }

            paused.earlyReturnReason = reason;
            pausedForNeed[pawn] = paused;
            Logger.Message($"{pawn.LabelShort}: no forced-meal retry while paused for Food - pawn is {reason}");
            ScreenNote(pawn, $"{pawn.LabelShort}: not sent to eat - {reason}");
        }

        // Found live 2026-09-26/27: a pawn paused by PauseForNeed for Rest
        // does not always get sent to bed once we let go, and nothing here
        // was retrying - Lady stood on Wait_MaintainPosture for about 19
        // minutes (22:41:14 to 23:00:38) after a Rest pause, where two
        // earlier Rest pauses the same session had resolved themselves in
        // under 4 and about 18 minutes. Decompiled RimWorld.JobGiver_GetRest
        // (Assembly-CSharp.dll via ilspycmd): its GetPriority returns 0
        // whenever the pawn's current TimeAssignment is Work, no matter how
        // low Rest actually is - only Sleep, or Anything/Joy/Meditate below
        // their own curLevel cutoffs, ever let it fire. That check has
        // nothing to do with how depleted the need is, and it sits entirely
        // in GetPriority - TryGiveJob itself has no timetable check at all.
        // So PauseForNeed correctly hands the pawn back to vanilla
        // (verified against EndCurrentJob/TryFindAndStartJob: no leftover
        // job, queue or assignment lock), but vanilla can decline to act on
        // it for as long as the schedule says Work, and the pawn just sits
        // on whatever JobGiver_Idle keeps re-issuing.
        //
        // Called by NeedMonitor every CheckIntervalTicks for a pawn already
        // paused, instead of skipping it outright. Bypasses GetPriority's
        // schedule gate by asking RestUtility for a bed directly - the same
        // call JobGiver_GetRest itself makes once its priority clears - and
        // issuing LayDown by hand if one exists. restThreshold already
        // exists specifically because a forced job overrides the schedule
        // once; this is the same override applied a second time, on the
        // vanilla side, once ours has already let go.
        // Ground-sleeping is deliberately NOT replicated here -
        // TryFindGroundSleepSpotFor is private and this is a bed-first
        // safety net, not a full reimplementation; a pawn with no bed still
        // gets vanilla's own normal retries.
        //
        // Review finding 2026-09-27, fixed same day: the first cut acted on
        // ANY current job every 60 ticks, which would have interrupted
        // eating, firefighting, being doctored, another mod's own sleep job
        // (Use Bedrolls), or an order the player gave by hand - breaking
        // section 16 ("his own orders always interrupt") and fighting
        // vanilla's own higher-priority needs. Two gates added: only acts
        // when the pawn is genuinely idle (no job, or one of the
        // JobDefOf.Wait*/GotoWander shapes JobSourcePatch already treats as
        // idle - verified against Assembly-CSharp.dll that these are the
        // real JobDefOf names) and not player-forced; and only retries at
        // most once per ForceRestRetryTicks, so a bed search no longer runs
        // every single check while paused.
        private const int ForceRestRetryTicks = 2500; // one in-game hour

        private static readonly HashSet<JobDef> IdleJobDefs = new HashSet<JobDef>
        {
            JobDefOf.Wait,
            JobDefOf.Wait_MaintainPosture,
            JobDefOf.Wait_Wander,
            JobDefOf.GotoWander
        };

        // Change C, 2026-10-02 (Nora, 21:22: Food 97%, Rest 30%, Recreation
        // 14% - the pause held by Recreation alone and nothing sent her to
        // it). Mirrors TryForceRestIfStuck: acts only on a genuinely idle
        // pawn, only once Food and Rest are satisfied and Recreation is
        // under its resume threshold, at most once per ForceJoyRetryTicks,
        // and writes the reason once per pause whenever it declines.
        //
        // The job comes from vanilla's own selection, RimWorld.
        // JobGiver_GetJoy, called through the public TryIssueJobPackage
        // (verified by reflection: GetJoy's protected TryGiveJob has no
        // timetable check at all - the timetable gate is only in the think
        // tree's priority node, which this call bypasses). The giver's own
        // joyGiverChances map is built in ResolveReferences, so a private
        // instance is resolved once. Be Lazy's Get Rec'd solves the same
        // problem by ranking JoyGiverDefs itself; the same two facts are
        // used here without referencing that mod.
        //
        // job.ignoreJoyTimeAssignment is set because JoyUtility.
        // JoyTickCheckEnd otherwise ends the job the moment the timetable
        // says Work. GiveJob's TryTakeOrderedJob sets playerForced, which is
        // how the job beats the timetable; the cost is that vanilla's
        // JobDriver_Reading, seeing playerForced, runs a fixed 5000 ticks
        // instead of stopping when Recreation is full. Accepted: the pause
        // resumes the sweep as soon as Recreation is satisfied and that
        // order's next job replaces the reading.
        private const int ForceJoyRetryTicks = 600;
        private static JobGiver_GetJoy joyGiver;

        public bool TryForceJoyIfStuck(Pawn pawn)
        {
            if (!pausedForNeed.TryGetValue(pawn, out PauseInfo paused))
            {
                return false;
            }

            if (NeedMonitor.JoySatisfied(pawn))
            {
                return false;
            }

            // Recreation is the low one but something else comes first.
            // Logged (change D) once per distinct reason per pause; the
            // behaviour is the same as before - nothing is sent.
            if (!NeedMonitor.FoodSatisfied(pawn) || !NeedMonitor.RestSatisfied(pawn))
            {
                string firstWhy = !NeedMonitor.FoodSatisfied(pawn)
                    ? "Recreation is low but Food is not satisfied yet - feeding comes first"
                    : "Recreation is low but Rest is not satisfied yet - resting comes first";
                if (paused.joyDeclineReason != firstWhy)
                {
                    paused.joyDeclineReason = firstWhy;
                    pausedForNeed[pawn] = paused;
                    Logger.Message($"{pawn.LabelShort}: no recreation job sent - {firstWhy} ({NeedMonitor.LevelsText(pawn)})");
                    ScreenNote(pawn, $"{pawn.LabelShort}: not sent to recreation - {firstWhy}");
                }

                return false;
            }

            Job curJob = pawn.jobs?.curJob;
            bool idle = curJob == null || (IdleJobDefs.Contains(curJob.def) && !curJob.playerForced);

            // 2026-10-04 (Jazzy: Food pause, Recreation 13% held it after the
            // meal, never sent to recreation). Same rule as the rest force,
            // order: "Not a nap, but the work yes." A pawn on ordinary work
            // is pulled off it and sent to recreation. A nap (LayDown), a
            // meal, joy, a player order, emergency work or any non-work job
            // is left alone.
            bool ordinaryWork = IsOrdinaryWork(curJob);
            if (!idle && !ordinaryWork)
            {
                if (curJob != null && curJob.def != JobDefOf.Ingest && curJob.def != JobDefOf.LayDown
                    && curJob.def.joyKind == null && paused.joyBusyJob != curJob.def.defName)
                {
                    paused.joyBusyJob = curJob.def.defName;
                    pausedForNeed[pawn] = paused;
                    Logger.Message($"{pawn.LabelShort}: paused for {paused.need}, Recreation holds it but the pawn is on {DescribeJob(curJob)}"
                        + (curJob.playerForced ? " (player-forced)" : curJob.workGiverDef != null ? $" (emergency work, {curJob.workGiverDef.defName})" : " (not a work job)")
                        + " - not interrupting");
                }
                else if (curJob != null && curJob.def == JobDefOf.LayDown && paused.joyBusyJob != curJob.def.defName)
                {
                    paused.joyBusyJob = curJob.def.defName;
                    pausedForNeed[pawn] = paused;
                    Logger.Message($"{pawn.LabelShort}: paused for {paused.need}, Recreation holds it but the pawn is resting ({curJob.def.defName}) - not interrupting a nap");
                }

                return false;
            }

            // A pause that started on another need checks at once the first
            // time Recreation holds it, then every ForceJoyRetryTicks; its
            // own timer is separate because lastForceCheckTick starts at the
            // pause's start. A Recreation pause keeps the old timer.
            bool joyStart = paused.need.StartsWith("Recreation");
            int now = Find.TickManager.TicksGame;
            int lastCheck = joyStart ? paused.lastForceCheckTick : paused.joyHeldCheckTick;
            if (lastCheck != 0 && now - lastCheck < ForceJoyRetryTicks)
            {
                return false;
            }

            paused.lastForceCheckTick = now;
            paused.joyHeldCheckTick = now;
            pausedForNeed[pawn] = paused;

            string decline = null;
            if (pawn.needs?.joy == null) decline = "has no recreation need";
            else if (pawn.Downed) decline = "downed";
            else if (pawn.Drafted) decline = "drafted";
            else if (pawn.InMentalState) decline = "in a mental state";
            else if (pawn.IsWildMan()) decline = "a wild man";
            else if (pawn.roping != null && pawn.roping.IsRoped) decline = "roped";
            else
            {
                Lord lord = pawn.GetLord();
                if (lord != null && lord.CurLordToil != null && !lord.CurLordToil.AllowSatisfyLongNeeds)
                {
                    decline = $"under a lord whose current duty ({lord.CurLordToil.GetType().Name}) does not allow satisfying long needs";
                }
            }

            Job joyJob = null;
            if (decline == null)
            {
                if (joyGiver == null)
                {
                    joyGiver = new JobGiver_GetJoy();
                    joyGiver.ResolveReferences();
                }

                joyJob = joyGiver.TryIssueJobPackage(pawn, default(JobIssueParams)).Job;
                if (joyJob == null)
                {
                    decline = $"vanilla's recreation giver found no job ({NeedMonitor.LevelsText(pawn)})";
                }
            }

            if (joyJob == null)
            {
                if (paused.joyDeclineReason != decline)
                {
                    paused.joyDeclineReason = decline;
                    pausedForNeed[pawn] = paused;
                    Logger.Message($"{pawn.LabelShort}: paused and held by Recreation, but no recreation job sent - {decline}");
                    ScreenNote(pawn, $"{pawn.LabelShort}: not sent to recreation - {decline}");
                }

                return false;
            }

            joyJob.ignoreJoyTimeAssignment = true;
            string joyInterrupted = idle ? "" : $" (interrupting ordinary work: {DescribeJob(curJob)})";
            ourJob[pawn] = joyJob;
            GiveJob(pawn, joyJob, false, "forced recreation");
            Logger.Message($"{pawn.LabelShort}: paused and held by Recreation with nothing from vanilla - sent to {joyJob.def.defName} ({NeedMonitor.LevelsText(pawn)})" + joyInterrupted);
            return true;
        }

        // Change E, 2026-10-02 ("make the mod print that error to screen"):
        // the same text as the log line, short, on RimWorld's message feed
        // with the pawn as the look target so a click jumps to it. Verified
        // by reflection: Verse.Messages.Message(string, LookTargets,
        // MessageTypeDef, bool historical); a Pawn converts to LookTargets
        // (NameAndSkip in LoopGizmoPatch already passes one). Always called
        // beside a log line that is itself throttled to once per distinct
        // reason per pause, so the screen is not spammed. The log line stays.
        private static void ScreenNote(Pawn pawn, string text)
        {
            Messages.Message(text, pawn, MessageTypeDefOf.CautionInput, false);
        }

        private void LogRestSkipped(Pawn pawn, string reason, string shortReason = null)
        {
            if (!pausedForNeed.TryGetValue(pawn, out PauseInfo paused) || paused.restDeclineReason == reason)
            {
                return;
            }

            paused.restDeclineReason = reason;
            pausedForNeed[pawn] = paused;
            Logger.Message($"{pawn.LabelShort}: no forced rest while paused - pawn is {reason} ({NeedMonitor.LevelsText(pawn)})");
            ScreenNote(pawn, $"{pawn.LabelShort}: not sent to bed - {shortReason ?? reason}");
        }

        // 2026-10-04: shared by the rest and recreation forces. Ordinary
        // vanilla work = a job that is not player-forced and came from a
        // non-emergency WorkGiver (Job.workGiverDef is set by JobGiver_Work).
        // Eating, sleeping, joy, lord duties, player orders and emergency
        // work (firefighting, doctoring, rescue) are not.
        private static bool IsOrdinaryWork(Job job)
        {
            return job != null && !job.playerForced
                && job.workGiverDef != null && !job.workGiverDef.emergency;
        }

        public bool TryForceRestIfStuck(Pawn pawn)
        {
            if (!pausedForNeed.TryGetValue(pawn, out PauseInfo paused))
            {
                return false;
            }

            // Change B, 2026-10-02 (Monkey: a Food pause, Food satisfied by
            // the meal, Rest what still held the pause - and this method
            // only ever acted on a pause that STARTED on Rest). Acts now on
            // a Rest pause as before, and also on any other pause once Rest
            // is the need under its resume threshold and Food is not (a
            // hungry pawn is fed first, by the forced-meal path). When only
            // Recreation or mood holds the pause the pawn is left to
            // vanilla, with one line per pause saying so.
            bool restStart = paused.need.StartsWith("Rest");
            bool restHolds = !NeedMonitor.RestSatisfied(pawn);
            bool foodOk = NeedMonitor.FoodSatisfied(pawn);
            bool otherNeedCase = !restStart && restHolds && foodOk;
            if (!restStart && !otherNeedCase)
            {
                if (foodOk && !restHolds && NeedMonitor.JoySatisfied(pawn) && !paused.otherNeedLogged)
                {
                    string held = NeedMonitor.UnsatisfiedNeedsText(pawn);
                    if (held.Length > 0)
                    {
                        paused.otherNeedLogged = true;
                        pausedForNeed[pawn] = paused;
                        Logger.Message($"{pawn.LabelShort}: paused for {paused.need}, but Food and Rest are fine now - the pause is held only by: {held}; leaving the pawn to vanilla");
                    }
                }

                return false;
            }

            // Only act on a genuinely idle pawn - never a job the player
            // gave by hand (section 16), never anything else vanilla or
            // another mod is already running for it (eating, doctoring,
            // firefighting, Use Bedrolls' own sleep job).
            Job curJob = pawn.jobs?.curJob;
            bool idle = curJob == null || (IdleJobDefs.Contains(curJob.def) && !curJob.playerForced);

            // 2026-10-04 (Van, 23:27-23:29): once Food was satisfied the pause
            // was held by Rest alone, but vanilla's think tree hands a pawn
            // an ordinary work job (JobGiver_Work sets Job.workGiverDef -
            // verified in the decompile) in the same tick it would otherwise
            // idle, so the pawn was never sampled idle here and was never
            // sent to bed - the timetable said Work, so JobGiver_GetRest
            // declined. For the case where Rest is what holds the pause, a
            // non-forced job from an ordinary (non-emergency) WorkGiver
            // counts as interruptible too. Firefighting, doctoring and
            // rescue are emergency WorkGivers and are left alone, as is
            // anything the player ordered and anything that is not a work
            // job at all (eating, sleeping, joy, a lord's duty).
            bool ordinaryWork = IsOrdinaryWork(curJob);
            if (!idle && !ordinaryWork)
            {
                if (curJob != null && curJob.def != JobDefOf.Ingest && curJob.def != JobDefOf.LayDown
                    && curJob.def.joyKind == null && paused.restBusyJob != curJob.def.defName)
                {
                    paused.restBusyJob = curJob.def.defName;
                    pausedForNeed[pawn] = paused;
                    Logger.Message($"{pawn.LabelShort}: paused for {paused.need}, Rest holds it but the pawn is on {DescribeJob(curJob)}"
                        + (curJob.playerForced ? " (player-forced)" : curJob.workGiverDef != null ? $" (emergency work, {curJob.workGiverDef.defName})" : " (not a work job)")
                        + " - not interrupting");
                }
                return false;
            }

            int now = Find.TickManager.TicksGame;
            int lastCheck = otherNeedCase ? paused.restHeldCheckTick : paused.lastForceCheckTick;
            if (lastCheck != 0 && now - lastCheck < ForceRestRetryTicks)
            {
                return false;
            }

            paused.lastForceCheckTick = now;
            paused.restHeldCheckTick = now;
            pausedForNeed[pawn] = paused;

            // Change D, 2026-10-02: each of these returns used to be silent
            // (koala, Rest 7%, a fire sweep: attempted at 22:27:38 and left
            // no reason). Same once-per-distinct-reason-per-pause pattern as
            // LogEatRetrySkipped. Behaviour is unchanged.
            if (pawn.needs?.rest == null)
            {
                LogRestSkipped(pawn, "has no rest need");
                return false;
            }

            if (RestUtility.DisturbancePreventsLyingDown(pawn))
            {
                LogRestSkipped(pawn, "RestUtility.DisturbancePreventsLyingDown is true (something nearby - a fire or a hostile - stops it lying down)", "something nearby (a fire or a hostile) stops it lying down");
                return false;
            }

            Lord lord = pawn.GetLord();
            if (lord != null && lord.CurLordToil != null && !lord.CurLordToil.AllowRestingInBed)
            {
                LogRestSkipped(pawn, $"under a lord whose current duty ({lord.CurLordToil.GetType().Name}) does not allow resting in bed");
                return false;
            }

            if (pawn.IsWildMan() || (pawn.InMentalState && !pawn.MentalState.AllowRestingInBed))
            {
                LogRestSkipped(pawn, pawn.IsWildMan() ? "a wild man" : "in a mental state that does not allow resting in bed");
                return false;
            }

            if (pawn.roping != null && pawn.roping.IsRoped)
            {
                LogRestSkipped(pawn, "roped");
                return false;
            }

            Building_Bed bed = RestUtility.FindBedFor(pawn);
            if (bed == null)
            {
                if (!paused.declineLogged)
                {
                    paused.declineLogged = true;
                    pausedForNeed[pawn] = paused;
                    Logger.Message($"{pawn.LabelShort}: still idle and paused for {paused.need} but no bed available - leaving vanilla to keep trying");
                }
                return false;
            }

            // Recorded before GiveJob - same fault as TryForceEatNow, found
            // in review 2026-09-27: GiveJob's TryTakeOrderedJob always sets
            // Job.playerForced, so without this, TryReportForeignJob's
            // periodic poll reads the forced LayDown as a foreign order and
            // ends the sweep.
            string interrupted = idle ? "" : $" (interrupting ordinary work: {DescribeJob(curJob)})";
            Job layDown = JobMaker.MakeJob(JobDefOf.LayDown, bed);
            ourJob[pawn] = layDown;
            GiveJob(pawn, layDown, false, "forced rest");
            Logger.Message($"{pawn.LabelShort}: still paused for {paused.need} with no rest job from vanilla - forced LayDown at {bed.LabelShort}"
                + (otherNeedCase ? $" (the pause started on another need; Rest is what holds it now - {NeedMonitor.LevelsText(pawn)})" : "")
                + interrupted);
            return true;
        }

        // Entry point from FloatMenuPatch. eligible pawns only - caller has
        // already run them through PawnValidator. Workstation WorkGivers
        // (WorkGiver_DoBill) get single-pawn best-of selection per
        // architecture doc 2; everything else fans the group out across
        // targets found within sweepRadius of clickedTarget.
        //
        // queueOrder is the queue-order key (shift) state at the moment of
        // the click, read by the caller inside its own FloatMenuOption
        // action - KeyBindingDefOf.QueueOrder.IsDownEvent only answers
        // correctly inside a live GUI event, which no longer exists by the
        // time a call reaches this deep. Section 16, settled 2026-09-20.
        public void BeginSweep(List<Pawn> eligiblePawns, LocalTargetInfo clickedTarget, WorkGiverDef workGiverDef, bool queueOrder)
        {
            if (map == null || eligiblePawns == null || eligiblePawns.Count == 0)
            {
                return;
            }

            // No PUAH redirect here any more (dnbl-architecture.md section
            // 18, decision 6) - DNBL stuffs inventory itself now, for a
            // general haul order same as any other, via
            // Patches/HaulInterceptPatch.cs postfixing the vanilla
            // HaulAIUtility methods this order's own WorkGiver calls.

            if (!(workGiverDef?.Worker is WorkGiver_Scanner scanner))
            {
                return;
            }

            // A bench and a scanner are each one station worked by one pawn,
            // so they take the workstation path rather than fanning a group
            // out across a radius. What differs is only the ending: a bench
            // runs out of bills, a scanner finds something. See ScannerCompat.
            if (scanner is WorkGiver_DoBill || ScannerCompat.IsScannerWork(workGiverDef))
            {
                BeginWorkstationSweep(eligiblePawns, clickedTarget.Thing, workGiverDef, scanner, queueOrder);
            }
            else if (VehicleCompat.IsPersistentTargetWork(workGiverDef))
            {
                // Same "keep coming back to this one thing" shape, but this
                // one takes the whole selection - see the method comment.
                BeginPersistentTargetSweep(eligiblePawns, clickedTarget.Thing, workGiverDef, scanner, queueOrder);
            }
            else
            {
                // Derived here rather than passed in: the clicked Thing is
                // the only place the player's actual order is recorded, and
                // this is the last point that still has it. Null for every
                // sweep that needs no narrowing.
                BeginAreaSweep(eligiblePawns, clickedTarget.Cell, workGiverDef, scanner,
                    PlantCompat.FilterFor(clickedTarget.Thing), PlantCompat.LabelFor(clickedTarget.Thing), queueOrder);
            }
        }

        // The routing rule above, asked as a question, because FloatMenuPatch
        // needs the same answer before it offers a radius probe: a
        // persistent-target order is one Thing, so a probe that hands back a
        // cell would build a menu entry that quietly does nothing.
        public static bool UsesPersistentTarget(WorkGiverDef def)
        {
            if (def == null || !(def.Worker is WorkGiver_Scanner scanner))
            {
                return false;
            }

            return scanner is WorkGiver_DoBill
                || ScannerCompat.IsScannerWork(def)
                || VehicleCompat.IsPersistentTargetWork(def);
        }

        // Vehicle packing, and anything else built on Vehicle Framework's
        // WorkGiver_CarryToVehicle. Structurally a workstation order - one
        // target, re-asked until it stops answering - with one deliberate
        // difference: the whole selection joins it, where a bench takes the
        // single best-ranked pawn.
        //
        // That difference is safe here and is not safe for a bench. Vehicle
        // Framework reserves per *item* inside FindThingToPack, and
        // CountLeftToPack subtracts what teammates are already carrying, so
        // parallel haulers divide the cargo between them instead of racing
        // for it. A bill, by contrast, is one pawn's job by design
        // (architecture doc section 2).
        //
        // The pawns share one SweepOrder, exactly as an area sweep does. Its
        // pool stays empty and unread; per-pawn retry and failure counts live
        // in SweepManager's own dictionaries, so one pawn giving up does not
        // touch the others.
        // Stuff-first hauling and loading (dnbl-architecture.md section 18).
        // Wraps a persistent-target job in DNBL's own stuffing job
        // (Jobs/JobDriver_StuffAndHaul, fixed-destination mode) when the
        // order is vehicle-packing work, so one trip carries several items
        // instead of one - the original reported symptom. Called from both
        // the first job a pawn gets (below) and every resumed one
        // (AssignNextTask); returns the raw job untouched for a bench or
        // scanner order, and for a vehicle order whose target does not
        // expose a ThingOwner to deliver into (see the driver's own
        // comment on why that check cannot be verified against Vehicle
        // Framework's DLL directly).
        private Job WrapForVehicleStuffing(Pawn pawn, Job rawJob, WorkGiverDef workGiverDef, Thing persistentTarget)
        {
            // Kill switch, dnbl-architecture.md section 18. Cheapest check
            // first - a static bool read, before touching the job.
            if (!DoNotBeLazyMod.Settings.stuffFirstHauling)
            {
                return rawJob;
            }

            if (rawJob == null || !VehicleCompat.IsPersistentTargetWork(workGiverDef))
            {
                return rawJob;
            }

            if (!(rawJob.targetA.Thing is Thing primary) || primary == persistentTarget)
            {
                return rawJob;
            }

            if (persistentTarget.TryGetInnerInteractableThingOwner() == null)
            {
                Logger.Message($"BeginSweep {workGiverDef.defName} at {persistentTarget.LabelShort}: target exposes no ThingOwner, stuffing skipped for this trip");
                return rawJob;
            }

            // Fixed 2026-09-22 - the same shape of same-tick crash fixed in
            // HaulInterceptPatch and TransporterInterceptPatch
            // (dnbl-architecture.md section 18). Same guard, same shared
            // calculation as the driver's own PickUpToil: a pawn who can't
            // carry even one unit of the primary target keeps the vehicle's
            // own raw job instead.
            if (!JobDriver_StuffAndHaul.CanPickUpAtLeastOne(pawn, primary))
            {
                // Throttled 2026-09-24 (dnbl-architecture.md section 18) -
                // same per-evaluation spam as the other two substitution
                // points' overencumber line.
                JobDriver_StuffAndHaul.LogOverencumberSkipThrottled(pawn, primary,
                    $"BeginSweep {workGiverDef.defName} at {persistentTarget.LabelShort}: {primary.LabelCap} would overencumber {pawn.LabelShort} before even one unit, stuffing skipped for this trip");
                return rawJob;
            }

            // Fixed 2026-09-24, dnbl-architecture.md section 18 (defect 2 of
            // two, shared with HaulInterceptPatch and
            // TransporterInterceptPatch). Without this, a stuffing job built
            // for an item another pawn already holds fails its own
            // TryMakePreToilReservations the instant it starts, and the job
            // giver rebuilds this exact job every call until the item frees
            // up. Leave the vehicle's own raw job in place instead, same idea
            // as CanPickUpAtLeastOne above.
            if (!JobDriver_StuffAndHaul.CanReserveItem(pawn, primary))
            {
                JobDriver_StuffAndHaul.LogReserveSkipThrottled(pawn, primary,
                    $"BeginSweep {workGiverDef.defName} at {persistentTarget.LabelShort}: {primary.LabelCap} is already reserved, stuffing skipped for this trip");
                return rawJob;
            }

            // Independent backstop, in case some other path still produces
            // a zero-progress trip for this pawn/target pair.
            if (JobDriver_StuffAndHaul.IsZeroProgressSuppressed(pawn, primary))
            {
                return rawJob;
            }

            Job stuffJob = JobMaker.MakeJob(DnblJobDefOf.StuffAndHaul, primary, persistentTarget);
            stuffJob.count = rawJob.count > 0 ? rawJob.count : primary.stackCount;
            stuffJob.haulMode = HaulMode.ToContainer; // signal to the driver: fixed-destination mode
            stuffJob.workGiverDef = workGiverDef;
            stuffJob.playerForced = rawJob.playerForced;

            PuahCompat.WarnIfCoexisting();
            Logger.Message($"BeginSweep {workGiverDef.defName} at {persistentTarget.LabelShort}: stuffing job created for {primary.LabelCap}");

            return stuffJob;
        }

        private void BeginPersistentTargetSweep(List<Pawn> eligiblePawns, Thing target, WorkGiverDef workGiverDef, WorkGiver_Scanner scanner, bool queueOrder)
        {
            if (target == null)
            {
                return;
            }

            var order = new SweepOrder(workGiverDef, new List<LocalTargetInfo>(), target);
            int joined = 0;

            // architecture section 12 - pawns already on an order queue this
            // one instead of starting it
            int queued = 0, duplicates = 0, minAhead = int.MaxValue, maxAhead = 0;

            foreach (Pawn pawn in eligiblePawns)
            {
                // Wrapped per-pawn 2026-09-27 per user order: "Don't end all
                // pawn's activities because one pawn stopped." An exception
                // asking this pawn's own answer must not stop the rest of the
                // group from being asked - see GroupOrderPawnExceptionCaught.
                try
                {
                    // Asked one pawn at a time, and the order matters: each
                    // GiveJob below puts a job in flight that the NEXT
                    // JobOnThing call can see, through
                    // TransferableCountHauledByOthersForPacking. So a pawn who
                    // gets null here is usually being told the cargo is already
                    // spoken for, which is the right answer rather than a
                    // failure - they simply do not join.
                    //
                    // Every pawn answers for itself, so this already keeps the
                    // pawns who can and drops the ones who cannot - checked
                    // 2026-09-13 against that rule, architecture doc section 9.
                    Job job = WrapForVehicleStuffing(pawn, scanner.JobOnThing(pawn, target, true), workGiverDef, target);
                    if (job == null)
                    {
                        Logger.Message($"BeginSweep {workGiverDef.defName}: no job on {target.LabelShort} for {pawn.LabelShort}, not joining");
                        continue;
                    }

                    // A pawn already on an order is not handed this job: the
                    // order goes on its queue, and it is asked again when its
                    // turn comes. Its answer here could not see the jobs of the
                    // pawns starting in this same loop, which costs nothing for
                    // that reason. Section 12, decision 7. Unless queueOrder is
                    // false, in which case this pawn's active order is replaced
                    // instead - section 16.
                    if (!JoinOrQueue(pawn, order, queueOrder, out int ahead, out bool appendBehindCurrentJob))
                    {
                        if (ahead < 0)
                        {
                            duplicates++;
                        }
                        else
                        {
                            queued++;
                            minAhead = Math.Min(minAhead, ahead);
                            maxAhead = Math.Max(maxAhead, ahead);
                        }
                        continue;
                    }

                    // a fresh assignment always clears leftover pause and retry
                    // state from a previous order
                    pausedForNeed.Remove(pawn);
                    workstationRetryAt.Remove(pawn);
                    areaRetryAt.Remove(pawn);
                    areaRetries.Remove(pawn);
                    consecutiveFailures.Remove(pawn);
                    activeSweeps[pawn] = order;
                    NoteOrderIssued(pawn, order);
                    joined++;

                    Logger.Message($"BeginSweep {workGiverDef.defName} at {target.LabelShort}: {pawn.LabelShort} joined, first job {DescribeJob(job)}"
                        + (appendBehindCurrentJob ? $" - queued behind {pawn.LabelShort}'s current job, not interrupting it" : ""));
                    ourJob[pawn] = job;
                    GiveJob(pawn, job, appendBehindCurrentJob);
                }
                catch (Exception ex)
                {
                    GroupOrderPawnExceptionCaught(pawn, workGiverDef.defName, ex);
                }
            }

            if (joined == 0 && queued == 0 && duplicates == 0)
            {
                Logger.Message($"BeginSweep {workGiverDef.defName}: no job on {target.LabelShort} for any of {eligiblePawns.Count} pawns, no sweep started");
                return;
            }

            Logger.Message($"BeginSweep {workGiverDef.defName} at {target.LabelShort}: {joined} of {eligiblePawns.Count} pawns joined"
                + QueueSummary(queued, eligiblePawns.Count, minAhead, maxAhead, duplicates));
        }

        private void BeginWorkstationSweep(List<Pawn> eligiblePawns, Thing billGiver, WorkGiverDef workGiverDef, WorkGiver_Scanner scanner, bool queueOrder)
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
            //
            // One worker by design (architecture doc section 2), and each
            // ranked pawn is asked for itself, so one pawn's "no" never stops
            // the next being asked - checked 2026-09-13, section 9.
            List<Pawn> ranked = RankForWorkstation(eligiblePawns, workGiverDef);

            foreach (Pawn pawn in ranked)
            {
                // Wrapped per-pawn 2026-09-27 per user order: "Don't end all
                // pawn's activities because one pawn stopped." The comment
                // above already promises "one pawn's no never stops the next
                // being asked" - an unhandled exception used to break that
                // promise exactly like a bad answer would. See
                // GroupOrderPawnExceptionCaught.
                try
                {
                    Job job = scanner.JobOnThing(pawn, billGiver, true);
                    if (job == null)
                    {
                        Logger.Message($"BeginSweep {workGiverDef.defName}: no job on {billGiver.LabelShort} for {pawn.LabelShort}, trying next");
                        continue;
                    }

                    // empty pool - see SweepOrder comment. billGiver is the
                    // order: every job after this one comes from AssignNextTask
                    // re-asking this same station.
                    var order = new SweepOrder(workGiverDef, new List<LocalTargetInfo>(), billGiver);

                    // The best-ranked pawn with a job takes the order even when
                    // it is busy: the order queues behind what it is doing,
                    // rather than falling to a lower-ranked idle pawn - that
                    // would change section 2's selection rule. Section 12,
                    // decision 7. The scan counter is read when a queued order
                    // starts, not here. Unless queueOrder is false, in which
                    // case this pawn's active order is replaced instead, and
                    // JoinOrQueue returns true so the join code below runs -
                    // section 16.
                    if (!JoinOrQueue(pawn, order, queueOrder, out int ahead, out bool appendBehindCurrentJob))
                    {
                        Logger.Message(ahead < 0
                            ? $"BeginSweep {workGiverDef.defName} at {billGiver.LabelShort}: {pawn.LabelShort} of {ranked.Count} ranked already has it"
                            : $"BeginSweep {workGiverDef.defName} at {billGiver.LabelShort}: {pawn.LabelShort} of {ranked.Count} ranked, queued behind {ahead} order{(ahead == 1 ? "" : "s")}");
                        return;
                    }

                    // A fresh assignment always clears leftover pause and retry
                    // state from a previous order
                    pausedForNeed.Remove(pawn);
                    workstationRetryAt.Remove(pawn);

                    // Seed the scan high-water mark from wherever this station
                    // already stands. Starting at zero would read the first
                    // sample as a find on a scanner that has been worked before.
                    CompScanner scannerComp = ScannerCompat.ScannerOn(billGiver);
                    if (scannerComp != null)
                    {
                        order.MaxScanDays = 0f;
                        ScannerCompat.FoundSomething(scannerComp, ref order.MaxScanDays);
                    }

                    activeSweeps[pawn] = order;
                    NoteOrderIssued(pawn, order);

                    // whole workstation path used to emit nothing at all - a
                    // bill order's only trace was one "job ended" line with no
                    // context, which is why the 08-22 log couldn't say whether
                    // an order had even started. job.def matters here: DoBill
                    // means the bill itself, anything else means the WorkGiver
                    // wants a haul-off or a refuel first.
                    Logger.Message($"BeginSweep {workGiverDef.defName} at {billGiver.LabelShort}: {pawn.LabelShort} of {ranked.Count} ranked, first job {DescribeJob(job)}"
                        + (appendBehindCurrentJob ? $" - queued behind {pawn.LabelShort}'s current job, not interrupting it" : ""));
                    areaRetryAt.Remove(pawn);
                    areaRetries.Remove(pawn);
                    ourJob[pawn] = job;
                    GiveJob(pawn, job, appendBehindCurrentJob);
                    return;
                }
                catch (Exception ex)
                {
                    GroupOrderPawnExceptionCaught(pawn, workGiverDef.defName, ex);
                }
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

        private void BeginAreaSweep(List<Pawn> eligiblePawns, IntVec3 clickCell, WorkGiverDef workGiverDef, WorkGiver_Scanner scanner, Predicate<Thing> targetFilter, string orderKind, bool queueOrder)
        {
            // THE RULE, in the user's words: "The group should drop the pawns
            // who cannot but keep the pawns who can do a certain thing."
            //
            // This used to build the pool off eligiblePawns[0] alone, on the
            // claim that the scan's checks "don't vary by which pawn asked".
            // They all do - allowed area, forbidden, reservation,
            // reachability, and the WorkGiver's own HasJobOn* for that pawn.
            // On 2026-09-13 "scan HaulToInventory r=50 ... for Abi: 0
            // targets" ended the order for the whole selection because Abi
            // alone found nothing.
            //
            // Now the pool is every target ANY eligible pawn can do, and
            // `able` is the pawns who can do at least one of them. Only they
            // join; the rest are left exactly as they were - not added, and
            // not struck off any order they were already on. Architecture doc
            // section 9, which also carries why this stays cheap at radius 50.
            int radius = DoNotBeLazyMod.Settings.sweepRadius;

            // read once, here, and stamped onto the order below - see
            // SweepOrder.CenterOut
            bool centerOut = DoNotBeLazyMod.Settings.centerOutOrder;
            List<LocalTargetInfo> pool = TaskScanner.FindTargetsForGroup(clickCell, radius, map, workGiverDef, eligiblePawns, out List<Pawn> able, 0, targetFilter);
            if (pool.Count == 0)
            {
                // the clicked target had a job or the option wouldn't have
                // been offered, so an empty pool here means the radius scan
                // disagreed with the click - reservations, reachability,
                // allowed area. Silence on this used to look identical to
                // "the mod is broken"; the float menu is already gone by
                // now, so a message is the only channel left.
                Logger.Message($"BeginSweep {workGiverDef.defName}: scan found nothing any of {eligiblePawns.Count} pawns can do, no sweep started");
                string what = workGiverDef.label.NullOrEmpty() ? workGiverDef.defName : workGiverDef.label.CapitalizeFirst();
                Messages.Message(
                    "* " + what + ": nothing to do within " + radius + " tiles.",
                    new TargetInfo(clickCell, map),
                    MessageTypeDefOf.RejectInput,
                    false);
                return;
            }

            // ONE line for the whole order: how many can, and who was left
            // out. The per-pawn scan line this replaces is gone - see
            // TaskScanner.FindTargetsForGroup.
            Logger.Message($"BeginSweep {workGiverDef.defName}: {pool.Count} targets, {able.Count} of {eligiblePawns.Count} pawns can do it, order {(centerOut ? "centre-out" : "pawn-nearest")}"
                + DroppedSummary(eligiblePawns, able));

            // every area sweep rescans when a pawn runs the pool dry now, not
            // just fire
            var order = new SweepOrder(workGiverDef, pool, null, clickCell, radius, centerOut, targetFilter, orderKind);

            // was: break out of this loop the moment the pool emptied, which
            // is why "* haul until done" with 36 selected sent exactly one
            // pawn - a 1-target pool dropped the other 35 without a word.
            // AssignNextTask rescans now, so let every pawn ask; the ones
            // with genuinely nothing to do drop out there instead.

            // Counted so the player can be told once, after the loop, that
            // the order took pawns it could not start - see the message
            // below. Added 2026-09-12.
            int joinedPaused = 0;

            // architecture section 12 - pawns already on an order queue this
            // one instead of starting it
            int queued = 0, duplicates = 0, minAhead = int.MaxValue, maxAhead = 0;

            // `able`, not eligiblePawns: a pawn who can do nothing in this
            // pool is left out of the order
            foreach (Pawn pawn in able)
            {
                // Wrapped per-pawn 2026-09-27 per user order: "Don't end all
                // pawn's activities because one pawn stopped." This loop IS
                // the group order - an unhandled exception for one pawn must
                // not stop the rest of `able` from joining. See
                // GroupOrderPawnExceptionCaught.
                try
                {
                    // Was an unconditional activeSweeps[pawn] = order, which is
                    // how a rice haul 148 targets strong was thrown away by a
                    // corn haul 13 seconds later on 2026-09-13. A pawn already on
                    // an order keeps it, and this one waits behind it if the
                    // player held shift - or the order is replaced, and its
                    // queue destroyed, if not (section 16, settled 2026-09-20).
                    if (!JoinOrQueue(pawn, order, queueOrder, out int ahead, out bool appendBehindCurrentJob))
                    {
                        if (ahead < 0)
                        {
                            duplicates++;
                        }
                        else
                        {
                            queued++;
                            minAhead = Math.Min(minAhead, ahead);
                            maxAhead = Math.Max(maxAhead, ahead);
                        }
                        continue;
                    }

                    activeSweeps[pawn] = order;
                    NoteOrderIssued(pawn, order);

                    // A fresh order clears leftover retry state from the pawn's
                    // previous one, as the other two entry points already did.
                    // Without it a pawn still waiting on an old areaRetryAt had
                    // the end of its first job on THIS order ignored by
                    // Notify_JobEnded, and carried the old try count in. Rare
                    // until 2026-09-13, when a pawn facing only reserved targets
                    // started waiting too - architecture doc section 10. The
                    // pause is not cleared here; the need check below decides it.
                    workstationRetryAt.Remove(pawn);
                    areaRetryAt.Remove(pawn);
                    areaRetries.Remove(pawn);
                    consecutiveFailures.Remove(pawn);

                    // A pawn already under threshold JOINS the sweep but does not
                    // start work - it is paused on the spot and picks the order
                    // up when the need is dealt with. Added 2026-09-07.
                    //
                    // This loop used to clear the pause unconditionally and
                    // assign immediately. Boom was paused on Rest 4% at 16:33,
                    // recruited by the next * haul at 16:38, and paused again one
                    // second later on Rest 1% - his rest fell while he hauled,
                    // because a new order simply forgot he was resting. Session
                    // total that day: 76 pauses against 30 resumes.
                    string need = NeedMonitor.CriticalNeedLabelFor(pawn);
                    if (need != null)
                    {
                        // false: leave the pawn on whatever it is already doing.
                        // See PauseForNeed for why ending it here froze pawns.
                        PauseForNeed(pawn, need, false);
                        Logger.Message($"{pawn.LabelShort} joined the sweep paused: {need} ({order.WorkGiverDef.defName}) - left on its current job");
                        joinedPaused++;
                        continue;
                    }

                    pausedForNeed.Remove(pawn);
                    AssignNextTask(pawn, order, null, appendBehindCurrentJob);
                }
                catch (Exception ex)
                {
                    GroupOrderPawnExceptionCaught(pawn, workGiverDef.defName, ex);
                }
            }

            // ONE line for the whole order when anybody queued it or already
            // had it - section 12, Logging
            if (queued > 0 || duplicates > 0)
            {
                Logger.Message($"BeginSweep {workGiverDef.defName}: {able.Count - queued - duplicates} started now"
                    + QueueSummary(queued, able.Count, minAhead, maxAhead, duplicates));
            }

            // ONE message for the whole order, never one per pawn. Added
            // 2026-09-12 on the user's words, which are the text verbatim: a
            // clear-snow order took 31 pawns that day and 16 of them never
            // moved, and the game said nothing at all - so an order that
            // looked accepted just did less than it should have, with no way
            // to tell that from a bug. Sixteen messages would be worse than
            // the silence, and naming or counting the pawns was not asked
            // for; the per-pawn log line above is where the names and the
            // needs live. Architecture doc section 8.
            //
            // Same shape as the empty-scan rejection above: RejectInput, a
            // look target on the clicked cell, not historical.
            if (joinedPaused > 0)
            {
                Messages.Message(
                    "Some pawns are too hungry, tired, or overworked.",
                    new TargetInfo(clickCell, map),
                    MessageTypeDefOf.RejectInput,
                    false);
            }
        }

        // ", dropped: Abi, Kuba", or "" when every eligible pawn can do
        // something in the pool. Names go in the one summary line rather than
        // a line each - a line per pawn per click is the shape the standing
        // logging rule forbids.
        private static string DroppedSummary(List<Pawn> eligiblePawns, List<Pawn> able)
        {
            if (able.Count >= eligiblePawns.Count)
            {
                return "";
            }

            var kept = new HashSet<Pawn>(able);
            var names = new List<string>();
            foreach (Pawn pawn in eligiblePawns)
            {
                if (pawn != null && !kept.Contains(pawn))
                {
                    names.Add(pawn.LabelShort);
                }
            }

            return names.Count == 0 ? "" : ", dropped: " + string.Join(", ", names.ToArray());
        }

        // endedJob is the job JobTrackerPatch's prefix captured before
        // EndCurrentJob cleared it. Optional only so an older call site
        // cannot fail to compile; JobTrackerPatch always passes it. Added
        // 2026-09-18: the line below named the ORDER's WorkGiverDef and
        // nothing about the job, so on 2026-09-17 a flak jacket job ending
        // was written as "job ended InterruptForced
        // (DoBillsFabricationBench)" and read as the fabrication order
        // ending. Architecture section 15.
        //
        // playerInterruptedForced is read by JobTrackerPatch's Prefix, before
        // EndCurrentJob pools the job and Job.Clear() wipes the flag - the
        // endedJob.playerInterruptedForced read below was always false by the
        // time it got here (fixed 2026-10-02, section 22).
        public void Notify_JobEnded(Pawn pawn, JobCondition condition, Job endedJob = null, bool playerInterruptedForced = false)
        {
            if (!activeSweeps.TryGetValue(pawn, out SweepOrder order))
            {
                return;
            }

            // The player's own orders always interrupt - section 16, settled
            // 2026-09-19/20. Job.playerInterruptedForced is vanilla's own
            // marker, written only by Pawn_JobTracker.TryTakeOrderedJob's
            // replace-without-shift branch onto the OUTGOING job, so its
            // being true here means a direct player order just took this
            // pawn over - not a reservation steal, not our own hand-off (both
            // of those never set it). Checked here, first, unconditionally,
            // because every branch below this point can resume the sweep on
            // its own reasoning and none of them looked at this flag:
            // pausedForNeed resumes once needs are satisfied whatever job
            // satisfied them, and a plain Succeeded hands out the next task
            // whatever job succeeded. Fixed 2026-09-26 - a pawn already
            // registered as paused for a need (or merely between two of our
            // own hand-offs) whose player-forced order then ran to its own
            // end reached one of those branches first and got resumed onto
            // the very order the player had just taken it off of. The
            // wording matches TargetFailureIsRecoverable's own
            // InterruptForced case below, which this makes unreachable for
            // playerInterruptedForced true - that case still exists for the
            // condition itself to read correctly when this flag is false.
            if (playerInterruptedForced || (endedJob != null && endedJob.playerInterruptedForced))
            {
                // endedJob has been pooled by now (def null), so name what
                // the pawn is on instead.
                Job nowOn = pawn.jobs?.curJob;
                string nowText = nowOn == null
                    ? "no job"
                    : $"{DescribeJob(nowOn)} from {DescribeJobSource(nowOn)}";
                ClearSweeps(pawn, $"the player gave this pawn a different order (now on {nowText})");
                return;
            }

            // waiting out a workstation cooldown. Whatever just ended is
            // vanilla's work rather than ours, and TryWorkstationRetry owns
            // this pawn until the clock runs out.
            if (workstationRetryAt.ContainsKey(pawn))
            {
                return;
            }

            // same contract for an area order waiting out an inert pool -
            // TryAreaRetry owns this pawn until its clock runs out, and
            // whatever just ended was vanilla's work, not ours
            if (areaRetryAt.ContainsKey(pawn))
            {
                return;
            }

            if (pausedForNeed.TryGetValue(pawn, out PauseInfo paused))
            {
                // Section 21, added 2026-09-28 per the logging standard
                // (CLAUDE.md section 4): one line per forced meal ending,
                // never per tick, naming why it ended and what replaced it
                // if knowable - the record that was missing for Lerb, whose
                // forced Ingest ended unlogged and left no trail to LayDown.
                // Only fires for the job we ourselves forced (ourJob[pawn]
                // is a reference match, same test EndOrder uses above).
                ourJob.TryGetValue(pawn, out Job forcedJob);
                if (endedJob != null && ReferenceEquals(endedJob, forcedJob) && endedJob.def == JobDefOf.Ingest)
                {
                    Job replacement = pawn.jobs?.curJob;
                    string replacementText = replacement == null
                        ? "nothing - pawn is idle"
                        : $"{DescribeJob(replacement)} from {DescribeJobSource(replacement)}";
                    Logger.Message($"{pawn.LabelShort}: forced meal ended {condition} - now on {replacementText}");
                }

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
                    // Used to RemoveSweep here once MaxPauseTicks elapsed, on
                    // the reasoning that a mood which never recovers would
                    // hold the pool forever. Ordered changed 2026-09-07: the
                    // sweep is meant to survive any number of need breaks and
                    // end only when the WORK is done, so a long pause is now
                    // reported once and waited out. A paused pawn holds no
                    // target - its own was consumed when its job was issued -
                    // so the pool is not actually hostage to it.
                    WarnOnLongPause(pawn, paused);
                    return;
                }

                pausedForNeed.Remove(pawn);
                Logger.Message($"{pawn.LabelShort}: {paused.need} satisfied, resuming sweep ({order.WorkGiverDef.defName})");
                Logger.Order($"ORDER {OrderIdFor(pawn)} resumed: {pawn.LabelShort} ({NeedMonitor.LevelsText(pawn)})");
                AssignNextTask(pawn, order);
                return;
            }

            // Fixed 2026-09-28, "bUILD IT" - Panna: Drop everything's first
            // queued drop finished, and this method handed her sweep its
            // next haul target through the Succeeded branch below - GiveJob
            // calls TryTakeOrderedJob with requestQueueing:false, which
            // clears a pawn's job queue, so the other 14 queued drops were
            // wiped before they ran.
            //
            // Revised the same day, review finding: gating the Succeeded
            // continuation on ourJob[pawn] alone was too strict. Vanilla's
            // think tree hands a pawn its next job in the same tick almost
            // every time something ends (usually JobGiver_Work), so the
            // pawn is hardly ever idle - TryResumeIdle below almost never
            // got a turn, and a sweep pawn who merely finished a firefight,
            // a social fight or any other job of its own sat doing ordinary
            // work forever instead of being taken back. What actually needs
            // protecting is not "was this job ours" but "would advancing
            // wipe something the player queued" - GiveJob's
            // requestQueueing:false only destroys pawn.jobs.jobQueue, so
            // that queue is the thing to check, not the job reference.
            //
            // The rule now: a non-sweep job ending with the pawn's queue
            // still holding something leaves the pawn alone (logged,
            // throttled) - advancing would wipe it, same as Panna. A
            // non-sweep job ending with the queue EMPTY is safe to advance
            // exactly as before this whole fix existed - falls through to
            // the ordinary Succeeded branch below. Once the queue drains
            // (its last entry ends), that ending is read with the queue
            // already empty, so the sweep takes the pawn back right there;
            // TryResumeIdle (MapComponentTick) stays as the fallback for a
            // pawn left idle with no job end to prompt this check at all.
            //
            // The retarget/failure path (TargetFailureIsRecoverable,
            // NoteTargetFailure, below) is NOT given the same relaxation:
            // it charges a failure to the sweep's own last target, and a
            // foreign job's non-Succeeded ending must never be read as that
            // target failing. So a non-sweep, non-Succeeded ending is
            // always left alone here, queue empty or not - only a
            // non-sweep Succeeded ending with an empty queue is allowed
            // through.
            ourJob.TryGetValue(pawn, out Job mine);
            bool isOurJob = endedJob != null && ReferenceEquals(endedJob, mine);
            if (endedJob != null && !isOurJob)
            {
                bool queueHasWork = pawn.jobs?.jobQueue != null && pawn.jobs.jobQueue.Count > 0;
                bool safeToAdvance = !queueHasWork && condition == JobCondition.Succeeded;
                if (!safeToAdvance)
                {
                    LogNonSweepJobEnded(pawn, endedJob, condition);
                    return;
                }
                // queue is empty and the job succeeded - falls through to
                // the same Succeeded handling below as a sweep-own job
                // would get, exactly as before this fix existed.
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
            // Scanner orders, and only scanner orders. Their vanilla job
            // carries expiryInterval 1500 with checkOverrideOnExpire, so
            // every 1500 ticks the think tree re-picks the same scanner as a
            // *different* Job instance, and the swap arrives here as
            // InterruptOptional while the pawn carries on scanning without
            // breaking stride. Treated as a departure, that would cap every
            // scanner order at roughly 25 seconds of play.
            //
            // It cannot be told apart from a real departure at this point:
            // the replacement job has not started yet, so pawn.CurJob is
            // still the one that just ended. TryScannerWatchdog looks 60
            // ticks later, when it has, and ends the sweep then if the pawn
            // really did walk off. InterruptForced no longer reaches this
            // check at all - see TargetFailureIsRecoverable, changed
            // 2026-09-19.
            if (condition == JobCondition.InterruptOptional
                && ScannerCompat.IsScannerWork(order.WorkGiverDef))
            {
                return;
            }

            if (!TargetFailureIsRecoverable(condition, endedJob))
            {
                // This is the site that ended legua's butcher sweep on
                // 2026-09-05 without a word. What is left here after
                // 2026-09-19 - InterruptOptional off a scanner, or Errored -
                // means something took the pawn or the job system itself
                // broke, and continuing would fight it or loop. Fatal is
                // right; silent was not.
                //
                // Since 2026-09-20, InterruptForced can also mean the player
                // gave this pawn a different order directly (rather than
                // through this mod's own menu, which JoinOrQueue already
                // handles before a job is touched) - endedJob.
                // playerInterruptedForced true, read by
                // TargetFailureIsRecoverable. Said plainly rather than as
                // the generic wording below, so the log reads as "the
                // player did this" and not as a fault.
                //
                // Drops the queue as well since 2026-09-13: a manual order
                // arrives as exactly this, and moving the pawn onto its next
                // queued order would fight whatever took it. Section 12,
                // decision 4.
                string reason = condition == JobCondition.InterruptForced
                    ? $"the player gave this pawn a different order (ended {DescribeJob(endedJob)})"
                    : $"job ended {condition} on {DescribeJob(endedJob)}";
                ClearSweeps(pawn, reason);
                return;
            }

            // The target this pawn was working on, captured before
            // NoteTargetFailure clears it - only meaningful for
            // InterruptForced, and only for as long as it takes to write the
            // dedicated retarget line below. lastAssignedTarget holds
            // nothing for a workstation or persistent-target order (they
            // have no pool), so endedJob.targetA is the fallback - usually
            // the station or the specific item the job was engaged with.
            LocalTargetInfo lostTarget = condition != JobCondition.InterruptForced
                ? LocalTargetInfo.Invalid
                : lastAssignedTarget.TryGetValue(pawn, out LocalTargetInfo pooled)
                    ? pooled
                    : endedJob?.targetA ?? LocalTargetInfo.Invalid;

            NoteTargetFailure(pawn, order, condition);

            consecutiveFailures.TryGetValue(pawn, out int failures);
            failures++;
            if (failures >= MaxConsecutiveFailures)
            {
                RemoveSweep(pawn, condition == JobCondition.InterruptForced
                    ? $"{failures} sweep tasks in a row had their target taken by another job"
                    : $"{failures} sweep tasks failed in a row ({condition})");
                return;
            }

            consecutiveFailures[pawn] = failures;
            AssignNextTask(pawn, order);

            // One dedicated line per retarget, naming what was lost and
            // what replaced it - ordered 2026-09-19, architecture section
            // 14. Only written when AssignNextTask actually handed out a
            // fresh job on THIS SAME order: not when it ended the order
            // (RemoveSweep/ClearSweeps already said why, immediately
            // above), armed a retry wait (the wait line already says the
            // pawn is staying in the sweep), or paused the pawn for a need
            // (PauseForNeed already said so, and section 12's pause/resume
            // machinery is not to be fought here). Reaching this point with
            // condition InterruptForced is now guaranteed (2026-09-20) to be
            // a teammate's reservation steal, never a direct player order -
            // TargetFailureIsRecoverable already sent that case to
            // ClearSweeps above, so this is never mislabelled as a retarget.
            if (condition == JobCondition.InterruptForced
                && activeSweeps.TryGetValue(pawn, out SweepOrder stillOn) && stillOn == order
                && !workstationRetryAt.ContainsKey(pawn) && !areaRetryAt.ContainsKey(pawn)
                && !pausedForNeed.ContainsKey(pawn))
            {
                string newWork = order.WorkstationTarget != null
                    ? order.WorkstationTarget.LabelShort
                    : lastAssignedTarget.TryGetValue(pawn, out LocalTargetInfo newTarget)
                        ? newTarget.ToString()
                        : "the next target";

                LogRetarget(pawn, order, lostTarget, newWork);
            }
        }

        // The line for a non-sweep job ending on a sweep pawn where the
        // queue gate above declined to advance - either the job did not
        // succeed, or the pawn's own job queue still had something in it
        // that advancing would have wiped. Per the logging standard
        // (CLAUDE.md section 4): the sweep deliberately did not advance, and
        // that decision is worth one line naming the job, not silence.
        // Throttled the same shape as Notify_JobEndDiscarded and LogRetarget
        // - at most one line per pawn per NonSweepJobEndQuietTicks, with the
        // count in between folded into the next line - because a pawn
        // running a queue of small jobs (Drop Everything's one-stack-per-job
        // drops, for one) ends several of these in quick succession, and a
        // line each would be exactly the per-target volume the standing
        // rule forbids. Added 2026-09-28, section 22 ("bUILD IT").
        private void LogNonSweepJobEnded(Pawn pawn, Job endedJob, JobCondition condition)
        {
            int now = Find.TickManager.TicksGame;
            nonSweepJobEnds.TryGetValue(pawn, out NonSweepJobEndInfo info);
            if (now < info.nextLogTick)
            {
                info.suppressed++;
                nonSweepJobEnds[pawn] = info;
                return;
            }

            string alsoEnded = info.suppressed == 0
                ? ""
                : $" (and {info.suppressed} more non-sweep job end{(info.suppressed == 1 ? "" : "s")} left alone since the last such line)";

            Logger.Message($"{pawn.LabelShort}: {DescribeJob(endedJob)} ended {condition} - not the sweep's own job, left alone{alsoEnded}");

            info.suppressed = 0;
            info.nextLogTick = now + NonSweepJobEndQuietTicks;
            nonSweepJobEnds[pawn] = info;
        }

        // Throttled the same way Notify_JobEndDiscarded is: at most one line
        // per pawn per RetargetQuietTicks, with the count of retargets in
        // between carried onto the next line. Without this, a pawn whose
        // target keeps getting taken over a long-running order would write
        // one line per incident - the standing rule against a line per
        // target, per def or per click applies here too. Added 2026-09-19 -
        // architecture section 14.
        private void LogRetarget(Pawn pawn, SweepOrder order, LocalTargetInfo lost, string newWork)
        {
            int now = Find.TickManager.TicksGame;
            retargets.TryGetValue(pawn, out RetargetInfo info);
            if (now < info.nextLogTick)
            {
                info.suppressed++;
                retargets[pawn] = info;
                return;
            }

            string alsoLost = info.suppressed == 0
                ? ""
                : $" (and {info.suppressed} more retarget{(info.suppressed == 1 ? "" : "s")} for this pawn since the last such line)";

            Logger.Message($"{pawn.LabelShort}: {(lost.IsValid ? lost.ToString() : "its target")} was taken by another job"
                + $" - retargeted to {newWork} ({order.WorkGiverDef.defName}){alsoLost}");

            info.suppressed = 0;
            info.nextLogTick = now + RetargetQuietTicks;
            retargets[pawn] = info;
        }

        // Charge one recoverable failure to whatever target this pawn was
        // last sent to, and retire the target once it has cost the sweep
        // MaxTargetFailures. Added 2026-09-07 - see SweepOrder.TargetFailures
        // for the forty-times blueprint that made it necessary.
        //
        // Logged only on the retirement, never per failure: the per-target
        // line is exactly the shape the standing rule forbids, and the
        // failures themselves are already visible as "job ended
        // Incompletable".
        private void NoteTargetFailure(Pawn pawn, SweepOrder order, JobCondition condition)
        {
            if (!lastAssignedTarget.TryGetValue(pawn, out LocalTargetInfo target))
            {
                return;
            }

            lastAssignedTarget.Remove(pawn);

            order.TargetFailures.TryGetValue(target, out int failures);
            failures++;
            order.TargetFailures[target] = failures;

            if (failures != MaxTargetFailures)
            {
                return;
            }

            // Drop it from the pool now as well. AddNewTargets keeps it out
            // from here on, but a copy may already be sitting in the pool -
            // requeued as a preparatory job, or re-added by a rescan that
            // ran before the count reached the cap.
            order.SharedPool.Remove(target);
            Logger.Message($"dropping {target} from the {order.WorkGiverDef.defName} pool - failed {failures} times ({condition})");
        }

        // Poll a paused pawn and put it back on its order once the need it
        // stopped for is dealt with. Returns true when it acted, so the
        // caller skips the retries - a pawn that just resumed has a job.
        private bool TryResumeFromNeed(Pawn pawn, SweepOrder order)
        {
            if (!pausedForNeed.TryGetValue(pawn, out PauseInfo paused))
            {
                return false;
            }

            if (!NeedMonitor.NeedsSatisfied(pawn))
            {
                WarnOnLongPause(pawn, paused);
                return true;
            }

            pausedForNeed.Remove(pawn);
            Logger.Message($"{pawn.LabelShort}: {paused.need} satisfied, resuming sweep ({order.WorkGiverDef.defName})");
            Logger.Order($"ORDER {OrderIdFor(pawn)} resumed: {pawn.LabelShort} ({NeedMonitor.LevelsText(pawn)})");
            AssignNextTask(pawn, order);
            return true;
        }

        // One line per pause, not one per check. A pause that outlives
        // MaxPauseTicks is worth knowing about - it is usually a mood that
        // will not climb - but it no longer ends the sweep, so it must not
        // fill the log while it waits.
        private void WarnOnLongPause(Pawn pawn, PauseInfo paused)
        {
            if (paused.warned || Find.TickManager.TicksGame - paused.tick <= MaxPauseTicks)
            {
                return;
            }

            paused.warned = true;
            pausedForNeed[pawn] = paused;
            Logger.Message($"{pawn.LabelShort}: {paused.need} still under threshold after {MaxPauseTicks} ticks - staying paused, sweep held");
        }

        // Split JobCondition into "that target didn't work out" (keep the
        // sweep, try the next one) and "something took this pawn away from
        // us" (stop - continuing would fight the player or the AI).
        //
        // InterruptForced moved into the recoverable set on 2026-09-19.
        // Verified from the IL, architecture section 14: every sweep job is
        // player-forced, and a player-forced job's own reservation can be
        // taken by ANOTHER player-forced job - ours or anyone else's -
        // which ends the holder's job with exactly this condition. The
        // user's words, answering the open question left at the end of
        // section 14: "If a resource needed to complete a job is consumed
        // by another job, the pawn should find the next closest resource of
        // that type and continue rather than abandon the job. This is
        // especially true of forced jobs." So this is no longer treated as
        // the player manually pulling the pawn away - it is coped with, the
        // same as any other target that stopped being workable. See
        // Notify_JobEnded for the dedicated retarget line this now writes.
        //
        // But InterruptForced is not ALWAYS a stolen target - a direct
        // player order replacing this pawn's job ends it with the identical
        // condition (section 16, "the hard part"). Verse.AI.
        // Job.playerInterruptedForced is vanilla's own way of telling them
        // apart. Verified 2026-09-20 by decompiling lib\Assembly-CSharp.dll
        // with ilspycmd (707,563 lines): Pawn_JobTracker.TryTakeOrderedJob's
        // replace-without-shift branch is the ONLY place in the whole
        // assembly that writes it - `curJob.playerInterruptedForced = true;`
        // on the pawn's OUTGOING job, immediately before
        // `curDriver.EndJobWith(JobCondition.InterruptForced)` ends it. The
        // reservation-steal path (ReservationManager.Reserve's player-forced
        // branch) ends the holder's job through
        // Pawn_JobTracker.EndCurrentOrQueuedJob -> EndCurrentJob directly and
        // never reads or writes the field. So false here (the common case)
        // means a teammate merely took the target - recoverable, same as
        // before. True means a direct player order took this pawn's job -
        // not recoverable, whether that order came through this mod's own
        // menu (JoinOrQueue's replace branch already handled that case
        // before a job was even touched, so this path is not reached for
        // it) or through anything else the player can do to a pawn -
        // matching the top-level rule that the player's own orders always
        // interrupt.
        //
        // InterruptOptional is left out on purpose: for a scanner order it
        // is handled two lines above (the 1500-tick expiry swap), and for
        // everything else it still means something else decided this pawn
        // should be doing something different - the same reasoning that
        // used to cover InterruptForced too. Errored means a genuine
        // exception in the job system, where retrying risks a loop rather
        // than a recovery.
        private static bool TargetFailureIsRecoverable(JobCondition condition, Job endedJob)
        {
            if (condition == JobCondition.InterruptForced)
            {
                return endedJob == null || !endedJob.playerInterruptedForced;
            }

            return condition == JobCondition.Incompletable       // target no longer workable
                || condition == JobCondition.QueuedNoLongerValid  // target invalidated before we got there
                || condition == JobCondition.ErroredPather;       // couldn't path to this one target
        }

        // resumedBecause names what the pawn is coming back from - "an area
        // wait" - and is folded into whichever assignment line this call
        // writes, so a resume costs no line of its own. Null on every call
        // that is a plain continuation. Added 2026-09-18, architecture
        // section 15. The need-pause resume and the workstation retry resume
        // already write their own line and do not use this.
        //
        // appendBehindCurrentJob is only ever true for a freshly joined
        // pawn's very first job on this order (BeginAreaSweep passes it
        // through from JoinOrQueue - see GiveJob). Every other caller
        // (a retry, a resume, a retarget after failure) defaults it false,
        // which is correct: by the time those run, this order's own prior
        // job on this pawn has already ended, so there is nothing of the
        // pawn's own to preserve. Added 2026-09-21.
        private void AssignNextTask(Pawn pawn, SweepOrder order, string resumedBecause = null, bool appendBehindCurrentJob = false)
        {
            string resumed = resumedBecause == null ? "" : $", resumed after {resumedBecause}";
            string queuedNote = appendBehindCurrentJob ? ", queued behind current job" : "";

            if (!(order.WorkGiverDef.Worker is WorkGiver_Scanner scanner))
            {
                RemoveSweep(pawn, "the WorkGiver is no longer a scanner");
                return;
            }

            // state can change between the tick check and here (downed by a
            // roof collapse mid-mining sweep is the obvious one)
            if (!PawnValidator.CanSweep(pawn, order.WorkGiverDef) || !pawn.Spawned || pawn.Map != map)
            {
                // RefusalReason gives the player's own wording - "is drafted",
                // "will never do butchering", "is not assigned to cooking".
                string refusal = !pawn.Spawned || pawn.Map != map
                    ? "no longer on this map"
                    : PawnValidator.RefusalReason(pawn, order.WorkGiverDef) ?? "no longer eligible";

                // Two different things since 2026-09-13. A pawn that is gone,
                // down, broken or drafted is out of the work, and its queue
                // goes with the order. A pawn refused on work settings is
                // refused for THIS order's work type only, and the next
                // queued order may be one it can do. Section 12, decision 4.
                if (!pawn.Spawned || pawn.Map != map || pawn.Dead || pawn.Downed || pawn.InMentalState || pawn.Drafted)
                {
                    ClearSweeps(pawn, refusal);
                }
                else
                {
                    RemoveSweep(pawn, refusal);
                }
                return;
            }

            if (order.WorkstationTarget != null)
            {
                // resuming (or continuing) a workstation order - always the
                // same station, never a pool to draw from
                if (order.WorkstationTarget.Destroyed)
                {
                    RemoveSweep(pawn, "the station was destroyed");
                    return;
                }

                // WorkGiver_OperateScanner.JobOnThing has no null path - its
                // whole body is one JobMaker call - so the "station gave me
                // nothing, we are done" ending below is unreachable for a
                // scanner, and it would hand out jobs at an unpowered or
                // roofed-over machine forever. HasJobOnThing is the gate that
                // actually answers, and it is the same one the float menu
                // used to offer the order in the first place.
                if (ScannerCompat.IsScannerWork(order.WorkGiverDef)
                    && !scanner.HasJobOnThing(pawn, order.WorkstationTarget, true))
                {
                    RemoveSweep(pawn, $"{order.WorkstationTarget.LabelShort} can't be worked now");
                    return;
                }

                // whatever the pawn just finished, ask the station what it
                // wants next - the bill itself, the haul-off it insists on
                // first, or a refuel. The old code read a null here as the
                // end of the order; see WorkstationHadNoJob for why it
                // usually isn't one.
                Job resumeJob = WrapForVehicleStuffing(pawn, scanner.JobOnThing(pawn, order.WorkstationTarget, true), order.WorkGiverDef, order.WorkstationTarget);
                if (resumeJob == null)
                {
                    WorkstationHadNoJob(pawn, order);
                    return;
                }

                consecutiveFailures.Remove(pawn);
                workstationRetryAt.Remove(pawn);

                // DescribeJob, not resumeJob.def.defName: for a bench order
                // the def is the word "DoBill" and the recipe is the thing
                // anyone reading this line wants. Architecture section 15.
                Logger.Message($"{pawn.LabelShort}: {DescribeJob(resumeJob)} at {order.WorkstationTarget.LabelShort} ({order.WorkGiverDef.defName}){resumed}{queuedNote}");
                ourJob[pawn] = resumeJob;
                GiveJob(pawn, resumeJob, appendBehindCurrentJob);
                return;
            }

            bool firefighting = FireCompat.IsFirefighting(order.WorkGiverDef);
            bool rescanned = false;

            // Targets this pawn has already been refused for on THIS call.
            // They stay in the shared pool now (see the RemoveAt note below),
            // so without this we'd hand the same rejected target straight
            // back to the same pawn and spin.
            var refused = new HashSet<LocalTargetInfo>();

            // how many pooled targets answered null this call - reported once
            // at the end rather than one line per target, see below
            int noJobCount = 0;

            // Same treatment, and for the same reason. The refusal line below
            // used to name every skipped target and is O(pool x pawns) - and
            // unlike noJobCount it survived the 2026-09-03 fix, because at the
            // time HaulGeneral pooled few enough targets for it not to show.
            // Redirecting * haul orders to Pick Up And Haul on 2026-09-04
            // changed that: PUAH pools far more per pawn, one session emitted
            // 1101 [DoNotBeLazy] lines, and RimWorld's `Reached max messages
            // limit` cost the evidence for a player's own report.
            // FIFTH time. Counted by reason, reported once.
            var refusalCounts = new Dictionary<string, int>();

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

                    // One pawn, deliberately - the order's first scan asks
                    // the whole group, but this rescan runs because THIS
                    // pawn ran out. Targets only a teammate can do would not
                    // help it and would multiply the cost by the group; each
                    // teammate rescans for itself when it runs out.
                    // Architecture doc section 9.
                    rescanned = true;
                    AddNewTargets(order, TaskScanner.FindTargets(order.ScanCenter, order.ScanRadius, map, order.WorkGiverDef, pawn, 0, order.TargetFilter));
                    continue;
                }

                LocalTargetInfo target = order.SharedPool[i];

                // gone for good (hauled by someone else, mined out, filth
                // swept up by another pawn's batch job) vs merely not
                // available to this pawn this second (reserved, forbidden,
                // outside their allowed area, unreachable). Only the first
                // kind leaves the pool - conflating the two is what the
                // discard bug was.
                if (TargetIsGone(target) || IsRetired(order, target))
                {
                    order.SharedPool.RemoveAt(i);
                    continue;
                }

                string refusal = TargetRefusalReason(pawn, target, scanner, firefighting);
                if (refusal != null)
                {
                    // counted, NOT logged per target - see refusalCounts
                    refusalCounts.TryGetValue(refusal, out int seen);
                    refusalCounts[refusal] = seen + 1;
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
                    //
                    // Counted, NOT logged per target. This line used to name
                    // every refusal and it is O(pool x pawns): one 359-target
                    // haul sweep emitted 964 lines, hit RimWorld's
                    // `Reached max messages limit`, and cost the session the
                    // evidence for the bug it was meant to help diagnose -
                    // the third time verbose logging has done that. The
                    // total is reported once, below, where it means
                    // something.
                    noJobCount++;
                    refused.Add(target);
                    continue;
                }

                // Leave out every queued target another pawn has reserved,
                // BEFORE the job is issued. A sweep job is player-forced, and
                // ReservationManager.Reserve takes a player-forced job's
                // reservation from whoever holds it and ends that pawn's
                // whole job - silently, because it happens inside GiveJob
                // while AssigningJob is set. Seventeen pawns on one clean
                // order spent 2026-09-13 ending each other's jobs and never
                // cleaning the firefoam at the click. Architecture doc
                // section 14.
                int leftOut = DropQueuedTargetsReservedByOthers(pawn, job);
                if (leftOut > 0 && !job.targetA.IsValid && job.targetQueueA.Count == 0)
                {
                    // nothing left for this job to do - it would end
                    // Succeeded on its first toil. Treat the target as
                    // reserved, the same as TargetRefusalReason would.
                    refusalCounts.TryGetValue("reserved", out int seenReserved);
                    refusalCounts["reserved"] = seenReserved + 1;
                    refused.Add(target);
                    continue;
                }

                order.SharedPool.RemoveAt(i);

                // plantDefToSow is the field the whole GrowerSow static-state
                // bug turned on, so name it explicitly - "which crop did we
                // actually tell them to plant, on which cell" is the single
                // most useful line in a sow trace
                // The cell and the two distances, added 2026-09-07. `target`
                // prints coordinates for a CELL target but only a label and
                // id for a THING target, so a tree-felling sweep left no
                // record of where anything was - and a report that jobs were
                // going to the pawn's own vicinity rather than the clicked
                // centre could be neither confirmed nor refuted from the log.
                // c= is distance from the order's scan centre, p= from the
                // pawn; under centre-out, c should climb as the sweep runs
                // and p should not.
                //
                // Cut on 2026-09-20 as one of two unthrottled lines, and
                // restored the same day - it was the only record of which
                // target a pawn was actually given, and losing it meant
                // "is she hauling farther out because the near stuff is
                // gone, or because something refused it" had no answer.
                // Fires once per job actually handed to a pawn, via the
                // return two statements below it - never once per candidate
                // looked at in the loop above.
                float fromCentre = (target.Cell - order.ScanCenter).LengthHorizontal;
                float fromPawn = (target.Cell - pawn.Position).LengthHorizontal;

                Logger.Message($"{pawn.LabelShort}: {DescribeJob(job)} on {target}"
                    + $" at {target.Cell} c={fromCentre:F0} p={fromPawn:F0}"
                    + $" for the {order.WorkGiverDef.defName} order"
                    + resumed
                    + queuedNote
                    + DescribeQueue(job)
                    + (leftOut > 0 ? $", {leftOut} reserved by others left out" : "")
                    + (order.CenterOut ? "" : " [pawn-nearest]")
                    + (job.plantDefToSow != null ? $" plant={job.plantDefToSow.defName}" : "")
                    + $" ({order.SharedPool.Count} left)"
                    // skips walked past on the way to this job, folded in
                    // rather than given a line of their own - otherwise the
                    // count is simply lost whenever the pawn does find work,
                    // which is most of the time
                    + RefusalSummary(refusalCounts));

                // WorkGiver answered "clear this blocker first" rather than
                // the work asked for (GrowerSow returns CutPlant/HaulAside).
                // Put the target back so the real work still happens once the
                // blocker's gone, instead of dropping the cell we just cleared.
                if (GrowerCompat.IsPreparatoryJob(job, target) && order.Requeued.Add(target))
                {
                    order.SharedPool.Add(target);
                }

                // The retry budget counts looks IN A ROW that found nothing.
                // It was documented that way from 2026-09-03 but never
                // cleared here, so it counted every wait in the whole sweep -
                // harmless while waiting was rare, and a pawn struck off
                // mid-sweep once reserved targets started waiting as well.
                // Architecture doc section 10.
                areaRetries.Remove(pawn);

                lastAssignedTarget[pawn] = target;
                ourJob[pawn] = job;
                GiveJob(pawn, job, appendBehindCurrentJob);

                // Added 2026-10-05. Seen 00:43:44 and 00:43:50 on a 12-pawn
                // ConstructDeliverResourcesToBlueprints order: handing Darcey
                // her job stole a reservation (every sweep job is
                // player-forced), the holder's reassignment reentrantly
                // stole one of Darcey's, and ReservationManager ended
                // Darcey's own half-started job - EndCurrentJob returned it
                // to the pool and Job.Clear emptied its targets - while her
                // own StartJob was still inside TryMakePreToilReservations,
                // which then threw (JobDriver_HaulToContainer.ThingToCarry,
                // targetA gone). That end was discarded by the IsBeingAssigned
                // guard, and the throw was caught in GiveJob, so nothing was
                // left to bring Darcey back: she went to DoBill and the
                // target she was given had left the pool. Put the target
                // back and ask again shortly, within the area-wait budget.
                if (LastGiveJobThrew)
                {
                    LastGiveJobThrew = false;
                    if (!IsRetired(order, target) && !order.SharedPool.Contains(target))
                    {
                        order.SharedPool.Add(target);
                    }

                    giveJobThrowStreak.TryGetValue(pawn, out int streak);
                    streak++;
                    giveJobThrowStreak[pawn] = streak;
                    if (streak >= MaxGiveJobThrowsInARow)
                    {
                        giveJobThrowStreak.Remove(pawn);
                        Logger.Message($"{pawn.LabelShort}: starting a job threw {streak} times in a row - taking the pawn off the {order.WorkGiverDef.defName} order, target {target} put back in the pool");
                        RemoveSweep(pawn, $"job kept failing to start: {LastGiveJobExceptionType}");
                    }
                    else
                    {
                        areaRetryAt[pawn] = Find.TickManager.TicksGame + GiveJobRetryTicks;
                        Logger.Message($"{pawn.LabelShort}: starting the job on {target} threw - target put back in the {order.WorkGiverDef.defName} pool, asking again in {GiveJobRetryTicks} ticks (throw {streak} of {MaxGiveJobThrowsInARow})");
                    }
                }

                return;
            }

            // One line per call, naming each reason and how many targets it
            // accounted for - which is what the per-target version was
            // actually being read for anyway.
            //
            // Written only when the pawn is about to END its sweep since
            // 2026-09-13. On the wait path below the same summary rides on
            // the wait line, which is written at most once per order per
            // round - otherwise this line alone would still be one per pawn
            // per look. Section 12, Logging.
            // Change A, 2026-10-02. A pawn the sweep found nothing for that
            // is still carrying cargo DNBL itself stuffed into its inventory
            // is sent to deliver that cargo, then comes straight back to
            // this method when the delivery job ends. Only
            // CompDnblCargo-registered things, never the pawn's own items.
            if (TryStartCargoDelivery(pawn, order, noJobCount, refusalCounts))
            {
                return;
            }

            refusalCounts.TryGetValue("reserved", out int reservedCount);
            areaRetries.TryGetValue(pawn, out int tries);
            tries++;
            bool poolBusy = order.SharedPool.Count > 0 && (noJobCount > 0 || reservedCount > 0);
            bool willWait = poolBusy && tries <= MaxAreaRetries;
            if (refusalCounts.Count > 0 && !willWait)
            {
                Logger.Message($"{pawn.LabelShort}: skipped{RefusalSummary(refusalCounts)} ({order.WorkGiverDef.defName})");
            }

            // TWO DIFFERENT ENDINGS, and collapsing them into one is the
            // bug this split fixes. The pool still holding targets means the
            // work is still there and this pawn simply cannot take any of it
            // *this second* - almost always because teammates have every
            // destination cell reserved. Striking the pawn off the sweep for
            // that is what produced "haul until done stopped and left things
            // in the radius that could be hauled": the pool survived, the
            // workers did not, and once every pawn had walked an inert pool
            // the order was over with hundreds of haulables still lying
            // there.
            //
            // The same distinction the target path already makes (see
            // TargetIsGone) - gone for good, versus not available right now -
            // finally applied to pawns as well.
            //
            // A target a teammate has RESERVED counts here too, since
            // 2026-09-13 - architecture doc section 10. It used to fall
            // through to "nothing left within N" below: 111 of the 132 such
            // endings in that day's log came straight after a skip line
            // whose only reason was "reserved". Pick Up And Haul reserves a
            // whole queue of stacks per job, so most of a group haul's pool
            // is reserved by someone for seconds at a time. CanReserve is
            // false here only when another pawn holds the target (the pawn
            // is spawned and TargetIsGone has already pruned the rest -
            // read in the decompiled ReservationManager), and that always
            // ends: the holder takes the thing, which despawns it and
            // TargetIsGone drops it next look, or lets it go. The other
            // refusals - forbidden, allowed area, unreachable - are about
            // this pawn and do not clear on their own, so they still end it.
            if (poolBusy)
            {
                if (willWait)
                {
                    areaRetries[pawn] = tries;
                    int now = Find.TickManager.TicksGame;
                    areaRetryAt[pawn] = now + AreaRetryTicks;

                    // Was one line per look, per pawn: about 130 in five
                    // minutes for 13 pawns on one pool on 2026-09-13. Now at
                    // most one per order per AreaRetryTicks - the first wait
                    // of a round writes it, the rest are counted and the
                    // count rides on the next line written. Only the log
                    // changes; every pawn still waits. Worded exactly as
                    // before up to the try count, so existing test entries
                    // still match. Section 12, Logging.
                    if (now < order.NextWaitLogTick)
                    {
                        order.WaitsNotLogged++;
                        LogPassedOverOnce(pawn, order, noJobCount, refusalCounts);
                        return;
                    }

                    // this pawn is named on the wait line below, which counts
                    // as its one explanation for this sweep
                    passedOverLogged.Add(pawn);

                    string waitingOn = reservedCount == 0
                        ? $"all {noJobCount} of {order.SharedPool.Count} pooled targets refused"
                        : $"{reservedCount} of {order.SharedPool.Count} pooled targets reserved by others, {noJobCount} refused";
                    string unlogged = order.WaitsNotLogged == 0
                        ? ""
                        : $" (and {order.WaitsNotLogged} more wait{(order.WaitsNotLogged == 1 ? "" : "s")} on this order since the last such line)";
                    Logger.Message($"{pawn.LabelShort}: {waitingOn} ({order.WorkGiverDef.defName}), staying in the sweep, looking again in {AreaRetryTicks} ticks (try {tries})"
                        + unlogged
                        + RefusalSummary(refusalCounts));

                    order.NextWaitLogTick = now + AreaRetryTicks;
                    order.WaitsNotLogged = 0;
                    return;
                }

                RemoveSweep(pawn, $"pool still inert after {MaxAreaRetries} tries ({order.SharedPool.Count} targets)");
                return;
            }

            // Pool's empty even after a rescan - this pawn is done, and this
            // is the real ending. Said nothing at all until now, so a sweep
            // ending looked exactly like a pawn wandering off for no reason;
            // seven pawns in the 08-22 log ended here with a bare
            // "job ended Succeeded" as their last line.
            RemoveSweep(pawn, $"nothing left within {order.ScanRadius} of {order.ScanCenter}");
        }

        // Change A. Sends a passed-over pawn to storage with the cargo DNBL
        // registered in its inventory (CompDnblCargo). Vanilla's
        // UnloadYourInventory cannot be pointed at specific things -
        // JobDriver_UnloadYourInventory (decompiled) takes
        // pawn.inventory.FirstUnloadableThing while UnloadEverything is set,
        // which would also empty the pawn's own medicine and food - so the
        // existing stuffing delivery path is used, in its "cargo only" mode
        // (JobDriver_StuffAndHaul.IsCargoDeliveryJob). Returns true when a
        // delivery job was started; the caller then leaves the pawn
        // registered and un-waited, and the job's end (recorded in ourJob)
        // goes back through AssignNextTask. A pawn over capacity with no
        // DNBL cargo is left alone and says so once per sweep.
        private bool TryStartCargoDelivery(Pawn pawn, SweepOrder order, int noJobCount, Dictionary<string, int> refusalCounts)
        {
            CompDnblCargo cargoComp = pawn.GetComp<CompDnblCargo>();
            if (cargoComp == null || pawn.inventory == null)
            {
                return false;
            }

            Thing first = null;
            int stacks = 0;
            foreach (Thing held in cargoComp.Carrying())
            {
                if (pawn.inventory.innerContainer.Contains(held))
                {
                    stacks++;
                    if (first == null)
                    {
                        first = held;
                    }
                }
            }

            if (first == null)
            {
                bool over = MassUtility.GearAndInventoryMass(pawn) > MassUtility.Capacity(pawn);
                if (over && overloadedOwnItemsLogged.Add(pawn))
                {
                    Logger.Message($"{pawn.LabelShort}: overloaded with its own items ({MassUtility.GearAndInventoryMass(pawn):F1} of {MassUtility.Capacity(pawn):F1} kg) and no DNBL cargo - inventory left untouched, staying in the {order.WorkGiverDef.defName} sweep");
                }

                return false;
            }

            int now = Find.TickManager.TicksGame;
            if (cargoUnloadAt.TryGetValue(pawn, out int notBefore) && now < notBefore)
            {
                return false;
            }

            cargoUnloadAt[pawn] = now + CargoUnloadQuietTicks;

            Job job = JobMaker.MakeJob(DnblJobDefOf.StuffAndHaul, first);
            job.count = first.stackCount;
            job.haulMode = HaulMode.ToCellStorage;
            job.playerForced = true;

            Logger.Message($"{pawn.LabelShort}: passed over by the {order.WorkGiverDef.defName} sweep while carrying {stacks} stack(s) of stuffed cargo ({noJobCount} targets answered no job)"
                + RefusalSummary(refusalCounts) + " - sending it to storage to unload, then back to the sweep");

            // recorded before GiveJob, same as every forced job here, so
            // TryReportForeignJob does not read it as a player order
            ourJob[pawn] = job;
            GiveJob(pawn, job, false, "unload stuffed cargo");
            return true;
        }

        // One line per pawn per sweep saying why the sweep found it nothing
        // and left it on vanilla's own jobs: the refusal counts, how many
        // pooled targets answered "no job", and what the pawn is carrying
        // against what it can carry (an overloaded pawn is refused every
        // haul). The wait line is throttled per ORDER, so under a busy
        // sweep most pawns were passed over without a word. Added
        // 2026-10-02. MassUtility.GearAndInventoryMass and Capacity are
        // public statics in RimWorld.MassUtility.
        private void LogPassedOverOnce(Pawn pawn, SweepOrder order, int noJobCount, Dictionary<string, int> refusalCounts)
        {
            if (!passedOverLogged.Add(pawn))
            {
                return;
            }

            string load = "";
            try
            {
                int stacks = pawn.inventory?.innerContainer?.Count ?? 0;
                load = $"; carrying {MassUtility.GearAndInventoryMass(pawn):F1} of {MassUtility.Capacity(pawn):F1} kg ({stacks} inventory stack{(stacks == 1 ? "" : "s")})";
            }
            catch (Exception)
            {
                // load is context only - never let it stop the sweep
            }

            Logger.Message($"{pawn.LabelShort}: passed over by the {order.WorkGiverDef.defName} sweep - no job found among {order.SharedPool.Count} pooled targets, {noJobCount} answered no job"
                + RefusalSummary(refusalCounts)
                + load
                + " (staying in the sweep; one line per pawn per sweep)");
        }

        // " - skipped 52 reserved, 3 forbidden", or "" when nothing was
        // skipped. Reasons, not targets: a target name per skip is what blew
        // RimWorld's message cap five sessions running, and the reason is the
        // part anyone was ever reading.
        private static string RefusalSummary(Dictionary<string, int> counts)
        {
            if (counts == null || counts.Count == 0)
            {
                return "";
            }

            var parts = new List<string>();
            foreach (KeyValuePair<string, int> pair in counts)
            {
                parts.Add($"{pair.Value} {pair.Key}");
            }

            return " - skipped " + string.Join(", ", parts.ToArray());
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

            if (VehicleCompat.IsPersistentTargetWork(order.WorkGiverDef))
            {
                // The vehicle equivalent of the bill test below: either the
                // cargo the player asked to have loaded is still outstanding,
                // or it is not. A null JobOnThing on top of an outstanding
                // manifest means the items are unreachable, forbidden, or
                // already in a teammate's arms - all worth another look
                // shortly rather than an ending.
                if (!VehicleCompat.WantsMoreCargo(order.WorkGiverDef, order.WorkstationTarget))
                {
                    RemoveSweep(pawn, $"nothing left to load at {station}");
                    return;
                }
            }
            else if (!(order.WorkstationTarget is IBillGiver billGiver)
                || billGiver.BillStack == null
                || !billGiver.BillStack.AnyShouldDoNow)
            {
                RemoveSweep(pawn, $"no bills left at {station}");
                return;
            }

            consecutiveFailures.TryGetValue(pawn, out int failures);
            failures++;
            if (failures >= MaxConsecutiveFailures)
            {
                RemoveSweep(pawn, $"{station} gave no job {failures} times running");
                return;
            }

            consecutiveFailures[pawn] = failures;
            workstationRetryAt[pawn] = Find.TickManager.TicksGame + WorkstationRetryTicks;
            Logger.Message($"{pawn.LabelShort}: no job at {station} (try {failures}), asking again in {WorkstationRetryTicks} ticks");
        }

        // The pool is built at sweep start as every target ANY pawn on the
        // order can do (2026-09-13, architecture doc section 9), so a target
        // in it may be one only a teammate can do. That is why the pawn is
        // asked here, and why a refusal leaves the target for someone else.
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
        private static bool IsRetired(SweepOrder order, LocalTargetInfo target)
        {
            return order.TargetFailures.TryGetValue(target, out int failures)
                && failures >= MaxTargetFailures;
        }

        private static void AddNewTargets(SweepOrder order, List<LocalTargetInfo> found)
        {
            if (found == null || found.Count == 0)
            {
                return;
            }

            var have = new HashSet<LocalTargetInfo>(order.SharedPool);
            foreach (LocalTargetInfo target in found)
            {
                // A retired target is found by every rescan for as long as it
                // exists, which is precisely how it got handed out forty
                // times. This is the gate that matters; the pool-walk check
                // is only there for a copy that slipped in earlier.
                if (IsRetired(order, target))
                {
                    continue;
                }

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
        // What a multi-target job actually claimed, added 2026-09-07.
        //
        // Pick Up And Haul's HaulToInventory job does not carry one thing, it
        // fills the pawn's inventory from a QUEUE and then makes a single
        // delivery run. Our pool gives out one entry per assignment, so on
        // 2026-09-07 a 285-target haul sweep with 43 pawns fell by eleven
        // across fifty-six assignments and nothing in the log said why - the
        // other stacks were claimed inside the job, invisible to us, and then
        // skipped by everyone else as "reserved". Peak that session: 225 of
        // 285 targets reserved.
        //
        // Read off vanilla's own Job fields (targetQueueA / countQueue,
        // verified against lib\Assembly-CSharp.dll), so this names no Pick Up
        // And Haul type and stays a soft dependency - the same rule
        // PuahCompat, VehicleCompat and RimWarReader follow. Empty string for
        // an ordinary single-target job, so no other sweep gains a character.
        private static string DescribeQueue(Job job)
        {
            int queued = job?.targetQueueA?.Count ?? 0;
            if (queued == 0)
            {
                return "";
            }

            int total = 0;
            if (job.countQueue != null)
            {
                for (int i = 0; i < job.countQueue.Count; i++)
                {
                    total += job.countQueue[i];
                }
            }

            return total > 0
                ? $" +{queued} queued ({total} items)"
                : $" +{queued} queued";
        }

        // Removes from the job's target queue A every target another pawn has
        // reserved, and returns how many went. Added 2026-09-13, architecture
        // doc section 14.
        //
        // WHY. WorkGiver_CleanFilth.JobOnThing (and GrowerHarvest.JobOnCell)
        // fill the queue with forced: true, which their HasJobOn* passes to
        // CanReserve as ignoreOtherReservations - so a teammate's targets get
        // in. The driver then calls ReserveAsManyAsPossible over the queue,
        // and for a player-forced job Verse.AI.ReservationManager.Reserve
        // does not skip a held target: it takes it and calls
        // EndCurrentOrQueuedJob(InterruptForced) on the holder. Read in the
        // decompiled assembly, not remembered.
        //
        // Only a queue with no countQueue. Without counts the entries are
        // independent targets and any can go; with counts (Pick Up And Haul)
        // each target is paired with a count by index, and that driver was
        // not read. CanReserve with its defaults is exactly what
        // ReserveAsManyAsPossible reserves with, and it answers true for the
        // pawn's own reservations.
        private int DropQueuedTargetsReservedByOthers(Pawn pawn, Job job)
        {
            List<LocalTargetInfo> queue = job.targetQueueA;
            if (queue == null || queue.Count == 0 || (job.countQueue != null && job.countQueue.Count > 0))
            {
                return 0;
            }

            int before = queue.Count;
            queue.RemoveAll(queued => !map.reservationManager.CanReserve(pawn, queued));
            return before - queue.Count;
        }

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
