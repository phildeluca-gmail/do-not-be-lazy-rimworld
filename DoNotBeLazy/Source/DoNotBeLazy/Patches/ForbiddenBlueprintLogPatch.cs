using System;
using System.Diagnostics;
using System.Text;
using DoNotBeLazy.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DoNotBeLazy.Patches
{
    // Diagnostic only (dnbl-architecture.md section 42). Postfix on the
    // CompForbiddable.Forbidden setter, the only writer of forbiddenInt
    // during play besides PostPostMake (verified 2026-11-08). PostExposeData
    // also writes it when a save loads, bypassing the setter, so a structure
    // already forbidden in a save writes no line until it is allowed and
    // forbidden again. When an owned Building, Blueprint or Frame changes from allowed
    // to forbidden, writes one line to DoNotBeLazy.log with the stack, so the
    // forbidding mod or method is named. Logs every real change, so a thing
    // forbidden again after being allowed logs again; changes no behaviour.
    [HarmonyPatch(typeof(CompForbiddable), nameof(CompForbiddable.Forbidden), MethodType.Setter)]
    internal static class ForbiddenBlueprintLogPatch
    {
        // Prefix: the setter returns early when the value is unchanged, but the
        // postfix still runs. Capture the old value so only a real change logs.
        private static void Prefix(CompForbiddable __instance, out bool __state)
        {
            __state = __instance.Forbidden;
        }

        private static void Postfix(CompForbiddable __instance, bool value, bool __state)
        {
            try
            {
                if (__state || !value) return;
                Thing t = __instance.parent;
                // Structural = Building with the forbid component (doors, turrets and
                // other buildings), plus Blueprint and Frame. Unowned ones are skipped.
                if (!(t is Building) && !(t is Blueprint) && !(t is Frame)) return;
                if (t.Faction == null) return;
                if (!LogFile.Available) return;

                string map = t.Map != null ? "map " + t.Map.uniqueID : "no map";
                string selected = "none";
                if (Find.Selector != null && Find.Selector.SingleSelectedThing is Pawn p)
                {
                    selected = p.LabelShort + " job " + (p.CurJob != null ? p.CurJob.def.defName : "none");
                }

                var sb = new StringBuilder();
                var frames = new StackTrace(1, false).GetFrames();
                int shown = 0;
                if (frames != null)
                {
                    foreach (StackFrame f in frames)
                    {
                        var m = f.GetMethod();
                        if (m == null || m.DeclaringType == null) continue;
                        if (m.DeclaringType == typeof(ForbiddenBlueprintLogPatch)) continue;
                        string name = m.DeclaringType.FullName + "." + m.Name;
                        if (name.StartsWith("HarmonyLib") || name.StartsWith("System.Reflection") || name.Contains("DMD<")) continue;
                        sb.Append(shown == 0 ? "" : " <- ").Append(name);
                        if (++shown >= 8) break;
                    }
                }

                LogFile.Write("FBD ", "Forbidden: " + t.LabelCap + " (" + t.ThingID + ") at " + t.Position + ", " + map
                    + ", owner " + t.Faction.Name + ", tick " + Find.TickManager.TicksGame + ", selected pawn: " + selected + ", stack: " + sb);
            }
            catch (Exception e)
            {
                Verse.Log.Warning("[DoNotBeLazy] ForbiddenBlueprintLogPatch: " + e.Message);
            }
        }
    }
}
