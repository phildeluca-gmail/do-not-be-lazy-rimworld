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
        // bar is set above that.
        //
        // It was 25 until 2026-09-10, then 12, and both numbers were too high
        // to ever be reached. Pawn_JobTracker.StartJob stops a pawn when
        // jobsGivenThisTick is over 10, so the eleventh job in the same tick
        // prints "started 10 jobs in one tick" and the pawn is given a wait
        // job instead. Eleven jobs at the same target is therefore the most
        // this counter can ever see in one tick, and asking for twelve asked
        // for one more than the game allows. Read out of the decompiled
        // StartJob on 2026-09-11, not from memory.
        private const int RepeatsBeforeSuspect = 10;

        // Repeats only count while they keep arriving. A pawn that takes the
        // same target twice an hour is not looping.
        //
        // This was 60 ticks, one second, until 2026-09-11, and one second was
        // too short. RimWorld cuts the pawn off after ten jobs in a tick, and
        // the next ten do not arrive until three to six seconds later - so
        // every group of ten was counted on its own and the count was thrown
        // away before the next group arrived. Jerbear produced nine such
        // groups on Thing_Bedroll9043738 between 22:16:42 and 22:17:18 and the
        // count never got past eleven. Ten seconds holds the count across
        // them.
        private const int WindowTicks = 600;

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
                // A job aimed at nothing tells us nothing, so leave the count
                // where it is rather than throwing it away.
                //
                // WHY. This line used to be streaks.Remove(pawn) and it is why
                // the watch recorded nothing on 2026-09-08 while a loop ran all
                // evening. Vanilla stops a pawn at 10 jobs in one tick and
                // immediately issues a recovery Wait, which has no target - so
                // the count reached about eleven, was wiped, and started again
                // at one. The count now ends only when the window below lapses
                // or a different thing turns up.
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
