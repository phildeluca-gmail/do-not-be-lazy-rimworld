using System.Collections.Generic;
using System.Linq;
using DoNotBeLazy.Components;
using DoNotBeLazy.Comps;
using DoNotBeLazy.Utility;
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

        // Things left on the ground this trip because the vehicle's load
        // list no longer asks for them (section 23, item 6). Kept apart from
        // `refused` so the trip-end line does not call them overencumbering.
        private readonly HashSet<Thing> notWanted = new HashSet<Thing>();

        // Section 23, item 6. True when this trip loads a Vehicle Framework
        // vehicle's cargo hold (a PackVehicle order, VF members resolved);
        // false for upgrade materials and everything else, which keep the
        // vanilla container path. `fixedTarget` is the vehicle, kept because
        // job.targetB is rewritten when a stack goes to storage instead.
        private bool vehicleCargo;
        private LocalTargetInfo fixedTarget;
        private bool vehicleReachChecked;

        // Stacks of this trip the vehicle will not take (no longer wanted,
        // or the vehicle cannot be reached). Still in `carried`; `deliver`
        // takes them to storage instead of carrying them on (review item 4).
        private readonly HashSet<Thing> toStorage = new HashSet<Thing>();

        private int opportunisticPicked;
        private int destinationsUsed;
        private int loopIterations;

        // The trip's original target, captured once in PickUpToil - used
        // only to key the zero-progress backstop below at trip end.
        private Thing primaryTarget;

        // Logged at most once per trip - see the Reserve skip below.
        private bool transporterReserveSkipLogged;

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

        // Order 2026-10-06 (dnbl-architecture.md section 23): the hold applied
        // when the job was ended from outside its own toils is 30 seconds.
        // Kept separate so the normal trip ending stays at 60 ticks.
        private const int ExternalEndingSuppressionTicks = 1800;

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

        // Set by FinishTripAndEndJob so ReleaseAnyStuckCargo does not record
        // the same zero-progress suppression a second time.
        private bool zeroProgressRecorded;

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

        // Fixed 2026-09-24, dnbl-architecture.md section 18 (defect 2 of
        // two). Shared by all three substitution points (HaulInterceptPatch,
        // TransporterInterceptPatch, SweepManager.WrapForVehicleStuffing) for
        // the same reason CanPickUpAtLeastOne above is: TryMakePreToilReservations
        // (line ~161) only reserves job.targetA, so a stuffing job built for
        // an item another pawn already holds is guaranteed to fail its own
        // pre-toil reservation the instant it starts. Before this fix nothing
        // checked that in advance, so the job giver rebuilt the same doomed
        // stuffing job up to several times a second until the item freed up.
        // Checking first and
        // leaving vanilla's own job in place matches the CanPickUpAtLeastOne
        // guard beside it - never substitute a job we already know will fail.
        internal static bool CanReserveItem(Pawn pawn, Thing thing)
        {
            return pawn != null && thing != null && pawn.Map != null
                && pawn.Map.reservationManager.CanReserve(pawn, thing);
        }

        // Throttle for the "can't reserve, leaving vanilla's job" log line -
        // same tick-expiry shape as zeroProgressExpiry below, kept separate
        // because it answers a different question (can this pawn reserve the
        // item right now) and must not be confused with "did the last trip
        // make progress". Without this, the log line above would just
        // replace one per-rebuild spam with another.
        private static readonly Dictionary<Pawn, Dictionary<Thing, int>> reserveSkipLogExpiry
            = new Dictionary<Pawn, Dictionary<Thing, int>>();

        // Fixed 2026-09-24, dnbl-architecture.md section 18. Elliott's log
        // showed "would overencumber before even one unit" ~100 times a
        // second for the same component - CanPickUpAtLeastOne is re-checked
        // on every job-giver evaluation, not once per job start, so an
        // un-throttled line there is exactly the per-evaluation spam the
        // logging standard forbids. Own table, not reserveSkipLogExpiry
        // above - the two guards answer different questions for the same
        // pawn/target pair and must not suppress each other's first line.
        private const int SkipLogThrottleTicks = 250;

        internal static void LogReserveSkipThrottled(Pawn pawn, Thing thing, string message)
        {
            LogSkipThrottled(reserveSkipLogExpiry, pawn, thing, message);
        }

        // Shared by all three substitution points (HaulInterceptPatch,
        // TransporterInterceptPatch, SweepManager.WrapForVehicleStuffing) -
        // same reason CanReserveItem's log throttle is shared.
        // Once per pawn per ThingDef per game hour. Keyed by def, not by Thing:
        // every stack of Food is a different Thing, so the old per-Thing key
        // logged again for each stack the pawn was offered (10-14 repeats of
        // identical numbers, 2026-10-12).
        internal static void LogOverencumberSkipThrottled(Pawn pawn, Thing thing, string message)
        {
            if (pawn == null || thing == null || Find.TickManager == null)
            {
                return;
            }
            if (!overencumberDefLogExpiry.TryGetValue(pawn, out Dictionary<ThingDef, int> byDef))
            {
                byDef = new Dictionary<ThingDef, int>();
                overencumberDefLogExpiry[pawn] = byDef;
            }
            if (byDef.TryGetValue(thing.def, out int expiryTick) && Find.TickManager.TicksGame < expiryTick)
            {
                return;
            }
            byDef[thing.def] = Find.TickManager.TicksGame + GenDate.TicksPerHour;
            Logger.Message(message + OverencumberNumbers(pawn, thing));
        }

        private static readonly Dictionary<Pawn, Dictionary<ThingDef, int>> overencumberDefLogExpiry
            = new Dictionary<Pawn, Dictionary<ThingDef, int>>();

        // 2026-10-12: stuff-first hauling is for pawns who can fill an
        // inventory pack. Verified by reflection on lib\Assembly-CSharp.dll:
        // MassUtility.Capacity is 0 unless RaceProps.ToolUser or packAnimal,
        // and animals (husky, megasloth) logged capacity 0.0 hundreds of
        // times. Humanlike is the primary test (animals never stuff a pack,
        // even pack animals whose capacity is non-zero); Capacity <= 0 also
        // catches a humanlike with no carrying ability. Callers return before
        // any capacity check, so vanilla's own job is untouched.
        private static readonly HashSet<Pawn> ignoredPawnsLogged = new HashSet<Pawn>();

        // Called from the ItemLedger constructor, which RimWorld runs on every
        // game create or load: pawns of the previous game must not stay as keys,
        // and the once-per-session ignore line is per loaded game.
        internal static void ResetLogThrottles()
        {
            ignoredPawnsLogged.Clear();
            overencumberDefLogExpiry.Clear();
        }

        internal static bool CanStuffPack(Pawn pawn)
        {
            if (pawn == null || pawn.RaceProps == null)
            {
                return false;
            }
            if (pawn.RaceProps.Humanlike && MassUtility.Capacity(pawn) > 0f)
            {
                return true;
            }
            if (ignoredPawnsLogged.Add(pawn))
            {
                Logger.Message($"{pawn.LabelShort}: stuff-first hauling ignores this pawn - " +
                    (pawn.RaceProps.Humanlike ? "carrying capacity is 0" : "not humanlike, animals cannot stuff a pack")
                    + "; vanilla's own haul job is left in place");
            }
            return false;
        }

        // 2026-10-11 (Bruise): the refusal line gave no numbers, so a pawn
        // the player saw at ~65 % could not be told apart from one truly full.
        // These are MassUtility's own figures - the same ones the check uses.
        private static string OverencumberNumbers(Pawn pawn, Thing thing)
        {
            try
            {
                return $" [capacity {MassUtility.Capacity(pawn):F1} kg, gear {MassUtility.GearMass(pawn):F1}, inventory {MassUtility.InventoryMass(pawn):F1}, free {MassUtility.FreeSpace(pawn):F1}, one unit {thing.GetStatValue(StatDefOf.Mass):F3} kg]";
            }
            catch (System.Exception)
            {
                return "";
            }
        }

        private static void LogSkipThrottled(Dictionary<Pawn, Dictionary<Thing, int>> expiryTable, Pawn pawn, Thing thing, string message)
        {
            if (pawn == null || thing == null)
            {
                return;
            }
            if (!expiryTable.TryGetValue(pawn, out Dictionary<Thing, int> byTarget))
            {
                byTarget = new Dictionary<Thing, int>();
                expiryTable[pawn] = byTarget;
            }
            if (byTarget.TryGetValue(thing, out int expiryTick) && Find.TickManager.TicksGame < expiryTick)
            {
                return;
            }
            byTarget[thing] = Find.TickManager.TicksGame + SkipLogThrottleTicks;
            Logger.Message(message);
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

        private static void RecordZeroProgress(Pawn pawn, Thing target, int ticks = ZeroProgressSuppressionTicks)
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

            byTarget[target] = Find.TickManager.TicksGame + ticks;
        }

        // 2026-10-06, dnbl-architecture.md section 23 (order 4.1). How full a
        // pawn is with things that are NOT DNBL cargo: gear plus inventory
        // mass, minus the mass of whatever CompDnblCargo has registered and
        // is still in the inventory, over carrying capacity. A pawn whose
        // own items fill the pack is refused every stuffing trip, so the
        // sweep drops it from a vehicle order instead of handing it the
        // same doomed job again. `heaviest` names the three heaviest
        // non-cargo inventory stacks for the log line.
        internal static float NonCargoEncumbrance(Pawn pawn, out string heaviest)
        {
            heaviest = "none";
            if (pawn?.inventory == null)
            {
                return 0f;
            }

            float capacity = MassUtility.Capacity(pawn);
            if (capacity <= 0f)
            {
                return 0f;
            }

            CompDnblCargo cargoComp = pawn.GetComp<CompDnblCargo>();
            float cargoMass = 0f;
            var stacks = new List<Thing>();
            foreach (Thing held in pawn.inventory.innerContainer)
            {
                if (cargoComp != null && cargoComp.IsCarrying(held))
                {
                    cargoMass += held.GetStatValue(StatDefOf.Mass) * held.stackCount;
                }
                else
                {
                    stacks.Add(held);
                }
            }

            heaviest = DescribeHeaviest(stacks);
            return (MassUtility.GearAndInventoryMass(pawn) - cargoMass) / capacity;
        }

        // Top three stacks by total mass, "Devilstrand x60 (9.0 kg), ...".
        internal static string DescribeHeaviest(List<Thing> stacks)
        {
            if (stacks == null || stacks.Count == 0)
            {
                return "none";
            }

            return string.Join(", ", stacks
                .OrderByDescending(t => t.GetStatValue(StatDefOf.Mass) * t.stackCount)
                .Take(3)
                .Select(t => $"{t.LabelNoCount} x{t.stackCount} ({t.GetStatValue(StatDefOf.Mass) * t.stackCount:F1} kg)"));
        }

        internal static string DescribeInventoryHeaviest(Pawn pawn)
        {
            return pawn?.inventory == null ? "none" : DescribeHeaviest(pawn.inventory.innerContainer.ToList());
        }

        private CompDnblCargo CargoComp => pawn.GetComp<CompDnblCargo>();

        // "Deliver what is already in my inventory" mode, added 2026-10-02
        // (change A): a stuffing job whose targetA is a Thing the pawn is
        // already holding in its inventory, in storage mode. The sweep
        // starts one for a passed-over pawn that is still loaded with DNBL
        // cargo (SweepManager.TryStartCargoDelivery). Uses the same deliver
        // toils as a normal trip; skips the walk to and pickup of a target,
        // and the opportunistic gather.
        internal static bool IsCargoDeliveryJob(Pawn pawn, Job job)
        {
            Thing held = job?.targetA.Thing;
            return job != null && job.haulMode == HaulMode.ToCellStorage && held != null
                && pawn?.inventory != null && pawn.inventory.innerContainer.Contains(held);
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // targetA is in the pawn's own inventory - nothing to reserve.
            if (IsCargoDeliveryJob(pawn, job))
            {
                Logger.Message($"{pawn.LabelShort}: unloading stuffed cargo to storage, starting with {job.targetA.Thing.LabelCap}");
                return true;
            }

            bool reserved = pawn.Reserve(job.targetA, job, errorOnFailed: errorOnFailed);

            // The one line for this event, fixed 2026-09-27 (dnbl-architecture.md
            // section 18): every substitution point (HaulInterceptPatch,
            // TransporterInterceptPatch, SweepManager.WrapForVehicleStuffing)
            // used to log "stuffing job created..." itself, once per WorkGiver
            // candidate evaluation rather than once per job actually started -
            // Pelican logged ~70 distinct items in under 2 seconds. This runs
            // once, here, when the job starts for real. Storage mode
            // (haulMode == ToCellStorage) has no fixed destination yet - each
            // item's destination is looked up fresh at delivery time - so only
            // the fixed-destination mode names one.
            if (reserved)
            {
                string destination = job.haulMode == HaulMode.ToContainer && job.targetB.Thing != null
                    ? job.targetB.Thing.LabelShort
                    : "storage (destination chosen per item)";
                // Encumbrance and the three heaviest stacks added 2026-10-06
                // (order 4.4). One line per job start, so it is bounded by
                // how often a job is actually handed out.
                Logger.Message($"{pawn.LabelShort}: stuffing job started for {job.targetA.Thing?.LabelCap}, destination {destination} (encumbrance {MassUtility.EncumbrancePercent(pawn):P0}, heaviest in inventory: {DescribeInventoryHeaviest(pawn)})");
            }

            return reserved;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            bool fixedDestination = job.haulMode == HaulMode.ToContainer;
            bool cargoOnly = IsCargoDeliveryJob(pawn, job);
            fixedTarget = job.targetB;
            vehicleCargo = fixedDestination && VehicleCompat.IsCargoLoadingWork(job.workGiverDef)
                && VehicleCompat.IsVehicle(job.targetB.Thing);

            // Stuck-cargo fix, dnbl-architecture.md section 19 (the
            // proposed fix recorded under section 18, "Open, not fixed
            // here - Elliott's inventory looked stuck at 100% full"),
            // ordered 2026-09-28 ("2 build all"). Every normal ending
            // (FinishDeliveryToil, DropAndSkip, EndTripAfterReservationFailure)
            // already drops and unregisters what it touches, so by the
            // time any of them calls EndJobWith, `carried` is already
            // empty. This is for every OTHER way the job can end - an
            // interrupt, an exception, a replacement job, anything
            // external - none of which run any toil in this driver.
            // AddFinishAction is the one hook Verse.AI.JobDriver calls on
            // every ending, success included, regardless of path
            // (verified public instance method on JobDriver against
            // lib\Assembly-CSharp.dll). Whatever is still in `carried`
            // when it fires is dropped near the pawn and unregistered from
            // CompDnblCargo here, so CargoDropGuardPatch stops blocking
            // vanilla's own JobGiver_DropUnusedInventory from it
            // afterward - Elliott's inventory sat at 100% full for the
            // rest of a session because nothing ever did this.
            AddFinishAction(ReleaseAnyStuckCargo);

            if (cargoOnly)
            {
                // Everything DNBL registered as cargo and still in the
                // inventory; the pawn's own medicine, food, drugs and gear
                // are never in this set.
                CompDnblCargo cargoComp = CargoComp;
                if (cargoComp != null)
                {
                    foreach (Thing held in cargoComp.Carrying())
                    {
                        if (pawn.inventory.innerContainer.Contains(held) && !carried.Contains(held))
                        {
                            ItemLedger.Ensure(pawn, held);
                            carried.Add(held);
                        }
                    }
                }
            }
            else
            {
                yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch)
                    .FailOnDespawnedNullOrForbidden(TargetIndex.A);
                yield return PickUpToil(isPrimary: true);
            }

            // Pre-declared so every toil below can jump to one another
            // regardless of yield order - the same forward/backward jump
            // pattern vanilla's own JobDriver_HaulToCell and PUAH's drivers
            // use.
            Toil deliver = new Toil();
            Toil moveToCarry = new Toil();
            Toil carryToCell = Toils_Haul.CarryHauledThingToCell(TargetIndex.B);
            Toil carryToContainer = Toils_Haul.CarryHauledThingToContainer();
            GuardCarriedThing(carryToCell);
            GuardCarriedThing(carryToContainer);
            Toil finishCell = FinishDeliveryToil(deliver);
            Toil finishContainer = FinishDeliveryToil(deliver);

            // Section 23, item 6: a Vehicle Framework vehicle is delivered to
            // straight from the inventory through VF's own call, not through
            // the carry tracker and vanilla's container toils, which it
            // refuses. Reached only by the jump in `deliver`.
            Toil gotoVehicle = Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.Touch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.B);

            ConfigureDeliverToil(deliver, moveToCarry, gotoVehicle, fixedDestination);
            ConfigureMoveToCarryToil(moveToCarry, deliver);

            if (cargoOnly)
            {
                // nothing to gather - straight to the deliver toils below
            }
            else if (fixedDestination)
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
                    if (refused.Contains(foundThing) || notWanted.Contains(foundThing))
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

                    // Fixed 2026-09-27, dnbl-architecture.md section 18 -
                    // same reservation gap as the storage-mode gather loop
                    // above, here for fixed-destination mode (vehicle
                    // packing, transport pods). The scanner's own
                    // JobOnThing never reserves foundThing for this pawn -
                    // its returned Job is discarded, never started, so its
                    // TryMakePreToilReservations never runs - so nothing
                    // held this item between being offered here and being
                    // picked up. Reserve it now; released in PickUpToil
                    // right after pickup. A failure is treated like an
                    // overencumber refusal: excluded from being re-offered,
                    // and the gather retries rather than ends, since the
                    // scanner may still have something else to offer.
                    if (!pawn.Map.reservationManager.Reserve(pawn, job, foundThing, errorOnFailed: false))
                    {
                        if (refused.Add(foundThing))
                        {
                            Logger.Message($"{pawn.LabelShort}: {foundThing.LabelCap} is already reserved, leaving it");
                        }
                        pawn.jobs.curDriver.JumpToToil(loopStart);
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
            // Never fallen into: finishContainer always jumps to `deliver`.
            // The 25-tick wait matches VF's own load job.
            yield return gotoVehicle;
            yield return Toils_General.Wait(25).WithProgressBarToilDelay(TargetIndex.B)
                .FailOnDespawnedNullOrForbidden(TargetIndex.B);
            yield return DeliverToVehicleToil(deliver);
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

                // Section 23, item 6: never gather more for a vehicle than its
                // load list still asks for, counting what this trip already
                // holds for the same entry. Vehicle Framework's own count
                // (job.count) leaves this pawn's stuffed stacks out.
                if (take > 0 && vehicleCargo)
                {
                    int stillWanted = VehicleCompat.StillWanted(job.targetB.Thing, thing, carried);
                    if (stillWanted >= 0 && stillWanted < take)
                    {
                        if (stillWanted <= 0)
                        {
                            if (notWanted.Add(thing))
                            {
                                Logger.Message($"{pawn.LabelShort}: {job.targetB.Thing.LabelShort} wants no more {thing.def.label} beyond what this trip carries, leaving {thing.LabelCap}");
                            }
                            if (pawn.Map.reservationManager.ReservedBy(thing, pawn, job))
                            {
                                pawn.Map.reservationManager.Release(thing, pawn, job);
                            }
                            return;
                        }
                        Logger.Message($"{pawn.LabelShort}: {job.targetB.Thing.LabelShort} still wants {stillWanted} {thing.def.label}, taking {stillWanted} instead of {take}");
                        take = stillWanted;
                    }
                }

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
                    // Same release as below - an opportunistic item reserved
                    // in GatherStorageCandidatesToil or the askScanner loop
                    // that turns out refused here must not stay reserved by
                    // this pawn for the rest of the trip; the job-end
                    // cleanup would release it eventually, but there is no
                    // reason to make some other pawn wait that long.
                    if (pawn.Map.reservationManager.ReservedBy(thing, pawn, job))
                    {
                        pawn.Map.reservationManager.Release(thing, pawn, job);
                    }
                    return;
                }

                Thing picked = thing.SplitOff(take);
                // No merge, 2026-10-09 (section 23, item 6): merging into a
                // stack already in the pack destroyed `picked`, which `carried`
                // and the cargo registry still pointed at, so the trip
                // delivered nothing. The pawn's own stacks also stay separate
                // from cargo this way.
                pawn.inventory.GetDirectlyHeldThings().TryAdd(picked, false);
                CargoComp?.Register(picked);
                carried.Add(picked);
                ItemLedger.Added(pawn, picked, SweepManager.CurrentOrderIdOrNull(pawn));
                if (!isPrimary)
                {
                    opportunisticPicked++;
                }

                // Fixed 2026-09-27, dnbl-architecture.md section 18.
                // Release whatever reservation covered this item - the
                // primary's own pre-toil reservation (TryMakePreToilReservations
                // above, on job.targetA before any toil ran), or the one
                // just taken on it in GatherStorageCandidatesToil or the
                // fixed-destination askScanner loop for an opportunistic
                // item. The item is off the map now (or reduced to
                // whatever's left of its stack); holding the reservation
                // any longer only blocks other pawns from the remainder for
                // no reason. Mirrors vanilla's own Toils_Haul.StartCarryThing,
                // which does the identical ReservedBy-guarded Release after
                // a partial pickup (verified by ildasm against
                // lib\Assembly-CSharp.dll).
                if (pawn.Map.reservationManager.ReservedBy(thing, pawn, job))
                {
                    pawn.Map.reservationManager.Release(thing, pawn, job);
                }

                Logger.Message($"{pawn.LabelShort} stuffed {picked.LabelCap} into inventory ({MassUtility.EncumbrancePercent(pawn):P0} full)");
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
                // Held in locals, fixed 2026-10-02 - Bag, 18:58:20, a
                // NullReferenceException from this toil's initAction at the
                // reserve-then-add step (the installed build already had the
                // 2026-09-28 queue guard, so the fault was a different
                // dereference there: job, job.targetQueueA or pawn.Map
                // going null after ReservationManager.Reserve, which can
                // reenter vanilla's job machinery for a player-forced job).
                // Which of the three could not be told from the log. None is
                // re-read after Reserve now; the pawn's spawned state is
                // checked each pass instead.
                Job thisJob = job;
                Map map = pawn.Map;
                var queue = new List<LocalTargetInfo>();
                var counts = new List<int>();
                job.targetQueueA = queue;
                job.countQueue = counts;

                if (opportunisticPicked >= MaxOpportunisticItems || MassUtility.EncumbrancePercent(pawn) >= 1f)
                {
                    return;
                }

                IntVec3 origin = pawn.Position;
                var candidates = new List<Thing>(map.listerHaulables.ThingsPotentiallyNeedingHauling());
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
                    if (candidate == null || !pawn.Spawned || pawn.Map != map || thisJob == null)
                    {
                        Logger.Message($"{pawn.LabelShort}: stuffing gather stopped - the pawn or its job is no longer in place (pawn spawned {pawn.Spawned}, job {(thisJob == null ? "null" : "set")}), ending gather with what is already carried");
                        return;
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

                    // Fixed 2026-09-27, dnbl-architecture.md section 18 -
                    // "X tried to start carry Y which isn't spawned" seen in
                    // Player.log for steel, corn, meat, cloth, silver and
                    // hay. PawnCanAutomaticallyHaulFast above only calls
                    // ReservationManager.CanReserve (verified by ildasm
                    // against lib\Assembly-CSharp.dll,
                    // Verse.AI.HaulAIUtility line 81) - a check, not a
                    // claim. Nothing held this candidate between this scan
                    // and its turn in the queue, so a vanilla hauler could
                    // reserve and start walking to the same item, and this
                    // pawn would still take it first when its turn came,
                    // despawning it out from under the other pawn's already
                    // running StartCarryThing toil
                    // (Verse.AI.Toils_Haul.ErrorCheckForCarry, verified by
                    // ildasm as the exact source of that log line). Reserve
                    // it now for the rest of this trip; released in
                    // PickUpToil right after the item is actually picked
                    // up. A failure here means another pawn won the race in
                    // the instant since the check above - skip the
                    // candidate, no error (errorOnFailed: false - CanReserve
                    // already filtered the ordinary case just above).
                    if (!map.reservationManager.Reserve(pawn, thisJob, candidate, errorOnFailed: false))
                    {
                        continue;
                    }

                    // Fixed 2026-09-28, dnbl-architecture.md section 18 -
                    // Clever's NullReferenceException. job.targetQueueA was
                    // set fresh at the top of this same initAction (above)
                    // but was found null again here, mid-loop - something
                    // else reset this Job's queues while this toil was
                    // still running (a concurrent job hand-out is the
                    // suspect; not confirmed). Rather than crash the toil,
                    // log and stop gathering - the primary item is already
                    // in `carried`/inventory, so the trip still delivers it;
                    // the queue-empty toils after this one already treat a
                    // null queue as empty via NullOrEmpty().
                    if (thisJob.targetQueueA == null || thisJob.countQueue == null)
                    {
                        Logger.Message($"{pawn.LabelShort}: stuffing job's target queue vanished mid-gather - job was likely replaced concurrently, ending gather with what is already carried");
                        return;
                    }

                    queue.Add(candidate);
                    counts.Add(candidate.stackCount);
                    added++;
                }
            };
            return toil;
        }

        // Picks the next carried item and its destination (fixed, for
        // vehicle packing, or recomputed per item via StoreUtility for
        // storage mode), reserves it, and jumps into the carry-and-deliver
        // toils. Ends the job once nothing is left to deliver.
        private void ConfigureDeliverToil(Toil deliver, Toil moveToCarry, Toil gotoVehicle, bool fixedDestination)
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

                bool toFixed = fixedDestination && !toStorage.Contains(next);
                if (toFixed)
                {
                    dest = fixedTarget;

                    // Section 23, item 6. No reservation (VF's own load job
                    // reserves only the item, so several pawns deliver at
                    // once) and no vanilla owner probe. Reach and forbidden
                    // are checked once per trip, not per stack.
                    if (vehicleCargo)
                    {
                        if (!vehicleReachChecked)
                        {
                            vehicleReachChecked = true;
                            bool reachable = pawn.CanReach(dest, PathEndMode.Touch, Danger.Deadly);
                            bool forbidden = dest.Thing.IsForbidden(pawn);
                            if (!reachable || forbidden)
                            {
                                Logger.Message($"{pawn.LabelShort}: delivery to {dest.Thing.LabelShort} will fail - reachable: {reachable}, forbidden: {forbidden}; taking {carried.Count} stack(s) to storage instead");
                                toStorage.UnionWith(carried);
                                pawn.jobs.curDriver.JumpToToil(deliver);
                                return;
                            }
                        }

                        string why = VehicleCompat.WhyNotWanted(dest.Thing, next);
                        if (why != null)
                        {
                            SendToStorage(next, $"{dest.Thing.LabelShort} will not take it: {why}");
                            pawn.jobs.curDriver.JumpToToil(deliver);
                            return;
                        }
                        job.SetTarget(TargetIndex.A, next);
                        job.SetTarget(TargetIndex.B, dest);
                        pawn.jobs.curDriver.JumpToToil(gotoVehicle);
                        return;
                    }
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

                // Fixed 2026-09-24, dnbl-architecture.md section 18 (defect 1
                // of two). Vanilla's own RimWorld.JobDriver_HaulToTransporter
                // never reserves the transporter/pod itself - verified by
                // ildasm against lib\Assembly-CSharp.dll: its
                // TryMakePreToilReservations only calls
                // ReservationUtility.ReserveAsManyAsPossible on the item
                // queues, and Notify_Starting only reserves the item
                // (job.targetA). Before this fix, this toil reserved `dest`
                // with the ordinary single-claimant reservation regardless of
                // what it was, so the first colonist to reach a transport pod
                // locked it and every other colonist loading the same pod
                // failed this Reserve call, dropped their cargo and ended
                // their trip. Skipping the reservation for a transporter
                // destination only - never for a vehicle or any other
                // destination, whose concurrency is unverified - matches
                // vanilla exactly. FinishDeliveryToil below already guards
                // its Release with ReservedBy, so nothing there needed to
                // change.
                bool isTransporterDestination = toFixed && dest.Thing != null
                    && dest.Thing.TryGetComp<CompTransporter>() != null;

                if (isTransporterDestination)
                {
                    if (!transporterReserveSkipLogged)
                    {
                        transporterReserveSkipLogged = true;
                        Logger.Message($"{pawn.LabelShort}: not reserving {dest.Thing.LabelShort} - matches vanilla transport pod loading, which reserves only the item");
                    }
                }
                else if (!pawn.Map.reservationManager.Reserve(pawn, job, dest))
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

                // 2026-10-06 (dnbl-architecture.md section 23, 2.3 follow-up):
                // every fixed-destination job to the vehicle "Resupplier"
                // ended Incompletable after the item reached the carry
                // tracker, 286 jobs and no delivery. Vanilla's carry-to-
                // container toil fails the job when the destination's
                // ThingOwner will not accept the item, or the pather cannot
                // reach it. Say which, once per delivery attempt, only when
                // one of the two is true.
                if (toFixed && dest.Thing != null)
                {
                    ThingOwner destOwner = dest.Thing.TryGetInnerInteractableThingOwner();
                    bool accepts = destOwner != null && destOwner.CanAcceptAnyOf(next);
                    bool reachable = pawn.CanReach(dest, PathEndMode.Touch, Danger.Deadly);
                    if (!accepts || !reachable)
                    {
                        Logger.Message($"{pawn.LabelShort}: delivery to {dest.Thing.LabelShort} will fail - owner {(destOwner == null ? "none" : destOwner.GetType().Name)} accepts {next.LabelCap}: {accepts}, reachable: {reachable}, forbidden: {dest.Thing.IsForbidden(pawn)}");
                    }
                }

                job.SetTarget(TargetIndex.A, next);
                job.SetTarget(TargetIndex.B, dest);
                pawn.jobs.curDriver.JumpToToil(moveToCarry);
            };
        }

        private void DropAndSkip(Thing thing, string reason)
        {
            LeftoverCargo.NoteForcedDrop(pawn, job, thing, reason);
            pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, out _);
            ItemLedger.Dropped(pawn, thing, reason, true);
            CargoComp?.Unregister(thing);
            carried.Remove(thing);
            Logger.Message($"{pawn.LabelShort}: {reason} - dropped {thing.LabelCap} near itself");
        }

        // Section 23, item 6. A stack the vehicle will not take (no longer
        // wanted, or none of it went in) stays in `carried` but is marked
        // for storage: `deliver` then finds it a storage destination like a
        // storage-mode trip, so the pawn does not carry it on into the next
        // trip. With no storage it is dropped near the pawn (DropAndSkip).
        private void SendToStorage(Thing thing, string reason)
        {
            toStorage.Add(thing);
            Logger.Message($"{pawn.LabelShort}: not delivering {thing.LabelCap} to the vehicle - {reason}; taking it to storage");
        }

        // Section 23, item 6. Standing at the vehicle: hands the stack over
        // through VehicleCompat.DeliverToVehicle (VF's own AddOrTransfer and
        // load-list bookkeeping), then the same bookkeeping as
        // FinishDeliveryToil, and back to `deliver` for the next stack.
        private Toil DeliverToVehicleToil(Toil deliver)
        {
            Toil toil = new Toil();
            toil.initAction = () =>
            {
                Thing item = job.GetTarget(TargetIndex.A).Thing;
                Thing vehicle = job.GetTarget(TargetIndex.B).Thing;
                if (item == null || item.Destroyed || !pawn.inventory.innerContainer.Contains(item))
                {
                    // `deliver` prunes it from `carried`; nothing is held to keep.
                    CargoComp?.Unregister(item);
                    pawn.jobs.curDriver.JumpToToil(deliver);
                    return;
                }

                string vehicleLabel = vehicle?.LabelShort ?? "the vehicle";
                int asked = item.stackCount;
                int added = VehicleCompat.DeliverToVehicle(vehicle, item, out int pendingLeft, out string reason);
                if (added <= 0)
                {
                    SendToStorage(item, $"{vehicleLabel} took none: {reason}");
                    pawn.jobs.curDriver.JumpToToil(deliver);
                    return;
                }

                destinationsUsed++;
                ItemLedger.Delivered(pawn, item, added, vehicleLabel + " (vehicle cargo)");
                Logger.Message($"{pawn.LabelShort}: delivered {item.def.label} x{added} to {vehicleLabel}, {pendingLeft} still pending for it");
                if (pawn.inventory.innerContainer.Contains(item))
                {
                    // The rest stays in `carried`. If another entry (or the
                    // same one) still wants it, `deliver` offers it again
                    // straight away; if not, `deliver` sends it to storage.
                    Logger.Message($"{pawn.LabelShort}: {vehicleLabel} took {added} of {asked} {item.def.label}, {item.stackCount} left in the pack");
                }
                else
                {
                    carried.Remove(item);
                    CargoComp?.Unregister(item);
                }
                pawn.jobs.curDriver.JumpToToil(deliver);
            };
            return toil;
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
                    LeftoverCargo.NoteForcedDrop(pawn, job, thing, "no destination could be reserved");
                    pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, out _);
                    ItemLedger.Dropped(pawn, thing, "no destination could be reserved", true);
                    CargoComp?.Unregister(thing);
                }
                carried.Clear();
                Logger.Message($"{pawn.LabelShort}: could not reserve a destination - dropped {remaining} carried item(s) and ending stuffing trip");
            }
            FinishTripAndEndJob(JobCondition.Incompletable);
        }

        // Stuck-cargo fix, dnbl-architecture.md section 19 - the
        // AddFinishAction registered in MakeNewToils. Fires once, on every
        // way this job can end, after any of this driver's own toils have
        // already had their chance to drain `carried`. A no-op on the
        // normal endings, where it is already empty by now; the one path
        // this exists for is everything else (an interrupt, an exception,
        // a replacement job) that ends the job without running any of
        // them.
        private void ReleaseAnyStuckCargo(JobCondition condition)
        {
            // Order 4.2, 2026-10-06 (dnbl-architecture.md section 23): an
            // ending that did not go through FinishTripAndEndJob (an
            // Incompletable from outside the toils) used to record no
            // zero-progress suppression, so the same pawn was handed the same
            // job straight away. Same table, same duration.
            string suppressNote = "";
            // Only a failure ending counts: a player order, a draft or a need
            // interrupt is not the job failing, and must not hold back the
            // same order for 30 seconds.
            bool failed = condition == JobCondition.Incompletable || condition == JobCondition.Errored;
            if (failed && !zeroProgressRecorded && destinationsUsed == 0 && primaryTarget != null
                && !IsCargoDeliveryJob(pawn, job))
            {
                RecordZeroProgress(pawn, primaryTarget, ExternalEndingSuppressionTicks);
                zeroProgressRecorded = true;
                suppressNote = $"; delivered nothing - suppressing a same-target stuffing job for {ExternalEndingSuppressionTicks} ticks";
            }

            if (carried.Count == 0)
            {
                if (suppressNote.Length > 0)
                {
                    Logger.Message($"{pawn.LabelShort}: stuffing job ended ({condition}) with nothing carried{suppressNote}");
                }
                // Items of this pawn in the ledger can outlive `carried`
                // (replaced job, thrown-away job): say so on every ending.
                ItemLedger.JobEnded(pawn, job?.loadID ?? 0, condition.ToString());
                return;
            }

            List<Thing> stuck = new List<Thing>(carried);
            string names = string.Join(", ", stuck.Select(t => t.LabelCap));

            int toilIndex = CurToilIndex;
            string toil = HaveCurToil ? CurToilString : "none";

            // Fixed 2026-10-09 (section 23, item 6): drop first, unregister
            // only what was dropped or is no longer held. This used to
            // unregister first and drop only from the inventory, so a stack
            // in the carry tracker, or any stack the pawn could not drop,
            // stayed in the pawn unregistered, where nothing would ever take
            // it out again (~12 pawns kept 30-75 wood). A stack still held
            // stays registered for SweepManager.DropRegisteredCargoOnLeaving.
            int dropped = 0;
            int kept = 0;
            foreach (Thing thing in stuck)
            {
                int stackBeforeDrop = thing.stackCount;
                bool inInventory = pawn.inventory != null && pawn.inventory.innerContainer.Contains(thing);
                bool inHands = pawn.carryTracker != null && pawn.carryTracker.innerContainer.Contains(thing);
                if (inInventory || inHands)
                {
                    if (pawn.Spawned && pawn.Map != null)
                    {
                        if (inInventory)
                        {
                            pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, out _);
                        }
                        else
                        {
                            pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                        }
                    }
                    if ((pawn.inventory != null && pawn.inventory.innerContainer.Contains(thing))
                        || (pawn.carryTracker != null && pawn.carryTracker.innerContainer.Contains(thing)))
                    {
                        kept++;
                        continue;
                    }
                    dropped++;
                    // only a drop that really happened is batched; count taken before the drop, a merge empties the stack
                    LeftoverCargo.NoteForcedDrop(pawn, job, thing, "job ended: " + condition, stackBeforeDrop);
                    ItemLedger.Dropped(pawn, thing, $"stuffing job ended ({condition})", true);
                }
                CargoComp?.Unregister(thing);
            }
            carried.Clear();
            ItemLedger.JobEnded(pawn, job?.loadID ?? 0, condition.ToString());

            // Order 4.4, corrected 2026-10-11: where each item is NOW, read
            // after the drops. It used to be read before them, so a stack the
            // job then dropped was still reported "in inventory" (Pavel's bow).
            // A dropped stack that merged into another on the ground is no
            // longer this exact Thing, so it shows as "gone (merged or moved)".
            string where = string.Join(", ", stuck.Select(t =>
            {
                if (pawn.inventory != null && pawn.inventory.innerContainer.Contains(t))
                {
                    return t.LabelCap + " in inventory";
                }
                if (pawn.carryTracker != null && pawn.carryTracker.innerContainer.Contains(t))
                {
                    return t.LabelCap + " in carry tracker";
                }
                return t.Spawned ? t.LabelCap + " on the ground at " + t.Position : t.LabelCap + " gone (merged or moved)";
            }));

            string keptNote = kept == 0 ? "" : $", {kept} could not be dropped ({(pawn.Spawned ? "drop failed" : "pawn not spawned")}) and stay registered as DNBL cargo";
            Logger.Message($"{pawn.LabelShort}: stuffing job ended ({condition}) in toil {toilIndex} ({toil}) with {names} still undelivered - dropped {dropped} near itself{keptNote}; item locations: {where}{suppressNote}");
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
                zeroProgressRecorded = true;
                suppressNote = $" - suppressing a same-target stuffing job for {ZeroProgressSuppressionTicks} ticks";
            }

            // Refusal summary belongs on this line unconditionally, not
            // only when the trip comes back empty - the same trap named in
            // CLAUDE.md section 4 (2026-09-20): a count computed on every
            // run but logged only on failure records nothing about a
            // successful trip.
            Logger.Message($"{pawn.LabelShort}: stuffing trip ended - {destinationsUsed} item(s) delivered, {opportunisticPicked} picked up opportunistically, {refused.Count} refused as overencumbering{(notWanted.Count > 0 ? $", {notWanted.Count} left because the vehicle wanted no more" : "")}{suppressNote}");
            EndJobWith(condition);
        }

        // 2026-10-11 (dnbl-architecture.md section 23 follow-up): vanilla's
        // CarryHauledThingToCell fail condition passes
        // carryTracker.CarriedThing straight to IsValidStorageFor, which
        // throws NullReferenceException when the hands are empty (seen live:
        // a minified Steel mortar left the hands while Adorbs walked to the
        // cell, the exception was logged by RimWorld and the job ended
        // Errored). The container toil has the same shape. This end
        // condition is inserted at index 0 so it is evaluated before
        // vanilla's own: with nothing in the hands the job ends
        // Incompletable with a logged reason, and vanilla's condition is
        // never reached. Fires once, because it ends the job.
        private void GuardCarriedThing(Toil toil)
        {
            toil.endConditions.Insert(0, () =>
            {
                Pawn actor = toil.actor;
                if (actor == null || actor.carryTracker == null || actor.carryTracker.CarriedThing != null)
                {
                    return JobCondition.Ongoing;
                }
                Thing target = actor.jobs?.curJob?.GetTarget(TargetIndex.A).Thing;
                string where = target == null ? "no target"
                    : target.Destroyed ? "destroyed"
                    : target.Spawned ? $"spawned at {target.Position}"
                    : actor.inventory != null && actor.inventory.innerContainer.Contains(target) ? "in the inventory"
                    : $"held by {(target.ParentHolder == null ? "nothing" : target.ParentHolder.GetType().Name)}";
                ItemLedger.NoteSeen(target, "hands-empty guard: " + where);
                Logger.Message($"{actor.LabelShort}: hands are empty while carrying to the destination - {(target == null ? "the item" : target.LabelCap)} is {where}; ending the stuffing trip");
                return JobCondition.Incompletable;
            });
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
                ItemLedger.Ensure(pawn, thing);
                // Review fix, 2026-10-09 (section 23, item 6): hands must be
                // empty first. Whatever blocks them is put away - our own
                // cargo back into the pack (no merge, still registered),
                // anything else dropped, as vanilla drops a carried thing
                // between jobs - so the stack below never merges into the
                // pawn's own carried material and is never the thing dropped.
                Thing blocking = pawn.carryTracker.CarriedThing;
                if (blocking != null)
                {
                    bool ours = CargoComp != null && CargoComp.IsCarrying(blocking);
                    if (ours)
                    {
                        pawn.carryTracker.innerContainer.TryTransferToContainer(blocking, pawn.inventory.innerContainer, blocking.stackCount, false);
                    }
                    if (pawn.carryTracker.innerContainer.Contains(blocking))
                    {
                        pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                        if (ours && !pawn.carryTracker.innerContainer.Contains(blocking))
                        {
                            ItemLedger.Dropped(pawn, blocking, "hands had to be cleared", true);
                            CargoComp.Unregister(blocking);
                            carried.Remove(blocking);
                        }
                    }
                    else if (ours && !carried.Contains(blocking))
                    {
                        carried.Add(blocking);
                    }
                    Logger.Message($"{pawn.LabelShort}: hands held {blocking.LabelCap} ({(ours ? "DNBL cargo" : "not cargo")}) before lifting {thing.LabelCap} - {(pawn.inventory.innerContainer.Contains(blocking) ? "put back in the pack" : pawn.carryTracker.innerContainer.Contains(blocking) ? "could not be put down" : "dropped near itself")}");
                }

                pawn.inventory.innerContainer.TryTransferToContainer(thing, pawn.carryTracker.innerContainer, thing.stackCount, out _);

                // The transfer's own result is not trusted: a merge returns a
                // destroyed Thing. What matters is what is now in the hands.
                // Hands were empty, so that is only ever this stack, or part
                // of it when the stack exceeded what the hands can hold.
                Thing inHands = pawn.carryTracker.CarriedThing;
                if (inHands == null)
                {
                    // Nothing moved (hands still blocked, or refused).
                    // Dropped like any other stack this driver cannot
                    // deliver; kept registered only if even the drop fails.
                    pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, out _);
                    ItemLedger.Dropped(pawn, thing, "could not lift it to the hands", true);
                    if (pawn.inventory.innerContainer.Contains(thing))
                    {
                        Logger.Message($"{pawn.LabelShort}: could not lift or drop {thing.LabelCap} - left in the pack as DNBL cargo");
                    }
                    else
                    {
                        CargoComp?.Unregister(thing);
                        Logger.Message($"{pawn.LabelShort}: could not lift {thing.LabelCap} out of the inventory to deliver it - dropped it near itself");
                    }
                    carried.Remove(thing);
                    pawn.jobs.curDriver.JumpToToil(deliver);
                    return;
                }
                if (inHands != thing)
                {
                    // A split: the rest stays in the pack, registered and in
                    // `carried`; the part in hands is tracked as well.
                    CargoComp?.Register(inHands);
                    ItemLedger.Split(thing, inHands);
                    if (!carried.Contains(inHands))
                    {
                        carried.Add(inHands);
                    }
                    if (thing.Destroyed || !pawn.inventory.innerContainer.Contains(thing))
                    {
                        CargoComp?.Unregister(thing);
                        carried.Remove(thing);
                    }
                }
                job.SetTarget(TargetIndex.A, inHands);
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
                if (delivered != null)
                {
                    LocalTargetInfo destTarget = job.GetTarget(TargetIndex.B);
                    string where = destTarget.HasThing ? destTarget.Thing.LabelShort
                        : $"storage cell {destTarget.Cell}";
                    // Anything the placement left in the hands was not delivered.
                    int left = pawn.carryTracker?.CarriedThing == delivered ? delivered.stackCount : 0;
                    int total = ItemLedger.CountOf(delivered);
                    ItemLedger.Delivered(pawn, delivered, total - left, where);
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
