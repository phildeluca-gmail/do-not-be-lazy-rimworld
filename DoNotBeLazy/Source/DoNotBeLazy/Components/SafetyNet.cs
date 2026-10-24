using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using DoNotBeLazy.Core;

namespace DoNotBeLazy.Components
{
    // Section 28 (ordered 2026-10-22): a safety net for colonists who are NOT
    // on a `*` order. Their food, sleep and recreation have their own
    // thresholds (safety*Threshold in the settings). Below one, the pawn is
    // sent to eat, to sleep or to recreation, by the same calls the need
    // pause of an order pawn uses: NeedMonitor.FindMealDroppingCargo,
    // vanilla's JobGiver_GetRest and vanilla's JobGiver_GetJoy. No Harmony
    // patch: NeedMonitor.GameComponentTick calls Run().
    //
    // Interrupts anything except: drafted, downed, a baby, in a mental state,
    // carrying another pawn, already in bed / eating / on recreation, an
    // emergency job (firefighting, doctoring, rescue, fleeing, fighting) and
    // a job the player forced (a queued player order counts as one). Every
    // job it hands out goes through SweepManager.GiveJob with a forced kind,
    // so the ORDER issued / started / ended-with-reason lines exist for each
    // intervention.
    public static class SafetyNet
    {
        // One pass at most this often (the pass itself walks every colonist).
        // NeedMonitor calls Run every 60 ticks, so in practice a pass runs
        // every fifth call, every 300 ticks.
        public const int PassIntervalTicks = 250;

        // After sending a pawn, leave it alone this long even if the forced
        // job ends early - keeps a job that does not raise its need from
        // being re-issued in a loop. After a decline (no food, no bed) the
        // wait is longer, since nothing has changed.
        private const int AfterSendQuietTicks = 600;
        private const int AfterDeclineQuietTicks = 2500;

        private static int lastPassTick = -PassIntervalTicks;
        private static readonly Dictionary<Pawn, int> quietUntil = new Dictionary<Pawn, int>();
        private static readonly Dictionary<Pawn, HashSet<string>> lastDecline = new Dictionary<Pawn, HashSet<string>>();

        // Section 29 (2026-10-23). A pawn below a threshold whom the net
        // skips: one line per pawn when it starts, again when the reason
        // changes (no sooner than SkipLogReasonChangeTicks) or after
        // SkipLogRepeatTicks (one game hour, the same length as
        // AfterDeclineQuietTicks). Cleared when the pawn is no longer low or
        // is sent.
        private const int SkipLogRepeatTicks = 2500;
        // a changed reason may log sooner, but not every pass - stops two alternating reasons logging every 250 ticks
        private const int SkipLogReasonChangeTicks = 1000;
        private struct SkipNote
        {
            public string Reason;
            public int Tick;
        }
        private static readonly Dictionary<Pawn, SkipNote> skipLogged = new Dictionary<Pawn, SkipNote>();

        // Section 29: needs already alerted for a drafted pawn ("Food",
        // "Rest"). A need leaves the set when it recovers; the pawn leaves it
        // when undrafted, so a redraft alerts again.
        private static readonly Dictionary<Pawn, HashSet<string>> draftedAlerted = new Dictionary<Pawn, HashSet<string>>();

        // Called from the NeedMonitor constructor: a new or loaded game must
        // not inherit the last game's pass tick, timers or Pawn references.
        public static void Reset()
        {
            lastPassTick = -PassIntervalTicks;
            quietUntil.Clear();
            lastDecline.Clear();
            skipLogged.Clear();
            draftedAlerted.Clear();
            SweepManager.ResetModIssuedJobs();
        }

        public static void Run()
        {
            DoNotBeLazySettings settings = DoNotBeLazyMod.Settings;
            if (settings == null)
            {
                return;
            }

            int now = Find.TickManager.TicksGame;
            if (now - lastPassTick < PassIntervalTicks)
            {
                return;
            }

            lastPassTick = now;

            foreach (Map map in Find.Maps)
            {
                SweepManager sweepManager = map.GetComponent<SweepManager>();
                List<Pawn> colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
                foreach (Pawn pawn in colonists)
                {
                    try
                    {
                        CheckPawn(pawn, sweepManager, settings, now);
                    }
                    catch (Exception ex)
                    {
                        // one pawn's failure never stops the rest of the pass
                        if (!quietUntil.TryGetValue(pawn, out int until) || now >= until)
                        {
                            quietUntil[pawn] = now + AfterDeclineQuietTicks;
                            Logger.Warning($"{pawn.LabelShort}: safety net check failed, skipping this pawn: {ex}");
                        }
                    }
                }
            }
        }

        private static bool Low(Need need, float threshold)
        {
            return need != null && threshold > 0f && need.CurLevelPercentage <= threshold;
        }

        private static void CheckPawn(Pawn pawn, SweepManager sweepManager, DoNotBeLazySettings settings, int now)
        {
            if (pawn.needs == null || pawn.jobs == null || !pawn.Spawned)
            {
                return;
            }

            // On a * order (active, paused or queued): the order's own need
            // pause owns this pawn.
            if (sweepManager != null && sweepManager.TryGetActiveSweep(pawn, out SweepOrder _))
            {
                lastDecline.Remove(pawn);
                skipLogged.Remove(pawn);
                draftedAlerted.Remove(pawn);
                return;
            }

            Need food = pawn.needs.food;
            Need rest = pawn.needs.rest;
            Need joy = pawn.needs.joy;
            bool lowFood = Low(food, settings.safetyHungerThreshold);
            bool lowRest = Low(rest, settings.safetySleepThreshold);
            bool lowJoy = Low(joy, settings.safetyRecreationThreshold);
            AlertIfDrafted(pawn, lowFood, lowRest, food, rest, settings);
            if (!lowFood && !lowRest && !lowJoy)
            {
                lastDecline.Remove(pawn);
                quietUntil.Remove(pawn);
                skipLogged.Remove(pawn);
                return;
            }

            if (quietUntil.TryGetValue(pawn, out int until) && now < until)
            {
                return;
            }

            // Babies are fed and put down by their carers, not sent.
            string skip = pawn.Dead ? "dead"
                : pawn.Drafted ? "drafted"
                : pawn.Downed ? "downed"
                : pawn.InMentalState ? "in a mental state"
                : pawn.IsWildMan() ? "a wild man"
                : pawn.DevelopmentalStage.Baby() ? "a baby"
                : null;
            if (skip != null)
            {
                NoteSkipped(pawn, skip, now);
                return;
            }

            // Carrying another pawn (a rescue, a patient, a prisoner) is
            // never interrupted.
            if (pawn.carryTracker?.CarriedThing is Pawn)
            {
                NoteSkipped(pawn, "carrying another pawn", now);
                return;
            }

            Job curJob = pawn.jobs.curJob;
            if (curJob != null)
            {
                // Section 30: the player's own order, or a queued one, always
                // stands. playerForced is also set on every job this mod hands
                // out (GiveJob), so a forced job counts only when the mod did
                // not issue it; the queue counts only when it holds such a job.
                bool playerOrder = curJob.playerForced && !SweepManager.IsModIssued(curJob);
                int queuedPlayerOrders = QueuedPlayerOrders(pawn);
                if (playerOrder || queuedPlayerOrders > 0)
                {
                    NoteSkipped(pawn, playerOrder
                        ? $"player-forced job ({SweepManager.DescribeJob(curJob)})"
                        : $"queued jobs ({queuedPlayerOrders} player order(s) waiting)", now);
                    return;
                }

                // already eating (only while the food can still be eaten),
                // in bed or on recreation
                if (curJob.def == JobDefOf.Ingest)
                {
                    string stale = StaleIngestReason(pawn, curJob);
                    if (stale == null)
                    {
                        return;
                    }

                    // fall through: the meal is gone, forbidden or out of reach
                    // throttled with the skip log: once per reason per pawn
                    string staleKey = "stale ingest: " + stale;
                    if (!skipLogged.TryGetValue(pawn, out SkipNote lastStale)
                        || lastStale.Reason != staleKey
                        || now - lastStale.Tick >= SkipLogRepeatTicks)
                    {
                        skipLogged[pawn] = new SkipNote { Reason = staleKey, Tick = now };
                        Logger.Message($"safety net: {pawn.LabelShort} is on Ingest but {stale} - not counted as eating ({NeedMonitor.LevelsText(pawn)})");
                    }
                }
                else if (curJob.def == JobDefOf.LayDown || curJob.def.joyKind != null)
                {
                    return;
                }

                // Answer 2a: firefighting, doctoring, rescue, fleeing and
                // fighting are never interrupted.
                if (IsEmergencyJob(curJob))
                {
                    NoteSkipped(pawn, $"emergency job ({SweepManager.DescribeJob(curJob)})", now);
                    return;
                }
            }

            if (pawn.InBed())
            {
                return;
            }

            Lord lord = pawn.GetLord();
            if (lord != null && lord.CurLordToil != null && !lord.CurLordToil.AllowSatisfyLongNeeds)
            {
                NoteSkipped(pawn, $"lord duty {lord.CurLordToil.GetType().Name} forbids long needs", now);
                return;
            }

            if (pawn.roping != null && pawn.roping.IsRoped)
            {
                NoteSkipped(pawn, "roped", now);
                return;
            }

            // Hunger first, then sleep, then recreation - the order the
            // need pause reports them in. A need that cannot be served is
            // declined (logged) and the next low need is tried.
            string levels = NeedMonitor.LevelsText(pawn);
            string interrupted = curJob == null ? "" : $" (interrupting {SweepManager.DescribeJob(curJob)})";

            if (lowFood && TrySendToEat(pawn, food, settings, levels, interrupted, now))
            {
                return;
            }

            if (lowRest && TrySendToSleep(pawn, rest, settings, levels, interrupted, now))
            {
                return;
            }

            if (lowJoy)
            {
                TrySendToRecreation(pawn, joy, settings, levels, interrupted, now);
            }
        }

        // Section 30: queued jobs that are a player's order (player-forced,
        // not issued by this mod).
        private static int QueuedPlayerOrders(Pawn pawn)
        {
            int count = 0;
            for (int i = 0; i < pawn.jobs.jobQueue.Count; i++)
            {
                Job queued = pawn.jobs.jobQueue[i].job;
                if (queued != null && queued.playerForced && !SweepManager.IsModIssued(queued))
                {
                    count++;
                }
            }

            return count;
        }

        // Section 30, fix 1.3: null while the Ingest job's food is still
        // usable; otherwise why it is not. A food target the pawn carries or
        // holds is always usable. The line this feeds is not throttled by
        // itself, but the pass is 250 ticks and a sent pawn waits 600.
        private static string StaleIngestReason(Pawn pawn, Job ingest)
        {
            Thing food = ingest.targetA.Thing;
            if (food == null || food.Destroyed)
            {
                return "its food is gone";
            }

            if (!food.Spawned)
            {
                return null;
            }

            if (food.IsForbidden(pawn))
            {
                return $"{food.LabelShort} is forbidden";
            }

            if (!pawn.CanReach(food, PathEndMode.Touch, Danger.Deadly))
            {
                return $"{food.LabelShort} cannot be reached";
            }

            return null;
        }

        // Section 29: one throttled line when a pawn below a threshold is
        // skipped, stating his levels.
        private static void NoteSkipped(Pawn pawn, string reason, int now)
        {
            // the job text carries a changing target; key on the part before " ("
            int paren = reason.IndexOf(" (", StringComparison.Ordinal);
            string key = paren > 0 ? reason.Substring(0, paren) : reason;
            if (skipLogged.TryGetValue(pawn, out SkipNote last))
            {
                int gap = now - last.Tick;
                // same reason: hold the full repeat gap
                if (last.Reason == key && gap < SkipLogRepeatTicks)
                {
                    return;
                }

                // reason changed: hold the shorter change gap
                if (last.Reason != key && gap < SkipLogReasonChangeTicks)
                {
                    return;
                }
            }

            skipLogged[pawn] = new SkipNote { Reason = key, Tick = now };
            Logger.Message($"safety net: {pawn.LabelShort} is below a need threshold but was skipped - {reason} ({NeedMonitor.LevelsText(pawn)})");
        }

        // Section 29: alert only. A drafted pawn whose food or rest is at or
        // below its safety threshold gets one on-screen message per need
        // (and a log line). Never undrafts, never gives a job. Verified by
        // reflection 2026-10-23: Messages.Message(string, LookTargets,
        // MessageTypeDef, bool); MessageTypeDefOf.NegativeEvent exists.
        private static void AlertIfDrafted(Pawn pawn, bool lowFood, bool lowRest, Need food, Need rest, DoNotBeLazySettings settings)
        {
            if (!pawn.Drafted)
            {
                draftedAlerted.Remove(pawn);
                return;
            }

            if (!draftedAlerted.TryGetValue(pawn, out HashSet<string> done))
            {
                done = new HashSet<string>();
                draftedAlerted[pawn] = done;
            }

            AlertNeed(pawn, done, "Food", lowFood, food, settings.safetyHungerThreshold);
            AlertNeed(pawn, done, "Rest", lowRest, rest, settings.safetySleepThreshold);
        }

        private static void AlertNeed(Pawn pawn, HashSet<string> done, string need, bool low, Need level, float threshold)
        {
            if (!low)
            {
                done.Remove(need);
                return;
            }

            if (!done.Add(need))
            {
                return;
            }

            string text = $"{pawn.LabelShort} is drafted and {need} is at {level.CurLevelPercentage:P0} (at or below {threshold:P0}). Not sent - undraft to let the safety net act.";
            Messages.Message(text, pawn, MessageTypeDefOf.NegativeEvent, false);
            // Logger.Order, not Message: a message shown to the player is logged whether or not verbose logging is on.
            Logger.Order($"safety net: ALERT {text} ({NeedMonitor.LevelsText(pawn)})");
        }

        // Jobs that mark an emergency, beyond workGiverDef.emergency (the
        // flag the order pause's IsOrdinaryWork also reads: the firefighting,
        // doctoring and rescue work givers carry it).
        private static readonly HashSet<JobDef> EmergencyJobDefs = new HashSet<JobDef>
        {
            JobDefOf.Flee,
            JobDefOf.FleeAndCower,
            JobDefOf.FleeAndCowerShort,
            JobDefOf.ExtinguishSelf,
            JobDefOf.ExtinguishFiresNearby,
            JobDefOf.BeatFire,
            JobDefOf.AttackMelee,
            JobDefOf.AttackStatic,
            JobDefOf.Wait_Combat,
            JobDefOf.Rescue,
            JobDefOf.TendPatient,
            JobDefOf.Arrest,
            JobDefOf.Capture,
            // carrying a meal to someone else; the meal is never dropped
            // (section 28 item 4, manager's reading of "doctoring exempt")
            JobDefOf.FeedPatient,
            JobDefOf.DeliverFood
        };

        private static bool IsEmergencyJob(Job job)
        {
            WorkTypeDef workType = job.workGiverDef?.workType;
            return (job.workGiverDef != null && job.workGiverDef.emergency)
                || workType == WorkTypeDefOf.Doctor
                || workType == WorkTypeDefOf.Firefighter
                || EmergencyJobDefs.Contains(job.def);
        }

        // LOCKED (dnbl H8) - passed 5x in play 2026-10-23; do not change without the user's explicit order.
        private static bool TrySendToEat(Pawn pawn, Need food, DoNotBeLazySettings settings, string levels, string interrupted, int now)
        {
            Job meal = NeedMonitor.FindMealDroppingCargo(pawn, out int candidates, out float moodEffect, out string declineReason, out string choiceNote, out string dropped);
            if (meal == null)
            {
                Decline(pawn, "Food", declineReason, levels, now);
                return false;
            }

            if (SweepManager.GiveJob(pawn, meal, false, "safety net meal"))
            {
                Sent(pawn, now, "Food", $"safety net: {pawn.LabelShort} Food {food.CurLevelPercentage:P0} is at or below {settings.safetyHungerThreshold:P0} - sent to eat {meal.targetA.Thing?.LabelShort ?? meal.def.defName} (mood effect {moodEffect:F0}, {candidates} candidate{(candidates == 1 ? "" : "s")}; {choiceNote}){dropped}{interrupted} ({levels})");
                return true;
            }

            Decline(pawn, "Food", "the game refused the Ingest job", levels, now);
            return false;
        }

        // Answer 2c (2026-10-22): "carried bag or bed or ground". Vanilla's
        // JobGiver_GetRest, called through the public TryIssueJobPackage as
        // Be Lazy does (bl-architecture.md 3.7), returns a bed LayDown; where
        // another mod has patched TryGiveJob to use a carried bedroll, that
        // mod's job; and with no bed a LayDown on a ground cell (verified by
        // reflection 2026-10-22: JobGiver_GetRest declares
        // TryFindGroundSleepSpotFor, the fallback TryGiveJob takes when no
        // bed is found). No other mod is named here.
        // LOCKED (dnbl H6) - passed 5x in play 2026-10-22; do not change without the user's explicit order.
        private static bool TrySendToSleep(Pawn pawn, Need rest, DoNotBeLazySettings settings, string levels, string interrupted, int now)
        {
            string why = RestRefusal(pawn);
            Job sleepJob = null;
            if (why == null)
            {
                sleepJob = new JobGiver_GetRest().TryIssueJobPackage(pawn, default(JobIssueParams)).Job;
                if (sleepJob == null)
                {
                    why = "vanilla's rest giver found no bed, bedroll or ground spot";
                }
            }

            if (why != null)
            {
                Decline(pawn, "Rest", why, levels, now);
                return false;
            }

            string where = sleepJob.targetA.Thing != null ? sleepJob.targetA.Thing.LabelShort
                : sleepJob.def == JobDefOf.LayDown ? "the ground at " + sleepJob.targetA.Cell
                : sleepJob.def.defName;
            if (SweepManager.GiveJob(pawn, sleepJob, false, "safety net rest"))
            {
                Sent(pawn, now, "Rest", $"safety net: {pawn.LabelShort} Rest {rest.CurLevelPercentage:P0} is at or below {settings.safetySleepThreshold:P0} - sent to {sleepJob.def.defName} on {where}{interrupted} ({levels})");
                return true;
            }

            Decline(pawn, "Rest", "the game refused the sleep job", levels, now);
            return false;
        }

        // LOCKED (dnbl H5) - passed 5x in play 2026-10-22; do not change without the user's explicit order.
        private static bool TrySendToRecreation(Pawn pawn, Need joy, DoNotBeLazySettings settings, string levels, string interrupted, int now)
        {
            Job joyJob = SweepManager.FindVanillaJoyJob(pawn);
            if (joyJob == null)
            {
                Decline(pawn, "Recreation", "vanilla's recreation giver found no job", levels, now);
                return false;
            }

            if (SweepManager.GiveJob(pawn, joyJob, false, "safety net recreation"))
            {
                Sent(pawn, now, "Recreation", $"safety net: {pawn.LabelShort} Recreation {joy.CurLevelPercentage:P0} is at or below {settings.safetyRecreationThreshold:P0} - sent to {joyJob.def.defName}{interrupted} ({levels})");
                return true;
            }

            Decline(pawn, "Recreation", "the game refused the recreation job", levels, now);
            return false;
        }

        // The gates TryForceRestIfStuck applies before a forced LayDown.
        private static string RestRefusal(Pawn pawn)
        {
            if (RestUtility.DisturbancePreventsLyingDown(pawn))
            {
                return "something nearby (a fire or a hostile) stops it lying down";
            }

            Lord lord = pawn.GetLord();
            if (lord != null && lord.CurLordToil != null && !lord.CurLordToil.AllowRestingInBed)
            {
                return $"under a lord whose current duty ({lord.CurLordToil.GetType().Name}) does not allow resting in bed";
            }

            return null;
        }

        private static void Sent(Pawn pawn, int now, string need, string line)
        {
            quietUntil[pawn] = now + AfterSendQuietTicks;
            skipLogged.Remove(pawn);
            if (lastDecline.TryGetValue(pawn, out HashSet<string> declined))
            {
                declined.RemoveWhere(k => k.StartsWith(need + ":"));
            }

            Logger.Message(line);
        }

        // One line per distinct reason per need while the need stays low;
        // cleared when the pawn recovers or is sent for that need. A decline
        // never shortens a wait that a send earlier in the pass set.
        private static void Decline(Pawn pawn, string need, string reason, string levels, int now)
        {
            if (!quietUntil.TryGetValue(pawn, out int until) || until < now + AfterDeclineQuietTicks)
            {
                quietUntil[pawn] = now + AfterDeclineQuietTicks;
            }

            if (!lastDecline.TryGetValue(pawn, out HashSet<string> declined))
            {
                declined = new HashSet<string>();
                lastDecline[pawn] = declined;
            }

            if (!declined.Add(need + ": " + NeedMonitor.DeclineKey(reason)))
            {
                return;
            }

            Logger.Message($"safety net: {pawn.LabelShort} is low on {need} but was not sent - {reason} ({levels})");
        }
    }
}
