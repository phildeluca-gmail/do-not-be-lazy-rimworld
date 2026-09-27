using RimWorld;

namespace DoNotBeLazy.Utility
{
    // Recognises a Vanilla Factions Expanded - Pirates "warcasket" apparel
    // piece, for the "Drop everything" gizmo (dnbl-architecture.md section
    // 22 subsection "Drop everything", built 2026-09-27). Soft dependency,
    // the same rule PuahCompat and VehicleCompat follow: VFEPirates.dll
    // never enters lib\ and nothing here names its type at compile time -
    // this checks a def's runtime type by its full name string instead.
    //
    // Verified 2026-09-27 against the mod's own shipped Defs (Steam
    // Workshop item 2723801948, installed under
    // E:\SteamLibrary\steamapps\workshop\content\294100\; folders 1.3
    // through 1.6 all agree). Every warcasket piece - the bodysuit, every
    // shell, every shoulder pad, every helmet, across every quality tier -
    // is declared with the XML element <VFEPirates.WarcasketDef>, its
    // ThingDef subclass (confirmed present in
    // 1.6\Assemblies\VFEPirates.dll by ildasm). The Thing's own runtime
    // class (`thingClass`) is NOT a reliable marker: the bodysuit
    // (VFEP_Warcasket_Bodysuit) overrides it back to plain `Apparel`,
    // while every other piece uses `VFEPirates.Apparel_Warcasket` - but
    // every one of them, without exception, keeps the WarcasketDef class
    // on its def. That is the one fact true of all of them.
    public static class WarcasketCompat
    {
        private const string WarcasketDefTypeName = "VFEPirates.WarcasketDef";

        public static bool IsWarcasket(Apparel apparel)
        {
            return apparel?.def != null && apparel.def.GetType().FullName == WarcasketDefTypeName;
        }
    }
}
