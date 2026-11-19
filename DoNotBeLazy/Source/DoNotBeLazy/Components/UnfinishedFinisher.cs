using System;
using System.Collections.Generic;
using System.Linq;
using DoNotBeLazy.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Components
{
    // dnbl-architecture.md section 51 (2026-11-15). User: "Me no like unfinished
    // items when pawn is on map especially if bill is incomplete. How make pawns
    // finish them? Want option to force finish even if bill is incomplete."
    //
    // Vanilla resumes an UnfinishedThing only from WorkGiver_DoBill, only for
    // the pawn that made it (Creator), and only while its bill passes
    // ShouldDoNow (not suspended, not paused, count not reached). The DoBill job
    // driver itself checks none of that: it fails only on a deleted bill or an
    // unusable table. So this component builds the same job vanilla builds
    // (WorkGiver_DoBill.FinishUftJob: DoBill on the table, bill, targetQueueB =
    // the item) and hands it to a capable pawn itself.
    //
    // Scan every 2500 ticks; check running orders every 60. Not saved, except
    // the list of one-off bills (see TempBill).
    public class UnfinishedFinisher : GameComponent
    {
        private const int ScanInterval = 2500;
        private const int CheckInterval = 60;
        private const int MaxAttempts = 3;
        private const int RetryCooldown = 2500;
        private const int OrderTimeout = 30000;

        private class Order
        {
            public UnfinishedThing Uft;
            public Pawn Pawn;
            public Bill_ProductionWithUft Bill;
            public bool TempBill;
            public Job HaulJob;
            public int StartTick;
            public bool Manual;
            public string Label;
        }

        private static readonly HashSet<string> IdleJobs = new HashSet<string>
        {
            "Wait", "Wait_Wander", "Wait_MaintainPosture", "GotoWander", "Wait_Downed"
        };

        private static readonly Dictionary<int, Order> active = new Dictionary<int, Order>();
        private static readonly Dictionary<int, int> attempts = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> nextTry = new Dictionary<int, int>();
        private static readonly Dictionary<int, string> lastNoPawnReason = new Dictionary<int, string>();
        private static readonly HashSet<int> gaveUp = new HashSet<int>();
        // Section 55 (2026-11-18): pawns who just finished an item, waiting for their
        // product haul to end before being handed the next item. Pawn -> tick finished.
        private static readonly Dictionary<Pawn, int> chainWait = new Dictionary<Pawn, int>();
        private const int ChainTimeout = 3000;
        private static bool chaining;
        // Names every pawn skipped by the last Plan, with the reason.
        private static readonly List<string> lastSkips = new List<string>();
        private bool reconciled;

        // One-off bills made for items whose own bill is gone. Saved so a save in the
        // middle of an order cannot leave one on a table: they are deleted on load.
        private List<Bill_ProductionWithUft> tempBills = new List<Bill_ProductionWithUft>();

        public UnfinishedFinisher(Game game)
        {
            active.Clear();
            attempts.Clear();
            nextTry.Clear();
            lastNoPawnReason.Clear();
            gaveUp.Clear();
            chainWait.Clear();
            lastSkips.Clear();
        }

        public override void ExposeData()
        {
            tempBills.RemoveAll(b => b == null);
            Scribe_Collections.Look(ref tempBills, "dnblTempBills", LookMode.Reference);
            if (tempBills == null) tempBills = new List<Bill_ProductionWithUft>();
        }

        public override void FinalizeInit()
        {
            // Section 55: one-off bills are no longer deleted here; a * finish order that
            // survived the load may still use one. Reconcile() runs on the first tick,
            // after SweepManager has rebuilt its orders.
            reconciled = false;
        }

        // After a load: a one-off bill that a saved finish order still uses is kept and its
        // order is tracked again; any other leftover one-off bill is deleted.
        // Also run from TryOrder, so a Finish this press while the game is still paused
        // after the load cannot order an item a saved finish order already holds.
        internal void EnsureReconciled()
        {
            if (!reconciled) Reconcile(Find.TickManager.TicksGame);
        }

        private void Reconcile(int tick)
        {
            reconciled = true;
            try
            {
                var used = new HashSet<Bill_ProductionWithUft>();
                foreach (Map map in Find.Maps)
                {
                    SweepManager sm = map.GetComponent<SweepManager>();
                    if (sm == null) continue;
                    foreach (Pawn p in sm.GetSweptPawns())
                    {
                        // Not FinishPending: an item carried at save time is not spawned yet.
                        if (!sm.TryGetActiveSweep(p, out SweepOrder so) || so.FinishUft == null || so.FinishUft.Destroyed) continue;
                        UnfinishedThing u = so.FinishUft;
                        if (active.ContainsKey(u.thingIDNumber)) continue;
                        Bill_ProductionWithUft b = null;
                        if (so.WorkstationTarget is IBillGiver g && g.BillStack != null)
                            foreach (Bill x in g.BillStack.Bills)
                                if (x.GetUniqueLoadID() == so.BillLoadId) b = x as Bill_ProductionWithUft;
                        bool temp = b != null && tempBills.Contains(b);
                        if (temp) used.Add(b);
                        active[u.thingIDNumber] = new Order
                        {
                            Uft = u, Pawn = p, Bill = b, TempBill = temp, StartTick = tick, Manual = false,
                            Label = u.LabelNoCount + " (" + u.ThingID + ") at " + u.PositionHeld
                        };
                        Logger.Always("Force-finish: after load, " + p.LabelShort + " still holds a * finish order on " + u.LabelNoCount + " (" + u.ThingID + ")" + (temp ? "; one-off bill kept." : "."));
                    }
                }
                foreach (Bill_ProductionWithUft b in tempBills.ToList())
                {
                    if (b == null || used.Contains(b)) continue;
                    if (!b.DeletedOrDereferenced)
                    {
                        b.billStack?.Delete(b);
                        Logger.Always("Force-finish: removed a leftover one-off bill (" + b.recipe?.label + ") after load.");
                    }
                    tempBills.Remove(b);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("UnfinishedFinisher.Reconcile failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public override void GameComponentTick()
        {
            int tick = Find.TickManager.TicksGame;
            try
            {
                if (!reconciled) Reconcile(tick);
                if ((active.Count > 0 || chainWait.Count > 0) && tick % CheckInterval == 0) CheckOrders(tick);
                if (tick % ScanInterval == 0 && DoNotBeLazyMod.Settings.forceFinishUnfinished) Scan(tick);
            }
            catch (Exception ex)
            {
                Logger.Warning("UnfinishedFinisher tick failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---- scan ----

        private static void Scan(int tick)
        {
            foreach (Map map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                foreach (UnfinishedThing uft in PendingItems(map, tick))
                    TryOrder(uft, null, false, out string why);
            }
        }

        // The unfinished items the scan may order: not already ordered, not given up on,
        // not cooling down, not forbidden or reserved, and not one vanilla will resume.
        private static List<UnfinishedThing> PendingItems(Map map, int tick)
        {
            var found = new List<UnfinishedThing>();
            foreach (Thing t in map.listerThings.AllThings)
            {
                UnfinishedThing uft = t as UnfinishedThing;
                if (uft == null || !uft.Spawned || uft.Recipe == null) continue;
                int id = uft.thingIDNumber;
                if (active.ContainsKey(id) || gaveUp.Contains(id)) continue;
                int at;
                if (nextTry.TryGetValue(id, out at) && tick < at) continue;
                if (uft.IsForbidden(Faction.OfPlayer)) continue;
                if (map.reservationManager.IsReservedByAnyoneOf(uft, Faction.OfPlayer)) continue;
                if (VanillaWillResume(uft)) continue;
                found.Add(uft);
            }
            return found;
        }

        // Section 55: hand a pawn who just finished an item the nearest pending item it can
        // do, same rules as the scan. One line either way.
        private static bool TryChain(Pawn pawn, int tick)
        {
            Map map = pawn.Map;
            if (map == null) return false;
            // Maker first, as in the scan: an item whose maker is free is left to the scan.
            int leftForMaker = 0;
            List<UnfinishedThing> items = PendingItems(map, tick)
                .Where(u => { bool m = MakerFree(u, pawn); if (m) leftForMaker++; return !m; })
                .OrderBy(u => (u.Position - pawn.Position).LengthHorizontalSquared).ToList();
            var one = new List<Pawn> { pawn };
            chaining = true;
            try
            {
                foreach (UnfinishedThing u in items)
                {
                    if (TryOrder(u, one, false, out string why))
                    {
                        Logger.Always("Force-finish chain: " + pawn.LabelShort + " finished an item and is handed the next, " + u.LabelNoCount + " (" + u.ThingID + ").");
                        return true;
                    }
                }
            }
            finally { chaining = false; }
            Logger.Always("Force-finish chain: " + pawn.LabelShort + " finished an item; " + (items.Count == 0 ? "no other unfinished item is pending" : items.Count + " pending item(s), none this pawn can do now")
                + (leftForMaker > 0 ? "; " + leftForMaker + " left for their free maker." : ".") + (lastSkips.Count > 0 ? " Last refusal: " + string.Join("; ", lastSkips.Distinct().ToArray()) : ""));
            return false;
        }

        // True when the item's maker is someone else who is on this map, up, undrafted,
        // not finishing an item and not on a * order.
        private static bool MakerFree(UnfinishedThing u, Pawn pawn)
        {
            Pawn c = u.Creator;
            if (c == null || c == pawn || c.Dead || !c.Spawned || c.Map != u.Map || c.Downed || c.Drafted || c.InMentalState) return false;
            foreach (Order o in active.Values) if (o.Pawn == c) return false;
            SweepManager sm = c.Map.GetComponent<SweepManager>();
            if (sm != null && (sm.TryGetActiveSweep(c, out _) || sm.IsPaused(c))) return false;
            return true;
        }

        // True when vanilla itself will hand this item back to its maker: the bill is
        // live and runnable, and the maker is alive on the map and still able to work.
        private static bool VanillaWillResume(UnfinishedThing uft)
        {
            Bill_ProductionWithUft bill = uft.BoundBill;
            if (bill == null || bill.DeletedOrDereferenced || bill.suspended) return false;
            if (bill.BoundWorker == null || bill.BoundWorker != uft.Creator) return false;
            return bill.ShouldDoNow();
        }

        // ---- ordering ----

        // Returns true when a pawn was sent. why says what happened either way.
        public static bool TryOrder(UnfinishedThing uft, List<Pawn> preferred, bool manual, out string why)
        {
            why = null;
            Current.Game?.GetComponent<UnfinishedFinisher>()?.EnsureReconciled();
            int tick = Find.TickManager.TicksGame;
            int id = uft.thingIDNumber;
            string label = uft.LabelNoCount + " (" + uft.ThingID + ") at " + uft.PositionHeld;
            try
            {
                if (!uft.Spawned || uft.Map == null) { why = "the item is not on the map"; return false; }
                if (active.ContainsKey(id)) { why = "a pawn is already finishing it"; return false; }
                Map map = uft.Map;
                RecipeDef recipe = uft.Recipe;

                Pawn pawn = null;
                Bill_ProductionWithUft bill = null;
                Thing table = null;
                bool temp = false;
                string noPawn = null;
                if (!Plan(uft, preferred, manual, out pawn, out bill, out table, out temp, out noPawn))
                {
                    why = noPawn;
                    if (!manual && !chaining)
                    {
                        string prev;
                        if (!lastNoPawnReason.TryGetValue(id, out prev) || prev != noPawn)
                        {
                            lastNoPawnReason[id] = noPawn;
                            Logger.Always("Force-finish: cannot start " + label + ": " + noPawn + ". Will look again in " + ScanInterval + " ticks.");
                        }
                    }
                    return false;
                }

                IBillGiver giver = (IBillGiver)table;
                Job haul = WorkGiverUtility.HaulStuffOffBillGiverJob(pawn, giver, uft);
                bool needHaul = haul != null && haul.targetA.Thing != uft;

                Job job = JobMaker.MakeJob(JobDefOf.DoBill, table);
                job.bill = bill;
                job.targetQueueB = new List<LocalTargetInfo> { uft };
                job.countQueue = new List<int> { 1 };
                job.haulMode = HaulMode.ToCellNonStorage;

                SweepManager.NoteModIssued(job);
                if (needHaul) SweepManager.NoteModIssued(haul);
                bool taken;
                if (needHaul)
                {
                    taken = pawn.jobs.TryTakeOrderedJob(haul, JobTag.Misc);
                    if (taken) pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, true);
                }
                else
                {
                    taken = pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }
                if (!taken)
                {
                    if (temp) RemoveTempBill(bill);
                    why = pawn.LabelShort + " refused the job";
                    CountFailure(id, tick, label, why, manual);
                    return false;
                }

                var o = new Order
                {
                    Uft = uft, Pawn = pawn, Bill = bill, TempBill = temp, HaulJob = needHaul ? haul : null,
                    StartTick = tick, Manual = manual, Label = label
                };
                active[id] = o;
                lastNoPawnReason.Remove(id);
                string billState = temp ? "one-off bill made (no runnable bill of its own)"
                    : (bill.suspended ? "bill suspended" : bill.paused ? "bill paused" : !bill.ShouldDoNow() ? "bill count reached or not due" : "bill runnable");
                Logger.Always("Force-finish started" + (manual ? " (player order)" : "") + ": " + label
                    + ", " + recipe.label + ", workLeft " + uft.workLeft.ToString("F0") + ", maker " + (uft.Creator?.LabelShort ?? "nobody")
                    + "; sent " + pawn.LabelShort + " to " + table.LabelShort + " at " + table.Position + "; " + billState
                    + (needHaul ? "; clearing the table first" : "") + "; " + lastPick);
                // Section 55 (2026-11-18, reverses "automatic scans stay plain jobs"): automatic
                // finish jobs are * orders too.
                {
                    string kind = manual ? "Finish this" : "Force-finish";
                    SweepManager sm = map.GetComponent<SweepManager>();
                    if (sm != null && sm.AdoptFinishOrder(pawn, table, bill, job, uft, manual))
                        Logger.Always(kind + ": " + pawn.LabelShort + " now holds a * order on " + table.LabelShort + " to " + recipe.label + " (keeps at it, pauses for needs, returns).");
                    else
                        Logger.Always(kind + ": " + pawn.LabelShort + " could not be given a * order on " + table.LabelShort + "; plain job only.");
                }
                why = pawn.LabelShort + " sent to finish it";
                return true;
            }
            catch (Exception ex)
            {
                why = "error: " + ex.GetType().Name;
                Logger.Warning("UnfinishedFinisher.TryOrder failed: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static void CountFailure(int id, int tick, string label, string reason, bool manual)
        {
            if (manual) { Logger.Always("Force-finish failed (player order): " + label + ": " + reason); return; }
            int n;
            attempts.TryGetValue(id, out n);
            n++;
            attempts[id] = n;
            nextTry[id] = tick + RetryCooldown;
            if (n >= MaxAttempts)
            {
                gaveUp.Add(id);
                Logger.Always("Force-finish failed: " + label + ": " + reason + ". Attempt " + n + " of " + MaxAttempts + "; giving up on this item (use Finish this on it).");
            }
            else
            {
                Logger.Always("Force-finish failed: " + label + ": " + reason + ". Attempt " + n + " of " + MaxAttempts + "; will retry.");
            }
        }

        // Picks pawn, bill and table. Candidates in order: the item's own bound bill, any
        // live bill of the same recipe on a table that can make it, then a one-off bill on
        // the nearest usable table. The nearest candidate with a capable pawn wins.
        private static bool Plan(UnfinishedThing uft, List<Pawn> preferred, bool manual,
            out Pawn pawn, out Bill_ProductionWithUft bill, out Thing table, out bool temp, out string why)
        {
            pawn = null; bill = null; table = null; temp = false;
            Map map = uft.Map;
            RecipeDef recipe = uft.Recipe;
            var cands = new List<KeyValuePair<Bill_ProductionWithUft, Thing>>();
            var tablesNoBill = new List<Thing>();

            Bill_ProductionWithUft own = uft.BoundBill;
            // A suspended bill is never used: DoRecipeWork fails while bill.suspended, and
            // lifting a player's suspension would let other pawns start new work on it.
            if (own != null && !own.DeletedOrDereferenced && !own.suspended && own.billStack?.billGiver is Thing ownTable && !ownTable.Destroyed
                && ((IBillGiver)ownTable).CurrentlyUsableForBills())
            {
                cands.Add(new KeyValuePair<Bill_ProductionWithUft, Thing>(own, ownTable));
            }

            var users = recipe.AllRecipeUsers.ToList();
            var givers = new List<Thing>();
            foreach (Thing g in map.listerThings.ThingsInGroup(ThingRequestGroup.PotentialBillGiver))
            {
                if (!g.Spawned || !users.Contains(g.def)) continue;
                IBillGiver ig = g as IBillGiver;
                if (ig == null || !ig.CurrentlyUsableForBills() || g.IsBurning()) continue;
                givers.Add(g);
            }
            givers = givers.OrderBy(g => (g.Position - uft.Position).LengthHorizontalSquared).ToList();
            foreach (Thing g in givers)
            {
                bool found = false;
                foreach (Bill b in ((IBillGiver)g).BillStack.Bills)
                {
                    Bill_ProductionWithUft pb = b as Bill_ProductionWithUft;
                    if (pb == null || pb.recipe != recipe || pb == own || pb.suspended) continue;
                    // A bill already holding a different item would lose that link.
                    if (pb.BoundUft != null && pb.BoundUft != uft && !pb.BoundUft.Destroyed) continue;
                    cands.Add(new KeyValuePair<Bill_ProductionWithUft, Thing>(pb, g));
                    found = true;
                }
                if (!found) tablesNoBill.Add(g);
            }

            var pool = new List<Pawn>();
            if (preferred != null && preferred.Count > 0) pool.AddRange(preferred);
            else pool.AddRange(map.mapPawns.FreeColonistsSpawned);

            var reasons = new List<string>();
            lastSkips.Clear();
            // Automatic only: try the maker at every table before anyone else at any table,
            // so a nearer table the maker cannot use does not hand the item to someone else.
            if (!manual)
            {
                foreach (var c in cands)
                {
                    Pawn m = BestPawn(pool, uft, c.Value, c.Key, manual, reasons, true);
                    if (m != null) { pawn = m; bill = c.Key; table = c.Value; why = null; return true; }
                }
                foreach (Thing g in tablesNoBill)
                {
                    Pawn m = BestPawn(pool, uft, g, null, manual, reasons, true);
                    if (m == null) continue;
                    Bill_ProductionWithUft nb = MakeTempBill(g, recipe, m);
                    if (nb == null) continue;
                    pawn = m; bill = nb; table = g; temp = true; why = null; return true;
                }
            }
            foreach (var c in cands)
            {
                Pawn p = BestPawn(pool, uft, c.Value, c.Key, manual, reasons);
                if (p != null) { pawn = p; bill = c.Key; table = c.Value; why = null; return true; }
            }
            foreach (Thing g in tablesNoBill)
            {
                Pawn p = BestPawn(pool, uft, g, null, manual, reasons);
                if (p == null) continue;
                Bill_ProductionWithUft nb = MakeTempBill(g, recipe, p);
                if (nb == null) { reasons.Add("could not make a one-off bill on " + g.LabelShort); continue; }
                pawn = p; bill = nb; table = g; temp = true; why = null; return true;
            }

            if (cands.Count == 0 && tablesNoBill.Count == 0)
                why = "no usable " + (users.Count > 0 ? users[0].label : "work table") + " to " + recipe.label;
            else
                why = "no capable pawn (" + (lastSkips.Count > 0 ? string.Join("; ", lastSkips.Distinct().ToArray()) : reasons.Count > 0 ? string.Join("; ", reasons.Distinct().ToArray()) : "none free") + ")";
            return false;
        }

        // Most capable first (2026-11-17, section 55): the highest level in the skill the
        // recipe uses, else the work type's first relevant skill; distance breaks ties.
        // Automatic force-finish only (2026-11-17 answer, section 55): the item's original
        // maker goes first when eligible and not already finishing an item; the Finish this
        // gizmo (manual) never uses the maker rule.
        private static Pawn BestPawn(List<Pawn> pool, UnfinishedThing uft, Thing table, Bill_ProductionWithUft bill,
            bool manual, List<string> reasons, bool makerOnly = false)
        {
            SkillDef skill = SkillFor(uft.Recipe, WorkTypeFor(table, uft.Recipe));
            string makerNote = "";
            if (!manual)
            {
                Pawn maker = uft.Creator;
                if (maker == null) makerNote = "maker skipped: nobody recorded; ";
                else if (!pool.Contains(maker)) makerNote = "maker " + maker.LabelShort + " skipped: not in the pool; ";
                else
                {
                    string mr = Incapable(maker, uft, table, bill, manual);
                    if (mr == null)
                    {
                        lastPick = maker.LabelShort + " chosen: original maker, " + (skill != null ? skill.label : "no skill") + " " + SkillLevel(maker, skill);
                        return maker;
                    }
                    makerNote = "maker " + maker.LabelShort + " skipped: " + mr + "; ";
                    lastSkips.Add(maker.LabelShort + ": " + mr);
                }
            }
            if (makerOnly) return null;
            Pawn best = null;
            int bestLevel = -1;
            float bestDist = float.MaxValue;
            int skipped = 0;
            foreach (Pawn p in pool)
            {
                string r = Incapable(p, uft, table, bill, manual);
                if (r != null) { reasons.Add(r); lastSkips.Add(p.LabelShort + ": " + r); skipped++; continue; }
                int level = SkillLevel(p, skill);
                float dist = (p.Position - uft.Position).LengthHorizontalSquared;
                if (level > bestLevel || level == bestLevel && dist < bestDist) { bestLevel = level; bestDist = dist; best = p; }
            }
            if (best != null)
                lastPick = makerNote + best.LabelShort + " chosen: " + (skill != null ? skill.label : "no skill") + " " + bestLevel
                    + ", most capable of " + (pool.Count - skipped) + " able pawn(s); skipped " + skipped + SkipSummary(reasons);
            return best;
        }

        private static string lastPick;

        private static string SkipSummary(List<string> reasons)
        {
            if (reasons.Count == 0) return "";
            return " (" + string.Join(", ", reasons.GroupBy(x => x).Select(g => g.Count() + " " + g.Key).ToArray()) + ")";
        }

        private static SkillDef SkillFor(RecipeDef recipe, WorkTypeDef wt)
        {
            if (recipe.workSkill != null) return recipe.workSkill;
            if (wt != null && wt.relevantSkills != null && wt.relevantSkills.Count > 0) return wt.relevantSkills[0];
            return null;
        }

        private static int SkillLevel(Pawn p, SkillDef skill)
        {
            if (skill == null || p.skills == null) return 0;
            SkillRecord r = p.skills.GetSkill(skill);
            return r == null || r.TotallyDisabled ? 0 : r.Level;
        }

        private static WorkTypeDef WorkTypeFor(Thing table, RecipeDef recipe)
        {
            if (recipe.requiredGiverWorkType != null) return recipe.requiredGiverWorkType;
            foreach (WorkGiverDef wg in DefDatabase<WorkGiverDef>.AllDefsListForReading)
            {
                if (wg.fixedBillGiverDefs != null && wg.fixedBillGiverDefs.Contains(table.def)) return wg.workType;
            }
            return null;
        }

        // null when the pawn can be sent, else the reason it cannot.
        private static string Incapable(Pawn p, UnfinishedThing uft, Thing table, Bill_ProductionWithUft bill, bool manual)
        {
            if (p == null || p.Dead || !p.Spawned || p.Map != uft.Map) return "not on the map";
            if (p.Faction != Faction.OfPlayer || !p.RaceProps.Humanlike) return "not a colonist";
            if (p.Downed) return "downed";
            if (p.Drafted) return "drafted";
            if (p.InMentalState) return "in a mental break";
            foreach (Order o in active.Values) if (o.Pawn == p) return "already finishing another item";
            RecipeDef recipe = uft.Recipe;
            WorkTypeDef wt = WorkTypeFor(table, recipe);
            if (wt != null)
            {
                if (p.WorkTypeIsDisabled(wt)) return "incapable of " + wt.labelShort;
                if (!manual && (p.workSettings == null || !p.workSettings.WorkIsActive(wt))) return wt.labelShort + " is off";
            }
            SweepManager sm = p.Map.GetComponent<SweepManager>();
            if (sm != null && (sm.TryGetActiveSweep(p, out SweepOrder so) || sm.IsPaused(p))) return "on a * order";
            if (!recipe.PawnSatisfiesSkillRequirements(p)) return "skill too low";
            if (!manual && bill != null && !bill.PawnAllowedToStartAnew(p)) return "bill restricts who may start it";
            if (uft.IsForbidden(p) || table.IsForbidden(p)) return "item or table forbidden";
            if (!p.CanReserveAndReach(table, PathEndMode.InteractionCell, Danger.Deadly)) return "cannot reach or reserve the table";
            if (!p.CanReserveAndReach(uft, PathEndMode.Touch, Danger.Deadly)) return "cannot reach or reserve the item";
            if (!manual && !chaining)
            {
                Job cj = p.CurJob;
                if (cj != null && !IdleJobs.Contains(cj.def.defName)) return "busy";
            }
            if (chaining)
            {
                // A chained pawn may be interrupted, but not below a need threshold and
                // not off a need, recreation or player-forced job.
                string lack = NeedMonitor.CriticalNeedLabelFor(p);
                if (lack != null) return "below a need threshold (" + lack + ")";
                Job cj = p.CurJob;
                // IsProtectedFromNeedInterrupt adds fight/flee responses, emergency
                // (firefighting, tending), self-preservation and drug jobs.
                if (cj != null && (cj.playerForced || cj.def == JobDefOf.Ingest || cj.def == JobDefOf.LayDown
                    || cj.def == JobDefOf.LayDownResting || cj.def.joyKind != null
                    || SweepManager.IsProtectedFromNeedInterrupt(cj)))
                    return "on " + cj.def.defName;
            }
            return null;
        }

        // ---- one-off bill ----

        // A do-once bill of the item's recipe, restricted to the chosen pawn so no other pawn
        // starts it. Not suspended: Toils_Recipe.DoRecipeWork fails while bill.suspended.
        private static Bill_ProductionWithUft MakeTempBill(Thing table, RecipeDef recipe, Pawn only)
        {
            try
            {
                Bill_ProductionWithUft nb = recipe.MakeNewBill() as Bill_ProductionWithUft;
                if (nb == null) return null;
                nb.repeatMode = BillRepeatModeDefOf.RepeatCount;
                nb.repeatCount = 1;
                nb.suspended = false; // DoRecipeWork fails on a suspended bill
                nb.SetPawnRestriction(only); // nobody else may start it
                ((IBillGiver)table).BillStack.AddBill(nb);
                Current.Game.GetComponent<UnfinishedFinisher>()?.tempBills.Add(nb);
                return nb;
            }
            catch (Exception ex)
            {
                Logger.Warning("UnfinishedFinisher.MakeTempBill failed: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static void RemoveTempBill(Bill_ProductionWithUft b)
        {
            try
            {
                Current.Game.GetComponent<UnfinishedFinisher>()?.tempBills.Remove(b);
                if (b != null && !b.DeletedOrDereferenced) b.billStack?.Delete(b);
            }
            catch (Exception ex)
            {
                Logger.Warning("UnfinishedFinisher.RemoveTempBill failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---- watching orders ----

        private static void CheckOrders(int tick)
        {
            var done = new List<int>();
            foreach (var kv in active)
            {
                Order o = kv.Value;
                string ended = null;
                bool finished = false;
                if (o.Uft.Destroyed)
                {
                    finished = o.Uft.workLeft <= 0.5f;
                    ended = finished ? "finished (the product was made)"
                        : "the item was destroyed or cancelled before it was finished (workLeft " + o.Uft.workLeft.ToString("F0") + ")";
                }
                else if (o.Pawn.Dead || o.Pawn.Destroyed || !o.Pawn.Spawned) ended = "the pawn is gone (dead or left the map)";
                else if (o.Pawn.Downed) ended = "the pawn was downed";
                else if (tick - o.StartTick > OrderTimeout) ended = "timed out after " + OrderTimeout + " ticks";
                else
                {
                    Job cj = o.Pawn.CurJob;
                    // A pawn holding the Finish this * order on this item (section 55)
                    // is still on it while paused for a need or clearing the table.
                    SweepManager sm = o.Pawn.Map?.GetComponent<SweepManager>();
                    bool holdsOrder = sm != null && sm.TryGetActiveSweep(o.Pawn, out SweepOrder so) && so.FinishUft == o.Uft;
                    // o.Bill can be null for an order re-tracked after a load; a null bill
                    // must not match every job that has no bill.
                    bool going = holdsOrder || (cj != null && (cj == o.HaulJob || (o.Bill != null && cj.bill == o.Bill)))
                        || (o.Bill != null && o.Pawn.jobs.jobQueue != null && o.Pawn.jobs.jobQueue.Any(q => q.job != null && q.job.bill == o.Bill));
                    if (!going && tick - o.StartTick > 120)
                        ended = "the pawn's job ended before the item was finished (now: " + (cj?.def.defName ?? "no job") + ", workLeft " + o.Uft.workLeft.ToString("F0") + ")";
                }
                if (ended == null) continue;
                done.Add(kv.Key);
                if (o.TempBill) RemoveTempBill(o.Bill);
                Logger.Always("Force-finish ended: " + o.Label + ", " + o.Pawn.LabelShort + ", after " + (tick - o.StartTick) + " ticks: " + ended
                    + (o.TempBill ? "; one-off bill removed" : ""));
                if (!finished && !o.Manual) CountFailure(kv.Key, tick, o.Label, ended, false);
                // The * order on this item ends with it, so it never runs on without the
                // tracking (or the one-off bill just removed) and a second pawn cannot be
                // sent while the first still holds it. A dead, downed or departed pawn's
                // order is ended by SweepManager itself.
                if (!finished && !o.Pawn.Dead && !o.Pawn.Destroyed && o.Pawn.Spawned && !o.Pawn.Downed)
                {
                    SweepManager esm = o.Pawn.Map?.GetComponent<SweepManager>();
                    if (esm != null && esm.EndFinishOrder(o.Pawn, o.Uft, "finish tracking ended: " + ended))
                        Logger.Always("Force-finish: " + o.Pawn.LabelShort + "'s * order on " + o.Label + " ended with it.");
                }
                if (finished)
                {
                    attempts.Remove(kv.Key); nextTry.Remove(kv.Key);
                    // Section 55: the product is made, so the pawn's * order on it ends and the
                    // pawn is free; the vanilla haul of the product goes on as a normal job.
                    SweepManager fsm = o.Pawn.Map?.GetComponent<SweepManager>();
                    if (fsm != null && fsm.EndFinishOrder(o.Pawn, o.Uft, "finished (product made)"))
                        Logger.Always("Force-finish: " + o.Pawn.LabelShort + "'s * order ended: finished (product made).");
                    if (DoNotBeLazyMod.Settings.forceFinishUnfinished) chainWait[o.Pawn] = tick;
                }
            }
            foreach (int k in done) active.Remove(k);
            ProcessChain(tick);
        }

        // Hands a pawn who just finished an item the next one, once its product haul is over.
        private static void ProcessChain(int tick)
        {
            if (chainWait.Count == 0) return;
            if (!DoNotBeLazyMod.Settings.forceFinishUnfinished) { chainWait.Clear(); return; }
            foreach (Pawn p in chainWait.Keys.ToList())
            {
                int since = chainWait[p];
                if (p == null || p.Dead || p.Destroyed || !p.Spawned || p.Downed || p.Drafted) { chainWait.Remove(p); continue; }
                Job cj = p.CurJob;
                bool hauling = cj != null && cj.def != null && cj.def.defName.StartsWith("Haul");
                if (hauling && tick - since < ChainTimeout) continue;
                chainWait.Remove(p);
                TryChain(p, tick);
            }
        }
    }
}
