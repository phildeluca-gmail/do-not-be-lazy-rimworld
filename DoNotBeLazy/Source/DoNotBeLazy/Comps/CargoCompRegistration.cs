using System.Collections.Generic;
using Verse;
using DoNotBeLazy.Core;

namespace DoNotBeLazy.Comps
{
    // Adds CompProperties_DnblCargo to every humanlike pawn ThingDef, in code.
    // Replaces Patches/DnblCargoComp.xml (deleted 2026-10-19): an XML patch
    // runs before ParentName inheritance is resolved, and Human gets its
    // thingClass from BasePawn, so the xpath thingClass="Pawn" matched
    // nothing and the comp never reached any pawn (2065 declines, 0 runs on
    // 2026-10-18).
    //
    // Timing, verified: [StaticConstructorOnStartup] runs from
    // PlayDataLoader after every def is loaded, inherited and resolved, and
    // before any save is loaded or any pawn is created, so ThingWithComps
    // .InitializeComps (which reads def.comps) sees the comp for every new
    // and every loaded pawn. ThingDef.race.Humanlike (verified by
    // reflection on lib\Assembly-CSharp.dll) is true for any def with
    // intelligence Humanlike, so modded races are covered. comps is null
    // on a def that declares none - handled.
    //
    // Old saves: the comp's PostExposeData tolerates a missing node (see
    // CompDnblCargo), so pawns saved without the comp load cleanly.
    [StaticConstructorOnStartup]
    internal static class CargoCompRegistration
    {
        static CargoCompRegistration()
        {
            int added = 0;
            int already = 0;
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                ThingDef def = defs[i];
                if (def.race == null || !def.race.Humanlike)
                {
                    continue;
                }

                if (def.comps == null)
                {
                    def.comps = new List<CompProperties>();
                }

                bool has = false;
                for (int c = 0; c < def.comps.Count; c++)
                {
                    if (def.comps[c] is CompProperties_DnblCargo)
                    {
                        has = true;
                        break;
                    }
                }

                if (has)
                {
                    already++;
                    continue;
                }

                def.comps.Add(new CompProperties_DnblCargo());
                added++;
            }

            string line = $"Cargo comp registered on {added} humanlike pawn defs ({already} already had it).";
            if (LogFile.Available)
            {
                LogFile.Write("INFO", line);
            }
            Verse.Log.Message("[DoNotBeLazy] " + line);
        }
    }
}
