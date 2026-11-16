using System.Collections.Generic;
using DoNotBeLazy.Core;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DoNotBeLazy.Patches
{
    // Section 50 (2026-11-15), the user's words: "Add feature DNBL: unpause
    // all paused bills at table. Uses table interface." Postfix on
    // ITab_Bills.FillTab (the bills tab of a work table, verified by
    // decompiling lib\Assembly-CSharp.dll). Draws one button, "Resume all
    // bills", left of the paste icon and clear of the "Add bill" button
    // (150 wide at x 10). A click resumes every bill on the table that is
    // suspended (Bill.suspended, the pause icon on each bill) and every
    // Bill_Production that is paused for count (Bill_Production.paused) and
    // could be unpaused with its own Unpause button (CanUnpause is private;
    // the same test is repeated below). Bills paused because they are at
    // their target count stay paused: ShouldDoNow would pause them again on
    // the next tick. Greyed out, with the reason as a tooltip, when there is
    // nothing to resume.
    [HarmonyPatch(typeof(ITab_Bills), "FillTab")]
    internal static class ResumeBillsPatch
    {
        private static readonly System.Reflection.PropertyInfo SelThingProp = AccessTools.Property(typeof(ITab), "SelThing");

        private static void Postfix(ITab_Bills __instance)
        {
            try
            {
                Building_WorkTable table = SelThingProp?.GetValue(__instance, null) as Building_WorkTable;
                if (table == null || table.billStack == null)
                {
                    return;
                }

                CountResumable(table, out int resumable, out int suspended, out int waiting);

                // Tab coordinates: 420 wide, paste icon at x 372, add-bill button x 10..160.
                Rect rect = new Rect(420f - 48f - 156f, 10f, 150f, 29f);
                string tip = resumable > 0
                    ? $"Resume {resumable} paused bill(s) on this table."
                    : waiting > 0
                        ? "No bill can be resumed: the paused bills have reached their target count and resume by themselves when stock falls."
                        : "No bill on this table is paused.";
                TooltipHandler.TipRegion(rect, tip);
                if (Widgets.ButtonText(rect, "Resume all bills", true, true, resumable > 0))
                {
                    int resumedSuspended = 0;
                    int resumedPaused = 0;
                    foreach (Bill bill in table.billStack.Bills)
                    {
                        if (bill.suspended)
                        {
                            bill.suspended = false;
                            resumedSuspended++;
                        }

                        if (bill is Bill_Production production && CanUnpause(production))
                        {
                            production.paused = false;
                            resumedPaused++;
                        }
                    }

                    DoNotBeLazy.Core.Logger.Message($"Resume all bills clicked on {table.LabelShort} at {table.Position}: {resumedSuspended + resumedPaused} bill(s) resumed ({resumedSuspended} suspended, {resumedPaused} paused for count), {table.billStack.Count} bill(s) on the table, {waiting} left paused at their target count");
                }
            }
            catch (System.Exception ex)
            {
                DoNotBeLazy.Core.Logger.Warning($"Resume all bills button failed: {ex}");
            }
        }

        // Same test as Bill_Production.CanUnpause (private).
        private static bool CanUnpause(Bill_Production bill)
        {
            return bill.paused && bill.repeatMode == BillRepeatModeDefOf.TargetCount && bill.pauseWhenSatisfied
                && bill.recipe.WorkerCounter.CountProducts(bill) < bill.targetCount;
        }

        private static void CountResumable(Building_WorkTable table, out int resumable, out int suspended, out int waiting)
        {
            resumable = 0;
            suspended = 0;
            waiting = 0;
            List<Bill> bills = table.billStack.Bills;
            for (int i = 0; i < bills.Count; i++)
            {
                Bill bill = bills[i];
                bool isSuspended = bill.suspended;
                bool canUnpause = bill is Bill_Production production && CanUnpause(production);
                if (isSuspended || canUnpause)
                {
                    resumable++;
                }

                if (isSuspended)
                {
                    suspended++;
                }

                if (!isSuspended && !canUnpause && bill is Bill_Production waitingBill && waitingBill.paused)
                {
                    waiting++;
                }
            }
        }
    }
}
