using System;
using System.Collections.Generic;
using System.Linq;
using DoNotBeLazy.Components;
using DoNotBeLazy.Core;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Logger = DoNotBeLazy.Core.Logger;
using Verse;

namespace DoNotBeLazy.Patches
{
    // dnbl-architecture.md section 51. Postfix on UnfinishedThing.GetGizmos (declared
    // on UnfinishedThing itself, so this is the exact method): adds "Finish this".
    // The selected colonists are tried first (the most capable one); with
    // none selected, the most capable colonist is sent (section 55). A player order ignores the
    // work-type priority and the bill's pawn restriction, nothing else.
    [HarmonyPatch(typeof(UnfinishedThing), nameof(UnfinishedThing.GetGizmos))]
    internal static class FinishThisGizmoPatch
    {
        private static Texture2D icon;
        private static bool iconLoaded;

        // Loaded once, on first use (main thread, inside GetGizmos).
        private static Texture2D Icon
        {
            get
            {
                if (!iconLoaded)
                {
                    iconLoaded = true;
                    icon = ContentFinder<Texture2D>.Get("UI/Gizmos/AutoRepair", false) ?? ContentFinder<Texture2D>.Get("UI/Designators/Haul", false);
                }
                return icon;
            }
        }

        private static void Postfix(UnfinishedThing __instance, ref IEnumerable<Gizmo> __result)
        {
            try
            {
                UnfinishedThing uft = __instance;
                if (uft == null || !uft.Spawned || uft.Faction != null && uft.Faction != Faction.OfPlayer) return;
                var cmd = new Command_Action
                {
                    defaultLabel = "Finish this",
                    defaultDesc = "Send the most capable colonist (or the most capable selected colonist) to finish this unfinished item now, whatever state its bill is in.",
                    icon = Icon,
                    action = delegate { Run(uft); }
                };
                __result = __result.Concat(new Gizmo[] { cmd });
            }
            catch (Exception ex)
            {
                Logger.Warning("FinishThisGizmoPatch failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Run(UnfinishedThing uft)
        {
            List<Pawn> sel = Find.Selector.SelectedPawns.Where(p => p.IsColonistPlayerControlled).ToList();
            string why;
            Logger.Always("Finish this pressed on " + uft.ThingID + " with " + (sel.Count > 0 ? sel.Count + " selected pawn(s)" : "no pawns selected"));
            if (UnfinishedFinisher.TryOrder(uft, sel.Count > 0 ? sel : null, true, out why))
                Messages.Message(why, uft, MessageTypeDefOf.TaskCompletion, false);
            else
            {
                Logger.Always("Finish this refused on " + uft.ThingID + ": " + why);
                Messages.Message("Cannot finish " + uft.LabelNoCount + ": " + why, uft, MessageTypeDefOf.RejectInput, false);
            }
        }
    }
}
