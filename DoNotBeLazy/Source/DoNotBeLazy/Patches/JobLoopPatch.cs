using DoNotBeLazy.Components;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Patches
{
    // Patches Verse.AI.Pawn_JobTracker.StartJob so JobLoopWatch can see every
    // job a pawn is given, whoever gave it. Postfix, per CLAUDE.md 4.
    //
    // The pawn is a private field on the tracker, so it comes in as ___pawn.
    // The full parameter list is long and we name only what we use - Harmony
    // matches by name, not position.
    //
    // This is the hottest patch in the mod by a wide margin: it runs on every
    // job start for every pawn. The body is one dictionary lookup and it
    // never allocates on the common path.
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class JobLoopPatch
    {
        public static void Postfix(Job newJob, ThinkNode jobGiver, Pawn ___pawn)
        {
            JobLoopWatch.Notify_JobStarted(___pawn, newJob, jobGiver);
        }
    }
}
