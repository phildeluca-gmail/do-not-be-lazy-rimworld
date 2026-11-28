using System.Collections.Generic;
using DoNotBeLazy.Core;
using RimWorld;
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
            public int firstTick;   // tick of the first start in this run, for the span in the warning
            public string giver;
        }

        private static readonly Dictionary<Pawn, Streak> streaks = new Dictionary<Pawn, Streak>();

        // Everything we have caught, newest last. Keyed by the thing so one
        // bedroll caught for two pawns is one entry.
        private static readonly List<Thing> suspects = new List<Thing>();

        private static readonly Dictionary<Thing, string> suspectBlame = new Dictionary<Thing, string>();

        // 2026-11-23 order "2 yes 3 yes" (A): a short per-pawn history of job
        // starts and ends, kept only so the one warning line can say how each
        // repeated job ended and what came in between. Never logged per entry;
        // read once, when the warning fires. Fixed size, no allocation per event.
        private const int HistorySize = 24;

        private struct HistoryEntry
        {
            public int tick;
            public bool isEnd;
            public JobDef def;
            public LocalTargetInfo target;
            public System.Type giver;
            public JobCondition condition;
        }

        private class History
        {
            public readonly HistoryEntry[] entries = new HistoryEntry[HistorySize];
            public int next;
            public int count;
        }

        private static readonly Dictionary<Pawn, History> histories = new Dictionary<Pawn, History>();

        // Clear() is never called, so the history would outlive a loaded game
        // and keep every dead pawn. Dropped on a game change (reference compare
        // per event) and pruned of dead, destroyed or non-player pawns when a
        // new pawn is added past PruneAbove - never on the common path.
        private const int PruneAbove = 64;
        private static Game historyGame;
        private static readonly List<Pawn> pruneScratch = new List<Pawn>();

        private static void Record(Pawn pawn, HistoryEntry entry)
        {
            if (historyGame != Current.Game)
            {
                histories.Clear();
                streaks.Clear();
                historyGame = Current.Game;
            }

            if (!histories.TryGetValue(pawn, out History h))
            {
                if (histories.Count >= PruneAbove)
                {
                    Prune();
                }

                h = new History();
                histories[pawn] = h;
            }

            h.entries[h.next] = entry;
            h.next = (h.next + 1) % HistorySize;
            if (h.count < HistorySize) h.count++;
        }

        private static void Prune()
        {
            pruneScratch.Clear();
            foreach (Pawn p in histories.Keys)
            {
                if (IsStale(p)) pruneScratch.Add(p);
            }

            for (int i = 0; i < pruneScratch.Count; i++)
            {
                histories.Remove(pruneScratch[i]);
            }

            pruneScratch.Clear();
        }

        // Same treatment for streaks (keyed by pawn alone): dropped on a game
        // change in Record, pruned here when a new pawn is added past PruneAbove.
        private static void PruneStreaks()
        {
            pruneScratch.Clear();
            foreach (Pawn p in streaks.Keys)
            {
                if (IsStale(p)) pruneScratch.Add(p);
            }

            for (int i = 0; i < pruneScratch.Count; i++)
            {
                streaks.Remove(pruneScratch[i]);
            }

            pruneScratch.Clear();
        }

        private static bool IsStale(Pawn p)
        {
            return p == null || p.Destroyed || p.Dead || p.Faction == null || !p.Faction.IsPlayer;
        }

        // Called from JobTrackerPatch.Prefix, before the ended job is pooled
        // (Job.Clear nulls its def). Same hook EndCurrentJob already has; no new patch.
        public static void Notify_JobEnding(Pawn pawn, Job job, JobCondition condition)
        {
            if (pawn == null || job == null || job.def == null || pawn.Faction == null || !pawn.Faction.IsPlayer)
            {
                return;
            }

            Record(pawn, new HistoryEntry
            {
                tick = Find.TickManager.TicksGame,
                isEnd = true,
                def = job.def,
                target = job.targetA,
                condition = condition
            });
        }

        private static string DescribeHistory(Pawn pawn, int now)
        {
            if (!histories.TryGetValue(pawn, out History h) || h.count == 0)
            {
                return "no history";
            }

            var sb = new System.Text.StringBuilder();
            int start = (h.next - h.count + HistorySize) % HistorySize;
            for (int i = 0; i < h.count; i++)
            {
                HistoryEntry e = h.entries[(start + i) % HistorySize];
                if (now - e.tick > WindowTicks)
                {
                    continue;
                }

                if (sb.Length > 0) sb.Append("; ");
                sb.Append("t-").Append(now - e.tick).Append(' ');
                sb.Append(e.isEnd ? "end " : "start ").Append(e.def?.defName ?? "?");
                Thing t = e.target.Thing;
                sb.Append(" -> ").Append(t != null ? t.ThingID : e.target.IsValid ? e.target.Cell.ToString() : "none");
                if (e.isEnd) sb.Append(" ").Append(e.condition);
                else if (e.giver != null) sb.Append(" from ").Append(e.giver.Name);
            }

            return sb.Length == 0 ? "no history in window" : sb.ToString();
        }

        private static string DescribeOwners(Thing target)
        {
            if (!(target is Building_Bed bed))
            {
                return null;
            }

            var owners = bed.OwnersForReading;
            if (owners == null || owners.Count == 0)
            {
                return "bed owners: none";
            }

            var sb = new System.Text.StringBuilder("bed owners: ");
            for (int i = 0; i < owners.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(owners[i]?.LabelShort ?? "?");
            }

            return sb.ToString();
        }

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
            histories.Clear();
        }

        // Called from JobLoopPatch on every job start. Must stay O(1) and must
        // never throw - this is one of the hottest paths in the game.
        public static void Notify_JobStarted(Pawn pawn, Job job, ThinkNode jobGiver)
        {
            if (pawn == null || job == null || pawn.Faction == null || !pawn.Faction.IsPlayer)
            {
                return;
            }

            // 2026-09-28: a vanilla social fight is not a loop - every punch is
            // its own job, so two pawns trade JobDefOf.SocialFight jobs at each
            // other dozens of times in a normal fight and both sides tripped
            // this warning. Skipped by the job's own def, which names the fight
            // itself regardless of which ThinkNode issued it - verified against
            // RimWorld.JobDefOf.SocialFight by reflection on lib\Assembly-CSharp.dll,
            // more robust than matching the giver's type name.
            if (job.def == JobDefOf.SocialFight)
            {
                return;
            }

            // 2026-10-04: a tantrum is not a loop either - the pawn smashes
            // things one job after another by design. JobDefOf has no Tantrum
            // field; JobGiver_Tantrum issues ordinary jobs, so the job def
            // cannot name it. Skipped by the pawn's mental state instead:
            // verified by reflection on lib\Assembly-CSharp.dll that
            // MentalState_TantrumAll, _TantrumRandom, _BedroomTantrum,
            // _SadisticRageTantrum and _TargetedTantrum all derive from
            // Verse.AI.MentalState_Tantrum, and that Pawn.MentalState exists.
            if (pawn.MentalState is MentalState_Tantrum)
            {
                return;
            }

            // 2026-11-10: a pawn or animal fighting back in melee is not a loop -
            // JobGiver_ReactToCloseMeleeThreat issues one attack job per swing
            // at the same attacker. Skipped by the issuing giver; the type name
            // was verified present in lib\Assembly-CSharp.dll and is referenced
            // directly, so a missing type fails the build.
            if (jobGiver is JobGiver_ReactToCloseMeleeThreat)
            {
                return;
            }

            Record(pawn, new HistoryEntry
            {
                tick = Find.TickManager.TicksGame,
                isEnd = false,
                def = job.def,
                target = job.targetA,
                giver = jobGiver?.GetType()
            });

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
                if (streaks.Count >= PruneAbove && !streaks.ContainsKey(pawn))
                {
                    PruneStreaks();
                }

                streaks[pawn] = new Streak { target = target, count = 1, lastTick = now, firstTick = now, giver = giver };
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
                $"over {now - s.firstTick} ticks (each restart within {WindowTicks} ticks of the last), from {giver} ({AssemblyNameOf(jobGiver)}). " +
                "Select any colonist and use \"Find job loop\" to jump to it.");

            // Same event, second line: who owns the bed and how each recent job
            // started and ended (oldest first, in the window).
            // Guarded: this runs inside StartJob's postfix, and a bed without
            // CompAssignableToPawn would throw from OwnersForReading.
            try
            {
                string owners = DescribeOwners(target);
                Logger.Warning($"job loop detail: {pawn.LabelShort}" + (owners != null ? $", {owners}" : "")
                    + $". Recent jobs: {DescribeHistory(pawn, now)}");
            }
            catch (System.Exception ex)
            {
                Logger.Warning($"job loop detail: {pawn.LabelShort}: could not be built ({ex.GetType().Name}: {ex.Message})");
            }
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
