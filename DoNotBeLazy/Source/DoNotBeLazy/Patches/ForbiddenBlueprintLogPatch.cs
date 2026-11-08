using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using DoNotBeLazy.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DoNotBeLazy.Patches
{
    // Diagnostic only (dnbl-architecture.md section 42). Postfix on the
    // CompForbiddable.Forbidden setter. ForbidUtility.SetForbidden ends in
    // that setter (verified by decompile), so one patch covers both. When a
    // player-faction Blueprint or Frame becomes forbidden, writes one line
    // to DoNotBeLazy.log with the stack, so the forbidding mod or method is
    // named. Once per thing; changes no behaviour.
    [HarmonyPatch(typeof(CompForbiddable), nameof(CompForbiddable.Forbidden), MethodType.Setter)]
    internal static class ForbiddenBlueprintLogPatch
    {
        private static readonly HashSet<int> Logged = new HashSet<int>();
        // Thing ids restart in another save or a new game, so the set is
        // emptied whenever the Game object changes, and capped.
        private static Game loggedFor;
        private const int LoggedCap = 5000;

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
                if (!ReferenceEquals(loggedFor, Current.Game) || Logged.Count >= LoggedCap)
                {
                    Logged.Clear();
                    loggedFor = Current.Game;
                }
                Thing t = __instance.parent;
                if (!(t is Blueprint) && !(t is Frame)) return;
                if (t.Faction != Faction.OfPlayer) return;
                if (!Logged.Add(t.thingIDNumber)) return;
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
                    + ", tick " + Find.TickManager.TicksGame + ", selected pawn: " + selected + ", stack: " + sb);
            }
            catch (Exception e)
            {
                Verse.Log.Warning("[DoNotBeLazy] ForbiddenBlueprintLogPatch: " + e.Message);
            }
        }
    }
}
