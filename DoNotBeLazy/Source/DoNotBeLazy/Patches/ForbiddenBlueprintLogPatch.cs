using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // forbidding mod or method is named. Buildings log every real change; a
    // Blueprint or Frame logs once. Changes no behaviour.
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
                // other buildings), plus Blueprint and Frame.
                bool plan = t is Blueprint || t is Frame;
                if (!(t is Building) && !plan) return;
                // Buildings: unowned skipped (user order 10). Blueprints and Frames:
                // unowned kept when sitting in the player's home area.
                if (t.Faction == null && !(plan && FbdLog.UnownedInHome(t))) return;
                if (!LogFile.Available) return;
                if (plan && !FbdLog.FirstFor(t, "set")) return;
                if (!FbdLog.TickBudget()) return;

                string map = t.Map != null ? "map " + t.Map.uniqueID : "no map";
                string selected = "none";
                if (Find.Selector != null && Find.Selector.SingleSelectedThing is Pawn p)
                {
                    selected = p.LabelShort + " job " + (p.CurJob != null ? p.CurJob.def.defName : "none");
                }

                LogFile.Write("FBD ", "Forbidden: " + t.LabelCap + " (" + t.ThingID + ") at " + t.Position + ", " + map
                    + ", owner " + FbdLog.Owner(t) + ", tick " + Find.TickManager.TicksGame + ", selected pawn: " + selected
                    + ", stack: " + FbdLog.Stack());
            }
            catch (Exception e)
            {
                Verse.Log.Warning("[DoNotBeLazy] ForbiddenBlueprintLogPatch: " + e.Message);
            }
        }
    }

    // Section 42 widening, 2026-11-15. Postfix on CompForbiddable.PostSpawnSetup:
    // logs a player-owned (or unowned-in-home) Blueprint or Frame that is already
    // forbidden when it spawns, which is how auto-rebuild blueprints arrive.
    // Skipped when respawning after a load. Changes no behaviour.
    [HarmonyPatch(typeof(CompForbiddable), nameof(CompForbiddable.PostSpawnSetup))]
    internal static class ForbiddenSpawnLogPatch
    {
        private static void Postfix(CompForbiddable __instance, bool respawningAfterLoad)
        {
            try
            {
                if (respawningAfterLoad || !__instance.Forbidden) return;
                Thing t = __instance.parent;
                if (!(t is Blueprint) && !(t is Frame)) return;
                if (t.Faction != Faction.OfPlayer && !(t.Faction == null && FbdLog.UnownedInHome(t))) return;
                if (!LogFile.Available || !FbdLog.FirstFor(t, "spawn") || !FbdLog.TickBudget()) return;
                LogFile.Write("FBD ", "Spawned forbidden: " + t.def.defName + " (" + t.ThingID + ") at " + t.Position
                    + ", owner " + FbdLog.Owner(t) + ", tick " + Find.TickManager.TicksGame + ", stack: " + FbdLog.Stack());
            }
            catch (Exception e)
            {
                Verse.Log.Warning("[DoNotBeLazy] ForbiddenSpawnLogPatch: " + e.Message);
            }
        }
    }

    // Postfix on CompForbiddable.PostPostMake: PostPostMake writes forbiddenInt
    // directly (bypassing the setter) when Props.forbidOnMake. Logs that for
    // Blueprints and Frames. Faction is usually not yet set at creation, so no
    // owner filter here. Changes no behaviour.
    [HarmonyPatch(typeof(CompForbiddable), nameof(CompForbiddable.PostPostMake))]
    internal static class ForbiddenMakeLogPatch
    {
        private static void Postfix(CompForbiddable __instance)
        {
            try
            {
                Thing t = __instance.parent;
                if (!(t is Blueprint) && !(t is Frame)) return;
                if (!__instance.Props.forbidOnMake || !__instance.Forbidden) return;
                if (!LogFile.Available || !FbdLog.FirstFor(t, "make") || !FbdLog.TickBudget()) return;
                LogFile.Write("FBD ", "Forbidden on make (forbidOnMake): " + t.def.defName + " (" + t.ThingID + "), owner "
                    + FbdLog.Owner(t) + ", tick " + (Find.TickManager != null ? Find.TickManager.TicksGame : -1)
                    + ", stack: " + FbdLog.Stack());
            }
            catch (Exception e)
            {
                Verse.Log.Warning("[DoNotBeLazy] ForbiddenMakeLogPatch: " + e.Message);
            }
        }
    }

    // Shared helpers for the three forbid-logging patches: once per thing per
    // source, a cap of 20 lines per game tick with one summary, a stack with
    // non-vanilla (mod) frames listed first.
    internal static class FbdLog
    {
        private const int PerTick = 20;
        private static readonly HashSet<string> seen = new HashSet<string>();
        private static int tickOf = -1;
        private static int inTick;

        public static bool FirstFor(Thing t, string source)
        {
            if (seen.Count > 5000) seen.Clear();
            return seen.Add(source + t.thingIDNumber);
        }

        public static bool TickBudget()
        {
            int tick = Find.TickManager != null ? Find.TickManager.TicksGame : -1;
            if (tick != tickOf) { tickOf = tick; inTick = 0; }
            inTick++;
            if (inTick <= PerTick) return true;
            if (inTick == PerTick + 1)
                LogFile.Write("FBD ", "More than " + PerTick + " forbid lines in tick " + tick + "; the rest of this tick is not logged");
            return false;
        }

        public static string Owner(Thing t)
        {
            return t.Faction != null ? t.Faction.Name : "unowned";
        }

        // Unowned thing standing on a cell of the player's home area.
        public static bool UnownedInHome(Thing t)
        {
            Map m = t.Map;
            if (m == null || m.areaManager == null || m.areaManager.Home == null) return false;
            return m.areaManager.Home[t.Position];
        }

        private static bool Vanilla(string name)
        {
            return name.StartsWith("Verse.") || name.StartsWith("RimWorld.") || name.StartsWith("UnityEngine.")
                || name.StartsWith("System.");
        }

        public static string Stack()
        {
            var mods = new List<string>();
            var game = new List<string>();
            var frames = new StackTrace(2, false).GetFrames();
            if (frames != null)
            {
                foreach (StackFrame f in frames)
                {
                    var m = f.GetMethod();
                    if (m == null || m.DeclaringType == null) continue;
                    string name = m.DeclaringType.FullName + "." + m.Name;
                    if (name.StartsWith("HarmonyLib") || name.StartsWith("System.Reflection") || name.Contains("DMD<")) continue;
                    if (name.StartsWith("DoNotBeLazy.Patches.Forbidden") || name.StartsWith("DoNotBeLazy.Patches.FbdLog")) continue;
                    if (Vanilla(name)) { if (game.Count < 4) game.Add(name); }
                    else if (mods.Count < 5) mods.Add(name);
                }
            }
            return "mod: " + (mods.Count > 0 ? string.Join(" <- ", mods.ToArray()) : "none")
                + " | game: " + string.Join(" <- ", game.ToArray());
        }
    }
}
