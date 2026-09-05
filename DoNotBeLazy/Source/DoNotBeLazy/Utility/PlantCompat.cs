using System;
using RimWorld;
using Verse;

namespace DoNotBeLazy.Utility
{
    // "Chop wood" and "cut plants" are two different orders the player
    // gives, and vanilla serves both with ONE WorkGiverDef. That collision
    // is why a chopped tree offered "* Cut plants until done" and then cut
    // every marked bush in the radius as well - the label was wrong for the
    // same reason the behaviour was.
    //
    // Verified against lib\Assembly-CSharp.dll on 2026-09-03 rather than
    // remembered:
    //
    //   - Designator_PlantsHarvestWood (the chop-wood order) writes
    //     DesignationDefOf.HarvestPlant; Designator_PlantsCut writes
    //     DesignationDefOf.CutPlant. Both exist as separate defs.
    //   - WorkGiverDef PlantsCut, label "cut plants", giverClass
    //     RimWorld.WorkGiver_PlantsCut (public, derives WorkGiver_Scanner),
    //     serves both: ShouldSkip checks AnySpawnedDesignationOfDef for
    //     EITHER designation, and JobOnThing walks AllDesignationsOn and
    //     accepts either - both producing JobDefOf.CutPlantDesignated.
    //   - DesignationManager.DesignationOn(Thing, DesignationDef) is the
    //     direct lookup and exists in this build.
    //
    // **Because the job is identical either way, scoping a sweep to one
    // order is purely a question of which targets go in the pool.** That is
    // the whole of the fix; nothing downstream of the pool changes.
    public static class PlantCompat
    {
        // WorkGiver_PlantsCut is public in this build (unlike
        // WorkGiver_CleanFilth, which is why FilthCompat has to match on a
        // type name), so this is a plain type test - and it picks up modded
        // subclasses for free.
        public static bool IsPlantCutWork(WorkGiverDef def)
        {
            return def?.Worker is WorkGiver_PlantsCut;
        }

        // Which of the two orders is actually on this thing. Null when it
        // carries neither, which covers every non-plant target and is the
        // signal to leave the sweep unscoped.
        public static DesignationDef OrderOn(Thing thing)
        {
            if (thing == null || thing.Map == null)
            {
                return null;
            }

            DesignationManager designations = thing.Map.designationManager;
            if (designations.DesignationOn(thing, DesignationDefOf.HarvestPlant) != null)
            {
                return DesignationDefOf.HarvestPlant;
            }

            if (designations.DesignationOn(thing, DesignationDefOf.CutPlant) != null)
            {
                return DesignationDefOf.CutPlant;
            }

            return null;
        }

        public static bool IsTree(Thing thing)
        {
            return thing?.def?.plant != null && thing.def.plant.IsTree;
        }

        // The words the player used for the order they actually gave, or
        // null to fall back on def.label.
        //
        // **HarvestPlant is not tree-specific.** It is also the designation
        // on an ordinary plant marked for harvest, so IsTree has to be part
        // of the test - without it a harvest-marked berry bush offers to be
        // chopped.
        public static string LabelFor(Thing thing)
        {
            DesignationDef order = OrderOn(thing);
            if (order == null)
            {
                return null;
            }

            if (order == DesignationDefOf.CutPlant)
            {
                return "Cut plants";
            }

            return IsTree(thing) ? "Chop trees" : "Harvest plants";
        }

        // The pool filter for a sweep started from this thing, or null when
        // the click carries no plant order and the sweep should stay
        // unscoped. Matches the label above case for case: a chop order
        // sweeps trees only, a harvest order sweeps non-trees only, and a
        // cut order sweeps anything marked CutPlant.
        //
        // Deliberately does NOT ask whether the def is PlantsCut - a target
        // carrying no plant designation returns null from OrderOn and the
        // filter never gets built, so every other work type is unaffected
        // without naming any of them.
        public static Predicate<Thing> FilterFor(Thing clicked)
        {
            DesignationDef order = OrderOn(clicked);
            if (order == null)
            {
                return null;
            }

            if (order == DesignationDefOf.CutPlant)
            {
                return t => OrderOn(t) == DesignationDefOf.CutPlant;
            }

            bool wantTrees = IsTree(clicked);
            return t => OrderOn(t) == DesignationDefOf.HarvestPlant && IsTree(t) == wantTrees;
        }
    }
}
