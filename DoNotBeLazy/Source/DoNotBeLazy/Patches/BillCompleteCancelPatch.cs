using System;
using System.Collections.Generic;
using System.Linq;
using DoNotBeLazy.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DoNotBeLazy.Patches
{
    // dnbl-architecture.md section 41. Postfix on Bill_Production.Notify_IterationCompleted,
    // which the game calls each time a production bill finishes one repeat (and which
    // Bill_ProductionWithUft's override reaches through base). When that repeat took a
    // "do X times" bill to 0 (the moment vanilla shows "bill complete"), or a "do until you have X" bill to its target - every
    // unfinished item still linked to the bill whose creating pawn is no longer on the
    // map is cancelled (vanilla Cancel, 75% of each ingredient comes back). An item
    // whose pawn is on the map is left alone. A deleted bill never gets here.
    [HarmonyPatch(typeof(Bill_Production), nameof(Bill_Production.Notify_IterationCompleted))]
    internal static class BillCompleteCancelPatch
    {
        // UnfinishedThing.BoundBill (the property) nulls this private field once the item's
        // creator is gone, so by the time the bill completes the field is often null. An item
        // is matched by recipe, with this field equal to the bill or null (unless another bill
        // on the map shares the recipe: then a null field is ambiguous and the item is skipped).
        // Verified in Assembly-CSharp.
        private static readonly AccessTools.FieldRef<UnfinishedThing, Bill_ProductionWithUft> BoundBillField =
            AccessTools.FieldRefAccess<UnfinishedThing, Bill_ProductionWithUft>("boundBillInt");

        // "Do X times" is complete at 0 left and is decided here. "Do until you have X"
        // (TargetCount) is NOT decided here: vanilla calls Notify_IterationCompleted BEFORE it
        // places the iteration's products, and CountProducts reads a cache refreshed every 204
        // ticks, so the bill is queued and checked on the next tick by PendingBillChecker, using
        // vanilla's own test (CountProducts >= targetCount after UpdateResourceCounts).
        // "Do forever" never completes. (dnbl-architecture.md section 41.)
        private static void Postfix(Bill_Production __instance)
        {
            try
            {
                if (!DoNotBeLazyMod.Settings.cancelUnfinishedOnBillDone) return;
                Bill_ProductionWithUft bill = __instance as Bill_ProductionWithUft;
                if (bill == null) return;
                if (bill.repeatMode == BillRepeatModeDefOf.RepeatCount)
                {
                    if (bill.repeatCount == 0) RunCancel(bill);
                }
                else if (bill.repeatMode == BillRepeatModeDefOf.TargetCount)
                {
                    PendingBillChecker checker = Current.Game?.GetComponent<PendingBillChecker>();
                    ShouldDoNowSatisfiedPatch.metBills.Add(bill); // stops the ShouldDoNow postfix queuing it a second time
                    if (checker != null) checker.Queue(bill);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("BillCompleteCancelPatch failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Bills queued by Postfix, checked on the next game tick. Not saved: if a save happens
        // in between, the queue is lost and nothing breaks (the bill simply keeps its items).
        internal class PendingBillChecker : GameComponent
        {
            private readonly List<Bill_ProductionWithUft> pending = new List<Bill_ProductionWithUft>();

            public PendingBillChecker(Game game) { }

            public void Queue(Bill_ProductionWithUft bill)
            {
                if (!pending.Contains(bill)) pending.Add(bill);
            }

            public override void GameComponentTick()
            {
                if (pending.Count == 0) return;
                var batch = new List<Bill_ProductionWithUft>(pending);
                pending.Clear();
                foreach (Bill_ProductionWithUft bill in batch)
                {
                    try
                    {
                        if (bill.DeletedOrDereferenced || bill.Map == null) continue;
                        if (bill.repeatMode != BillRepeatModeDefOf.TargetCount) continue;
                        bill.Map.resourceCounter.UpdateResourceCounts();
                        if (bill.recipe.WorkerCounter.CountProducts(bill) >= bill.targetCount) RunCancel(bill);
                        else if (!bill.paused) ShouldDoNowSatisfiedPatch.metBills.Remove(bill); // not met yet: let ShouldDoNow queue it again
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("BillCompleteCancelPatch deferred check failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }

        // dnbl-architecture.md section 41, second patch (2026-11-07). Postfix on
        // Bill_Production.ShouldDoNow, where vanilla itself decides a "do until you have X" bill
        // is satisfied (returns false when CountProducts >= targetCount, or when paused). This
        // covers products that reached the count later than the next-tick check (dropped on the
        // floor, hauled in afterwards). Fires the cancel once per transition from not-met to met:
        // the per-bill state is one HashSet lookup, and the work is queued for the next tick.
        [HarmonyPatch(typeof(Bill_Production), nameof(Bill_Production.ShouldDoNow))]
        internal static class ShouldDoNowSatisfiedPatch
        {
            internal static readonly HashSet<Bill_ProductionWithUft> metBills = new HashSet<Bill_ProductionWithUft>();
            private static Game trackedGame;

            private static void Postfix(Bill_Production __instance, bool __result)
            {
                try
                {
                    if (__instance.repeatMode != BillRepeatModeDefOf.TargetCount) return;
                    Bill_ProductionWithUft bill = __instance as Bill_ProductionWithUft;
                    if (bill == null) return;
                    if (trackedGame != Current.Game)
                    {
                        metBills.Clear();
                        trackedGame = Current.Game;
                    }
                    if (__result) { metBills.Remove(bill); return; }
                    if (bill.suspended) return;
                    if (!DoNotBeLazyMod.Settings.cancelUnfinishedOnBillDone) return;
                    if (!metBills.Add(bill)) return;
                    PendingBillChecker checker = Current.Game?.GetComponent<PendingBillChecker>();
                    if (checker != null) checker.Queue(bill);
                }
                catch (Exception ex)
                {
                    Logger.Warning("ShouldDoNowSatisfiedPatch failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void RunCancel(Bill_ProductionWithUft bill)
        {
            try
            {
                Map map = bill.Map;
                ThingDef uftDef = bill.recipe?.unfinishedThingDef;
                if (map == null || uftDef == null) return;

                var cancelled = new List<string>();
                var left = new List<string>();
                var notPlaced = new List<string>();
                // Spawned items plus items in pawn hands, inventories and containers (runs once
                // per bill completion). Destroying a held item is safe: Thing.Destroy removes it
                // from its holder.
                var found = new List<Thing>();
                ThingOwnerUtility.GetAllThingsRecursively(map, ThingRequest.ForDef(uftDef), found);

                bool otherBillSameRecipe = false;
                foreach (Thing giver in map.listerThings.ThingsInGroup(ThingRequestGroup.PotentialBillGiver))
                {
                    List<Bill> bills = (giver as IBillGiver)?.BillStack?.Bills;
                    if (bills == null) continue;
                    foreach (Bill other in bills)
                    {
                        if (other != bill && other is Bill_ProductionWithUft && other.recipe == bill.recipe) { otherBillSameRecipe = true; break; }
                    }
                    if (otherBillSameRecipe) break;
                }

                foreach (Thing t in found.Distinct())
                {
                    UnfinishedThing uft = t as UnfinishedThing;
                    if (uft == null || uft.Destroyed || uft.Recipe != bill.recipe) continue;
                    Bill_ProductionWithUft bound = BoundBillField(uft);
                    if (bound != null && bound != bill) continue;
                    if (bound == null && otherBillSameRecipe) continue;
                    Pawn creator = uft.Creator;
                    bool onMap = creator != null && !creator.Destroyed && !creator.Dead && creator.MapHeld == map;
                    string label = uft.LabelNoCount + " (" + uft.ThingID + ") at " + uft.PositionHeld + ", made by " + (creator?.LabelShort ?? "nobody");
                    if (onMap)
                    {
                        left.Add(label);
                        continue;
                    }
                    // Cancel's refund is placed at the item's own position and map; a held item
                    // has neither, so drop it beside its holder first.
                    if (!uft.Spawned)
                    {
                        Map heldMap = uft.MapHeld;
                        ThingOwner holder = uft.holdingOwner;
                        Thing dropped;
                        if (holder == null || heldMap == null
                            || !holder.TryDrop(uft, uft.PositionHeld, heldMap, ThingPlaceMode.Near, out dropped)
                            || !uft.Spawned)
                        {
                            notPlaced.Add(label);
                            continue;
                        }
                    }
                    uft.Destroy(DestroyMode.Cancel);
                    cancelled.Add(label);
                }

                if (cancelled.Count > 0 || left.Count > 0 || notPlaced.Count > 0)
                {
                    Logger.Always("Bill complete: " + bill.Label + " on " + (bill.billStack?.billGiver?.LabelShort ?? "?")
                        + "; cancelled unfinished: " + (cancelled.Count > 0 ? string.Join("; ", cancelled.ToArray()) : "none")
                        + "; left (pawn on the map): " + (left.Count > 0 ? string.Join("; ", left.ToArray()) : "none")
                        + "; skipped (held, could not be placed on the map): " + (notPlaced.Count > 0 ? string.Join("; ", notPlaced.ToArray()) : "none"));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning("BillCompleteCancelPatch failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
