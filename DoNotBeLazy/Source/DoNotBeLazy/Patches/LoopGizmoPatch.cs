using System.Collections.Generic;
using DoNotBeLazy.Components;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

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
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class LoopGizmoPatch
    {
        private const int GroupKey = 0x444E424C; // "DNBL"

        private static int cycle;

        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
        {
            foreach (Gizmo gizmo in gizmos)
            {
                yield return gizmo;
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
