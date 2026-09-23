using DoNotBeLazy.Core;
using DoNotBeLazy.Jobs;
using DoNotBeLazy.Utility;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Patches
{
    // Stuff-first hauling and loading, interception layer 2 -
    // dnbl-architecture.md section 18. Postfixes the two vanilla methods
    // every ordinary "carry this to a cell or a container" job is built
    // from, substituting a DNBL stuffing job (Jobs/JobDriver_StuffAndHaul)
    // for the single-item job vanilla would have handed out.
    //
    // This catches vanilla WorkGiver_HaulGeneral / WorkGiver_HaulCorpses,
    // any modded haul WorkGiver that calls the same vanilla utility (most
    // do by convention), and DNBL's own "* Haul" order with no separate
    // integration: WorkGiver_HaulGeneral.JobOnThing calls
    // HaulAIUtility.HaulToStorageJob internally (verified against
    // lib\Assembly-CSharp.dll), and SweepManager.AssignNextTask asks that
    // same JobOnThing method for a job exactly like vanilla does.
    //
    // Verified against lib\Assembly-CSharp.dll, not remembered: this
    // build's HaulToStorageJob is the 2-parameter form and
    // HaulToContainerJob takes (Pawn, Thing, Thing) - see
    // dnbl-architecture.md section 18's verified facts. PUAH's own 1.6
    // source calls a 3-parameter "forced" overload that does not exist in
    // this 1.5 build.
    //
    // Takes over unconditionally, whether or not Pick Up And Haul is
    // installed - no detection gates this. PuahCompat.WarnIfCoexisting logs
    // once per session if PUAH is also active, per the accepted, named risk
    // in section 18; it does not change what happens here.
    [HarmonyPatch(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToStorageJob))]
    internal static class HaulToStorageJobPatch
    {
        private static void Postfix(Pawn p, ref Job __result)
        {
            HaulInterceptPatch.TrySubstitute(p, ref __result);
        }
    }

    // Postfixes HaulToContainerJob the same way HaulToStorageJobPatch
    // above postfixes HaulToStorageJob - every haul-to-container job
    // (graves, gibbet cages, pack-animal carriers, biosculpter pods,
    // growth vats, subcore scanners) is substituted by TrySubstitute.
    // A 2026-09-22 hopper-specific decline here, and its DeclineContainerJob
    // helper, were removed the same night as dead code: verified against
    // lib\Assembly-CSharp.dll that a nutrient paste hopper can never reach
    // this postfix with a non-null __result in the first place - see
    // dnbl-architecture.md section 18.
    [HarmonyPatch(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToContainerJob))]
    internal static class HaulToContainerJobPatch
    {
        private static void Postfix(Pawn p, ref Job __result)
        {
            HaulInterceptPatch.TrySubstitute(p, ref __result);
        }
    }

    internal static class HaulInterceptPatch
    {
        internal static void TrySubstitute(Pawn pawn, ref Job __result)
        {
            // Kill switch, dnbl-architecture.md section 18. Cheapest check
            // first - a static bool read, before touching pawn or job.
            if (!DoNotBeLazyMod.Settings.stuffFirstHauling)
            {
                return;
            }

            if (__result == null || pawn == null || pawn.inventory == null)
            {
                return;
            }

            // Ambient hauling only makes sense for the player's own
            // colonists - leaves hostile and wild pawns' hauling AI alone.
            if (pawn.Faction != Faction.OfPlayer)
            {
                return;
            }

            if (DnblJobDefOf.StuffAndHaul == null || __result.def == DnblJobDefOf.StuffAndHaul)
            {
                return;
            }

            if (!(__result.targetA.Thing is Thing primary))
            {
                return;
            }

            // Fixed 2026-09-22 - the same-tick crash (dnbl-architecture.md
            // section 18). A pawn who cannot carry even one unit of the
            // primary target must keep vanilla's own job rather than being
            // handed a stuffing job guaranteed to refuse it and accomplish
            // nothing: vanilla hauls via carryTracker, which has no mass
            // limit, so its own job can very likely still succeed. Uses the
            // exact calculation JobDriver_StuffAndHaul.PickUpToil applies,
            // so the two can't disagree.
            if (!JobDriver_StuffAndHaul.CanPickUpAtLeastOne(pawn, primary))
            {
                Logger.Message($"{pawn.LabelShort}: {primary.LabelCap} would overencumber before even one unit - leaving vanilla's own haul job in place");
                return;
            }

            // Independent backstop, in case some other path still produces
            // a zero-progress trip for this pawn/target pair: don't recreate
            // the same stuffing job while that pair is still suppressed.
            if (JobDriver_StuffAndHaul.IsZeroProgressSuppressed(pawn, primary))
            {
                return;
            }

            PuahCompat.WarnIfCoexisting();

            Job stuffJob = JobMaker.MakeJob(DnblJobDefOf.StuffAndHaul, primary);
            stuffJob.count = __result.count > 0 ? __result.count : primary.stackCount;
            stuffJob.haulMode = HaulMode.ToCellStorage; // signal to the driver: storage mode
            stuffJob.playerForced = __result.playerForced;

            Logger.Message($"{pawn.LabelShort}: stuffing job created for {primary.LabelCap}, replacing a single-item haul");

            __result = stuffJob;
        }
    }
}
