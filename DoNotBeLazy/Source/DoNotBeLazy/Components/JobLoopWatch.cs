using System.Collections.Generic;
using DoNotBeLazy.Core;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Components
{
    // Catches a pawn being handed the SAME job target over and over, and
    // remembers what it was, so the thing causing it can be found and looked
    // at instead of hunted for.
    //
    // WHY THIS EXISTS. On 2026-09-07 four colonists stood still for hours.
    // RimWorld says "Monkey started 10 jobs in one tick" and names the target
    // by id - Thing_Bedroll5438124 - and there is no way in the game to turn
    // a thing id into a place. Finding the first one took reading the save
    // file by hand. This turns that into a button.
    //
    // It is a DIAGNOSTIC and nothing else: it never issues, blocks or alters
    // a job, and it has no opinion about whose fault the loop is. Both loops
    // found so far belonged to another mod.
    public static class JobLoopWatch
    {
        // Same target this many times in a row, inside the window below,
        // before we call it a loop. A pawn legitimately re-takes a target a
        // few times - a haul that gets interrupted, a bill re-issued - so the
        // bar is set well above that. RimWorld's own guard fires at 10 in one
        // tick; this is deliberately less trigger-happy than that.
        private const int RepeatsBeforeSuspect = 25;

        // Repeats only count while they keep arriving. A pawn that takes the
        // same target twice an hour is not looping.
        private const int WindowTicks = 60;

        private struct Streak
        {
            public Thing target;
            public int count;
            public int lastTick;
            public string giver;
        }

        private static readonly Dictionary<Pawn, Streak> streaks = new Dictionary<Pawn, Streak>();

        // Everything we have caught, newest last. Keyed by the thing so one
        // bedroll caught for two pawns is one entry.
        private static readonly List<Thing> suspects = new List<Thing>();

        private static readonly Dictionary<Thing, string> suspectBlame = new Dictionary<Thing, string>();

        public static int SuspectCount => suspects.Count;

        public static IReadOnlyList<Thing> Suspects => suspects;

        public static string BlameFor(Thing thing)
        {
            return thing != null && suspectBlame.TryGetValue(thing, out string who) ? who : "unknown";
        }

        public static void Clear()
        {
            suspects.Clear();
            suspectBlame.Clear();
            streaks.Clear();
        }

        // Called from JobLoopPatch on every job start. Must stay O(1) and must
        // never throw - this is one of the hottest paths in the game.
        public static void Notify_JobStarted(Pawn pawn, Job job, ThinkNode jobGiver)
        {
            if (pawn == null || job == null || pawn.Faction == null || !pawn.Faction.IsPlayer)
            {
                return;
            }

            Thing target = job.targetA.Thing;
            if (target == null)
            {
                streaks.Remove(pawn);
                return;
            }

            int now = Find.TickManager.TicksGame;
            string giver = jobGiver == null ? "none" : jobGiver.GetType().Name;

            if (!streaks.TryGetValue(pawn, out Streak s)
                || s.target != target
                || now - s.lastTick > WindowTicks)
            {
                streaks[pawn] = new Streak { target = target, count = 1, lastTick = now, giver = giver };
                return;
            }

            s.count++;
            s.lastTick = now;
            s.giver = giver;
            streaks[pawn] = s;

            if (s.count != RepeatsBeforeSuspect || suspectBlame.ContainsKey(target))
            {
                return;
            }

            suspects.Add(target);
            suspectBlame[target] = giver + " (" + AssemblyNameOf(jobGiver) + ")";

            // ONE line, on the transition only. A line per repeat would be
            // thousands - the exact defect this whole class exists to find.
            Logger.Warning($"job loop: {pawn.LabelShort} took {DescribeThing(target)} {s.count} times " +
                $"in {WindowTicks} ticks, from {giver} ({AssemblyNameOf(jobGiver)}). " +
                "Select any colonist and use \"Find job loop\" to jump to it.");
        }

        public static string DescribeThing(Thing thing)
        {
            if (thing == null)
            {
                return "nothing";
            }

            string where = thing.Spawned
                ? thing.Position + " on " + MapNameOf(thing.Map)
                : "not spawned (held, minified or in a container)";

            return thing.LabelShort + " [" + thing.ThingID + "] at " + where;
        }

        private static string MapNameOf(Map map)
        {
            if (map == null)
            {
                return "no map";
            }

            return map.Parent == null ? "map " + map.Index : map.Parent.LabelCap.ToString();
        }

        private static string AssemblyNameOf(ThinkNode node)
        {
            return node == null
                ? "-"
                : node.GetType().Assembly.GetName().Name;
        }
    }
}
