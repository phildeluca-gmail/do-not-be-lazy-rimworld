using HarmonyLib;
using UnityEngine;
using Verse;

namespace DoNotBeLazy.Core
{
    // Mod entry point. RimWorld constructs one instance of this per load,
    // passing the ModContentPack. Applies Harmony patches and hosts the
    // in-game settings UI (Options > Mod Settings > Do Not Be Lazy).
    public class DoNotBeLazyMod : Mod
    {
        public const string HarmonyId = "phildeluca.donotbelazy";

        public static DoNotBeLazySettings Settings;

        public DoNotBeLazyMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DoNotBeLazySettings>();

            var harmony = new Harmony(HarmonyId);
            harmony.PatchAll();

            // By hand, after PatchAll, because PatchAll cannot resolve a
            // type this assembly does not reference. No-op unless Vehicle
            // Framework is installed - see Patches/VehicleMenuPatch.
            Patches.VehicleMenuPatch.TryPatch(harmony);

            // Same reason, for Use Bedrolls. No-op unless that mod is
            // installed - see Patches/BedrollLoopPatch.
            Patches.BedrollLoopPatch.TryPatch(harmony);

            Logger.Message("Harmony patches applied.");
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings.DoWindowContents(inRect);
        }

        public override string SettingsCategory()
        {
            return "Do Not Be Lazy";
        }
    }
}
