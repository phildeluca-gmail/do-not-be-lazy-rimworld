using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Utility
{
    // "* build" on blueprints and frames - dnbl-architecture.md section 58
    // (2026-11-25). User: "Prioritize build exists for blueprints, where they
    // work on the object until it is complete. Why not do that? Can have
    // deliver and build, no?"
    //
    // One WorkGiver that answers for both kinds of target by asking the three
    // vanilla workers in turn, so the existing sweep (pool, group scan, rescan,
    // wait on reserved targets, endings) runs it unchanged:
    //   Blueprint -> ConstructDeliverResourcesToBlueprints
    //   Frame     -> ConstructDeliverResourcesToFrames, then, once every
    //                material is in, ConstructFinishFrames
    // A blueprint turns into a Frame when its first delivery lands (vanilla
    // Toils_Construct.MakeSolidThingFromBlueprintIfNecessary), which is a new
    // Thing. The scan includes both groups, so the next rescan finds the frame;
    // SweepManager.NoteBuildProgress also follows it at once and logs it.
    //
    // Verified against lib\Assembly-CSharp.dll 2026-11-25: vanilla's own
    // right-click on a blueprint offers only the delivery job, which ends when
    // its load is delivered. Nothing carries the pawn on to FinishFrame.
    public class WorkGiver_DnblBuild : WorkGiver_Scanner
    {
        public const string DefName = "DNBL_Build";

        private WorkGiver_Scanner deliverBlueprints;
        private WorkGiver_Scanner deliverFrames;
        private WorkGiver_Scanner finishFrames;
        private bool resolved;

        // ThingRequestGroup.Construction is blueprints and frames together
        // (Verse.ThingListGroupHelper.Includes: IsBlueprint || IsFrame). It
        // scopes the menu's greyed "why not" lines and SweepManager's
        // IsRestoredOwnStep, so a frame must be accepted as well as a blueprint.
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.Construction);

        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override Danger MaxPathDanger(Pawn pawn)
        {
            return Danger.Deadly;
        }

        // The map lister's own list, so every pawn gets the same object and
        // TaskScanner walks it once for the group (it skips a list already
        // walked by reference).
        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            Map map = pawn?.Map;
            return map == null ? new List<Thing>() : map.listerThings.ThingsInGroup(ThingRequestGroup.Construction);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Resolve();

            if (t is Blueprint)
            {
                return deliverBlueprints?.JobOnThing(pawn, t, forced);
            }

            if (t is Frame)
            {
                Job job = deliverFrames?.JobOnThing(pawn, t, forced);
                if (job != null)
                {
                    return job;
                }

                // every material is in - the next step is the building
                return finishFrames?.JobOnThing(pawn, t, forced);
            }

            return null;
        }

        private void Resolve()
        {
            if (resolved)
            {
                return;
            }

            resolved = true;
            deliverBlueprints = DefDatabase<WorkGiverDef>.GetNamedSilentFail("ConstructDeliverResourcesToBlueprints")?.Worker as WorkGiver_Scanner;
            deliverFrames = DefDatabase<WorkGiverDef>.GetNamedSilentFail("ConstructDeliverResourcesToFrames")?.Worker as WorkGiver_Scanner;
            finishFrames = DefDatabase<WorkGiverDef>.GetNamedSilentFail("ConstructFinishFrames")?.Worker as WorkGiver_Scanner;
        }
    }

    // DNBL ships no Defs\ folder, so the def is built in code, the same way
    // DnblJobDefOf's JobDefs are (Jobs/JobDef_StuffAndHaulRegistration.cs).
    // It is added to the DefDatabase so FloatMenuPatch.EligibleDefs finds it and
    // a saved order can look it up by defName. It is NOT in any WorkTypeDef's
    // workGiversByPriority, so no pawn's own AI ever runs it.
    [StaticConstructorOnStartup]
    internal static class BuildWorkGiverRegistration
    {
        static BuildWorkGiverRegistration()
        {
            try
            {
                if (DefDatabase<WorkGiverDef>.GetNamedSilentFail(WorkGiver_DnblBuild.DefName) != null)
                {
                    Core.Logger.Warning($"WorkGiverDef {WorkGiver_DnblBuild.DefName} already registered - not adding a duplicate.");
                    return;
                }

                var def = new WorkGiverDef
                {
                    defName = WorkGiver_DnblBuild.DefName,
                    label = "build",
                    giverClass = typeof(WorkGiver_DnblBuild),
                    workType = WorkTypeDefOf.Construction,
                    verb = "build",
                    gerund = "building",
                    priorityInType = 0,
                    scanThings = true,
                    scanCells = false,
                    directOrderable = true,
                    requiredCapacities = new List<PawnCapacityDef> { PawnCapacityDefOf.Manipulation },
                };

                DefDatabase<WorkGiverDef>.Add(def);

                var taken = new HashSet<ushort>();
                foreach (WorkGiverDef other in DefDatabase<WorkGiverDef>.AllDefsListForReading)
                {
                    if (other != def)
                    {
                        taken.Add(other.shortHash);
                    }
                }

                ushort hash = (ushort)(GenText.StableStringHash(def.defName) % 65535);
                while (hash == 0 || taken.Contains(hash))
                {
                    hash++;
                }
                def.shortHash = hash;

                Core.Logger.Message($"Registered WorkGiverDef {def.defName} (the * build option).");
            }
            catch (Exception ex)
            {
                Core.Logger.Warning($"could not register the * build WorkGiverDef - the option will not appear: {ex}");
            }
        }
    }
}
