using DoNotBeLazy.Jobs;
using DoNotBeLazy.Utility;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Logger = DoNotBeLazy.Core.Logger;

namespace DoNotBeLazy.Patches
{
    // Stuff-first hauling and loading, transporter loading -
    // dnbl-architecture.md section 18 (deferred in the v1 build, extended
    // here). Postfixes RimWorld.LoadTransportersJobUtility.JobOnTransporter
    // (Pawn, CompTransporter) - the single point BOTH
    // WorkGiver_LoadTransporters.JobOnThing (a colonist's own Hauling work)
    // and JobGiver_LoadTransporters.TryGiveJob (the Lord's own automatic
    // loading during LordToil_LoadAndEnterTransporters, when a caravan is
    // departing) call to build a "load this transporter" Job - verified
    // from the IL of both callers in lib\Assembly-CSharp.dll (ildasm), not
    // guessed. Section 18's verified facts already established that
    // transporter loading never goes through HaulAIUtility, so layer 2's
    // existing HaulInterceptPatch never reaches it - this is its own
    // construction point, same idea, different method.
    //
    // JobOnTransporter's own raw Job carries an INVALID targetA - the item
    // is picked later, inside JobDriver_HaulToTransporter's own toils, via
    // LoadTransportersJobUtility.FindThingToLoad (also public static,
    // confirmed by reflection). That is why this postfix calls
    // FindThingToLoad itself before substituting: unlike the vehicle-
    // packing wrap (Components/SweepManager.cs, WrapForVehicleStuffing),
    // the raw job here has no primary item to read off __result.
    //
    // targetB is transporter.parent (ThingComp.parent, a public field).
    // RimWorld.CompTransporter implements Verse.IThingHolder directly -
    // verified by reflection (GetInterfaces() on the real type in
    // lib\Assembly-CSharp.dll) - so transporter.parent.
    // TryGetInnerInteractableThingOwner() resolves through the comp-search
    // branch of Verse.ThingOwnerUtility, the same generic mechanism a
    // vehicle relies on. Fully verified here, unlike the vehicle case:
    // CompTransporter lives in lib\Assembly-CSharp.dll, not a third-party
    // DLL that can't enter lib\.
    //
    // ignoreForbidden = true on the substitute matches JobOnTransporter's
    // own raw job (set right after JobMaker.MakeJob in the decompiled IL) -
    // ToilFailConditions.FailOnForbidden reads job.ignoreForbidden off the
    // pawn's CURRENT job before failing the toil (verified in the IL of its
    // compiler-generated closure), so leaving this unset would make
    // JobDriver_StuffAndHaul's own FailOnDespawnedNullOrForbidden toil
    // abort on a forbidden item transporter loading is normally allowed to
    // carry regardless.
    [HarmonyPatch(typeof(LoadTransportersJobUtility), nameof(LoadTransportersJobUtility.JobOnTransporter))]
    internal static class LoadTransportersJobPatch
    {
        private static void Postfix(Pawn p, CompTransporter transporter, ref Job __result)
        {
            TransporterInterceptPatch.TrySubstitute(p, transporter, ref __result);
        }
    }

    internal static class TransporterInterceptPatch
    {
        // Resolved once, lazily - DefDatabase is not guaranteed ready at
        // static-constructor time (PuahCompat.cs follows the same pattern).
        // Matched by Worker type rather than defName, the same convention
        // VehicleCompat.cs uses for Vehicle Framework's own WorkGivers: this
        // is a vanilla def and its name is not expected to change, but
        // matching by type is one fewer thing that can silently drift.
        // Used only to let JobDriver_StuffAndHaul's fixed-destination mode
        // ask for another opportunistic item the same way - a null here
        // just means that mode's opportunistic scan finds nothing, not a
        // failure to load at all.
        private static WorkGiverDef loadTransportersDef;
        private static bool loadTransportersDefResolved;

        private static WorkGiverDef LoadTransportersDef
        {
            get
            {
                if (!loadTransportersDefResolved)
                {
                    loadTransportersDefResolved = true;
                    foreach (WorkGiverDef def in DefDatabase<WorkGiverDef>.AllDefsListForReading)
                    {
                        if (def.Worker is WorkGiver_LoadTransporters)
                        {
                            loadTransportersDef = def;
                            break;
                        }
                    }
                    if (loadTransportersDef == null)
                    {
                        Logger.Warning("No WorkGiverDef found with a WorkGiver_LoadTransporters worker - transporter stuffing will still fire, but opportunistic extra items are disabled for it.");
                    }
                }
                return loadTransportersDef;
            }
        }

        internal static void TrySubstitute(Pawn pawn, CompTransporter transporter, ref Job __result)
        {
            // Kill switch, dnbl-architecture.md section 18. Cheapest check
            // first - a static bool read, before touching pawn or job.
            if (!DoNotBeLazy.Core.DoNotBeLazyMod.Settings.stuffFirstHauling)
            {
                return;
            }

            if (__result == null || pawn == null || pawn.inventory == null || transporter?.parent == null)
            {
                return;
            }

            // Same conservative default as HaulInterceptPatch: ambient
            // hauling only makes sense for the player's own colonists.
            // JobGiver_LoadTransporters only ever drives a departing
            // caravan's own pawns, so this never actually excludes it - the
            // guard is here for the same reason it's on every other
            // interception point.
            if (pawn.Faction != Faction.OfPlayer)
            {
                return;
            }

            if (DnblJobDefOf.StuffAndHaul == null || __result.def == DnblJobDefOf.StuffAndHaul)
            {
                return;
            }

            if (transporter.parent.TryGetInnerInteractableThingOwner() == null)
            {
                Logger.Message($"{pawn.LabelShort}: {transporter.parent.LabelShort} exposes no ThingOwner, transporter stuffing skipped for this trip");
                return;
            }

            ThingCount toLoad = LoadTransportersJobUtility.FindThingToLoad(pawn, transporter);
            if (toLoad.Thing == null || toLoad.Count <= 0)
            {
                // HasJobOnTransporter said yes but nothing is actually
                // findable for this pawn right now - let the raw job run
                // and fail (or succeed) on its own terms rather than
                // substituting a job for nothing.
                return;
            }

            // Fixed 2026-09-22 - the same shape of same-tick crash
            // HaulInterceptPatch hit (dnbl-architecture.md section 18),
            // confirmed in the archived log through this patch too (a
            // transport-pod load, not a general haul). Same guard, same
            // shared calculation as the driver's own PickUpToil.
            if (!JobDriver_StuffAndHaul.CanPickUpAtLeastOne(pawn, toLoad.Thing))
            {
                // Throttled 2026-09-24 (dnbl-architecture.md section 18) -
                // same per-evaluation spam as HaulInterceptPatch's line.
                JobDriver_StuffAndHaul.LogOverencumberSkipThrottled(pawn, toLoad.Thing,
                    $"{pawn.LabelShort}: {toLoad.Thing.LabelCap} would overencumber before even one unit - leaving vanilla's own load job in place");
                return;
            }

            // Fixed 2026-09-24, dnbl-architecture.md section 18 (defect 2 of
            // two). Without this, a stuffing job built for an item another
            // pawn already holds fails its own TryMakePreToilReservations the
            // instant it starts, and the job giver rebuilds this exact job -
            // up to several times a second - until the item frees up. Leave
            // vanilla's own job in place instead, same idea as
            // CanPickUpAtLeastOne above.
            if (!JobDriver_StuffAndHaul.CanReserveItem(pawn, toLoad.Thing))
            {
                JobDriver_StuffAndHaul.LogReserveSkipThrottled(pawn, toLoad.Thing,
                    $"{pawn.LabelShort}: {toLoad.Thing.LabelCap} is already reserved - leaving vanilla's own load job in place");
                return;
            }

            // Independent backstop, in case some other path still produces
            // a zero-progress trip for this pawn/target pair.
            if (JobDriver_StuffAndHaul.IsZeroProgressSuppressed(pawn, toLoad.Thing))
            {
                return;
            }

            PuahCompat.WarnIfCoexisting();

            Job stuffJob = JobMaker.MakeJob(DnblJobDefOf.StuffAndHaul, toLoad.Thing, transporter.parent);
            stuffJob.count = toLoad.Count;
            stuffJob.haulMode = HaulMode.ToContainer; // signal to the driver: fixed-destination mode
            stuffJob.workGiverDef = LoadTransportersDef;
            stuffJob.ignoreForbidden = true;
            stuffJob.playerForced = __result.playerForced;

            // Fixed 2026-09-27, dnbl-architecture.md section 18: same
            // per-candidate spam as HaulInterceptPatch's line, same fix -
            // the one line for this event now lives in
            // JobDriver_StuffAndHaul.TryMakePreToilReservations, which fires
            // once when the job actually starts.
            __result = stuffJob;
        }
    }
}
