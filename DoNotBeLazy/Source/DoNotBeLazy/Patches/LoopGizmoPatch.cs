using System.Collections.Generic;
using DoNotBeLazy.Components;
using DoNotBeLazy.Jobs;
using DoNotBeLazy.Utility;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Logger = DoNotBeLazy.Core.Logger;

namespace DoNotBeLazy.Patches
{
    // Patches Verse.Pawn.GetGizmos to add a "Find job loop" button whenever
    // JobLoopWatch has caught something. Postfix, per CLAUDE.md 4, and
    // modelled on RimWar Odds' PawnGizmoPatch which does the same thing.
    //
    // Ordered 2026-09-07: "intercept the standing still and give me a command
    // button to find the problematic bedroll." RimWorld names a looping
    // target only by thing id, and nothing in the game turns an id into a
    // place - the first one had to be found by reading the save file.
    //
    // Every selected pawn yields one of these, so groupKey collapses them
    // into a single button. The action reads the watch list rather than the
    // pawn it came from, so which merged copy was clicked does not matter.
    //
    // Added 2026-09-27: a second, unrelated gizmo on the same postfix -
    // "Drop everything" (dnbl-architecture.md section 22 subsection "Drop
    // everything"). Its own groupKey/label/icon keep it from merging with
    // the "Find job loop" button above.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class LoopGizmoPatch
    {
        private const int GroupKey = 0x444E424C; // "DNBL"
        private const int DropEverythingGroupKey = 0x44524F50; // "DROP"

        private static int cycle;

        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
        {
            foreach (Gizmo gizmo in gizmos)
            {
                yield return gizmo;
            }

            // "Drop everything" - not drafted-restricted, the same as
            // vanilla's own apparel/equipment/inventory drop buttons in the
            // gear tab, which work whether or not the pawn is drafted.
            // Colonist only (Pawn.IsColonist, the same gate CompDnblCargo
            // uses), spawned, and alive.
            if (__instance != null && __instance.IsColonist && __instance.Spawned && !__instance.Dead)
            {
                yield return new Command_Action
                {
                    defaultLabel = "Drop everything",
                    defaultDesc = "Drop every carried item, worn apparel piece and the weapon. "
                        + "Locked apparel and warcaskets are left where they are.",
                    icon = TexButton.Drop,
                    groupKey = DropEverythingGroupKey,
                    action = () => QueueDropEverything(__instance)
                };
            }

            if (JobLoopWatch.SuspectCount == 0)
            {
                yield break;
            }

            if (__instance == null || __instance.Faction != Faction.OfPlayer || !__instance.Spawned)
            {
                yield break;
            }

            int count = JobLoopWatch.SuspectCount;

            yield return new Command_Action
            {
                defaultLabel = count == 1 ? "Find job loop" : "Find job loop (" + count + ")",
                defaultDesc = Describe(),
                icon = ContentFinder<Texture2D>.Get("UI/Buttons/OpenStatsReport", false),
                groupKey = GroupKey,
                action = JumpToNext
            };
        }

        // Builds this one pawn's own drop queue - dnbl-architecture.md
        // section 22 subsection "Drop everything". Called once per
        // selected pawn: GizmoGridDrawer merges same-groupKey gizmos into
        // one button for display, but still calls ProcessInput (and so
        // this action) on every merged gizmo when clicked, by default
        // (Gizmo.alsoClickIfOtherInGroupClicked defaults true - verified
        // against lib\Assembly-CSharp.dll), which is how the existing
        // "Find job loop" button above already relies on one closure per
        // pawn. That is what gives each selected pawn its own queue rather
        // than the click firing once for the whole selection.
        //
        // Order follows Pawn_JobTracker.TryTakeOrderedJob's own contract
        // (verified against lib\Assembly-CSharp.dll): the first call per
        // pawn passes requestQueueing: false, which replaces the pawn's
        // current job; every later call passes requestQueueing: true,
        // which appends instead of clearing what was just queued.
        //
        // Fixed 2026-09-28, Panna: the active DNBL sweep was never told
        // about the drop. Reported live: TryTakeOrderedJob's own
        // requestQueueing: false branch calls jobQueue.Clear() before
        // enqueueing (verified in lib\Assembly-CSharp.dll) - harmless the
        // first time since the queue is empty, but the FIRST drop job
        // (say, one worn apparel piece) then succeeds like any ordinary
        // job, and SweepManager.Notify_JobEnded does not check whether the
        // job that just succeeded was its own before calling
        // AssignNextTask - a sweep this mod still thinks is active reacts
        // to ANY Succeeded job ending as "task finished, hand out the
        // next one." That next GiveJob call is itself a
        // requestQueueing: false TryTakeOrderedJob, and its jobQueue.Clear()
        // wiped out the rest of the drop queue (the remaining apparel,
        // the weapon, all nine inventory stacks) before any of it ran -
        // seen live as the sweep handing Panna a fresh DNBL_StuffAndHaul
        // job for the next haul target twelve minutes after the button
        // was pressed, with no apparel ever removed and no
        // "dropped ... (drop everything)" line ever printed for her.
        // Fixed by ending the pawn's sweep - running, paused or queued -
        // through SweepManager.ClearSweeps before any job is queued, the
        // same ending path and message every other sweep-ending use
        // already goes through. With the sweep gone, Notify_JobEnded is
        // never called for these jobs (TryGetActiveSweep returns false),
        // so nothing hands Panna a replacement job or clears her queue
        // out from under her. See dnbl-architecture.md section 22.
        private static void QueueDropEverything(Pawn pawn)
        {
            if (pawn?.jobs == null)
            {
                return;
            }

            pawn.Map?.GetComponent<SweepManager>()?.ClearSweeps(pawn, "drop everything pressed");

            // ORDER lines, 2026-10-02 - see SweepManager.NextOrderId. The
            // started line is written when the first queued job is accepted
            // and the ended line by PollDropOrders, because the queued jobs
            // run in vanilla drivers that give no hook of their own.
            string orderId = SweepManager.NextOrderId();
            Logger.Order($"ORDER {orderId} issued: {pawn.LabelShort} drop everything ({Logger.Keys()})");

            bool firstJob = true;
            bool startedLogged = false;
            int apparelQueued = 0, apparelLocked = 0, apparelWarcasket = 0;
            int weaponQueued = 0;
            int inventoryQueued = 0;

            if (pawn.apparel != null)
            {
                // Snapshot - Pawn_ApparelTracker.WornApparel is the
                // tracker's own live list, and nothing removes from it
                // until a queued RemoveApparel job actually runs, well
                // after this loop finishes.
                foreach (Apparel apparel in new List<Apparel>(pawn.apparel.WornApparel))
                {
                    if (pawn.apparel.IsLocked(apparel))
                    {
                        apparelLocked++;
                        NameAndSkip(pawn, apparel, "locked");
                        continue;
                    }

                    if (WarcasketCompat.IsWarcasket(apparel))
                    {
                        apparelWarcasket++;
                        NameAndSkip(pawn, apparel, "a warcasket");
                        continue;
                    }

                    string apparelLabel = apparel.LabelCap;
                    Job job = JobMaker.MakeJob(JobDefOf.RemoveApparel, apparel);
                    // RemoveApparel and DropEquipment are vanilla drivers and
                    // log nothing when they run, and no patch is added for
                    // them, so the record is made here, per item, at the
                    // moment it is queued or refused. Added 2026-10-02 - the
                    // inventory drops already log in JobDriver_DropInventoryStack.
                    if (pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, requestQueueing: !firstJob))
                    {
                        firstJob = false;
                        apparelQueued++;
                        if (!startedLogged)
                        {
                            startedLogged = true;
                            Logger.Order($"ORDER {orderId} started: {pawn.LabelShort} {job.def.defName} {job.targetA}");
                        }
                        Logger.Message($"{pawn.LabelShort}: drop everything - {apparelLabel} queued to be taken off");
                    }
                    else
                    {
                        Logger.Message($"{pawn.LabelShort}: drop everything - {apparelLabel} NOT queued, the game refused the remove-apparel order");
                    }
                }
            }

            if (pawn.equipment?.Primary != null)
            {
                string weaponLabel = pawn.equipment.Primary.LabelCap;
                Job job = JobMaker.MakeJob(JobDefOf.DropEquipment, pawn.equipment.Primary);
                if (pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, requestQueueing: !firstJob))
                {
                    firstJob = false;
                    weaponQueued++;
                    if (!startedLogged)
                    {
                        startedLogged = true;
                        Logger.Order($"ORDER {orderId} started: {pawn.LabelShort} {job.def.defName} {job.targetA}");
                    }
                    Logger.Message($"{pawn.LabelShort}: drop everything - {weaponLabel} queued to be dropped");
                }
                else
                {
                    Logger.Message($"{pawn.LabelShort}: drop everything - {weaponLabel} NOT queued, the game refused the drop-equipment order");
                }
            }

            if (pawn.inventory != null)
            {
                // Snapshot for the same reason as WornApparel above - the
                // container is not touched until each queued job runs.
                foreach (Thing thing in new List<Thing>(pawn.inventory.innerContainer))
                {
                    Job job = JobMaker.MakeJob(DnblJobDefOf.DropInventoryStack, thing);
                    if (pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, requestQueueing: !firstJob))
                    {
                        firstJob = false;
                        inventoryQueued++;
                        if (!startedLogged)
                        {
                            startedLogged = true;
                            Logger.Order($"ORDER {orderId} started: {pawn.LabelShort} {job.def.defName} {job.targetA}");
                        }
                    }
                }
            }

            int totalQueued = apparelQueued + weaponQueued + inventoryQueued;
            string outcome = totalQueued == 0
                ? "nothing to drop"
                : $"apparel {apparelQueued} queued ({apparelLocked} locked, {apparelWarcasket} warcasket skipped), "
                    + $"weapon {weaponQueued} queued, inventory {inventoryQueued} queued";
            Logger.Message($"{pawn.LabelShort}: drop everything pressed - {outcome}");

            if (totalQueued == 0)
            {
                Logger.Order($"ORDER {orderId} ended (failed to start: nothing to drop or every order refused): {pawn.LabelShort} drop everything");
            }
            else
            {
                pendingDrops[pawn] = orderId;
            }
        }

        // Drop everything orders still waiting for their queue to run out.
        // Ended by PollDropOrders, called from NeedMonitor.GameComponentTick
        // every 60 ticks - the queued jobs are vanilla's RemoveApparel and
        // DropEquipment plus our own DropInventoryStack, and the first two
        // have no hook for an ending short of a patch. The reason is the
        // best that can be known from the queue: it drained (finished, or
        // another order cleared it - the two cannot be told apart here).
        private static readonly Dictionary<Pawn, string> pendingDrops = new Dictionary<Pawn, string>();

        public static void PollDropOrders()
        {
            if (pendingDrops.Count == 0)
            {
                return;
            }

            var done = new List<Pawn>();
            foreach (KeyValuePair<Pawn, string> pair in pendingDrops)
            {
                Pawn pawn = pair.Key;
                string reason = null;
                if (pawn == null || pawn.Destroyed || pawn.Dead)
                {
                    reason = "pawn dead or gone";
                }
                else if (pawn.Downed)
                {
                    reason = "pawn downed";
                }
                else
                {
                    Job cur = pawn.jobs?.curJob;
                    bool queueEmpty = pawn.jobs?.jobQueue == null || pawn.jobs.jobQueue.Count == 0;
                    bool onDropJob = cur != null && cur.def != null
                        && (cur.def == JobDefOf.RemoveApparel || cur.def == JobDefOf.DropEquipment || cur.def == DnblJobDefOf.DropInventoryStack);
                    if (queueEmpty && !onDropJob)
                    {
                        reason = "queue drained - finished, or another order cleared it";
                    }
                }

                if (reason != null)
                {
                    Logger.Order($"ORDER {pair.Value} ended ({reason}): {pawn?.LabelShort} drop everything");
                    done.Add(pawn);
                }
            }

            foreach (Pawn pawn in done)
            {
                pendingDrops.Remove(pawn);
            }
        }

        // One message naming the pawn and the item, per dnbl-architecture.md
        // section 22 ("throw a message; applies to warcaskets as well") -
        // also logged, per CLAUDE.md 4 ("a message shown to the player is
        // not a log line" on its own).
        private static void NameAndSkip(Pawn pawn, Apparel apparel, string reason)
        {
            string text = $"{pawn.LabelShort}: leaving {apparel.LabelCap} - {reason}.";
            Messages.Message(text, pawn, MessageTypeDefOf.RejectInput, false);
            Logger.Message(text);
        }

        private static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Things a pawn has been handed over and over in a few ticks - the usual cause of a colonist standing still.");
            sb.AppendLine();

            IReadOnlyList<Thing> suspects = JobLoopWatch.Suspects;
            for (int i = 0; i < suspects.Count; i++)
            {
                Thing t = suspects[i];
                sb.AppendLine(JobLoopWatch.DescribeThing(t) + " - from " + JobLoopWatch.BlameFor(t));
            }

            sb.AppendLine();
            sb.Append("Click to jump to each in turn. This only reports; nothing is changed.");
            return sb.ToString();
        }

        // Cycles rather than always jumping to the first, so two loops can
        // both be reached from the one button.
        private static void JumpToNext()
        {
            IReadOnlyList<Thing> suspects = JobLoopWatch.Suspects;
            if (suspects.Count == 0)
            {
                return;
            }

            for (int attempt = 0; attempt < suspects.Count; attempt++)
            {
                Thing t = suspects[cycle % suspects.Count];
                cycle++;

                if (t == null || t.Destroyed)
                {
                    continue;
                }

                // A minified or carried thing has no map position of its own;
                // jump to whatever is holding it instead, which is what the
                // player can actually click on.
                Thing shown = t.SpawnedOrAnyParentSpawned ? t : null;
                if (shown == null)
                {
                    Messages.Message(
                        "Do Not Be Lazy: " + JobLoopWatch.DescribeThing(t) + " - nothing on the map to jump to.",
                        MessageTypeDefOf.RejectInput, false);
                    return;
                }

                Messages.Message(
                    "Do Not Be Lazy: " + JobLoopWatch.DescribeThing(t) + " - from " + JobLoopWatch.BlameFor(t),
                    MessageTypeDefOf.NeutralEvent, false);

                CameraJumper.TryJumpAndSelect(new GlobalTargetInfo(t));
                return;
            }
        }
    }
}
