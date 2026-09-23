using System.Collections.Generic;
using DoNotBeLazy.Comps;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Logger = DoNotBeLazy.Core.Logger;

namespace DoNotBeLazy.Jobs
{
    // Stuff-first hauling and loading - dnbl-architecture.md section 18.
    // Shaped like Pick Up And Haul's JobDriver_HaulToInventory /
    // JobDriver_UnloadYourHauledInventory pair - inspiration only, verified
    // by reading PUAH's own shipped source (Source/PickUpAndHaul/*.cs under
    // its Workshop item folder); no PUAH type or file is referenced here.
    // Written independently and simplified for v1: no Combat Extended or
    // Extended Storage compatibility, no per-cell capacity bookkeeping -
    // every destination is looked up fresh, one item at a time, at delivery
    // time, the same way PUAH's own unload driver does it.
    //
    // Two modes, set once by job.haulMode at creation:
    //   ToCellStorage - "storage" mode (HaulInterceptPatch, layer 2). Built
    //     from an ordinary vanilla HaulToCell/HaulToContainer job.
    //     Opportunistic items come from the map's listerHaulables; every
    //     item, primary included, gets its own destination via
    //     StoreUtility.TryFindBestBetterStorageFor at delivery time.
    //   ToContainer - "fixed destination" mode (SweepManager, vehicle
    //     packing). job.targetB is the persistent target (the vehicle) for
    //     the whole trip. Opportunistic items come from asking
    //     job.workGiverDef's own WorkGiver_Scanner for the same target
    //     again after each pickup - once an item is off the map, spawned
    //     is false, so the scanner's own candidate search naturally offers
    //     something else next time. This assumes the scanner filters its
    //     candidates on Spawned, the near-universal RimWorld WorkGiver
    //     convention; Vehicle Framework's own DLL is not in lib\ and this
    //     could not be verified against it directly - see dnbl-architecture.md
    //     section 18.
    public class JobDriver_StuffAndHaul : JobDriver
    {
        private const int MaxOpportunisticItems = 8;
        private const float MaxOpportunisticScanRange = 24f;

        // Independent backstop, unrelated to MaxOpportunisticItems and not
        // to be tied to it - a generous multiple so a normal trip (at most
        // MaxOpportunisticItems successful pickups, plus a handful of
        // refusals along the way) never comes close, while still capping
        // any future defect in the two exit gates below at a bounded number
        // of same-tick iterations rather than none at all. See
        // dnbl-architecture.md section 18, fixed 2026-09-22.
        private const int MaxLoopIterations = 50;

        private readonly List<Thing> carried = new List<Thing>();

        // Things refused this trip because taking them would overencumber
        // the pawn - keyed on Thing instance (reference equality, the same
        // assumption `carried.Contains` already makes in this file; Thing
        // is not seen to override Equals/GetHashCode). Once refused, an
        // item is never re-offered for the rest of the trip.
        private readonly HashSet<Thing> refused = new HashSet<Thing>();

        private int opportunisticPicked;
        private int destinationsUsed;
        private int loopIterations;

        // The trip's original target, captured once in PickUpToil - used
        // only to key the zero-progress backstop below at trip end.
        private Thing primaryTarget;

        // Backstop for the 2026-09-22 same-tick crash (dnbl-architecture.md
        // section 18) - independent of CanPickUpAtLeastOne below, which is
        // the actual fix for the case that crashed the game. A trip that
        // delivered nothing and picked up nothing must not be immediately
        // recreated for the same pawn/target pair, regardless of which of
        // the three construction points (HaulInterceptPatch,
        // TransporterInterceptPatch, SweepManager.WrapForVehicleStuffing)
        // built it or why the trip failed - so this table is shared by all
        // three rather than owned by one patch. Reference-keyed like
        // `refused` above; pruned lazily on lookup, so it never holds more
        // than one entry per colonist who has hit a zero-progress trip
        // recently.
        private static readonly Dictionary<Pawn, Dictionary<Thing, int>> zeroProgressExpiry
            = new Dictionary<Pawn, Dictionary<Thing, int>>();

        private const int ZeroProgressSuppressionTicks = 60;

        // Delivery-side backstop, added 2026-09-22 alongside the
        // HaulInterceptPatch fix for the three same-tick crashes that
        // night (dnbl-architecture.md section 18) - independent of that
        // fix, and holds even if some other path reaches the same
        // zero-progress state. Mirrors the fixed-destination gather loop's
        // MaxLoopIterations above: a generous multiple so a normal trip
        // (at most MaxOpportunisticItems + 1 deliveries) never comes
        // close, while still capping any future defect that re-enters
        // `deliver` without the carried set ever shrinking.
        private const int MaxStuckDeliveryAttempts = 50;
        private int stuckDeliveryAttempts;

        // Progress measure for the backstop above - re-armed 2026-09-22
        // for the third same-night defect (dnbl-architecture.md section
        // 18). Was keyed on carried.Count, which also changes when a
        // reservation failure drops an item (see EndTripAfterReservationFailure
        // below), so a trip that dropped its whole inventory one item at a
        // time never tripped it - carried.Count kept shrinking while
        // nothing was ever delivered. destinationsUsed only increases on
        // an actual delivery, so it is the right measure of progress.
        private int lastDeliverEntryDestinationsUsed = -1;

        // Shared with every interception point so the capacity check that
        // decides whether to substitute a stuffing job can never disagree
        // with the one PickUpToil applies at delivery time - the actual fix
        // for the 2026-09-22 crash: a pawn who cannot carry even one unit
        // of the primary target must keep vanilla's own job (which hauls
        // via carryTracker, not inventory, and has no mass limit) rather
        // than being handed a stuffing job guaranteed to refuse it and end
        // having accomplished nothing.
        internal static bool CanPickUpAtLeastOne(Pawn pawn, Thing thing)
        {
            return pawn != null && thing != null && MassUtility.CountToPickUpUntilOverEncumbered(pawn, thing) > 0;
        }

        internal static bool IsZeroProgressSuppressed(Pawn pawn, Thing target)
        {
            if (pawn == null || target == null
                || !zeroProgressExpiry.TryGetValue(pawn, out Dictionary<Thing, int> byTarget)
                || !byTarget.TryGetValue(target, out int expiryTick))
            {
                return false;
            }

            if (Find.TickManager.TicksGame >= expiryTick)
            {
                byTarget.Remove(target);
                if (byTarget.Count == 0)
                {
                    zeroProgressExpiry.Remove(pawn);
                }
                return false;
            }

            return true;
        }

        private static void RecordZeroProgress(Pawn pawn, Thing target)
        {
            if (pawn == null || target == null)
            {
                return;
            }

            if (!zeroProgressExpiry.TryGetValue(pawn, out Dictionary<Thing, int> byTarget))
            {
                byTarget = new Dictionary<Thing, int>();
                zeroProgressExpiry[pawn] = byTarget;
            }

            byTarget[target] = Find.TickManager.TicksGame + ZeroProgressSuppressionTicks;
        }

        private CompDnblCargo CargoComp => pawn.GetComp<CompDnblCargo>();

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, errorOnFailed: errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            bool fixedDestination = job.haulMode == HaulMode.ToContainer;

            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.A);
            yield return PickUpToil(isPrimary: true);

            // Pre-declared so every toil below can jump to one another
            // regardless of yield order - the same forward/backward jump
            // pattern vanilla's own JobDriver_HaulToCell and PUAH's drivers
            // use.
            Toil deliver = new Toil();
            Toil moveToCarry = new Toil();
            Toil carryToCell = Toils_Haul.CarryHauledThingToCell(TargetIndex.B);
            Toil carryToContainer = Toils_Haul.CarryHauledThingToContainer();
            Toil finishCell = FinishDeliveryToil(deliver);
            Toil finishContainer = FinishDeliveryToil(deliver);

            ConfigureDeliverToil(deliver, moveToCarry, fixedDestination);
            ConfigureMoveToCarryToil(moveToCarry, deliver);

            if (fixedDestination)
            {
                Toil loopStart = new Toil();
                yield return loopStart;

                Toil askScanner = new Toil();
                askScanner.initAction = () =>
                {
                    // Hard backstop, checked before anything else: no
                    // defect in the gates below can spin this loop past
                    // MaxLoopIterations same-tick iterations.
                    loopIterations++;
                    if (loopIterations > MaxLoopIterations)
                    {
                        Logger.Message($"{pawn.LabelShort}: fixed-destination gather loop capped at {MaxLoopIterations} iterations, ending trip");
                        pawn.jobs.curDriver.JumpToToil(deliver);
                        return;
                    }

                    if (opportunisticPicked >= MaxOpportunisticItems)
                    {
                        Logger.Message($"{pawn.LabelShort}: opportunistic pickup cap ({MaxOpportunisticItems}) reached, ending gather");
                        pawn.jobs.curDriver.JumpToToil(deliver);
                        return;
                    }
                    if (MassUtility.EncumbrancePercent(pawn) >= 1f)
                    {
                        Logger.Message($"{pawn.LabelShort}: fully encumbered, ending gather");
                        pawn.jobs.curDriver.JumpToToil(deliver);
                        return;
                    }
                    if (!(job.workGiverDef?.Worker is WorkGiver_Scanner scanner)
                        || !(job.targetB.Thing is Thing persistentTarget))
                    {
                        pawn.jobs.curDriver.JumpToToil(deliver);
                        return;
                    }

                    Job found = scanner.JobOnThing(pawn, persistentTarget, true);
                    Thing foundThing = found?.targetA.Thing;
                    if (foundThing == null || foundThing == persistentTarget || carried.Contains(foundThing))
                    {
                        Logger.Message($"{pawn.LabelShort}: nothing more to gather, ending gather");
                        pawn.jobs.curDriver.JumpToToil(deliver);
                        return;
                    }
                    if (refused.Contains(foundThing))
                    {
                        // The scanner's best remaining offer is something
                        // this trip already refused as overencumbering -
                        // the defect that crashed the game twice on
                        // 2026-09-22 (see dnbl-architecture.md section 18):
                        // re-offering a refused item forever, tripping
                        // neither exit gate. End the gather rather than
                        // retry it.
                        Logger.Message($"{pawn.LabelShort}: only already-refused items remain, ending gather");
                        pawn.jobs.curDriver.JumpToToil(deliver);
                        return;
                    }

                    job.SetTarget(TargetIndex.A, foundThing);
                    job.count = found.count > 0 ? found.count : foundThing.stackCount;
                };
                yield return askScanner;
                yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch)
                    .FailOnDespawnedNullOrForbidden(TargetIndex.A);
                yield return PickUpToil(isPrimary: false);
                yield return Toils_Jump.Jump(loopStart);
            }
            else
            {
                yield return GatherStorageCandidatesToil();

                // Guard against an empty queue before the loop's first
                // extract: ExtractNextTargetFromQueue does nothing when the
                // queue is empty, which would otherwise leave
                // TargetIndex.A pointing at the already-picked-up (and so
                // already-despawned) primary target and send the pawn
                // walking back to it.
                Toil nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A, false);
                yield return Toils_Jump.JumpIf(deliver, () => job.targetQueueA.NullOrEmpty());
                yield return nextTarget;
                yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch)
                    .FailOnDespawnedNullOrForbidden(TargetIndex.A);
                yield return PickUpToil(isPrimary: false);
                yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty());
            }

            yield return deliver;
            yield return moveToCarry;
            yield return Toils_Jump.JumpIf(carryToContainer, () => job.GetTarget(TargetIndex.B).HasThing);
            yield return carryToCell;
            yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.B, carryToCell, true);
            yield return finishCell;
            yield return carryToContainer;
            yield return Toils_Haul.DepositHauledThingInContainer(TargetIndex.B, TargetIndex.None);
            yield return finishContainer;
        }

        // Picks up job's current TargetIndex.A (job.count, clamped live
        // against the pawn's remaining carry capacity) into pawn.inventory
        // - not carryTracker - and registers it as DNBL cargo. Used for the
        // primary target and for every opportunistic one alike.
        private Toil PickUpToil(bool isPrimary)
        {
            Toil toil = new Toil();
            toil.initAction = () =>
            {
                Thing thing = job.GetTarget(TargetIndex.A).Thing;
                if (thing == null || !thing.Spawned)
                {
                    return;
                }
                if (isPrimary)
                {
                    primaryTarget = thing;
                }
                if (Toils_Haul.ErrorCheckForCarry(pawn, thing))
                {
                    return;
                }

                int want = Mathf.Min(job.count > 0 ? job.count : thing.stackCount, thing.stackCount);
                int can = MassUtility.CountToPickUpUntilOverEncumbered(pawn, thing);
                int take = Mathf.Min(want, can);
                if (take <= 0)
                {
                    // Once per item per trip, not once per loop iteration -
                    // this item is now in `refused` and the scanner will
                    // not be allowed to re-offer it for the rest of the
                    // trip (see askScanner). Guard the log too, in case
                    // something offers the same Thing again some other way.
                    if (refused.Add(thing))
                    {
                        Logger.Message($"{pawn.LabelShort}: {thing.LabelCap} would overencumber, leaving it");
                    }
                    return;
                }

                Thing picked = thing.SplitOff(take);
                pawn.inventory.GetDirectlyHeldThings().TryAdd(picked, true);
                CargoComp?.Register(picked);
                carried.Add(picked);
                if (!isPrimary)
                {
                    opportunisticPicked++;
                }

                Logger.Message($"{pawn.LabelShort} stuffed {picked.LabelCap} x{take} into inventory ({MassUtility.EncumbrancePercent(pawn):P0} full)");
            };
            return toil;
        }

        // Storage mode only. One-shot batch scan of the map's haulables,
        // nearest first from the pawn's current position (standing at the
        // primary item, having just picked it up) - PUAH's own outward-sort
        // is the model. Filtered through the same vanilla gate every
        // ordinary automatic haul uses, so nothing here second-guesses
        // vanilla about what is haulable.
        private Toil GatherStorageCandidatesToil()
        {
            Toil toil = new Toil();
            toil.initAction = () =>
            {
                job.targetQueueA = new List<LocalTargetInfo>();
                job.countQueue = new List<int>();

                if (opportunisticPicked >= MaxOpportunisticItems || MassUtility.EncumbrancePercent(pawn) >= 1f)
                {
                    return;
                }

                IntVec3 origin = pawn.Position;
                var candidates = new List<Thing>(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling());
                candidates.Sort((a, b) =>
                    (a.Position - origin).LengthHorizontalSquared.CompareTo((b.Position - origin).LengthHorizontalSquared));

                float rangeSquared = MaxOpportunisticScanRange * MaxOpportunisticScanRange;
                int added = 0;
                foreach (Thing candidate in candidates)
                {
                    if (added >= MaxOpportunisticItems - opportunisticPicked || MassUtility.EncumbrancePercent(pawn) >= 1f)
                    {
                        break;
                    }
                    if (carried.Contains(candidate))
                    {
                        continue;
                    }
                    if ((candidate.Position - origin).LengthHorizontalSquared > rangeSquared)
                    {
                        continue;
                    }
                    if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, candidate, true))
                    {
                        continue;
                    }
                    if (MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, candidate, 1))
                    {
                        continue;
                    }

                    job.targetQueueA.Add(candidate);
                    job.countQueue.Add(candidate.stackCount);
                    added++;
                }
            };
            return toil;
        }

        // Picks the next carried item and its destination (fixed, for
        // vehicle packing, or recomputed per item via StoreUtility for
        // storage mode), reserves it, and jumps into the carry-and-deliver
        // toils. Ends the job once nothing is left to deliver.
        private void ConfigureDeliverToil(Toil deliver, Toil moveToCarry, bool fixedDestination)
        {
            deliver.initAction = () =>
            {
                // Hard backstop, checked before anything else this toil
                // does: if `deliver` keeps being re-entered without an
                // actual delivery happening, something is looping without
                // progress - regardless of which path (fixed-destination
                // gather, a reservation failure, or anything else) is
                // driving the re-entry.
                if (destinationsUsed == lastDeliverEntryDestinationsUsed)
                {
                    stuckDeliveryAttempts++;
                    if (stuckDeliveryAttempts > MaxStuckDeliveryAttempts)
                    {
                        Logger.Message($"{pawn.LabelShort}: delivery capped at {MaxStuckDeliveryAttempts} attempts without a delivery succeeding, ending stuffing trip");
                        EndJobWith(JobCondition.Incompletable);
                        return;
                    }
                }
                else
                {
                    stuckDeliveryAttempts = 0;
                }
                lastDeliverEntryDestinationsUsed = destinationsUsed;

                carried.RemoveAll(t => t == null || t.Destroyed || !pawn.inventory.innerContainer.Contains(t));

                if (carried.Count == 0)
                {
                    FinishTripAndEndJob(JobCondition.Succeeded);
                    return;
                }

                Thing next = carried[0];
                LocalTargetInfo dest;

                if (fixedDestination)
                {
                    dest = job.targetB;
                }
                else if (StoreUtility.TryFindBestBetterStorageFor(next, pawn, pawn.Map, StoragePriority.Unstored, pawn.Faction, out IntVec3 cell, out IHaulDestination haulDestination))
                {
                    // Classify the destination the way vanilla does - fixed
                    // 2026-09-22, dnbl-architecture.md section 18. Verified
                    // against lib\Assembly-CSharp.dll: both of vanilla's own
                    // equivalent call sites, HaulAIUtility.HaulToStorageJob
                    // and Pawn_JobTracker's opportunistic-haul search, check
                    // ISlotGroupParent first for cell storage and only then
                    // treat a Thing as a container, and only when
                    // ThingOwnerUtility.TryGetInnerInteractableThingOwner
                    // (Verse, extension on Thing) returns non-null. A bare
                    // `is Thing` test is not enough: Building_Storage - a
                    // nutrient paste hopper, or an ordinary shelf - is a
                    // Thing and an ISlotGroupParent but not an IThingHolder,
                    // so it has no ThingOwner and must be delivered to as
                    // cell storage, never routed into
                    // Toils_Haul.DepositHauledThingInContainer, which can
                    // never succeed for it. This was the real cause of the
                    // 2026-09-22 crashes - the hopper case reached this line
                    // as a storage-mode delivery, not as a
                    // HaulToContainerJob call, which is why the hopper-def
                    // decline in HaulToContainerJobPatch.Postfix
                    // (Patches/HaulInterceptPatch.cs) never saw it.
                    if (haulDestination is ISlotGroupParent)
                    {
                        dest = cell;
                    }
                    else if (haulDestination is Thing containerThing && containerThing.TryGetInnerInteractableThingOwner() != null)
                    {
                        dest = containerThing;
                    }
                    else
                    {
                        // Mirrors vanilla's own Log.Error fallback in
                        // HaulToStorageJob for a haulDestination it can't
                        // classify either way - should not happen given the
                        // two checks above, but never silently misroute.
                        DropAndSkip(next, "destination could not be classified as cell or container storage");
                        pawn.jobs.curDriver.JumpToToil(deliver);
                        return;
                    }
                }
                else
                {
                    DropAndSkip(next, "no destination found for it");
                    pawn.jobs.curDriver.JumpToToil(deliver);
                    return;
                }

                if (!pawn.Map.reservationManager.Reserve(pawn, job, dest))
                {
                    // A reservation failure ends the trip - fixed
                    // 2026-09-22, third defect that night (dnbl-architecture.md
                    // section 18). Before this fix the failing item was
                    // dropped and `deliver` was re-entered on the very same
                    // carried list, which tried the very same destination
                    // again for the next item: for a fixed-destination trip
                    // (job.targetB never changes) every remaining carried
                    // item failed the identical reservation, one drop line
                    // each - 288 lines across three pawns in two minutes
                    // live. The destination is not coming back this trip,
                    // so stop asking it.
                    EndTripAfterReservationFailure();
                    return;
                }

                job.SetTarget(TargetIndex.A, next);
                job.SetTarget(TargetIndex.B, dest);
                pawn.jobs.curDriver.JumpToToil(moveToCarry);
            };
        }

        private void DropAndSkip(Thing thing, string reason)
        {
            pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, out _);
            CargoComp?.Unregister(thing);
            carried.Remove(thing);
            Logger.Message($"{pawn.LabelShort}: {reason} - dropped {thing.LabelCap} near itself");
        }

        // Everything still in the pawn's inventory when a destination
        // reservation fails is dropped here, in one line - it cannot be
        // carried forever, and delivery has already proved it cannot be
        // placed this trip. One summary line with a count, not DropAndSkip
        // per stack, is the point of this method: the old per-item path is
        // exactly what produced 288 near-identical log lines in two
        // minutes on 2026-09-22.
        private void EndTripAfterReservationFailure()
        {
            int remaining = carried.Count;
            if (remaining > 0)
            {
                foreach (Thing thing in carried)
                {
                    pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, out _);
                    CargoComp?.Unregister(thing);
                }
                carried.Clear();
                Logger.Message($"{pawn.LabelShort}: could not reserve a destination - dropped {remaining} carried item(s) and ending stuffing trip");
            }
            FinishTripAndEndJob(JobCondition.Incompletable);
        }

        // Shared trip-end logging and the zero-progress backstop - used by
        // both the ordinary "nothing left to carry" ending and the
        // reservation-failure ending above, so both go through the same
        // suppression check and the same one summary line.
        private void FinishTripAndEndJob(JobCondition condition)
        {
            // Zero-progress backstop - dnbl-architecture.md section 18.
            // Originally fixed 2026-09-22 for the same-tick crash, keyed on
            // destinationsUsed == 0 && opportunisticPicked == 0. Widened
            // the same night, third defect: a trip can pick up a full load
            // of eight items and still deliver none of them, if every
            // delivery fails to reserve a destination - opportunisticPicked
            // being nonzero proves the gather side worked, not that the
            // trip accomplished anything. The measure that matters is
            // whether anything was ever delivered.
            bool zeroProgress = destinationsUsed == 0;
            string suppressNote = "";
            if (zeroProgress && primaryTarget != null)
            {
                RecordZeroProgress(pawn, primaryTarget);
                suppressNote = $" - suppressing a same-target stuffing job for {ZeroProgressSuppressionTicks} ticks";
            }

            // Refusal summary belongs on this line unconditionally, not
            // only when the trip comes back empty - the same trap named in
            // CLAUDE.md section 4 (2026-09-20): a count computed on every
            // run but logged only on failure records nothing about a
            // successful trip.
            Logger.Message($"{pawn.LabelShort}: stuffing trip ended - {destinationsUsed} item(s) delivered, {opportunisticPicked} picked up opportunistically, {refused.Count} refused as overencumbering{suppressNote}");
            EndJobWith(condition);
        }

        private void ConfigureMoveToCarryToil(Toil moveToCarry, Toil deliver)
        {
            moveToCarry.initAction = () =>
            {
                Thing thing = job.GetTarget(TargetIndex.A).Thing;
                if (thing == null || !pawn.inventory.innerContainer.Contains(thing))
                {
                    pawn.jobs.curDriver.JumpToToil(deliver);
                    return;
                }
                pawn.inventory.innerContainer.TryTransferToContainer(thing, pawn.carryTracker.innerContainer, thing.stackCount, out Thing resultingThing);
                job.SetTarget(TargetIndex.A, resultingThing);
            };
        }

        // After a successful cell or container drop: release the
        // destination reservation, drop the bookkeeping for the delivered
        // item, and go ask for the next one.
        private Toil FinishDeliveryToil(Toil deliver)
        {
            Toil toil = new Toil();
            toil.initAction = () =>
            {
                Thing delivered = job.GetTarget(TargetIndex.A).Thing;
                if (delivered != null && pawn.Map.reservationManager.ReservedBy(job.targetB, pawn, job))
                {
                    pawn.Map.reservationManager.Release(job.targetB, pawn, job);
                }
                carried.Remove(delivered);
                CargoComp?.Unregister(delivered);
                destinationsUsed++;
                pawn.jobs.curDriver.JumpToToil(deliver);
            };
            return toil;
        }
    }
}
