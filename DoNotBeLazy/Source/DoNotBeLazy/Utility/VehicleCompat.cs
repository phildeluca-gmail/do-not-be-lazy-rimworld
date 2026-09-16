using System;
using System.Collections;
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
    }
}
