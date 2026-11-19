using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Utility
{
    // Everything this mod has to do by hand because it calls JobOnCell /
    // HasJobOnCell directly instead of enumerating PotentialWorkCellsGlobal
    // the way vanilla's JobGiver_Work does.
    //
    // WorkGiver_Grower keeps a shared mutable static, wantedPlantDef, that
    // is only ever set up inside that enumeration: WorkGiver_GrowerSow's
    // ExtraRequirements assigns it per zone/building, and the enumerator
    // nulls it out again after each one. JobOnCell only recomputes it
    // lazily, "if (wantedPlantDef == null)". Skip the enumeration and the
    // field just holds whatever the last caller - us, or any other pawn's
    // vanilla work scan running on the same tick - left in it. Two separate
    // things break, and both were reported from real play:
    //
    //   1. The stale def is baked into the job as plantDefToSow, and
    //      JobDriver_PlantSow's goto toil fails on
    //      !plantDefToSow.CanEverPlantAt(cell), so the job dies during the
    //      walk. That reads to SweepManager as a failed task and ends the
    //      whole sweep.
    //   2. Nothing in JobOnCell checks zone membership explicitly. The only
    //      gate is CalculateWantedPlantDef returning null for a cell with no
    //      growing zone and no plant-grower building - and that call sits
    //      inside the lazy branch. A stale non-null value skips the branch,
    //      so bare unzoned dirt passes straight through to a sow job.
    //
    // WorkGiver_GrowerHarvest doesn't have this problem: no ExtraRequirements
    // override, and it recomputes the wanted def per cell itself.
    public static class GrowerCompat
    {
        // Cached ref-delegate rather than FieldInfo.SetValue per call -
        // ResetWantedPlantDef runs once per cell across a whole radial scan.
        private static readonly AccessTools.FieldRef<ThingDef> WantedPlantDef = BuildWantedPlantDefRef();

        private static AccessTools.FieldRef<ThingDef> BuildWantedPlantDefRef()
        {
            // protected static, so it needs reflection to reach
            FieldInfo field = AccessTools.Field(typeof(WorkGiver_Grower), "wantedPlantDef");
            if (field == null)
            {
                Core.Logger.Warning("WorkGiver_Grower.wantedPlantDef not found - sow sweeps may target the wrong crop or unzoned cells");
                return null;
            }

            return AccessTools.StaticFieldRefAccess<ThingDef>(field);
        }

        // Call immediately before every HasJobOnCell/JobOnCell on a Grower
        // scanner. Nulling the static is what makes vanilla's own lazy
        // branch recompute for the cell actually being asked about, which
        // restores both the correct plantDefToSow and the zone gate.
        public static void ResetWantedPlantDef(WorkGiver_Scanner scanner)
        {
            if (WantedPlantDef == null || !(scanner is WorkGiver_Grower))
            {
                return;
            }

            WantedPlantDef() = null;
        }

        // The preconditions that live in WorkGiver_GrowerSow.ExtraRequirements
        // and nowhere else. Resetting the static above does NOT bring these
        // back - ExtraRequirements is only ever called from
        // PotentialWorkCellsGlobal, which this mod never touches - so without
        // this a growing zone with "allow sow" switched off still gets swept,
        // as does an unpowered hydroponics basin.
        //
        // Sow only. GrowerHarvest inherits the base ExtraRequirements (which
        // just returns true), so there's nothing to mirror for it.
        public static bool SowSettingsAllow(WorkGiver_Scanner scanner, IntVec3 cell, Map map)
        {
            if (!(scanner is WorkGiver_GrowerSow))
            {
                return true;
            }

            // null for a cell with neither a plant-grower edifice nor a
            // growing zone - the same lookup CalculateWantedPlantDef uses
            IPlantToGrowSettable settable = cell.GetPlantToGrowSettable(map);
            if (settable == null)
            {
                return false;
            }

            if (!settable.CanAcceptSowNow())
            {
                return false;
            }

            return !(settable is Zone_Growing zone) || zone.allowSow;
        }

        // WorkGiver_Grower.AllowUnreachable is true, which is exactly why
        // vanilla runs its own pawn.CanReach per zone inside
        // PotentialWorkCellsGlobal - the framework skips its usual
        // reachability filter for these. We skip both, so an unreachable
        // target becomes a job that fails on pathing, and that failure ends
        // the sweep. Danger.Deadly rather than pawn.NormalMaxDanger(): these
        // are manually-issued player orders, so erring toward permissive
        // matches how the rest of the mod treats forced work.
        public static bool CanReachTarget(Pawn pawn, LocalTargetInfo target, WorkGiver_Scanner scanner)
        {
            return pawn.CanReach(target, scanner.PathEndMode, Danger.Deadly);
        }

        // A WorkGiver can legitimately answer "do this other thing first"
        // rather than the work asked about - GrowerSow returns CutPlant for a
        // blocking plant, or HaulAsideJobFor for a blocking item, instead of
        // Sow. Those jobs succeed, which previously read as "target done" and
        // moved the sweep on, so the cell that was just cleared never got
        // sown. Detected by the job pointing somewhere other than the target
        // we asked about; the caller re-queues that target (once - see
        // SweepManager) so the follow-up work actually happens.
        public static bool IsPreparatoryJob(Job job, LocalTargetInfo target)
        {
            // The paint WorkGivers (PaintFloor, PaintBuilding, RemovePaint*)
            // leave targetA empty and put every target, ours included, in
            // targetQueueA. That is the work asked for, not a blocker -
            // without this every paint target was handed out twice. Limited
            // to the four paint jobs so no other WorkGiver's behaviour
            // changes. Added 2026-11-18, architecture section 56.
            if (job != null && job.targetQueueA != null && IsPaintJob(job.def) && job.targetQueueA.Contains(target))
            {
                return false;
            }
            return job != null && job.targetA != target;
        }

        // The four vanilla paint jobs (RimWorld.JobDefOf, verified 2026-11-18).
        public static bool IsPaintJob(JobDef def)
        {
            return def != null && (def == JobDefOf.PaintFloor || def == JobDefOf.PaintBuilding
                || def == JobDefOf.RemovePaintFloor || def == JobDefOf.RemovePaintBuilding);
        }
    }
}
