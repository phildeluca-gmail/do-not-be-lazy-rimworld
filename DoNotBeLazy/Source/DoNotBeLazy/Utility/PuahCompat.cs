using RimWorld;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Utility
{
    // Pick Up And Haul (Mehni.PickUpAndHaul) adds one WorkGiverDef,
    // "HaulToInventory" - its worker stuffs a pawn's inventory with several
    // things and then makes a single delivery run, instead of vanilla's one
    // carry per trip. When it is installed the player wants every "* haul"
    // order to use it, so a sweep ordered on vanilla HaulGeneral is
    // redirected to PUAH's WorkGiver at BeginSweep time.
    //
    // Soft dependency, the same rule Vehicle Framework and RimWar follow:
    // PickUpAndHaul.dll never enters lib\ and nothing here names its types.
    // The def is looked up by name, once; when it is absent every call is a
    // no-op and a * haul order runs on vanilla HaulGeneral exactly as before.
    public static class PuahCompat
    {
        // PUAH's def name, from its Defs/JobDefs/WorkGiver.xml. Its label is
        // "stuff things in inventory and haul" and its workType is Hauling,
        // which is why it already shows up as its own * entry - see
        // IsRedundantEntry.
        private const string PuahDefName = "HaulToInventory";

        // Only the general "pick this up and put it in a stockpile" order is
        // redirected. The other Hauling defs - Refuel, RearmTurrets,
        // HaulToContainer, FillFermentingBarrel and the rest - are "bring
        // this specific thing to that specific place", which is not what PUAH
        // does; substituting there would silently change what the order means.
        private const string VanillaHaulDefName = "HaulGeneral";

        private static bool resolved;
        private static WorkGiverDef puahDef;

        // Resolved on first use rather than at startup: this runs off a
        // right-click, long after DefDatabase is populated, and a static
        // constructor here would race the def load order.
        private static WorkGiverDef PuahDef
        {
            get
            {
                if (!resolved)
                {
                    resolved = true;
                    puahDef = DefDatabase<WorkGiverDef>.GetNamedSilentFail(PuahDefName);

                    // A def whose worker is not a scanner cannot drive a
                    // sweep - TaskScanner and AssignNextTask both need
                    // WorkGiver_Scanner. Treat that as "not installed".
                    if (puahDef != null && !(puahDef.Worker is WorkGiver_Scanner))
                    {
                        puahDef = null;
                    }

                    Core.Logger.Message(puahDef != null
                        ? "Pick Up And Haul detected - * haul orders will stuff inventory before delivering."
                        : "Pick Up And Haul not present - * haul orders use vanilla HaulGeneral.");
                }

                return puahDef;
            }
        }

        public static bool IsInstalled => PuahDef != null;

        // Called at the top of BeginSweep. Hands back the WorkGiverDef the
        // sweep should actually run on: PUAH's when the order was a general
        // haul and PUAH is installed, otherwise the def the player picked,
        // untouched.
        public static WorkGiverDef Substitute(WorkGiverDef workGiverDef)
        {
            if (workGiverDef == null || workGiverDef.defName != VanillaHaulDefName)
            {
                return workGiverDef;
            }

            return PuahDef ?? workGiverDef;
        }

        // With the substitution in place, PUAH's own entry and the vanilla
        // haul entry would start the same sweep, so the menu would show two
        // options doing one thing. Suppress PUAH's - the player asked for
        // "* haul" to mean PUAH, not for a second entry beside it.
        public static bool IsRedundantEntry(WorkGiverDef workGiverDef)
        {
            return workGiverDef != null
                && workGiverDef.defName == PuahDefName
                && IsInstalled;
        }
    }
}
