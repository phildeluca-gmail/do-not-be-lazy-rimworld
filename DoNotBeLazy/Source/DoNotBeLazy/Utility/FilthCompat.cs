using System;
using RimWorld;
using Verse;

namespace DoNotBeLazy.Utility
{
    // Why a piece of filth cannot be cleaned, in words.
    //
    // Named after FireCompat, but it is deliberately NOT the same kind of
    // file. FireCompat re-implements a vanilla check minus its home-area
    // gate, so the mod can override that gate on an explicit click. **The
    // same override is not possible for cleaning**, and this file exists
    // because that had to be established rather than assumed:
    //
    //   - WorkGiver_CleanFilth.HasJobOnThing gates on
    //     t.Map.areaManager.Home[t.Position] (IL of lib\Assembly-CSharp.dll,
    //     read 2026-08-30), and writes NO JobFailReason while doing it - so
    //     the refusal is silent, which is why filth outside the home area
    //     produces no menu entry and no explanation from anyone.
    //   - JobDriver_CleanFilth's toils call
    //     ToilJumpConditions.JumpIfOutsideHomeArea **twice**. So even a job
    //     handed straight to a pawn on non-home filth would walk them over
    //     and skip the target. Bypassing the WorkGiver alone buys nothing,
    //     and getting past the driver would need a transpiler (banned) or a
    //     custom JobDriver.
    //
    // So the mod explains instead of overriding. The greyed-out entry is
    // the whole deliverable here.
    public static class FilthCompat
    {
        // WorkGiver_CleanFilth.MinTicksSinceThickened - an instance field,
        // initialised to 0x258 in that class's constructor. Read out of the
        // IL rather than remembered. Ten seconds: filth that was just spread
        // or just walked through is left alone until it settles.
        private const int MinTicksSinceThickened = 600;

        // The filth WorkGiver specifically, not "cleaning work" generally -
        // CleanClearSnow is also Cleaning and none of this applies to it.
        //
        // Matched on the declared giverClass by name, walking base types so a
        // modded subclass still counts. Not `Worker is WorkGiver_CleanFilth`:
        // that type is internal in this build and will not compile from here,
        // the same wall FireCompat hit with WorkGiver_FightFires.
        public static bool IsFilthCleaning(WorkGiverDef def)
        {
            for (Type type = def?.giverClass; type != null; type = type.BaseType)
            {
                if (type.Name == "WorkGiver_CleanFilth")
                {
                    return true;
                }
            }

            return false;
        }

        // Null when this isn't filth, or when the reason it was refused is
        // one of the ordinary ones the caller already reports (reserved,
        // forbidden). Only the two silent vanilla gates get words here.
        public static string RefusalReason(Thing thing)
        {
            Filth filth = thing as Filth;
            if (filth == null || filth.Map == null)
            {
                return null;
            }

            // checked first because it is the permanent one - a firefoam
            // blob outside the home area is never getting cleaned, where a
            // freshly thickened one is cleanable in a moment
            if (!filth.Map.areaManager.Home[filth.Position])
            {
                return "outside the home area";
            }

            if (filth.TicksSinceThickened < MinTicksSinceThickened)
            {
                return "just spread - cleanable in a few seconds";
            }

            return null;
        }
    }
}
