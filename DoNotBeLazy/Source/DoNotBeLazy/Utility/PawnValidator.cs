using RimWorld;
using Verse;

namespace DoNotBeLazy.Utility
{
    // Central "can this pawn take part in a sweep of this work type" check.
    // Reused by SweepManager (Phase 3) both when a sweep starts and on
    // every tick while a pawn remains in an active sweep, per the
    // death/downed/mental-break edge case in the architecture doc.
    //
    // Per-bill skill requirements (Bill.recipe.skillRequirements) are a
    // workstation-specific concern handled where bills are selected, not
    // here - this checks general pawn eligibility for a work type.
    public static class PawnValidator
    {
        // Why a pawn was turned away. The menu wants the reason; the
        // per-tick recheck in SweepManager only wants yes/no, so the check
        // hands back one of these and only the menu turns it into words.
        public enum Refusal
        {
            None,
            Unknown,
            Downed,
            MentalState,
            Drafted,
            NeverWorks,
            NeverDoesType,
            NotAssigned,
            NoManipulation,
        }

        public static bool CanSweep(Pawn pawn, WorkGiverDef workGiverDef)
        {
            return Check(pawn, workGiverDef) == Refusal.None;
        }

        // Can this pawn EVER do this work, whatever the player does with the
        // work tab? NeverWorks and NeverDoesType are settled facts about the
        // pawn - a backstory, a trait, a gene - and no priority change fixes
        // them. NotAssigned looks the same to a player but is one click away
        // from being false, so it is deliberately NOT in here.
        //
        // The menu uses this to decide whether a greyed "why not" entry is
        // worth drawing. Telling someone their brawler will never do
        // research is noise; telling them Doctor sits at priority 0 is a
        // fix they can act on.
        public static bool Hopeless(Pawn pawn, WorkGiverDef workGiverDef)
        {
            Refusal r = Check(pawn, workGiverDef);
            return r == Refusal.NeverWorks || r == Refusal.NeverDoesType;
        }

        // Words for the greyed-out float menu entry, null when the pawn is
        // fine. Hardcoded English around the def's own translated labels
        // rather than vanilla's keys - CannotPrioritizeNotAssignedToWorkType
        // and friends all start "Cannot prioritize:", which reads wrong
        // inside "* Haul until done - ...". Rest of this mod's UI text is
        // hardcoded anyway.
        public static string RefusalReason(Pawn pawn, WorkGiverDef workGiverDef)
        {
            // gerundLabel is "hauling"/"cleaning"/etc
            string doing = workGiverDef?.workType?.gerundLabel ?? "this work";

            switch (Check(pawn, workGiverDef))
            {
                case Refusal.Downed:
                    return "is down";
                case Refusal.MentalState:
                    return "is having a mental break";
                case Refusal.Drafted:
                    return "is drafted";
                case Refusal.NeverWorks:
                    return "cannot be assigned work";
                case Refusal.NeverDoesType:
                    return "will never do " + doing;
                case Refusal.NotAssigned:
                    return "is not assigned to " + doing;
                case Refusal.NoManipulation:
                    return "cannot manipulate";
                default:
                    return null;
            }
        }

        // Group order rule. 2026-11-15 first version dropped sleepers; the user
        // corrected it 2026-11-19: "I never said to leave the sleepers out, you
        // misunderstood. I want everyone capable of doing the work to do the
        // work, unless they meet the criteria for not doing the work (incapable
        // or mood close to break)." So a sleeper is NOT blocked - the order
        // wakes it (Pawn_JobTracker.TryTakeOrderedJob replaces the LayDown job
        // and starts ours with continueSleeping false; verified in
        // Assembly-CSharp). Left out of a group order: a pawn who cannot move,
        // and a pawn whose mood is at or below the mood setting
        // (NeedMonitor.MoodAtSetting, DoNotBeLazySettings.moodBuffer).
        // Returns the reason in words, or null when the pawn takes the order.
        // Deliberately NOT part of CanSweep: that check also runs every
        // assignment of a running sweep, and a pawn already on an order stays on it.
        public static string GroupOrderBlock(Pawn pawn)
        {
            if (pawn == null)
            {
                return "is missing";
            }
            if (pawn.health?.capacities == null)
            {
                return "has no health state";
            }
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
            {
                return "cannot move";
            }
            if (DoNotBeLazy.Components.NeedMonitor.MoodAtSetting(pawn))
            {
                return "is close to a mental break (mood at or below the mood setting)";
            }
            return null;
        }

        // was the whole body of CanSweep - split out so the menu can name
        // which check tripped without the order of them drifting apart
        private static Refusal Check(Pawn pawn, WorkGiverDef workGiverDef)
        {
            if (pawn == null || workGiverDef?.workType == null)
            {
                return Refusal.Unknown;
            }

            if (pawn.Dead || pawn.Downed)
            {
                return Refusal.Downed;
            }
            if (pawn.InMentalState)
            {
                return Refusal.MentalState;
            }
            if (pawn.Drafted)
            {
                return Refusal.Drafted;
            }

            if (pawn.workSettings == null || !pawn.workSettings.EverWork)
            {
                return Refusal.NeverWorks;
            }

            WorkTypeDef workType = workGiverDef.workType;

            if (pawn.WorkTypeIsDisabled(workType))
            {
                return Refusal.NeverDoesType;
            }

            if (!pawn.workSettings.WorkIsActive(workType))
            {
                return Refusal.NotAssigned;
            }

            if (pawn.health?.capacities == null || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
            {
                return Refusal.NoManipulation;
            }

            return Refusal.None;
        }

    }
}
