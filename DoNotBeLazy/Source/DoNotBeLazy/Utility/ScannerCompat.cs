using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DoNotBeLazy.Utility
{
    // Same job as GrowerCompat and FireCompat: the things this mod has to do
    // by hand for one family of WorkGivers.
    //
    // The two scanners - the long-range mineral scanner (LongRangeScan) and
    // the ground-penetrating scanner (GroundPenetratingScan) - were
    // unreachable from this mod for exactly one reason: both are workType
    // Research, and Research is not in FloatMenuPatch's supported work-type
    // set. Everything else about them already passed. They are
    // directOrderable, they are WorkGiver_Scanner, and scanThings defaults
    // true, so the clicked building answers HasJobOnThing normally.
    //
    // They are matched here by worker class (WorkGiver_OperateScanner), not
    // by defName and not by adding Research to the work-type set. Both
    // vanilla scanners share that one giverClass, so a modded scanner
    // declaring the same class comes along for free - the same reasoning as
    // the WorkGiver_DoBill branch. Adding "Research" instead would also drag
    // in the research bench, which has no completion condition to sweep
    // toward at all.
    //
    // Three things about the vanilla scanner job that the sweep has to work
    // around, all read off the real 1.5 DLL rather than assumed:
    //
    //   1. JobDriver_OperateScanner's work toil is
    //      ToilCompleteMode.Never. The job does not end when the scan finds
    //      something - it does not end at all. So there is no job-end event
    //      to hang "until done" on, and the completion condition has to be
    //      observed rather than waited for. That is what FoundSomething is.
    //   2. WorkGiver_OperateScanner.JobOnThing returns a job
    //      unconditionally - its whole body is one JobMaker call, with no
    //      null path. A null answer is what SweepManager's workstation
    //      branch normally reads as "this station is finished", so a scanner
    //      order can never end that way and has to be checked with
    //      HasJobOnThing instead (power cut, roof built over it, forbidden).
    //   3. That job carries expiryInterval 1500 with checkOverrideOnExpire
    //      true, so every 1500 ticks Pawn_JobTracker calls
    //      CheckForJobOverride. The think tree re-picks the same scanner,
    //      but as a *different Job instance*, and ShouldStartJobFromThinkTree
    //      returns true for it because our job was issued by
    //      TryTakeOrderedJob and so has a null jobGiver. The result is a real
    //      EndCurrentJob(InterruptOptional) roughly every 25 seconds of play
    //      while the pawn carries on scanning regardless. SweepManager treats
    //      that condition as fatal everywhere else and would have killed
    //      every scanner sweep within half a minute; see the scanner branch
    //      in Notify_JobEnded for how it is told apart from the pawn
    //      genuinely being pulled away.
    public static class ScannerCompat
    {
        // protected instance field on CompScanner, so it needs reflection.
        // Cached ref-delegate rather than FieldInfo.GetValue per call - the
        // watchdog reads it for every scanner sweep every 60 ticks.
        private static readonly AccessTools.FieldRef<CompScanner, float> DaysSinceFindingRef = BuildDaysRef();

        private static AccessTools.FieldRef<CompScanner, float> BuildDaysRef()
        {
            FieldInfo field = AccessTools.Field(typeof(CompScanner), "daysWorkingSinceLastFinding");
            if (field == null)
            {
                Core.Logger.Warning("CompScanner.daysWorkingSinceLastFinding not found - scanner sweeps cannot detect a find and will run until interrupted.");
                return null;
            }
            return AccessTools.FieldRefAccess<CompScanner, float>(field);
        }

        // Matches both vanilla scanners and anything modded that reuses the
        // giverClass. Deliberately class-based, not defName-based.
        public static bool IsScannerWork(WorkGiverDef def)
        {
            return def?.Worker is WorkGiver_OperateScanner;
        }

        // The CompScanner on a clicked building, or null if it isn't one.
        // CompDeepScanner (ground-penetrating) and
        // CompLongRangeMineralScanner both derive from it, so one lookup
        // covers both and the sweep never needs to know which it has.
        public static CompScanner ScannerOn(Thing thing)
        {
            return thing?.TryGetComp<CompScanner>();
        }

        // Vanilla's own "is this scanner usable right now" property: spawned,
        // powered, not roofed over, not forbidden, player faction. The job
        // carries this as a fail condition too, so a sweep that ignores it
        // just watches jobs die.
        public static bool CanUseNow(CompScanner comp)
        {
            return comp != null && comp.CanUseNow;
        }

        // How the completion condition is detected.
        //
        // CompScanner.Used runs every tick a pawn works the scanner. It adds
        // that tick's progress to daysWorkingSinceLastFinding, and on a find
        // it calls DoFind and resets the field to zero. The field is
        // otherwise monotonically increasing. So the find is visible as the
        // number going *down*, and nothing else makes it go down.
        //
        // Compared against the highest value seen for this order rather than
        // the previous sample: they are usually the same, but a pawn who was
        // paused for a need contributed no growth in between, and the maximum
        // is the reading that survives that.
        //
        // Known blind spot, small and accepted: sampling every 60 ticks means
        // a find is missed if the field climbs back past its old maximum
        // between two samples. That needs the old maximum to be under one
        // sample's worth of progress, i.e. a find inside roughly the first
        // minute of a fresh scanner's first sweep. The sweep simply carries
        // on to the next find. Both vanilla scanners send their own letter on
        // a find regardless, so nothing is lost but the auto-stop.
        public static bool FoundSomething(CompScanner comp, ref float maxSeen)
        {
            if (comp == null || DaysSinceFindingRef == null)
            {
                return false;
            }

            float now = DaysSinceFindingRef(comp);
            if (now < maxSeen)
            {
                return true;
            }

            maxSeen = now;
            return false;
        }
    }
}
