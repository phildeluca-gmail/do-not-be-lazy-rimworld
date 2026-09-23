using System.Collections.Generic;
using Verse;

namespace DoNotBeLazy.Comps
{
    // Stuff-first hauling and loading - dnbl-architecture.md section 18,
    // decision 5. DNBL's own cargo-protection comp: NOT Pick Up And Haul's
    // CompHauledToInventory, no dependency on PUAH, no PUAH type referenced.
    //
    // Shape verified by reading PUAH's own shipped source (inspiration
    // only): CompHauledToInventory lives on the PAWN, not on each carried
    // item, and is added to every pawn ThingDef by a vanilla XML
    // PatchOperationAdd (Patches/PickUpAndHaul.xml in its own mod folder),
    // not by code. DNBL follows the same shape for the same reason - a
    // ThingComp cannot be attached to an arbitrary carried item at runtime
    // in general: ThingWithComps.AllComps returns a *shared* empty list for
    // any ThingDef that declares no comps in XML, and most simple
    // haulables (Steel, WoodLog, ...) are not even ThingWithComps at all,
    // only plain Thing. A pawn is always ThingWithComps, so tagging the
    // pawn is the only place this can safely live.
    //
    // Registered onto every humanlike ThingDef with thingClass="Pawn" by
    // Patches/DnblCargoComp.xml (a vanilla XML patch, not Harmony). Narrowed
    // further to colonists only at runtime, below - see AppliesToThisPawn.
    //
    // JobDriver_StuffAndHaul registers each item it picks into this set the
    // moment it goes into pawn.inventory, and removes it once delivered (or
    // dropped). CargoDropGuardPatch reads it to stop
    // JobGiver_DropUnusedInventory from dumping cargo mid-trip.
    public class CompDnblCargo : ThingComp
    {
        private HashSet<Thing> carrying = new HashSet<Thing>();

        // Colonists only - dnbl-architecture.md section 18, narrowed
        // 2026-09-22 on the user's order. The user's words: "Apply the
        // logic only to colonists. Mechanoids never pick anything up, the
        // animals are okay and already provide a bonus, and raiders don't
        // have time to stuff their inventory. Most animals don't haul, so
        // checking them is nonsensical anyway." Patches/DnblCargoComp.xml
        // already keeps this comp off every animal and mechanoid ThingDef,
        // but a hostile or prisoner Human is still the same ThingDef as a
        // colonist, so that XML narrowing cannot finish the job - only a
        // runtime check of the pawn can. Verified against
        // lib\Assembly-CSharp.dll: Pawn.IsColonist is
        // `Faction == Faction.OfPlayer && RaceProps.Humanlike && (!IsSlave
        // || guest.SlaveIsSecure) && !IsMutant` - true for a secure slave
        // doing colony work, false for a raider, a loose prisoner or a
        // hostile mutant. Checked once here so Register, Unregister and
        // IsCarrying all read as no-ops for anyone else, instead of
        // repeating the check at JobDriver_StuffAndHaul's two call sites
        // and CargoDropGuardPatch's one.
        private bool AppliesToThisPawn => parent is Pawn pawn && pawn.IsColonist;

        public void Register(Thing thing)
        {
            if (thing != null && AppliesToThisPawn)
            {
                carrying.Add(thing);
            }
        }

        public void Unregister(Thing thing)
        {
            if (AppliesToThisPawn)
            {
                carrying.Remove(thing);
            }
        }

        public bool IsCarrying(Thing thing)
        {
            return thing != null && AppliesToThisPawn && carrying.Contains(thing);
        }

        // Live set, pruned of anything destroyed since the last look.
        // Callers should use it and not hold onto the reference.
        public HashSet<Thing> Carrying()
        {
            carrying.RemoveWhere(t => t == null || t.Destroyed);
            return carrying;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();

            // Write nothing when there's nothing to write - fixed
            // 2026-09-22. This comp is on every humanlike ThingDef, which
            // includes every world pawn the save keeps alive (faction
            // leaders, quest pawns, anyone related to the colony), and
            // almost none of them are ever carrying anything: an empty
            // <dnblCarrying/> node on every one of them, every save, is
            // pure bloat. Verified against lib\Assembly-CSharp.dll
            // (Scribe_Collections.Look's List<T> and HashSet<T>
            // overloads): when Scribe.EnterNode fails to find a node -
            // because it was never written - loading falls through
            // cleanly with no exception, so an earlier save with the node
            // present, or a later save without it, both still load; the
            // null-guard below is what turns "no node found" into an
            // empty set either way.
            if (Scribe.mode != LoadSaveMode.Saving || carrying.Count > 0)
            {
                Scribe_Collections.Look(ref carrying, "dnblCarrying", LookMode.Reference);
            }
            if (carrying == null)
            {
                carrying = new HashSet<Thing>();
            }

            // Prune stale references once cross-reference resolution has
            // finished for the whole save, not right after Look() above -
            // LookMode.Reference only records loadIDs during LoadingVars;
            // a reference that failed to resolve (the thing was destroyed,
            // or on a map that did not load) only comes back null by
            // PostLoadInit. This does not stop Scribe's own "Could not
            // resolve reference" warning - that is logged during
            // ResolvingCrossRefs, before this runs - it only stops the
            // dangling null surviving into this pawn's set afterward.
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                carrying.RemoveWhere(t => t == null || t.Destroyed);
            }
        }
    }

    public class CompProperties_DnblCargo : CompProperties
    {
        public CompProperties_DnblCargo()
        {
            compClass = typeof(CompDnblCargo);
        }
    }
}
