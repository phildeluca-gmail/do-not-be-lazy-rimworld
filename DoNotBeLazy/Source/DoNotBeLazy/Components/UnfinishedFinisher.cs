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
        }

        public override void ExposeData()
        {
            tempBills.RemoveAll(b => b == null);
            Scribe_Collections.Look(ref tempBills, "dnblTempBills", LookMode.Reference);
            if (tempBills == null) tempBills = new List<Bill_ProductionWithUft>();
        }

        public override void FinalizeInit()
        {
            try
            {
                foreach (Bill_ProductionWithUft b in tempBills.ToList())
                {
                    if (b != null && !b.DeletedOrDereferenced)
                    {
                        b.billStack?.Delete(b);
                        Logger.Always("Force-finish: removed a leftover one-off bill (" + b.recipe?.label + ") after load.");
                    }
                }
                tempBills.Clear();
            }
            catch (Exception ex)
            {
                Logger.Warning("UnfinishedFinisher.FinalizeInit failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public override void GameComponentTick()
        {
            int tick = Find.TickManager.TicksGame;
            try
            {
                if (active.Count > 0 && tick % CheckInterval == 0) CheckOrders(tick);
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
                var found = new List<UnfinishedThing>();
                foreach (Thing t in map.listerThings.AllThings)
                {
                    UnfinishedThing u = t as UnfinishedThing;
                    if (u != null && u.Spawned && u.Recipe != null) found.Add(u);
                }
                foreach (UnfinishedThing uft in found)
                {
                    int id = uft.thingIDNumber;
                    if (active.ContainsKey(id) || gaveUp.Contains(id)) continue;
                    int at;
                    if (nextTry.TryGetValue(id, out at) && tick < at) continue;
                    if (uft.IsForbidden(Faction.OfPlayer)) continue;
                    if (map.reservationManager.IsReservedByAnyoneOf(uft, Faction.OfPlayer)) continue;
                    if (VanillaWillResume(uft)) continue;
                    TryOrder(uft, null, false, out string why);
                }
            }
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
                    if (!manual)
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
                    + (needHaul ? "; clearing the table first" : ""));
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
                why = "no usable " + (users.Count > 0 ? users[0].label : "work table") + " for " + recipe.label;
            else
                why = "no capable pawn (" + (reasons.Count > 0 ? string.Join("; ", reasons.Distinct().Take(3).ToArray()) : "none free") + ")";
            return false;
        }

        private static Pawn BestPawn(List<Pawn> pool, UnfinishedThing uft, Thing table, Bill_ProductionWithUft bill,
            bool manual, List<string> reasons)
        {
            Pawn best = null;
            float bestScore = float.MaxValue;
            foreach (Pawn p in pool)
            {
                string r = Incapable(p, uft, table, bill, manual);
                if (r != null) { reasons.Add(p.LabelShort + ": " + r); continue; }
                float score = (p.Position - uft.Position).LengthHorizontalSquared;
                if (p == uft.Creator) score -= 1000000f; // its maker first, as vanilla would
                if (score < bestScore) { bestScore = score; best = p; }
            }
            return best;
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
            if (!manual)
            {
                Job cj = p.CurJob;
                if (cj != null && !IdleJobs.Contains(cj.def.defName)) return "busy";
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
                    bool going = (cj != null && (cj == o.HaulJob || cj.bill == o.Bill))
                        || (o.Pawn.jobs.jobQueue != null && o.Pawn.jobs.jobQueue.Any(q => q.job != null && q.job.bill == o.Bill));
                    if (!going && tick - o.StartTick > 120)
                        ended = "the pawn's job ended before the item was finished (now: " + (cj?.def.defName ?? "no job") + ", workLeft " + o.Uft.workLeft.ToString("F0") + ")";
                }
                if (ended == null) continue;
                done.Add(kv.Key);
                if (o.TempBill) RemoveTempBill(o.Bill);
                Logger.Always("Force-finish ended: " + o.Label + ", " + o.Pawn.LabelShort + ", after " + (tick - o.StartTick) + " ticks: " + ended
                    + (o.TempBill ? "; one-off bill removed" : ""));
                if (!finished && !o.Manual) CountFailure(kv.Key, tick, o.Label, ended, false);
                if (finished) { attempts.Remove(kv.Key); nextTry.Remove(kv.Key); }
            }
            foreach (int k in done) active.Remove(k);
        }
    }
}
