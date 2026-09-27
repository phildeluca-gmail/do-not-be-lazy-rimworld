using System;
using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Jobs
{
    // The JobDefs the mod's own JobDrivers run on. DNBL ships no Defs\
    // folder - see dnbl-manifest.md - so these are built and registered in
    // code rather than XML. StuffAndHaul was the first, and the only one,
    // until 2026-09-27 (dnbl-architecture.md section 18); DropInventoryStack
    // was added that day for the "Drop everything" gizmo (section 22
    // subsection "Drop everything") - vanilla has no queued job for
    // dropping a single inventory stack.
    public static class DnblJobDefOf
    {
        public static JobDef StuffAndHaul;
        public static JobDef DropInventoryStack;
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
    //
    // Refactored 2026-09-27 into RegisterOrReuse/AssignShortHash so a
    // second def (DropInventoryStack) could be added without duplicating
    // the short-hash algorithm; behaviour for StuffAndHaul is unchanged.
    [StaticConstructorOnStartup]
    internal static class JobDefRegistration
    {
        static JobDefRegistration()
        {
            DnblJobDefOf.StuffAndHaul = RegisterOrReuse(
                "DNBL_StuffAndHaul", "stuff and haul",
                typeof(JobDriver_StuffAndHaul), "hauling several things.");

            DnblJobDefOf.DropInventoryStack = RegisterOrReuse(
                "DNBL_DropInventoryStack", "drop inventory item",
                typeof(JobDriver_DropInventoryStack), "dropping an item.");
        }

        private static JobDef RegisterOrReuse(string defName, string label, Type driverClass, string reportString)
        {
            JobDef existing = DefDatabase<JobDef>.GetNamedSilentFail(defName);
            if (existing != null)
            {
                Core.Logger.Warning($"JobDef {defName} already registered - reusing it instead of adding a duplicate.");
                return existing;
            }

            var def = new JobDef
            {
                defName = defName,
                label = label,
                driverClass = driverClass,
                reportString = reportString,
                playerInterruptible = true,
                checkOverrideOnDamage = CheckJobOverrideOnDamageMode.Always,
            };

            DefDatabase<JobDef>.Add(def);
            AssignShortHash(def);

            Core.Logger.Message($"Registered JobDef {defName}.");
            return def;
        }

        private static void AssignShortHash(JobDef def)
        {
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
        }
    }
}
