using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;
using DoNotBeLazy.Utility;
using Logger = DoNotBeLazy.Core.Logger;

namespace DoNotBeLazy.Patches
{
    // Prefix on the Vehicles.FloatMenuMulti constructor - the first Harmony
    // patch in this mod aimed at another mod's type.
    //
    // Why a constructor and not a method: Vehicle Framework does not build
    // its group menu through anything vanilla calls. It prefixes
    // Selector.HandleMapClicks, and when 2+ pawns are selected and the cell
    // under the cursor holds a VehiclePawn it constructs its own
    // FloatMenuMulti and returns false, so FloatMenuMakerMap
    // .ChoicesAtForMultiSelect never runs and FloatMenuPatch.MultiSelect
    // never fires. The constructor is the only point where the finished
    // option list, the selected pawns and the click position are all in
    // hand at once - which happens to be exactly the three arguments
    // AddSweepOptions already takes.
    //
    // Why mutating the incoming list is safe: a Harmony prefix runs before
    // the derived constructor's body, and therefore before its call to the
    // base Verse.FloatMenu constructor, which is where option sizes are
    // measured and cached. Entries added here are laid out normally.
    //
    // The patch is applied by hand rather than by attribute, because
    // PatchAll cannot resolve a type this assembly does not reference.
    // Everything is soft: no Vehicles.dll in lib\, no hard type reference,
    // and a missing Vehicle Framework simply means TryPatch does nothing.
    public static class VehicleMenuPatch
    {
        public static void TryPatch(Harmony harmony)
        {
            if (!VehicleCompat.Active)
            {
                return;
            }

            ConstructorInfo constructor = VehicleCompat.FloatMenuMultiConstructor();
            if (constructor == null)
            {
                return;
            }

            try
            {
                harmony.Patch(constructor, prefix: new HarmonyMethod(typeof(VehicleMenuPatch), nameof(Prefix)));
                Logger.Message("Vehicle Framework detected - patched Vehicles.FloatMenuMulti so * options survive a group right-click on a vehicle.");
            }
            catch (Exception e)
            {
                // A failed patch here costs the * options on vehicles and
                // nothing else. It must never take the mod down with it.
                Logger.Error("could not patch Vehicles.FloatMenuMulti, * options will be missing from group right-clicks on a vehicle: " + e);
            }
        }

        // Parameter names match the constructor's own (options, selPawns,
        // clickPos), which is how Harmony binds them; clickedPawn and title
        // are not needed and so are not declared.
        //
        // multiSelect: true - this menu only ever exists for a 2+ pawn
        // selection. That flag also governs FloatMenuPatch's "Move here"
        // entry, which is owed only when our own options are the reason a
        // menu opened at all. Here they are not: Vehicle Framework has
        // already put "Board <vehicle>" in the list, so the count is
        // non-zero and no move entry is added. Correct - the player's squad
        // move was swallowed by Vehicle Framework, not by us.
        public static void Prefix(List<FloatMenuOption> options, List<Pawn> selPawns, Vector3 clickPos)
        {
            FloatMenuPatch.AddSweepOptions(clickPos, selPawns, options, true);
        }
    }
}
