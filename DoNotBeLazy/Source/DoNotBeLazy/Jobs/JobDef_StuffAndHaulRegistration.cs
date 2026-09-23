using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Jobs
{
    // The JobDef the stuffing driver runs on. DNBL ships no Defs\ folder -
    // see dnbl-manifest.md - so this is built and registered in code rather
    // than XML, the first JobDef this mod has needed (dnbl-architecture.md
    // section 18).
    public static class DnblJobDefOf
    {
        public static JobDef StuffAndHaul;
    }

    // [StaticConstructorOnStartup] fires after every mod's own constructor
    // has run (see PipelineCensus.cs for the same technique), which is well
    // after def loading and RimWorld's own ShortHashGiver.GiveAllShortHashes
    // pass - so a def added here needs its own short hash, done by hand
    // below, or it stays 0 and a save referencing it cannot round-trip
    // safely. ShortHashGiver.GiveShortHash itself is private; this
    // reimplements its algorithm (GenText.StableStringHash, then a linear
    // probe past 0 and anything already taken) against every JobDef
    // registered right now, rather than reflecting into the private method.
    [StaticConstructorOnStartup]
    internal static class JobDefRegistration
    {
        static JobDefRegistration()
        {
            const string defName = "DNBL_StuffAndHaul";

            JobDef existing = DefDatabase<JobDef>.GetNamedSilentFail(defName);
            if (existing != null)
            {
                Core.Logger.Warning($"JobDef {defName} already registered - reusing it instead of adding a duplicate.");
                DnblJobDefOf.StuffAndHaul = existing;
                return;
            }

            var def = new JobDef
            {
                defName = defName,
                label = "stuff and haul",
                driverClass = typeof(JobDriver_StuffAndHaul),
                reportString = "hauling several things.",
                playerInterruptible = true,
                checkOverrideOnDamage = CheckJobOverrideOnDamageMode.Always,
            };

            DefDatabase<JobDef>.Add(def);

            var taken = new HashSet<ushort>();
            foreach (JobDef other in DefDatabase<JobDef>.AllDefsListForReading)
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

            DnblJobDefOf.StuffAndHaul = def;
            Core.Logger.Message($"Registered JobDef {defName} for stuff-first hauling.");
        }
    }
}
