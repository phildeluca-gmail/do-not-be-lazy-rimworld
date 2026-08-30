// using System.Collections.Generic;  // was for the EndingJobs dict, __state replaced it
using HarmonyLib;
using Verse;
using Verse.AI;
using DoNotBeLazy.Components;

namespace DoNotBeLazy.Patches
{
    // Postfix on Pawn_JobTracker.EndCurrentJob - the single point where a
    // sweep pawn's current task finishes. Every ended job is handed to
    // SweepManager.Notify_JobEnded, which owns the whole decision: resume a
    // paused pawn, chain to the next target, re-ask a workstation, or end
    // the sweep.
    //
    // Workstation continuation used to live here and was gated on
    // endedJob.def == JobDefOf.DoBill, re-asking the scanner for another job
    // on endedJob.targetA and returning before Notify_JobEnded saw it. That
    // gate is why bill sweeps stopped after a single bill (fixed
    // 2026-08-30): WorkGiver_DoBill.TryStartNewDoBillJob hands back a
    // HaulToCell rather than a DoBill whenever finished product is still on
    // the bench - the normal state right after a bill - so the haul-off's
    // end missed the branch and fell through to an unconditional
    // RemoveSweep. It now lives in SweepManager.AssignNextTask, which
    // re-asks order.WorkstationTarget whatever the pawn just finished, and
    // reaching it through Notify_JobEnded means the need-pause check runs
    // first (it did not before, so a bill sweep could out-argue a meal).
    //
    // curJob is cleared (and can already be replaced by a new job) partway
    // through EndCurrentJob's own body, so the Prefix captures it before
    // that happens and the Postfix reads it back via Harmony's __state.
    // Only its existence is read now - a null curJob means there was no job
    // to end and so nothing for a sweep to react to - but the capture is
    // still the only way to know that from a postfix.
    //
    // (was a static Dictionary<Pawn_JobTracker, Job> keyed on the instance -
    // EndCurrentJob nests, so an inner call overwrote and then removed the
    // outer call's entry and the outer postfix saw nothing. __state is
    // per-call so nesting is a non-issue, and nothing leaks if a postfix
    // never runs.)
    //
    // Depends on SweepManager (Phase 3), which must expose:
    //   bool TryGetActiveSweep(Pawn pawn, out SweepOrder order)
    //   void Notify_JobEnded(Pawn pawn, JobCondition condition)
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class JobTrackerPatch
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(Pawn_JobTracker __instance, out Job __state)
        {
            __state = __instance.curJob;
        }

        public static void Postfix(JobCondition condition, Pawn ___pawn, Job __state)
        {
            // we're inside our own TryTakeOrderedJob - this job end is the
            // interrupt we caused, not the pawn finishing something. Acting
            // on it cancels the sweep we're in the middle of handing out.
            if (SweepManager.AssigningJob)
            {
                return;
            }

            Job endedJob = __state;

            Pawn pawn = ___pawn;
            if (pawn == null || endedJob == null)
            {
                return;
            }

            Map map = pawn.Map;
            if (map == null)
            {
                return;
            }

            SweepManager sweepManager = map.GetComponent<SweepManager>();

            // cheap early-out: EndCurrentJob fires for every pawn on the map
            // and almost none of them are sweeping. Notify_JobEnded does the
            // same lookup, so this only saves the call.
            if (sweepManager == null || !sweepManager.TryGetActiveSweep(pawn, out _))
            {
                return;
            }

            sweepManager.Notify_JobEnded(pawn, condition);
        }
    }
}
