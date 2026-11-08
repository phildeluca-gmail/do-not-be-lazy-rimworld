using System;
using DoNotBeLazy.Components;
using DoNotBeLazy.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DoNotBeLazy.Patches
{
    // dnbl-architecture.md section 25 (2026-11-07). Prefix on the setter of
    // Pawn_DraftController.Drafted. The game's setter clears the pawn's job queue after this
    // prefix, which would otherwise make a draft look like any other queue clear to the
    // 60-tick watchdog. User: "drafting drops the items which are part of the current job."
    [HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
    internal static class DraftDropPatch
    {
        private static void Prefix(Pawn_DraftController __instance, bool value)
        {
            try
            {
                if (!value || __instance.Drafted) return;
                LeftoverCargo.Notify_Drafting(__instance.pawn);
            }
            catch (Exception ex)
            {
                Logger.Warning("DraftDropPatch failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
