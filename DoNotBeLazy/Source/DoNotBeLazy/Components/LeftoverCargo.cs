using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DoNotBeLazy.Comps;
using DoNotBeLazy.Core;
using DoNotBeLazy.Jobs;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using UnityEngine;
using Logger = DoNotBeLazy.Core.Logger;

namespace DoNotBeLazy.Components
{
    // Leftover bulk goods - dnbl-architecture.md section 25, ordered
    // 2026-10-11: "A pawn that holds more than 2 of an item is probably
    // carrying bulk goods left over from a job. Detect that." Answers:
    // exclude what the pawn's inventory rules tell it to carry; deliver
    // them; check whenever a pawn gets a new job.
    //
    // Hook: JobLoopPatch (the existing postfix on Pawn_JobTracker.StartJob)
    // calls Notify_JobStarted, which only marks the pawn as due. The check
    // itself runs from GameComponentTick, never inside StartJob, so queueing
    // a job cannot re-enter the job tracker mid-start.
    //
    // Delivery reuses the cargo-only mode of JobDriver_StuffAndHaul: the
    // excess is split into its own stacks, registered in CompDnblCargo,
    // given ITEM ledger entries tied to a new ORDER id, and one stuffing
    // job is queued BEHIND the pawn's current job.
    //
    // Lines (always on, Logger.Order):
    //   ORDER id issued: <pawn> leftover bulk delivery - <item> x<excess> (holds <n>, rules keep <k>: <why>), ...
    //   ORDER id started: / ended (<reason>): ...
    public class LeftoverCargo : GameComponent
    {
        private class Pending
        {
            public Pawn pawn;
            public Job job;
            public string orderId;
            public string summary;
            public int createdTick;
            public readonly List<Thing> things = new List<Thing>();
        }

        // Excess above this many, per ThingDef, is leftover.
        private const int MinExcess = 3;
        private const int HourTicks = 2500;
        private const int MinGapTicks = 250;
        private const int PendingExpiryTicks = 7500;

        private struct Seen
        {
            public int tick;
            public string signature;
        }

        private static readonly HashSet<Pawn> due = new HashSet<Pawn>();
        private static readonly Dictionary<Pawn, Seen> lastDetection = new Dictionary<Pawn, Seen>();
        private static readonly Dictionary<Job, Pending> pending = new Dictionary<Job, Pending>();
        private static bool tickFaultLogged;

        // Leftover delivery jobs currently running (added in OnDeliveryStarted,
        // removed when the job ends). Only drops made by these are batched.
        private static readonly HashSet<Job> activeDeliveries = new HashSet<Job>();

        // Deliveries that have started and not yet ended, kept only so a
        // save can write them down (architecture section 26).
        private static readonly List<Pending> running = new List<Pending>();

        // Forced-drop batches, ordered 2026-10-11: when leftover delivery has
        // to drop goods beside the pawn because storage is full, drops pile up
        // per pawn and ONE summary line is written 5 real seconds after the
        // last drop (each new drop restarts the timer). Flushed on save, on
        // game load and on application quit so nothing is lost.
        private const float DropBatchQuietSeconds = 5f;
        private class DropBatch
        {
            public float lastDropTime;
            public readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        }
        private static readonly Dictionary<Pawn, DropBatch> dropBatches = new Dictionary<Pawn, DropBatch>();
        private static bool quitHooked;

        // Jobs that are themselves moving goods in or out of a pack.
        private static readonly HashSet<string> SkipJobDefs = new HashSet<string>
        {
            "UnloadYourInventory", "HaulToInventory", "TakeInventory", "DropInventory",
            // Pick Up And Haul (not in the 2026-10-11 mod list; no dependency)
            "UnloadYourHauledInventory"
        };

        public LeftoverCargo(Game game)
        {
            FlushDropBatches("game loaded");
            activeDeliveries.Clear();
            running.Clear();
            loadedRows = null;
            // per-game reset: a new Game constructs its components before anything loads
            SweepManager.LoadSummaryOrderRows = 0;
            SweepManager.LoadSummaryMaps = 0;
            ItemLedger.LoadSummaryItemRows = 0;
            if (!quitHooked)
            {
                quitHooked = true;
                Application.quitting += () => FlushDropBatches("game exit");
            }
            foreach (Pawn dp in due)
            {
                Logger.Message($"{dp?.LabelShort}: leftover check marked but never run - game loaded first");
            }
            due.Clear();
            lastDetection.Clear();
            lastDecline.Clear();
            foreach (Pending p in pending.Values.ToList())
            {
                Unregister(p, "game loaded");
                Logger.Order($"ORDER {p.orderId} ended (game loaded): {p.pawn?.LabelShort} {p.summary}");
            }
            pending.Clear();
            Logger.Message($"Leftover bulk detection: {(DoNotBeLazyMod.Settings.detectLeftoverBulk ? "on" : "off")}.");
        }

        // ---- hook from JobLoopPatch -------------------------------------

        private static bool notifyFaultLogged;

        // Guarded 2026-10-11: a NullReferenceException at IL offset 0x5b
        // escaped this postfix on 21:50-21:51 (save load, then normal play via
        // TKS_PriorityTreatment's TickRare). The only dereference at that
        // offset is job.def.defName, and another mod can start a job whose
        // def is null (a bad saved DoBill job during load recovery; a null-def
        // job from a tick prefix). Skip those quietly; the body is also wrapped
        // so this postfix can never throw into vanilla's StartJob.
        public static void Notify_JobStarted(Pawn pawn, Job job)
        {
            try
            {
                if (pawn == null || job == null || job.def == null || Current.ProgramState != ProgramState.Playing)
                {
                    return;
                }

                if (pending.Count > 0 && pending.TryGetValue(job, out Pending p))
                {
                    OnDeliveryStarted(pawn, job, p);
                    return;
                }

                if (DoNotBeLazyMod.Settings == null || !DoNotBeLazyMod.Settings.detectLeftoverBulk
                    || pawn.Faction == null || !pawn.Faction.IsPlayer)
                {
                    return;
                }

                if (job.def == DnblJobDefOf.StuffAndHaul || SkipJobDefs.Contains(job.def.defName))
                {
                    return;
                }

                ThingOwner<Thing> inv = pawn.inventory?.innerContainer;
                if (inv == null || inv.Count == 0)
                {
                    return;
                }

                due.Add(pawn);
            }
            catch (Exception ex)
            {
                if (!notifyFaultLogged)
                {
                    notifyFaultLogged = true;
                    Logger.Warning($"LeftoverCargo.Notify_JobStarted threw {ex.GetType().Name}: {ex.Message} (logged once)");
                }
            }
        }

        // ---- forced-drop batching ---------------------------------------

        // Called by the stuffing driver just BEFORE it drops a stack beside
        // the pawn. Ignored unless the pawn is running a leftover delivery.
        internal static void NoteForcedDrop(Pawn pawn, Job job, Thing thing, string reason, int count = -1)
        {
            try
            {
                if (pawn == null || job == null || thing == null || thing.def == null || !activeDeliveries.Contains(job))
                {
                    return;
                }
                if (!dropBatches.TryGetValue(pawn, out DropBatch batch))
                {
                    batch = new DropBatch();
                    dropBatches[pawn] = batch;
                }
                batch.lastDropTime = Time.realtimeSinceStartup;
                string cell = pawn.Spawned ? pawn.Position.ToString() : "unspawned";
                string key = thing.def.defName + "|" + cell + "|" + reason;
                batch.counts.TryGetValue(key, out int n);
                batch.counts[key] = n + (count >= 0 ? count : thing.stackCount);
            }
            catch (Exception)
            {
                // logging aid only
            }
        }

        private static void FlushDropBatches(string why, bool onlyQuiet = false)
        {
            if (dropBatches.Count == 0)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            foreach (Pawn pawn in dropBatches.Keys.ToList())
            {
                DropBatch batch = dropBatches[pawn];
                if (onlyQuiet && now - batch.lastDropTime < DropBatchQuietSeconds)
                {
                    continue;
                }
                dropBatches.Remove(pawn);
                try
                {
                    var sb = new StringBuilder();
                    foreach (KeyValuePair<string, int> kv in batch.counts)
                    {
                        string[] parts = kv.Key.Split('|');
                        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(parts[0]);
                        if (sb.Length > 0)
                        {
                            sb.Append("; ");
                        }
                        sb.Append($"{(def != null ? def.label : parts[0])} x{kv.Value} at {parts[1]} ({parts[2]})");
                    }
                    Logger.Message($"{pawn.LabelShort}: leftover delivery dropped goods beside itself, storage full ({why}): {sb}");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"LeftoverCargo drop summary failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        public override void GameComponentUpdate()
        {
            if (dropBatches.Count > 0)
            {
                FlushDropBatches("5 s after the last drop", true);
            }
        }

        // Deliveries still open at save time (queued or running) - architecture
        // section 26, ordered 2026-10-12: "Make sure the open orders get
        // picked up again when the game restarts."
        public class Row : IExposable
        {
            public Pawn pawn;
            public int jobId;
            public string orderId;
            public string summary;
            public int createdTick;
            public List<Thing> things = new List<Thing>();

            public void ExposeData()
            {
                Scribe_References.Look(ref pawn, "pawn");
                Scribe_Values.Look(ref jobId, "jobId", 0);
                Scribe_Values.Look(ref orderId, "orderId");
                Scribe_Values.Look(ref summary, "summary");
                Scribe_Values.Look(ref createdTick, "createdTick", 0);
                Scribe_Collections.Look(ref things, "things", LookMode.Reference);
                if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.PostLoadInit)
                {
                    things = things ?? new List<Thing>();
                }
            }
        }

        private static List<Row> loadedRows;

        public override void ExposeData()
        {
            // Scribed straight into a static field and passed every pass; a
            // local would be lost between LoadingVars and LoadedGame.
            bool saving = Scribe.mode == LoadSaveMode.Saving;
            if (saving)
            {
                FlushDropBatches("game saved");
                List<Row> rows = new List<Row>();
                foreach (Pending p in pending.Values.Concat(running))
                {
                    if (p.pawn == null || p.pawn.Destroyed || p.pawn.Discarded || p.job == null)
                    {
                        continue;
                    }
                    rows.Add(new Row
                    {
                        pawn = p.pawn,
                        jobId = p.job.loadID,
                        orderId = p.orderId,
                        summary = p.summary,
                        createdTick = p.createdTick,
                        things = p.things.Where(t => t != null && !t.Destroyed && !t.Discarded).ToList()
                    });
                }
                loadedRows = rows;
            }

            if (!saving || loadedRows.Count > 0)
            {
                Scribe_Collections.Look(ref loadedRows, "dnblDeliveries", LookMode.Deep);
            }

            if (saving)
            {
                loadedRows = null;
            }
        }

        // Runs after every map is finalised, so each pawn's job and job
        // queue are back. Finds each saved delivery's job by load id.
        public override void LoadedGame()
        {
            List<Row> rows = loadedRows;
            loadedRows = null;
            int deliveryRows = rows == null ? 0 : rows.Count;
            int orderRows = SweepManager.LoadSummaryOrderRows;
            int itemRows = ItemLedger.LoadSummaryItemRows;
            // the one game-wide load line; LoadedGame runs only when a save is loaded, after every map is finalised
            Logger.Message(orderRows + itemRows + deliveryRows == 0
                ? "Load: no open orders, ITEM entries or leftover deliveries were saved in this game, nothing to restore"
                : $"Load: game load read {orderRows} saved pawn order row(s) on {SweepManager.LoadSummaryMaps} map(s), {itemRows} ITEM row(s), {deliveryRows} leftover delivery row(s)");
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            Logger.Message($"Load: read {rows.Count} saved leftover delivery row(s); restoring");
            foreach (Row row in rows)
            {
                try
                {
                    RestoreDelivery(row);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Restoring leftover delivery {row?.orderId} after load threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static void RestoreDelivery(Row row)
        {
            Pawn pawn = row.pawn;
            string name = pawn?.LabelShort ?? "(a pawn that no longer exists)";
            if (pawn == null || pawn.Destroyed || pawn.Discarded || pawn.Dead)
            {
                // Release the delivery's entries, as the "job no longer exists" path does.
                // Nothing else does: CompDnblCargo only prunes destroyed things. A null pawn has no comp or ledger entry.
                if (pawn != null && row.things != null)
                {
                    try
                    {
                        CompDnblCargo deadComp = pawn.GetComp<CompDnblCargo>();
                        foreach (Thing t in row.things)
                        {
                            if (t == null)
                            {
                                continue;
                            }
                            deadComp?.Unregister(t);
                            ItemLedger.Released(pawn, t, "delivery order withdrawn (game loaded, pawn dead or gone)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Releasing cargo of {name} (order {row.orderId}) after load threw {ex.GetType().Name}: {ex.Message}");
                    }
                }
                Logger.Order($"ORDER {row.orderId} ended (game loaded: pawn {(pawn != null && pawn.Dead ? "dead" : "gone")}): {name} {row.summary}; stacks released from DNBL cargo");
                return;
            }

            var p = new Pending
            {
                pawn = pawn,
                orderId = row.orderId,
                summary = row.summary,
                createdTick = row.createdTick
            };
            foreach (Thing t in row.things)
            {
                if (t != null && !t.Destroyed)
                {
                    p.things.Add(t);
                }
            }

            Job cur = pawn.jobs?.curJob;
            if (cur != null && cur.loadID == row.jobId && cur.def == DnblJobDefOf.StuffAndHaul && pawn.jobs.curDriver != null)
            {
                HookRunning(pawn, cur, p, pawn.jobs.curDriver);
                Logger.Order($"ORDER {p.orderId} resumed: {name} leftover bulk delivery after game load - already running, {p.things.Count} stacks");
                return;
            }

            JobQueue queue = pawn.jobs?.jobQueue;
            if (queue != null)
            {
                for (int i = 0; i < queue.Count; i++)
                {
                    Job queued = queue[i].job;
                    if (queued != null && queued.loadID == row.jobId && queued.def == DnblJobDefOf.StuffAndHaul)
                    {
                        p.job = queued;
                        pending[queued] = p;
                        Logger.Order($"ORDER {p.orderId} resumed: {name} leftover bulk delivery after game load - still queued behind {pawn.CurJob?.def?.defName ?? "no job"}, {p.things.Count} stacks");
                        return;
                    }
                }
            }

            Unregister(p, "game loaded, the delivery job no longer exists");
            Logger.Order($"ORDER {p.orderId} ended (game loaded: the delivery job no longer exists): {name} {p.summary}; stacks released from DNBL cargo, still in the pack");
        }

        // ---- tick --------------------------------------------------------

        public override void GameComponentTick()
        {
            if (due.Count == 0 && pending.Count == 0)
            {
                return;
            }

            try
            {
                if (due.Count > 0)
                {
                    List<Pawn> batch = due.ToList();
                    due.Clear();
                    foreach (Pawn pawn in batch)
                    {
                        try
                        {
                            Evaluate(pawn);
                        }
                        catch (Exception ex)
                        {
                            // due was cleared above, so a fault cannot repeat per tick;
                            // throttled too in case job starts re-mark the pawn
                            if (pawn != null && ThrottleAllows(pawn, "evaluation threw " + ex.GetType().Name))
                            {
                                Logger.Warning($"{pawn.LabelShort}: leftover check marked but not completed - {ex.GetType().Name}: {ex.Message}");
                            }
                        }
                    }
                }

                if (pending.Count > 0 && Find.TickManager.TicksGame % 60 == 0)
                {
                    Watchdog();
                }
            }
            catch (Exception ex)
            {
                if (!tickFaultLogged)
                {
                    tickFaultLogged = true;
                    Logger.Warning($"LeftoverCargo tick threw {ex.GetType().Name}: {ex}");
                }
            }
        }

        // ---- the check ---------------------------------------------------

        private struct Finding
        {
            public ThingDef def;
            public int held;
            public int keep;
            public string why;
            public int excess;
        }

        // Marks a pawn for the check on the next tick (used when a vehicle
        // order turns it away for a full pack).
        public static void MarkDue(Pawn pawn)
        {
            if (pawn != null && Current.ProgramState == ProgramState.Playing)
            {
                due.Add(pawn);
            }
        }

        // Why an evaluation did nothing. Logged once per pawn per reason per
        // game hour so a silent check is answerable; never per tick.
        private static readonly Dictionary<Pawn, Dictionary<string, int>> lastDecline = new Dictionary<Pawn, Dictionary<string, int>>();

        private static void Decline(Pawn pawn, string reason)
        {
            if (ThrottleAllows(pawn, reason))
            {
                Logger.Message($"{pawn.LabelShort}: leftover check declined - {reason}");
            }
        }

        // True once per pawn per reason per game hour. The table is cleared on
        // game load and pruned of gone pawns when it grows, so it stays small.
        private static bool ThrottleAllows(Pawn pawn, string reason)
        {
            int now = Find.TickManager.TicksGame;
            if (lastDecline.TryGetValue(pawn, out Dictionary<string, int> reasons)
                && reasons.TryGetValue(reason, out int lastTick) && now - lastTick < HourTicks)
            {
                return false;
            }
            if (lastDecline.Count > 200)
            {
                foreach (Pawn gone in lastDecline.Keys.Where(k => k == null || k.Destroyed || k.Discarded).ToList())
                {
                    lastDecline.Remove(gone);
                    lastDetection.Remove(gone);
                }
            }
            if (reasons == null)
            {
                reasons = new Dictionary<string, int>();
                lastDecline[pawn] = reasons;
            }
            reasons[reason] = now;
            return true;
        }

        private static void Evaluate(Pawn pawn)
        {
            if (pawn == null)
            {
                return;
            }
            if (!DoNotBeLazyMod.Settings.detectLeftoverBulk)
            {
                Decline(pawn, "leftover detection is switched off in settings");
                return;
            }
            if (pawn.Dead || pawn.Destroyed || !pawn.Spawned || pawn.Map == null || pawn.inventory == null || pawn.jobs == null)
            {
                Decline(pawn, "dead, gone, not on a map or missing its pack");
                return;
            }
            if (pawn.Downed || pawn.Drafted || pawn.InMentalState || pawn.IsCaravanMember())
            {
                Decline(pawn, "downed, drafted, in a mental state or in a caravan");
                return;
            }

            // Another mod owns this pack's contents: Pick Up And Haul tracks
            // its hauled stacks in CompHauledToInventory; Combat Extended
            // keeps ammo and loadouts in CompInventory. Matched by type name
            // so there is no dependency; neither was in the mod list on
            // 2026-10-11, this keeps us off their goods if added later.
            foreach (ThingComp c in pawn.AllComps)
            {
                string cn = c.GetType().Name;
                if (cn == "CompHauledToInventory" || cn == "CompInventory")
                {
                    Decline(pawn, "another mod (" + cn + ") owns this pack");
                    return;
                }
            }

            // caravan being loaded: its goods sit in packs until it leaves
            if (pawn.GetLord()?.LordJob is LordJob_FormAndSendCaravan)
            {
                Decline(pawn, "forming a caravan");
                return;
            }

            Job cur = pawn.jobs.curJob;
            if (cur?.def == DnblJobDefOf.StuffAndHaul || SweepManager.IsBeingAssigned(pawn))
            {
                Decline(pawn, "mid stuffing job or being assigned a job");
                return;
            }

            // vanilla is already emptying this pack, or the pawn carries
            // caravan goods it will unpack itself
            if (pawn.inventory.UnloadEverything || pawn.inventory.HasAnyUnpackedCaravanItems)
            {
                Decline(pawn, pawn.inventory.UnloadEverything ? "vanilla is unloading this pack" : "holds unpacked caravan items");
                return;
            }

            SweepManager mgr = pawn.Map.GetComponent<SweepManager>();
            if (mgr != null && mgr.TryGetActiveSweep(pawn, out _))
            {
                Decline(pawn, "on an active sweep order");
                return;
            }

            foreach (Pending existing in pending.Values)
            {
                if (existing.pawn == pawn)
                {
                    Decline(pawn, "a leftover delivery is already queued");
                    return;
                }
            }

            CompDnblCargo cargoComp = pawn.GetComp<CompDnblCargo>();
            if (cargoComp == null)
            {
                Decline(pawn, "no cargo component on this pawn");
                return;
            }

            // mid stuffing trip: what is registered is the sweep's business
            foreach (Thing held in cargoComp.Carrying())
            {
                if (pawn.inventory.innerContainer.Contains(held))
                {
                    Decline(pawn, "mid stuffing trip, registered cargo in pack");
                    return;
                }
            }

            int now = Find.TickManager.TicksGame;
            if (lastDetection.TryGetValue(pawn, out Seen seen) && now - seen.tick < MinGapTicks)
            {
                Decline(pawn, "checked less than " + MinGapTicks + " ticks ago");
                return;
            }

            // total held per def, leaving out what is not ours to move
            var totals = new SortedDictionary<ThingDef, int>(Comparer<ThingDef>.Create((a, b) => string.CompareOrdinal(a.defName, b.defName)));
            ThingOwner<Thing> inv = pawn.inventory.innerContainer;
            for (int i = 0; i < inv.Count; i++)
            {
                Thing t = inv[i];
                ThingDef key = CountDef(t);
                if (key.IsBed || IsPersonal(pawn, t) || pawn.inventory.NotForSale(t) || cargoComp.IsCarrying(t))
                {
                    continue;
                }
                totals.TryGetValue(key, out int n);
                totals[key] = n + t.stackCount;
            }

            var findings = new List<Finding>();
            // one shared food allowance for the whole pack, as vanilla's
            // JobGiver_PackFood has (see TryKeepCount)
            float foodBudget = pawn.needs?.food != null ? pawn.needs.food.MaxLevel : 0f;
            foreach (KeyValuePair<ThingDef, int> pair in totals)
            {
                if (pair.Value < MinExcess)
                {
                    continue;
                }
                if (!TryKeepCount(pawn, pair.Key, pair.Value, ref foodBudget, out int keep, out string why))
                {
                    continue;
                }
                int excess = pair.Value - keep;
                if (excess >= MinExcess)
                {
                    findings.Add(new Finding { def = pair.Key, held = pair.Value, keep = keep, why = why, excess = excess });
                }
            }

            if (findings.Count == 0)
            {
                Decline(pawn, "checked " + inv.Count + " stacks held, nothing exceeds what its rules keep");
                return;
            }

            findings.Sort((a, b) => string.CompareOrdinal(a.def.defName, b.def.defName));
            string signature = string.Join(";", findings.Select(f => f.def.defName + ":" + f.excess));
            if (seen.signature == signature && now - seen.tick < HourTicks)
            {
                Decline(pawn, "same excess as the last detection, within the hour");
                return;
            }
            lastDetection[pawn] = new Seen { tick = now, signature = signature };
            Logger.Message($"{pawn.LabelShort}: leftover check ran - {inv.Count} stacks held, excess found: {string.Join(", ", findings.Select(f => f.def.defName + " x" + f.excess + " (holds " + f.held + ", keep " + f.keep + ")"))} - ordering a delivery");

            QueueDelivery(pawn, cargoComp, findings);
        }

        // The def a pack item is counted under. Every minified building is a
        // MinifiedThing, so those count under the building's own def; beds
        // (Use Bedrolls' carried bedroll included) are skipped by the caller.
        // Non-stackable items (stackLimit 1) are counted per item, summed
        // across their separate stacks. Equipped weapons and worn apparel
        // live in pawn.equipment / pawn.apparel, not in the inventory.
        private static ThingDef CountDef(Thing t)
        {
            MinifiedThing m = t as MinifiedThing;
            return m?.InnerThing != null ? m.InnerThing.def : t.def;
        }

        // Never ours to move: biocoded to this pawn (a bladelink weapon is a
        // CompBiocodable too), the pawn's bonded weapon, or a quest item.
        private static bool IsPersonal(Pawn pawn, Thing t)
        {
            return CompBiocodable.IsBiocodedFor(t, pawn)
                || (t is ThingWithComps twc && pawn.equipment?.bondedWeapon == twc)
                || (t.questTags != null && t.questTags.Count > 0);
        }

        // How many of this def the pawn's inventory rules tell it to carry.
        // False means "do not touch this def at all".
        //
        // Verified against lib\Assembly-CSharp.dll (ilspycmd, 2026-10-11):
        //  - Pawn.inventoryStock.stockEntries (Pawn_InventoryStockTracker,
        //    medicine etc.): entry.thingDef / entry.count are the wanted stock.
        //  - DrugPolicy[ThingDef].takeToInventory is the count the policy
        //    wants; JobGiver_DropUnusedInventory.ShouldKeepDrugInInventory is
        //    what vanilla uses to keep a drug that has no take count (addicts).
        //  - JobGiver_PackFood (re-read 2026-10-11): ONE allowance for all
        //    packable food together, the food bar's MaxLevel in nutrition,
        //    counting only food with preferability >= MealAwful that the
        //    pawn WillEat. So foodBudget is shared across defs, spent in
        //    defName order; a def the pawn would not eat keeps nothing.
        //  - Pawn_InventoryTracker.NotForSale(Thing) and
        //    HasAnyUnpackedCaravanItems: handled by the caller.
        private static bool TryKeepCount(Pawn pawn, ThingDef def, int held, ref float foodBudget, out int keep, out string why)
        {
            keep = 0;
            why = "no rule keeps it";
            try
            {
                var reasons = new List<string>();

                if (pawn.inventoryStock != null)
                {
                    foreach (InventoryStockEntry entry in pawn.inventoryStock.stockEntries.Values)
                    {
                        if (entry.thingDef == def && entry.count > 0)
                        {
                            keep += entry.count;
                            reasons.Add("inventory stock " + entry.count);
                        }
                    }
                }

                if (def.IsDrug)
                {
                    DrugPolicy policy = pawn.drugs?.CurrentPolicy;
                    int take = policy != null ? policy[def].takeToInventory : 0;
                    if (take > 0)
                    {
                        keep += take;
                        reasons.Add("drug policy " + take);
                    }
                    else
                    {
                        Thing sample = pawn.inventory.innerContainer.FirstOrDefault(t => t.def == def);
                        if (sample != null && JobGiver_DropUnusedInventory.ShouldKeepDrugInInventory(pawn, sample))
                        {
                            return false;
                        }
                    }
                }

                if (def.IsNutritionGivingIngestible && def.EverHaulable && def.ingestible != null
                    && (int)def.ingestible.preferability >= (int)FoodPreferability.MealAwful
                    && pawn.needs?.food != null)
                {
                    Thing foodSample = pawn.inventory.innerContainer.FirstOrDefault(t => t.def == def);
                    float nutrition = def.GetStatValueAbstract(StatDefOf.Nutrition);
                    if (foodSample != null && nutrition > 0f && foodBudget > 0f && pawn.WillEat(foodSample, null, false))
                    {
                        int packed = (int)Math.Ceiling(foodBudget / nutrition);
                        keep += packed;
                        reasons.Add("packed food " + packed);
                        foodBudget -= Math.Min(packed, held) * nutrition;
                    }
                }

                if (reasons.Count > 0)
                {
                    why = string.Join(" + ", reasons);
                }
                return true;
            }
            catch (Exception ex)
            {
                // an unreadable rule is not a licence to take the item
                Logger.Message($"{pawn.LabelShort}: leftover check skipped {def.defName}, rules unreadable: {ex.GetType().Name}");
                return false;
            }
        }

        // ---- delivery ----------------------------------------------------

        private static void QueueDelivery(Pawn pawn, CompDnblCargo cargoComp, List<Finding> findings)
        {
            string orderId = SweepManager.NextOrderId();
            var p = new Pending { pawn = pawn, orderId = orderId, createdTick = Find.TickManager.TicksGame };
            var parts = new List<string>();

            foreach (Finding f in findings)
            {
                int remaining = f.excess;
                // largest stacks first so fewer splits are needed
                List<Thing> stacks = pawn.inventory.innerContainer
                    .Where(t => CountDef(t) == f.def && !IsPersonal(pawn, t) && !pawn.inventory.NotForSale(t) && !cargoComp.IsCarrying(t))
                    .OrderByDescending(t => t.stackCount).ToList();
                foreach (Thing stack in stacks)
                {
                    if (remaining <= 0)
                    {
                        break;
                    }
                    // a non-stackable item is always taken whole (stackCount 1)
                    int take = Math.Min(remaining, stack.stackCount);
                    Thing piece = stack;
                    if (take < stack.stackCount)
                    {
                        // split off only the excess; same call the pickup uses
                        piece = stack.SplitOff(take);
                        pawn.inventory.GetDirectlyHeldThings().TryAdd(piece, false);
                        if (!pawn.inventory.innerContainer.Contains(piece))
                        {
                            // could not put it back in the pack; give it back to the stack
                            stack.TryAbsorbStack(piece, true);
                            continue;
                        }
                    }
                    remaining -= take;
                    p.things.Add(piece);
                    cargoComp.Register(piece);
                    ItemLedger.Added(pawn, piece, orderId);
                }
                parts.Add($"{f.def.label} x{f.excess - remaining} (holds {f.held}, rules keep {f.keep}: {f.why})");
            }

            if (p.things.Count == 0)
            {
                Decline(pawn, "found excess but could not split any of it off for delivery");
                return;
            }

            p.summary = string.Join(", ", parts.ToArray());
            Thing first = p.things[0];
            Job job = JobMaker.MakeJob(DnblJobDefOf.StuffAndHaul, first);
            job.count = first.stackCount;
            job.haulMode = HaulMode.ToCellStorage;

            string now = pawn.CurJob?.def?.defName ?? "no job";
            // Section 31: an idle job the player did not force is interrupted
            // rather than queued behind (a queued job waits out the whole idle).
            Job cur = pawn.CurJob;
            bool idle = cur != null && !cur.playerForced && !pawn.Drafted && pawn.GetLord() == null
                && (cur.def == JobDefOf.Wait_MaintainPosture
                    || cur.def == JobDefOf.Wait_Wander
                    || cur.def == JobDefOf.GotoWander
                    || (cur.def == JobDefOf.Wait && cur.workGiverDef == null));
            string how = idle ? $"replaced idle {now}" : $"queued behind {now}";
            Logger.Order($"ORDER {orderId} issued: {pawn.LabelShort} leftover bulk delivery - {p.summary}; {how} (keys: none - issued by the mod's own check, not a click)");

            p.job = job;
            pending[job] = p;
            bool took = SweepManager.GiveJob(pawn, job, !idle);
            if (!took)
            {
                if (pending.Remove(job))
                {
                    Unregister(p, "delivery job refused");
                    Logger.Order($"ORDER {orderId} ended (failed to start: the game refused the delivery job): {pawn.LabelShort} {p.summary}");
                }
            }
        }

        private static void OnDeliveryStarted(Pawn pawn, Job job, Pending p)
        {
            pending.Remove(job);
            JobDriver driver = pawn.jobs?.curDriver;
            if (pawn.jobs?.curJob != job || driver == null)
            {
                Unregister(p, "delivery job accepted but not running");
                Logger.Order($"ORDER {p.orderId} ended (failed to start: accepted but not running): {pawn.LabelShort} {p.summary}");
                return;
            }

            Logger.Order($"ORDER {p.orderId} started: {pawn.LabelShort} {job.def.defName} leftover bulk delivery");
            HookRunning(pawn, job, p, driver);
        }

        // Marks a delivery as running and attaches the line that says how it
        // ended. Shared by a fresh start and by a delivery found running
        // after a game load.
        private static void HookRunning(Pawn pawn, Job job, Pending p, JobDriver driver)
        {
            p.job = job;
            activeDeliveries.Add(job);
            running.Add(p);
            driver.AddFinishAction(condition =>
            {
                activeDeliveries.Remove(job);
                running.Remove(p);
                Logger.Order($"ORDER {p.orderId} ended ({SweepManager.DescribeEnding(pawn, condition, job)}): {pawn.LabelShort} {p.summary} ({condition})");
            });
        }

        // A queued delivery that never started (the player gave other orders
        // and the queue was cleared, the pawn went down): release the
        // registration so vanilla's drop guard stops protecting the stacks.
        private static void Watchdog()
        {
            int now = Find.TickManager.TicksGame;
            foreach (KeyValuePair<Job, Pending> pair in pending.ToList())
            {
                Pending p = pair.Value;
                bool gone = p.pawn == null || p.pawn.Dead || p.pawn.Destroyed || p.pawn.Downed;
                if (!gone && now - p.createdTick < PendingExpiryTicks)
                {
                    continue;
                }
                pending.Remove(pair.Key);
                string reason = gone ? "pawn down, dead or gone" : "never started within " + PendingExpiryTicks + " ticks";
                // take the dead order out of the queue so it cannot start later
                // with its stacks no longer registered
                bool removed = false;
                try
                {
                    removed = p.pawn?.jobs?.jobQueue != null && p.job != null && p.pawn.jobs.jobQueue.Extract(p.job) != null;
                }
                catch (Exception)
                {
                    // a queue we cannot edit: leave the job; it is a plain delivery of pack stacks
                }
                Unregister(p, reason);
                Logger.Order($"ORDER {p.orderId} ended ({reason}): {p.pawn?.LabelShort} {p.summary}; stacks released from DNBL cargo, still in the pack; queued job {(removed ? "removed" : "not found in the queue")}");
            }
        }

        private static void Unregister(Pending p, string why)
        {
            CompDnblCargo comp = p.pawn?.GetComp<CompDnblCargo>();
            foreach (Thing t in p.things)
            {
                comp?.Unregister(t);
                ItemLedger.Released(p.pawn, t, "delivery order withdrawn (" + why + ")");
            }
        }
    }
}
