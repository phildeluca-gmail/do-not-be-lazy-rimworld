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
        private static void QueueDropEverything(Pawn pawn)
        {
            if (pawn?.jobs == null)
            {
                return;
            }

            bool firstJob = true;
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

                    Job job = JobMaker.MakeJob(JobDefOf.RemoveApparel, apparel);
                    if (pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, requestQueueing: !firstJob))
                    {
                        firstJob = false;
                        apparelQueued++;
                    }
                }
            }

            if (pawn.equipment?.Primary != null)
            {
                Job job = JobMaker.MakeJob(JobDefOf.DropEquipment, pawn.equipment.Primary);
                if (pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, requestQueueing: !firstJob))
                {
                    firstJob = false;
                    weaponQueued++;
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
                    }
                }
            }

            int totalQueued = apparelQueued + weaponQueued + inventoryQueued;
            string outcome = totalQueued == 0
                ? "nothing to drop"
                : $"apparel {apparelQueued} queued ({apparelLocked} locked, {apparelWarcasket} warcasket skipped), "
                    + $"weapon {weaponQueued} queued, inventory {inventoryQueued} queued";
            Logger.Message($"{pawn.LabelShort}: drop everything pressed - {outcome}");
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
