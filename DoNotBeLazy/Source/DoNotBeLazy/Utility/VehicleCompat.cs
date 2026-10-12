using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DoNotBeLazy.Utility
{
    // Same job as FireCompat, GrowerCompat and ScannerCompat: what this mod
    // has to do by hand for one family of WorkGivers. This one differs in a
    // way worth stating up front - it reaches into *another mod*, Vehicle
    // Framework, and nothing here may assume that mod is installed. Every
    // handle below resolves to null when it isn't, Active answers for the
    // lot, and no type from Vehicles.dll is named at compile time. That DLL
    // never enters lib\ (the same rule RimWar Odds follows for RimWar).
    //
    // Two problems are being solved, unrelated except in subject:
    //
    //   1. Menu reachability. Vehicle Framework prefixes
    //      Selector.HandleMapClicks and, on a 2+ pawn right-click over a
    //      cell holding a VehiclePawn, opens its own one-entry
    //      ("Board <vehicle>") Vehicles.FloatMenuMulti and returns false.
    //      Vanilla HandleMapClicks never runs, so
    //      FloatMenuMakerMap.ChoicesAtForMultiSelect is never called and
    //      neither of this mod's postfixes fires. The consequence is wider
    //      than packing: on a group right-click over any vehicle, EVERY *
    //      option disappears - haul, refuel, repair, clean, Consume, all of
    //      it. FloatMenuMultiConstructor is the hook that gets them back;
    //      see Patches/VehicleMenuPatch.
    //
    //   2. Repeat semantics. Packing a vehicle is a "keep bringing things
    //      to this one thing" WorkGiver, which the shared pool cannot model
    //      - PotentialWorkThingsGlobal returns the *vehicles* awaiting
    //      loading, not the items, so the pool holds exactly one entry and
    //      one LoadVehicle job later the sweep is over. SweepManager's
    //      persistent-target path handles it instead, and
    //      IsPersistentTargetWork is what routes it there.
    //
    // Everything below was read off the shipped 1.5 Vehicles.dll by
    // reflection on 2026-09-02, not remembered:
    //
    //   - Vehicles.FloatMenuMulti : Verse.FloatMenu, one constructor,
    //     (List<FloatMenuOption> options, List<Pawn> selPawns,
    //      Pawn clickedPawn, string title, Vector3 clickPos).
    //   - Vehicles.WorkGiver_CarryToVehicle : RimWorld.WorkGiver_Scanner,
    //     with exactly two subclasses in this build,
    //     Vehicles.WorkGiver_PackVehicle and
    //     Vehicles.WorkGiver_BringUpgradeMaterial. (The architecture doc
    //     calls the second one "LoadUpgradeMaterials" - wrong name, right
    //     idea. Matching on the base class is why that mistake costs
    //     nothing, and why a future subclass comes along for free.)
    //   - WorkGiver_CarryToVehicle.JobOnThing answers null on any of: wrong
    //     faction, !JobAvailable(vehicle), Transferables(vehicle) empty, no
    //     ThingDefs, cannot reach, or FindThingToPack returning null. Only
    //     the third of those means "this vehicle is finished" - read off the
    //     IL call sequence, not guessed. FindThingToPack subtracts
    //     TransferableCountHauledByOthersForPacking, so with several pawns
    //     packing at once a null is routinely just "the others have it
    //     covered this second", which is exactly why SweepManager treats a
    //     null as a retry rather than an ending.
    public static class VehicleCompat
    {
        private static readonly Type CarryToVehicleType = AccessTools.TypeByName("Vehicles.WorkGiver_CarryToVehicle");
        private static readonly Type FloatMenuMultiType = AccessTools.TypeByName("Vehicles.FloatMenuMulti");

        // WorkGiver_CarryToVehicle.Transferables(VehiclePawn) - public and
        // virtual on the base, overridden by WorkGiver_PackVehicle, so an
        // ordinary Invoke on the worker instance dispatches correctly.
        private static readonly MethodInfo TransferablesMethod = BuildTransferables();

        // Resolved once, at first touch. AccessTools.TypeByName searches
        // every loaded assembly, and LoadedModManager.LoadAllActiveMods runs
        // LoadModContent() - which loads every mod's assemblies - before
        // CreateModClasses(), which constructs Mod subclasses, this one
        // included. Confirmed against the IL of LoadAllActiveMods rather
        // than assumed, because the whole VehicleMenuPatch hangs off it: by
        // the time anything here is asked a question, Vehicles.dll is either
        // loaded or genuinely absent. There is no "too early" case to guard.
        public static bool Active
        {
            get { return CarryToVehicleType != null; }
        }

        private static MethodInfo BuildTransferables()
        {
            if (CarryToVehicleType == null)
            {
                return null;
            }

            MethodInfo method = AccessTools.Method(CarryToVehicleType, "Transferables");
            if (method == null)
            {
                Core.Logger.Warning("Vehicles.WorkGiver_CarryToVehicle.Transferables not found - a vehicle packing sweep cannot tell 'fully loaded' from 'busy right now', and will end on its retry budget instead.");
            }

            return method;
        }

        // The Vehicles.FloatMenuMulti constructor, or null when Vehicle
        // Framework is absent. Returned rather than patched here so the
        // patch itself stays in Patches\ with the mod's other Harmony work.
        public static ConstructorInfo FloatMenuMultiConstructor()
        {
            if (FloatMenuMultiType == null)
            {
                return null;
            }

            ConstructorInfo[] constructors = FloatMenuMultiType.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            // There is exactly one in this build. Taking the only one rather
            // than matching a parameter list keeps a Vehicle Framework
            // update that adds an argument from silently un-patching us; if
            // it ever grows a second constructor this returns null and says
            // so, rather than patching whichever one happened to be first.
            if (constructors.Length != 1)
            {
                Core.Logger.Warning($"Vehicles.FloatMenuMulti has {constructors.Length} constructors, expected 1 - not patching, so * options stay missing from a group right-click on a vehicle.");
                return null;
            }

            return constructors[0];
        }

        // Vehicles.FloatMenuMulti.StillValid, or null when Vehicle Framework
        // is absent or the method is not the shape this mod was built
        // against. Returned rather than patched here, same as the
        // constructor above.
        //
        // Read off the IL of the shipped 1.5 Vehicles.dll on 2026-09-13, not
        // remembered:
        //
        //   private static bool StillValid(FloatMenuOption opt,
        //                                  List<Pawn> pawns, Pawn ship)
        //
        // 87 bytes of IL, called only from FloatMenuMulti.DoWindowContents
        // every third frame, which sets Disabled = true on any option it
        // answers false for. It never reads `opt`: one downed pawn in the
        // selection fails it for every option in the menu, ours included.
        // Architecture doc section 11.
        //
        // DeclaredOnly, so nothing inherited from Verse.FloatMenu can be
        // picked up in its place. Every part of the signature is checked,
        // because the postfix binds `opt` and `ship` by name and position:
        // a Vehicle Framework update that overloads, renames or reshapes the
        // method gets a warning and no patch, never a patch on the wrong
        // thing.
        public static MethodInfo FloatMenuMultiStillValid()
        {
            if (FloatMenuMultiType == null)
            {
                return null;
            }

            MethodInfo found = null;
            int count = 0;
            foreach (MethodInfo method in FloatMenuMultiType.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.Name == "StillValid")
                {
                    found = method;
                    count++;
                }
            }

            if (count != 1)
            {
                Core.Logger.Warning($"Vehicles.FloatMenuMulti has {count} static StillValid methods, expected 1 - not patching, so a downed pawn in the selection will still grey out * options on a vehicle.");
                return null;
            }

            ParameterInfo[] parameters = found.GetParameters();
            bool shapeMatches = found.ReturnType == typeof(bool)
                && parameters.Length == 3
                && parameters[0].ParameterType == typeof(FloatMenuOption)
                && parameters[0].Name == "opt"
                && parameters[1].ParameterType == typeof(System.Collections.Generic.List<Pawn>)
                && parameters[1].Name == "pawns"
                && parameters[2].ParameterType == typeof(Pawn)
                && parameters[2].Name == "ship";

            if (!shapeMatches)
            {
                Core.Logger.Warning("Vehicles.FloatMenuMulti.StillValid is not (FloatMenuOption opt, List<Pawn> pawns, Pawn ship) returning bool - not patching, so a downed pawn in the selection will still grey out * options on a vehicle.");
                return null;
            }

            return found;
        }

        // Does this WorkGiver work one target over and over, rather than a
        // pool of separate targets? True for vehicle packing and upgrade
        // material delivery. Matched by base class, deliberately - not by
        // defName, and not by work type: packing is workType Hauling, and
        // treating all of Hauling this way would be badly wrong.
        public static bool IsPersistentTargetWork(WorkGiverDef def)
        {
            return CarryToVehicleType != null
                && def != null
                && def.Worker != null
                && CarryToVehicleType.IsInstanceOfType(def.Worker);
        }

        // The vehicle analogue of BillStack.AnyShouldDoNow: is there still
        // cargo the player asked to have loaded? Transferables is the list
        // the loading dialog wrote, so an empty one means the order is over.
        //
        // Deliberately NOT a test of whether anything is packable this
        // second - that is the question JobOnThing already answered null to,
        // and reading that null as an ending is the exact bug this path
        // exists to avoid.
        //
        // Returns true - "assume there is more" - whenever it cannot tell.
        // The caller's retry budget (MaxConsecutiveFailures) ends the order
        // in about eighty seconds either way, so an unreadable answer costs
        // a slow ending, never a stuck pawn.
        public static bool WantsMoreCargo(WorkGiverDef def, Thing vehicle)
        {
            if (TransferablesMethod == null || vehicle == null || def == null || def.Worker == null)
            {
                return true;
            }

            // Transferables takes a VehiclePawn. Anything else arriving here
            // is a caller error rather than a state to handle, but Invoke
            // would throw for it, and a throw out of the sweep loop is worse
            // than a slow ending.
            ParameterInfo[] parameters = TransferablesMethod.GetParameters();
            if (parameters.Length != 1 || !parameters[0].ParameterType.IsInstanceOfType(vehicle))
            {
                return true;
            }

            try
            {
                var transferables = TransferablesMethod.Invoke(def.Worker, new object[] { vehicle }) as ICollection;
                return transferables != null && transferables.Count > 0;
            }
            catch (Exception e)
            {
                Core.Logger.Warning("Vehicles.WorkGiver_CarryToVehicle.Transferables threw, assuming there is more to load: " + e.Message);
                return true;
            }
        }

        // ---- Delivering stuffed cargo into a vehicle (section 23, item 6) ----
        //
        // Vanilla's container toils cannot deliver into a vehicle: logged
        // 2026-10-09, "owner ThingOwner`1 accepts Wood x38: False", every
        // stuffing job Incompletable. Which owner
        // ThingOwnerUtility.TryGetInnerInteractableThingOwner found was not
        // proved (VehiclePawn does not override GetDirectlyHeldThings, so it
        // comes from a comp or a child holder). Vehicle Framework's own
        // Vehicles.JobDriver_LoadVehicle.GiveAsMuchToVehicleAsPossible never
        // asks it and goes straight to the hold; this does the same. Read
        // off the shipped 1.5 Vehicles.dll on 2026-10-09:
        //
        //   - VehiclePawn.AddOrTransfer(Thing thing, int count, Pawn holder = null) : int
        //     = (holder?.carryTracker.innerContainer ?? inventory.innerContainer)
        //       .TryAddOrTransfer(thing, count), then fires CargoAdded.
        //     Its return is NOT the count delivered: ThingOwner.TryTransferToContainer
        //     returns the moved stack's stackCount, which is 0 when the stack
        //     merged into one already in the hold. So the amount delivered is
        //     measured from what is left in the source, never from the return.
        //   - VehiclePawn.cargoToLoad : List<TransferableOneWay>, public field.
        //   - VF's job, after adding: GetTransferable(cargoToLoad, vehicle, item)
        //     - the entry whose `things` holds the exact Thing, else the
        //     first holding any Thing of the same ThingDef - then
        //     countToTransfer -= stack count, and Remove(entry) at <= 0. It
        //     never touches entry.things, so the delivered Thing stays listed
        //     there; this does the same. It adds the whole stack whatever the
        //     entry asks for; this adds only what the entry still asks for.
        //   - Transferable.ForceTo(int) is public and only calls the
        //     CountToTransfer setter (field plus edit buffer), so it is used
        //     instead of VF's reflected private field write.
        //   - VF's job reserves only the item, never the vehicle.
        //   - VehiclePawn.inventory.innerContainer is a plain ThingOwner<Thing>
        //     (Pawn_InventoryTracker ctor), no stack limit, so it accepts.
        //   - Nothing in VF does any bookkeeping when cargoToLoad empties: the
        //     "LoadVehicle" lister is removed only by the vehicle's Cancel
        //     gizmo, and VehicleReservationManager's tick prunes reservations,
        //     not listers. JobOnThing simply finds Transferables empty.
        //   - Only Vehicles.WorkGiver_PackVehicle reads cargoToLoad.
        //     WorkGiver_BringUpgradeMaterial (the other CarryToVehicle
        //     subclass) fills CompUpgradeTree.upgradeContainer from ThingDefs,
        //     so upgrade orders keep the old container path.
        private static readonly Type VehiclePawnType = AccessTools.TypeByName("Vehicles.VehiclePawn");
        private static readonly Type PackVehicleType = AccessTools.TypeByName("Vehicles.WorkGiver_PackVehicle");
        private static readonly MethodInfo AddOrTransferMethod = BuildAddOrTransfer();
        private static readonly FieldInfo CargoToLoadField = BuildCargoToLoad();

        private static MethodInfo BuildAddOrTransfer()
        {
            if (VehiclePawnType == null)
            {
                return null;
            }

            MethodInfo method = AccessTools.Method(VehiclePawnType, "AddOrTransfer", new Type[] { typeof(Thing), typeof(int), typeof(Pawn) });
            if (method == null || method.ReturnType != typeof(int))
            {
                Core.Logger.Warning("Vehicles.VehiclePawn.AddOrTransfer(Thing, int, Pawn) not found - stuffed cargo cannot be delivered into a vehicle and stays in the pawn's inventory.");
                return null;
            }

            return method;
        }

        private static FieldInfo BuildCargoToLoad()
        {
            if (VehiclePawnType == null)
            {
                return null;
            }

            FieldInfo field = AccessTools.Field(VehiclePawnType, "cargoToLoad");
            if (field == null || !typeof(List<TransferableOneWay>).IsAssignableFrom(field.FieldType))
            {
                Core.Logger.Warning("Vehicles.VehiclePawn.cargoToLoad is missing or not a List<TransferableOneWay> - stuffed cargo cannot be delivered into a vehicle and stays in the pawn's inventory.");
                return null;
            }

            return field;
        }

        // Does this order load a vehicle's cargo hold from its cargoToLoad
        // list, and can this mod do that? False for upgrade materials, and
        // false when either reflected member is missing (warned once above),
        // so the caller keeps the old container path.
        public static bool IsCargoLoadingWork(WorkGiverDef def)
        {
            return PackVehicleType != null && AddOrTransferMethod != null && CargoToLoadField != null
                && def?.Worker != null && PackVehicleType.IsInstanceOfType(def.Worker);
        }

        // Is this a Vehicle Framework vehicle? False when the mod is absent.
        public static bool IsVehicle(Thing thing)
        {
            return VehiclePawnType != null && thing != null && VehiclePawnType.IsInstanceOfType(thing);
        }

        // The vehicle's pending load list; empty when VF has not made one
        // yet. Null only when the field is missing or this is no vehicle.
        private static List<TransferableOneWay> CargoToLoad(Thing vehicle)
        {
            if (CargoToLoadField == null || !IsVehicle(vehicle))
            {
                return null;
            }

            return CargoToLoadField.GetValue(vehicle) as List<TransferableOneWay> ?? new List<TransferableOneWay>();
        }

        // VF's own GetTransferable matches the exact Thing, then the
        // ThingDef alone - so two entries of one def with different stuff or
        // quality are confused there. A middle pass is added: the entry whose
        // things TransferAsOne(PodsOrCaravanPacking) with the item (same def,
        // stuff, quality, rot, as RimWorld's own packing groups them, and as
        // VF's CountFromJob already compares). VF's def-only pass stays last.
        private static TransferableOneWay FindEntry(List<TransferableOneWay> pending, Thing item)
        {
            foreach (TransferableOneWay entry in pending)
            {
                if (entry.things != null && entry.things.Contains(item))
                {
                    return entry;
                }
            }
            foreach (TransferableOneWay entry in pending)
            {
                if (entry.things != null && entry.things.Any(t => t != null
                    && TransferableUtility.TransferAsOne(t, item, TransferAsOneMode.PodsOrCaravanPacking)))
                {
                    return entry;
                }
            }
            foreach (TransferableOneWay entry in pending)
            {
                if (entry.things != null && entry.things.Any(t => t != null && t.def == item.def))
                {
                    return entry;
                }
            }
            return null;
        }

        // Null when the vehicle still wants `item`, otherwise why not.
        public static string WhyNotWanted(Thing vehicle, Thing item)
        {
            List<TransferableOneWay> pending = CargoToLoad(vehicle);
            if (pending == null)
            {
                return "its load list cannot be read";
            }

            TransferableOneWay entry = FindEntry(pending, item);
            if (entry == null || entry.CountToTransfer <= 0)
            {
                return "it no longer asks for " + item.def.label;
            }

            return null;
        }

        // How many more of `item` the vehicle's matching entry asks for,
        // less what `alreadyCarried` already holds for that same entry. -1
        // when the list cannot be read, so the caller does not cap at all.
        // Other pawns' cargo in transit is not counted - nothing here tracks
        // it - so two pawns can still both gather for one entry; the second
        // finds it gone on arrival and keeps its stack (WhyNotWanted).
        public static int StillWanted(Thing vehicle, Thing item, IEnumerable<Thing> alreadyCarried)
        {
            List<TransferableOneWay> pending = CargoToLoad(vehicle);
            if (pending == null || item == null)
            {
                return -1;
            }

            TransferableOneWay entry = FindEntry(pending, item);
            if (entry == null)
            {
                return 0;
            }

            int wanted = entry.CountToTransfer;
            foreach (Thing held in alreadyCarried)
            {
                if (held != null && !held.Destroyed && held != item && FindEntry(pending, held) == entry)
                {
                    wanted -= held.stackCount;
                }
            }
            return Math.Max(0, wanted);
        }

        // Puts as much of `item` as the vehicle's matching entry still asks
        // for into the vehicle's hold, from wherever the pawn holds it, and
        // lowers that entry the way VF's own load job does. Returns the
        // number delivered. 0 with `reason` set when nothing went in;
        // `pendingLeft` is what the entry still asks for afterwards.
        public static int DeliverToVehicle(Thing vehicle, Thing item, out int pendingLeft, out string reason)
        {
            pendingLeft = 0;
            reason = null;
            if (AddOrTransferMethod == null || item == null || item.Destroyed)
            {
                reason = AddOrTransferMethod == null ? "VehiclePawn.AddOrTransfer was not found" : "the item is gone";
                return 0;
            }

            reason = WhyNotWanted(vehicle, item);
            if (reason != null)
            {
                return 0;
            }

            List<TransferableOneWay> pending = CargoToLoad(vehicle);
            TransferableOneWay entry = FindEntry(pending, item);
            int count = Math.Min(item.stackCount, entry.CountToTransfer);
            int before = item.stackCount;
            ThingOwner source = item.holdingOwner;

            try
            {
                AddOrTransferMethod.Invoke(vehicle, new object[] { item, count, null });
            }
            catch (Exception e)
            {
                reason = "AddOrTransfer threw " + (e.InnerException ?? e).Message;
                pendingLeft = entry.CountToTransfer;
                return 0;
            }

            // A whole stack that moved (merged or not) has left `source`; a
            // split one leaves the rest behind in it.
            int left = !item.Destroyed && source != null && source.Contains(item) ? item.stackCount : 0;
            int added = before - left;
            if (added <= 0)
            {
                reason = "the vehicle's hold took none of it";
                pendingLeft = entry.CountToTransfer;
                return 0;
            }

            entry.ForceTo(entry.CountToTransfer - added);
            if (entry.CountToTransfer <= 0)
            {
                pending.Remove(entry);
            }
            pendingLeft = Math.Max(0, entry.CountToTransfer);
            return added;
        }
    }
}
