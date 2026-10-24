using System;
using System.Collections.Generic;
using System.Linq;
using DoNotBeLazy.Jobs;
using RimWorld;
using Verse;
using Logger = DoNotBeLazy.Core.Logger;

namespace DoNotBeLazy.Components
{
    // Item ledger - dnbl-architecture.md section 24, ordered 2026-10-11:
    // "When we cause a pawn to add to its inventory, we must track that item
    // being delivered."
    //
    // Every stack Do Not Be Lazy puts into a pawn's inventory gets an id
    // (ITEM DNBL-HHmm-n, the same launch stamp as ORDER ids) and three kinds
    // of line, written whether or not verbose logging is on:
    //   ITEM id in: <pawn> <item> x<n> (order <ORDER id> | no order)
    //   ITEM id part: <what happened>; <n> still open        (stays open)
    //   ITEM id ended (<reason>): <pawn> <item> x<n> <detail>
    //
    // Tracking is by COUNT per Thing reference, not by identity of one stack.
    // An entry owns a set of parts (Thing -> count it last saw). Pickup never
    // merges into an existing pack stack (JobDriver_StuffAndHaul.PickUpToil
    // passes canMergeWithExistingStacks false), so an entry starts as one
    // part. Lifting it to the hands can split it: Split() adds the new Thing
    // as a second part of the same entry. A merge on the ground or into a
    // container destroys the delivered Thing, so deliveries are logged from
    // the count recorded BEFORE the placement, never from the Thing after it.
    // A partial delivery lowers the part's count and leaves the rest open.
    //
    // Endings the hooks cannot see (eaten, used, dropped by Interface
    // Changes, pawn downed or dead) are found by Reconcile, which runs every
    // 250 ticks and only while the ledger is non-empty; it skips a pawn that
    // is mid stuffing job, where items are legitimately between places.
    // Nothing here runs per tick per item and every line is per event.
    public class ItemLedger : GameComponent
    {
        private class Entry
        {
            public string id;
            public Pawn pawn;
            public string pawnLabel;
            public string label;
            public string orderId;
            public string lastSeen;
            public int notedJob = -1;
            public readonly Dictionary<Thing, int> parts = new Dictionary<Thing, int>();
        }

        private const int ReconcileIntervalTicks = 250;
        private static readonly string launchStamp = DateTime.Now.ToString("HHmm");
        private static int counter;
        private static readonly List<Entry> open = new List<Entry>();
        private static readonly Dictionary<Thing, Entry> owner = new Dictionary<Thing, Entry>();
        private static bool tickFaultLogged;

        // RimWorld builds every GameComponent when a game is created or
        // loaded. Anything still open belongs to the previous game.
        public ItemLedger(Game game)
        {
            foreach (Entry e in open.ToList())
            {
                Close(e, "game loaded", $"{e.pawnLabel} {e.label} x{Remaining(e)} - the ledger restarts with the game");
            }
            open.Clear();
            owner.Clear();
            JobDriver_StuffAndHaul.ResetLogThrottles();
        }

        // ---- open entries survive a game load - architecture section 26 ----
        // Ordered 2026-10-12: "Make sure the open orders get picked up again
        // when the game restarts." Each open entry is saved with its id, so
        // the log reads "resumed" under the same ITEM id after a load.
        public class Row : IExposable
        {
            public string id;
            public Pawn pawn;
            public string pawnLabel;
            public string label;
            public string orderId;
            public string lastSeen;
            public List<Thing> things = new List<Thing>();
            public List<int> counts = new List<int>();

            public void ExposeData()
            {
                Scribe_Values.Look(ref id, "id");
                Scribe_References.Look(ref pawn, "pawn");
                Scribe_Values.Look(ref pawnLabel, "pawnLabel");
                Scribe_Values.Look(ref label, "label");
                Scribe_Values.Look(ref orderId, "orderId");
                Scribe_Values.Look(ref lastSeen, "lastSeen");
                Scribe_Collections.Look(ref things, "things", LookMode.Reference);
                Scribe_Collections.Look(ref counts, "counts", LookMode.Value);
                if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit)
                {
                    things = things ?? new List<Thing>();
                    counts = counts ?? new List<int>();
                }
            }
        }

        // Scribed straight into this field and passed every pass; a local
        // would be lost between LoadingVars and FinalizeInit.
        private List<Row> loadedRows;

        // Total for the one game-wide load line (LeftoverCargo.LoadedGame); reset in its constructor.
        public static int LoadSummaryItemRows;

        public override void ExposeData()
        {
            bool saving = Scribe.mode == LoadSaveMode.Saving;
            if (saving)
            {
                List<Row> rows = new List<Row>();
                foreach (Entry e in open)
                {
                    if (e.pawn == null || e.pawn.Destroyed || e.pawn.Discarded)
                    {
                        continue;
                    }
                    var row = new Row { id = e.id, pawn = e.pawn, pawnLabel = e.pawnLabel, label = e.label, orderId = e.orderId, lastSeen = e.lastSeen };
                    foreach (KeyValuePair<Thing, int> part in e.parts)
                    {
                        if (part.Key == null || part.Key.Destroyed || part.Key.Discarded)
                        {
                            continue;
                        }
                        row.things.Add(part.Key);
                        row.counts.Add(part.Value);
                    }
                    rows.Add(row);
                }
                loadedRows = rows;
            }

            if (!saving || loadedRows.Count > 0)
            {
                Scribe_Collections.Look(ref loadedRows, "dnblLedger", LookMode.Deep);
            }

            if (saving)
            {
                loadedRows = null;
            }
        }

        // Runs after every pass, so each row's pawn and things are resolved.
        public override void FinalizeInit()
        {
            base.FinalizeInit();

            List<Row> rows = loadedRows;
            loadedRows = null;
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            LoadSummaryItemRows += rows.Count;

            Logger.Message($"Load: read {rows.Count} saved ITEM row(s); restoring");
            foreach (Row row in rows)
            {
                try
                {
                    Restore(row);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Restoring ITEM {row?.id} after load threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static void Restore(Row row)
        {
            var e = new Entry
            {
                id = row.id ?? "DNBL-" + launchStamp + "-" + (++counter),
                pawn = row.pawn,
                pawnLabel = row.pawnLabel ?? row.pawn?.LabelShort ?? "(a pawn that no longer exists)",
                label = row.label ?? "item",
                orderId = row.orderId,
                lastSeen = row.lastSeen ?? "before the game was saved"
            };

            int total = 0;
            for (int i = 0; i < row.things.Count; i++)
            {
                Thing t = row.things[i];
                int count = i < row.counts.Count ? row.counts[i] : 0;
                if (t == null || t.Destroyed || count <= 0 || owner.ContainsKey(t))
                {
                    continue;
                }
                e.parts[t] = count;
                total += count;
            }

            string forOrder = e.orderId == null ? "no order" : "order " + e.orderId;
            if (e.pawn == null || e.pawn.Destroyed || e.pawn.Discarded || e.pawn.Dead)
            {
                Logger.Order($"ITEM {e.id} ended (game loaded: pawn {(e.pawn != null && e.pawn.Dead ? "dead" : "gone")}): {e.pawnLabel} {e.label} x{total} (last seen: {e.lastSeen})");
                return;
            }
            if (e.parts.Count == 0)
            {
                Logger.Order($"ITEM {e.id} ended (game loaded: the item no longer exists): {e.pawnLabel} {e.label} (last seen: {e.lastSeen})");
                return;
            }

            open.Add(e);
            foreach (Thing t in e.parts.Keys)
            {
                owner[t] = e;
            }
            Logger.Order($"ITEM {e.id} resumed: {e.pawnLabel} {e.label} x{total} after game load ({forOrder})");
        }

        public override void GameComponentTick()
        {
            if (open.Count == 0 || Find.TickManager.TicksGame % ReconcileIntervalTicks != 0)
            {
                return;
            }

            try
            {
                Reconcile();
            }
            catch (Exception ex)
            {
                if (!tickFaultLogged)
                {
                    tickFaultLogged = true;
                    Logger.Warning($"ItemLedger.Reconcile threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // ---- hooks -------------------------------------------------------

        // The single place the mod adds to an inventory (PickUpToil).
        public static void Added(Pawn pawn, Thing item, string orderId)
        {
            if (pawn == null || item == null)
            {
                return;
            }
            Entry e = NewEntry(pawn, item, orderId);
            Logger.Order($"ITEM {e.id} in: {e.pawnLabel} {e.label} x{item.stackCount} ({(orderId == null ? "no order" : "order " + orderId)})");
        }

        // Gives an item that is already in the pack, but unknown to the ledger
        // (put there before a game load), an entry now, while its count is
        // still right.
        public static void Ensure(Pawn pawn, Thing item)
        {
            if (pawn != null && item != null && !owner.ContainsKey(item))
            {
                Lookup(pawn, item, adopt: true);
            }
        }

        // The count the ledger last recorded for this Thing, 0 if unknown.
        public static int CountOf(Thing thing)
        {
            return thing != null && owner.TryGetValue(thing, out Entry e) && e.parts.TryGetValue(thing, out int n) ? n : 0;
        }

        // `to` was split off `from` (lifting a stack to the hands that the
        // hands could only partly hold). Same entry, two parts.
        public static void Split(Thing from, Thing to)
        {
            if (from == null || to == null || !owner.TryGetValue(from, out Entry e))
            {
                return;
            }
            if (from.Destroyed || from.stackCount <= 0)
            {
                e.parts.Remove(from);
                owner.Remove(from);
            }
            else
            {
                e.parts[from] = from.stackCount;
            }
            e.parts[to] = to.stackCount;
            owner[to] = e;
        }

        // `count` of the part was delivered to `where`. Partial leaves the rest open.
        public static void Delivered(Pawn pawn, Thing thing, int count, string where)
        {
            Entry e = Lookup(pawn, thing, adopt: true);
            if (e == null || count <= 0)
            {
                return;
            }
            e.parts.TryGetValue(thing, out int have);
            if (count >= have)
            {
                count = have;
                PartEnds(e, thing, count, "delivered", "to " + where);
                return;
            }
            e.parts[thing] = have - count;
            Logger.Order($"ITEM {e.id} part: {e.pawnLabel} delivered {e.label} x{count} to {where}; {Remaining(e)} still open");
        }

        // Called right after an attempted drop: closes the part when it left
        // the pawn. `adopt` is true on DNBL's own cargo paths, where an item
        // carried over from before a game load has no entry yet.
        public static void Dropped(Pawn pawn, Thing thing, string why, bool adopt)
        {
            try
            {
                DroppedCore(pawn, thing, why, adopt);
            }
            catch (Exception ex)
            {
                // Must never break job cleanup (finish actions call this).
                Logger.Warning($"ItemLedger.Dropped threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void DroppedCore(Pawn pawn, Thing thing, string why, bool adopt)
        {
            Entry e = Lookup(pawn, thing, adopt);
            if (e == null || !e.parts.TryGetValue(thing, out int have))
            {
                return;
            }
            if (HeldWhere(pawn, thing) != null)
            {
                e.lastSeen = "still held after a drop attempt (" + why + ")";
                return;
            }
            string at = thing.Spawned ? thing.Position.ToString() : pawn.Position.ToString();
            PartEnds(e, thing, have, "dropped near pawn", $"at {at}: {why}");
        }

        // An order that put this item on the ledger was withdrawn before the
        // item moved (leftover bulk delivery refused or expired). Ends the
        // part with the reason; the item itself stays in the pack.
        public static void Released(Pawn pawn, Thing thing, string reason)
        {
            if (thing != null && owner.TryGetValue(thing, out Entry e) && e.parts.TryGetValue(thing, out int have))
            {
                PartEnds(e, thing, have, reason, "- still in the pack of " + e.pawnLabel);
            }
        }

        // Last known place of an item, for the vanished-item ending.
        public static void NoteSeen(Thing thing, string where)
        {
            if (thing != null && owner.TryGetValue(thing, out Entry e))
            {
                e.lastSeen = where;
            }
        }

        // Every job end path of the stuffing driver (its finish action runs
        // for success, interruption, replacement and exception alike): any
        // item of this pawn still held is said to be still held, once per job.
        public static void JobEnded(Pawn pawn, int jobId, string condition)
        {
            try
            {
                JobEndedCore(pawn, jobId, condition);
            }
            catch (Exception ex)
            {
                // Must never break job cleanup (finish actions call this).
                Logger.Warning($"ItemLedger.JobEnded threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void JobEndedCore(Pawn pawn, int jobId, string condition)
        {
            foreach (Entry e in open)
            {
                if (e.pawn != pawn || e.notedJob == jobId)
                {
                    continue;
                }
                int held = 0;
                foreach (var p in e.parts)
                {
                    if (HeldWhere(pawn, p.Key) != null)
                    {
                        held += p.Key.stackCount;
                    }
                }
                if (held > 0)
                {
                    e.notedJob = jobId;
                    Logger.Order($"ITEM {e.id} part: {e.pawnLabel} {e.label} x{held} still in inventory when the stuffing job ended ({condition}); stays open until a later job delivers it or it leaves the pack; {Remaining(e)} still open");
                }
            }
        }

        // ---- internals ---------------------------------------------------

        private static Entry NewEntry(Pawn pawn, Thing item, string orderId)
        {
            var e = new Entry
            {
                id = "DNBL-" + launchStamp + "-" + (++counter),
                pawn = pawn,
                pawnLabel = pawn.LabelShort,
                label = item.LabelNoCount,
                orderId = orderId,
                lastSeen = "in inventory of " + pawn.LabelShort
            };
            e.parts[item] = item.stackCount;
            open.Add(e);
            owner[item] = e;
            return e;
        }

        private static Entry Lookup(Pawn pawn, Thing thing, bool adopt)
        {
            if (thing == null)
            {
                return null;
            }
            if (owner.TryGetValue(thing, out Entry e))
            {
                return e;
            }
            if (!adopt || pawn == null)
            {
                return null;
            }
            e = NewEntry(pawn, thing, null);
            Logger.Order($"ITEM {e.id} in: {e.pawnLabel} {e.label} x{thing.stackCount} (adopted: put in the pack before the game was loaded; no order)");
            return e;
        }

        private static int Remaining(Entry e)
        {
            return e.parts.Values.Sum();
        }

        private static string HeldWhere(Pawn pawn, Thing t)
        {
            if (pawn == null || t == null)
            {
                return null;
            }
            if (pawn.inventory != null && pawn.inventory.innerContainer.Contains(t))
            {
                return "inventory";
            }
            if (pawn.carryTracker != null && pawn.carryTracker.innerContainer.Contains(t))
            {
                return "hands";
            }
            return null;
        }

        private static void PartEnds(Entry e, Thing t, int count, string reason, string detail)
        {
            e.parts.Remove(t);
            owner.Remove(t);
            if (e.parts.Count == 0)
            {
                Close(e, reason, $"{e.pawnLabel} {e.label} x{count} {detail}");
            }
            else
            {
                Logger.Order($"ITEM {e.id} part: {e.pawnLabel} {e.label} x{count} {reason} {detail}; {Remaining(e)} still open");
            }
        }

        private static void Close(Entry e, string reason, string detail)
        {
            Logger.Order($"ITEM {e.id} ended ({reason}): {detail}");
            foreach (Thing t in e.parts.Keys)
            {
                owner.Remove(t);
            }
            e.parts.Clear();
            open.Remove(e);
        }

        private static void Reconcile()
        {
            foreach (Entry e in open.ToList())
            {
                Pawn p = e.pawn;
                if (p == null || p.Destroyed || p.Dead)
                {
                    Close(e, p == null || p.Destroyed ? "pawn gone" : "pawn dead", $"{e.pawnLabel} {e.label} x{Remaining(e)} (last seen: {e.lastSeen})");
                    continue;
                }
                if (p.Downed)
                {
                    Close(e, "pawn downed", $"{e.pawnLabel} {e.label} x{Remaining(e)} (last seen: {e.lastSeen})");
                    continue;
                }
                if (p.jobs?.curDriver is JobDriver_StuffAndHaul)
                {
                    continue;
                }

                foreach (KeyValuePair<Thing, int> part in e.parts.ToList())
                {
                    Thing t = part.Key;
                    int was = part.Value;
                    string held = HeldWhere(p, t);
                    if (held != null && !t.Destroyed)
                    {
                        e.lastSeen = held == "inventory" ? "in inventory of " + e.pawnLabel : "in the hands of " + e.pawnLabel;
                        int now = t.stackCount;
                        if (now < was)
                        {
                            e.parts[t] = now;
                            Logger.Order($"ITEM {e.id} part: {e.pawnLabel} {e.label} x{was - now} used or taken by something else, {now} of this stack still held; {Remaining(e)} still open");
                        }
                        continue;
                    }
                    if (t.Destroyed || (t.stackCount <= 0 && !t.Spawned))
                    {
                        PartEnds(e, t, was, "destroyed or used up", $"(last seen: {e.lastSeen})");
                    }
                    else if (t.Spawned)
                    {
                        PartEnds(e, t, was, "taken out by something else", $"- now lying at {t.Position} (not delivered by DNBL)");
                    }
                    else
                    {
                        string holder = t.ParentHolder == null ? "nothing" : t.ParentHolder.GetType().Name;
                        PartEnds(e, t, was, "taken out by something else", $"- now held by {holder} (last seen: {e.lastSeen})");
                    }
                }
            }
        }
    }
}
