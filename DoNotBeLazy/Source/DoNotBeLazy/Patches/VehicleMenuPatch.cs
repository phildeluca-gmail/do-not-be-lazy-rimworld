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
    // Since 2026-09-13 there is a second patch here, a postfix on
    // Vehicles.FloatMenuMulti.StillValid - see StillValidPostfix.
    //
    // Both patches are applied by hand rather than by attribute, because
    // PatchAll cannot resolve a type this assembly does not reference.
    // Everything is soft: no Vehicles.dll in lib\, no hard type reference,
    // and a missing Vehicle Framework simply means TryPatch does nothing.
    public static class VehicleMenuPatch
    {
        // The FloatMenuOption instances this mod added to the most recently
        // built vehicle menu. Emptied at the start of every Prefix, so it
        // holds one menu's options at most and nothing leaks into the next
        // - RimWorld shows one float menu at a time, and a new right-click
        // closes the old one. Identity, not label: FloatMenuOption does not
        // override Equals or GetHashCode (checked in the decompiled type),
        // so this compares instances, and another mod's "* " label can never
        // be mistaken for ours.
        private static readonly HashSet<FloatMenuOption> ourOptions = new HashSet<FloatMenuOption>();

        // The in-play line is said once per session, not once per menu - the
        // postfix runs every third frame for every option while a menu is
        // open.
        private static bool overrideAnnounced;

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

                // Without the prefix no option in a vehicle menu is ours, so
                // the StillValid postfix would have nothing to protect.
                return;
            }

            // After the prefix, and only if it went on. The resolve warns on
            // its own when the method is missing or reshaped.
            MethodInfo stillValid = VehicleCompat.FloatMenuMultiStillValid();
            if (stillValid == null)
            {
                return;
            }

            try
            {
                harmony.Patch(stillValid, postfix: new HarmonyMethod(typeof(VehicleMenuPatch), nameof(StillValidPostfix)));
                Logger.Message("Vehicle Framework detected - patched Vehicles.FloatMenuMulti.StillValid so a downed or broken pawn in the selection no longer greys out our * options.");
            }
            catch (Exception e)
            {
                Logger.Error("could not patch Vehicles.FloatMenuMulti.StillValid, a downed pawn in the selection will still grey out * options on a vehicle: " + e);
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
            ourOptions.Clear();
            if (options == null)
            {
                return;
            }

            // Whatever is already in the list is not ours. Everything in it
            // afterwards that was not there before is - which catches an
            // entry inserted at the front as well as the ones appended.
            var before = new HashSet<FloatMenuOption>(options);

            FloatMenuPatch.AddSweepOptions(clickPos, selPawns, options, true);

            foreach (FloatMenuOption option in options)
            {
                if (option != null && !before.Contains(option))
                {
                    ourOptions.Add(option);
                }
            }
        }

        // Postfix on Vehicles.FloatMenuMulti.StillValid(FloatMenuOption opt,
        // List<Pawn> pawns, Pawn ship), private static, read off the 1.5
        // Vehicles.dll IL.
        //
        // Why: FloatMenuMulti.DoWindowContents asks StillValid about every
        // option every third frame and sets Disabled = true on a false -
        // and Disabled's setter nulls the action, so the grey is permanent.
        // StillValid never reads the option. It fails for ALL of them when
        // any selected pawn is dead, downed or in a mental state, which is
        // how a downed Mata greyed out a * pack-vehicle order that did not
        // include her. Our options carry their own pawn lists, already
        // filtered by PawnValidator / CanConsume for exactly those states.
        //
        // What it does: when VF said false and the option is one we added,
        // say true instead - unless the vehicle is gone or dead, or nobody
        // in the selection is left who could act. A downed or moving vehicle
        // does not disable ours. Architecture doc section 11 has the table.
        // VF's own options are never touched.
        //
        // Declared as Pawn, not VehiclePawn: no Vehicle Framework type is
        // named at compile time. Runs per option per third frame, so no
        // allocation and no log line on the common path.
        public static void StillValidPostfix(FloatMenuOption opt, List<Pawn> pawns, Pawn ship, ref bool __result)
        {
            if (__result || opt == null || !ourOptions.Contains(opt))
            {
                return;
            }

            if (ship == null || !ship.Spawned || ship.Dead)
            {
                return;
            }

            if (!AnyoneCanAct(pawns))
            {
                return;
            }

            __result = true;

            if (!overrideAnnounced)
            {
                overrideAnnounced = true;
                Logger.Message("Vehicle Framework tried to grey out a * option (a selected pawn is dead, downed or in a mental state, or the vehicle is downed or moving) - kept it enabled. Said once per session.");
            }
        }

        // At least one selected pawn who is not dead, downed or in a mental
        // state - the three things StillValid checks on the pawns. With none
        // left, the order has nobody to carry it out and VF's grey stands.
        private static bool AnyoneCanAct(List<Pawn> pawns)
        {
            if (pawns == null)
            {
                return false;
            }

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn != null && !pawn.Dead && !pawn.Downed && !pawn.InMentalState)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
