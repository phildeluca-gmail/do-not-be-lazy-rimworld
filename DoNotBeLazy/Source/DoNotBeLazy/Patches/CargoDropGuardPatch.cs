using DoNotBeLazy.Comps;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DoNotBeLazy.Patches
{
    // Stuff-first hauling and loading - dnbl-architecture.md section 18,
    // decision 5 (cargo protection). JobGiver_DropUnusedInventory drops old
    // raw ingestible food and any drug the pawn's schedule doesn't want
    // kept, straight off pawn.inventory.innerContainer with no comp check
    // of its own - so a meal or a stray drug DNBL is deliberately carrying
    // to deliver could be dropped mid-trip. Prefixes its private
    // Drop(Pawn, Thing) - the one place both of its loops actually remove
    // an item - and skips the drop when CompDnblCargo says this pawn is
    // carrying that thing for delivery.
    [HarmonyPatch(typeof(JobGiver_DropUnusedInventory))]
    [HarmonyPatch("Drop")]
    internal static class CargoDropGuardPatch
    {
        private static bool Prefix(Pawn pawn, Thing thing)
        {
            CompDnblCargo cargo = pawn?.GetComp<CompDnblCargo>();
            if (cargo != null && cargo.IsCarrying(thing))
            {
                return false; // skip - DNBL still wants this delivered
            }
            return true;
        }
    }
}
