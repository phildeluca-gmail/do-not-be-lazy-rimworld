using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using DoNotBeLazy.Components;
using DoNotBeLazy.Utility;
// UnityEngine has its own Logger and this file needs UnityEngine for Vector3
using Logger = DoNotBeLazy.Core.Logger;

namespace DoNotBeLazy.Patches
{
    // Postfixes on the two float menu builders in FloatMenuMakerMap.
    // Confirmed against the actual game DLL for this version
    // (1.5.9214.33606) via reflection rather than guessed - there is no
    // GetOptions method on FloatMenuMakerMap in this build. The real
    // entry points are:
    //
    //   ChoicesAtFor(Vector3, Pawn, bool)        - exactly 1 pawn selected
    //   ChoicesAtForMultiSelect(Vector3, List<Pawn>)  - 2+ pawns selected
    //
    // Both are patched. The single-pawn one just wraps its pawn in a
    // one-element list and hands off to the same builder, so the
    // eligibility scan lives in exactly one place.
    //
    // clickPos is a world-space Vector3, not a cell or thing - vanilla
    // resolves it internally and we don't have access to that. We convert
    // to a cell ourselves and look at what's there to decide which
    // sweep-eligible WorkGiverDefs apply, rather than trying to pull a
    // WorkGiverDef back out of the FloatMenuOptions vanilla already built
    // (they don't carry one anywhere accessible).
    public static class FloatMenuPatch
    {
        // Hauling/Construction/Cleaning/Mining/Growing per architecture doc
        // section 2's examples. PlantCutting added separately from Growing -
        // cutting/chopping plants (PlantsCut) is its own WorkTypeDef, not
        // part of Growing, and was missing entirely (clicking an
        // already-marked-for-cutting plant did nothing). Workstation bills
        // aren't listed by name here - any WorkGiverDef whose Worker is
        // WorkGiver_DoBill qualifies regardless of WorkTypeDef, since
        // there's one per crafting station and hardcoding them all would
        // break against mod/DLC additions.
        private static readonly HashSet<string> SupportedWorkTypeDefNames = new HashSet<string>
        {
            "Hauling",
            "Construction",
            "Cleaning",
            "Mining",
            "Growing",
            "PlantCutting",
            // Firefighting is a special case - FightFires is
            // directOrderable:false so IsSweepEligible short-circuits on
            // FireCompat.IsFirefighting before this set is ever consulted.
            // Listed anyway so the supported set reads as the real answer to
            // "what work types does this mod sweep".
            "Firefighter",
        };

        // CookFillHopper (workType Hauling, vanilla defName) matches on any
        // food item near a hopper that needs fuel - right-clicking food or
        // drugs was offering "* fill food hoppers", an obscure mechanic
        // nobody was asking for in that context. Everything else under
        // Hauling is still fine; this one def just doesn't belong.
        //
        // DeliverResourcesToFrames/Blueprints (workType Hauling) are exact
        // duplicates of ConstructDeliverResourcesToFrames/Blueprints
        // (workType Construction) - same giverClass, registered twice so
        // whichever work priority is higher governs it. Excluding the
        // Hauling-tagged copies specifically, by defName, rather than
        // deduping by giverClass generally: giverClass is NOT a safe
        // uniqueness key across the whole WorkGiverDef set - all ~19
        // workstation bill types (cooking, smithing, tailoring, art,
        // stonecutting, everything) share the single giverClass
        // WorkGiver_DoBill, so a giverClass-based dedup was silently
        // collapsing every workstation type down to just one. Fixed by
        // reverting to this narrow, explicit denylist instead.
        private static readonly HashSet<string> ExcludedDefNames = new HashSet<string>
        {
            "CookFillHopper",
            "DeliverResourcesToFrames",
            "DeliverResourcesToBlueprints",
        };

        // Built once on first right-click instead of walking the whole
        // DefDatabase every time the menu opens. def.Worker instantiates the
        // worker, so doing it per click on a 200-def modlist is wasted work.
        private static List<WorkGiverDef> eligibleDefs;

        // ceiling on the greyed-out "why not" entries. Three is enough to
        // explain a click; thirty would be a wall of grey nobody reads.
        private const int MaxFeedbackOptions = 3;

        // 1 pawn selected
        [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.ChoicesAtFor))]
        public static class SingleSelect
        {
            public static void Postfix(Vector3 clickPos, Pawn pawn, List<FloatMenuOption> __result)
            {
                if (pawn == null)
                {
                    return;
                }
                AddSweepOptions(clickPos, new List<Pawn> { pawn }, __result, false);
            }
        }

        // 2+ pawns selected
        [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.ChoicesAtForMultiSelect))]
        public static class MultiSelect
        {
            public static void Postfix(Vector3 clickPos, List<Pawn> pawns, List<FloatMenuOption> __result)
            {
                AddSweepOptions(clickPos, pawns, __result, true);
            }
        }

        // Whole body is wrapped - an exception escaping a float menu postfix
        // takes the right-click menu down for every other mod in the chain
        // too (Achtung patches the same area). Better to silently lose our
        // * entries than to break the menu.
        private static void AddSweepOptions(Vector3 clickPos, List<Pawn> pawns, List<FloatMenuOption> options, bool multiSelect)
        {
            if (pawns == null || pawns.Count == 0 || options == null)
            {
                return;
            }

            try
            {
                Build(clickPos, pawns, options, multiSelect);
            }
            catch (Exception e)
            {
                Logger.Error("float menu postfix blew up, skipping sweep options: " + e);
            }
        }

        private static void Build(Vector3 clickPos, List<Pawn> pawns, List<FloatMenuOption> options, bool multiSelect)
        {
            // A right-click on a drafted selection is a move order, not a
            // work order, and appending anything at all breaks it: vanilla
            // auto-executes a float menu whose options are all autoTakeable
            // (that is how a drafted right-click moves a pawn with no menu),
            // and our entries are never autoTakeable. Since CanSweep rejects
            // drafted pawns anyway, every entry we'd add here is a greyed-out
            // one - we were trading the move order for a line of grey text.
            //
            // All drafted, not any: a mixed selection still holds pawns that
            // can genuinely sweep, and vanilla wasn't going to auto-take for
            // that selection either.
            if (AllDrafted(pawns))
            {
                return;
            }

            // what vanilla had already put in the menu before we touched it.
            // If this is zero on the multi-select path, the menu was going to
            // be empty - and an empty multi-select menu is what lets the
            // squad move happen (TryMakeMultiSelectFloatMenu returns false).
            // Adding even one option suppresses that move, so if we do add
            // one we owe the player a way to move. See MoveHereOption.
            int vanillaOptionCount = options.Count;

            // don't trust pawns[0] to be spawned - caravan/world pawns can
            // sit in a selection and their Map is null
            Map map = null;
            foreach (Pawn p in pawns)
            {
                if (p != null && p.Spawned && p.Map != null)
                {
                    map = p.Map;
                    break;
                }
            }
            if (map == null)
            {
                return;
            }

            IntVec3 cell = clickPos.ToIntVec3();
            if (!cell.InBounds(map))
            {
                return;
            }

            // not early-returning when this is empty - scanCells defs (sow
            // crops) target the clicked cell itself, which is normally
            // empty by definition
            List<Thing> thingsHere = cell.GetThingList(map);

            // GrowerSow/GrowerHarvest are both scanCells (target the cell
            // itself, not a Thing on it) and neither checks for fire - a
            // burning farm tile with a scorched-but-still-mature plant on
            // it, or one already burnt down to bare ground, still passes
            // HasJobOnCell. So a burning cell still suppresses every other
            // sweep type rather than special-casing each WorkGiverDef.
            //
            // It used to bail on the whole click. Now it flips to
            // firefighting-only instead: "cannot group-select to put out
            // fires" was the exact thing that early return made impossible.
            bool fireHere = false;
            foreach (Thing thing in thingsHere)
            {
                if (thing is Fire)
                {
                    fireHere = true;
                    break;
                }
            }

            // disabled "here's why you can't" entries, collected as we go
            // and appended after the real options so they sort last
            var feedback = new List<FloatMenuOption>();

            foreach (WorkGiverDef def in EligibleDefs())
            {
                // burning cell -> firefighting and nothing else; anywhere
                // else -> everything except firefighting, since there's no
                // fire to fight
                if (fireHere != FireCompat.IsFirefighting(def))
                {
                    continue;
                }

                List<Pawn> eligiblePawns = EligiblePawns(pawns, map, def);
                if (eligiblePawns.Count == 0)
                {
                    // used to just continue here, which is how "Hauling is
                    // priority 0" and "the pawn is drafted" came out as
                    // silent nothing - the whole point of the greyed-out
                    // entries was to stop that
                    if (feedback.Count < MaxFeedbackOptions && WantsSomethingHere(def, thingsHere, fireHere))
                    {
                        string why = FirstRefusal(pawns, map, def, out Pawn refused);
                        if (why != null)
                        {
                            feedback.Add(DisabledOption(def, refused.LabelShort, why));
                        }
                    }
                    continue;
                }

                var scanner = (WorkGiver_Scanner)def.Worker;
                LocalTargetInfo target = FindTargetWithJob(eligiblePawns, def, scanner, cell, thingsHere, out string failReason, out Thing failThing);

                // Cleaning is asked for by area, not by tile. Ordered
                // 2026-08-30: clicking a filthy floor should offer to clear
                // the snow a few tiles over too, and clicking a snowy one
                // should offer the filth - "when both exist in the radius"
                // was the wording. Every other work type still needs the
                // clicked cell to answer for itself, which is what keeps
                // this off the general per-click cost open item 6 warns
                // about: at most two probes, and only when the click itself
                // came back empty.
                if (!target.IsValid
                    && IsCleaningWork(def)
                    && TaskScanner.HasTargetInRadius(cell, Core.DoNotBeLazyMod.Settings.sweepRadius, map, def, eligiblePawns[0]))
                {
                    Logger.Message($"{def.defName}: nothing on the clicked cell, but the radius has work - offering anyway");
                    target = cell;
                }

                if (!target.IsValid)
                {
                    if (failReason != null && feedback.Count < MaxFeedbackOptions)
                    {
                        feedback.Add(DisabledOption(def, failThing?.LabelShort, failReason));
                    }
                    continue;
                }

                // The thing under the cursor was refused even though the
                // sweep is on offer - the radius fallback above is the only
                // way both can be true. Say so anyway: "I clicked the
                // firefoam and they cleaned everything except the firefoam"
                // is exactly the report this is here to prevent.
                if (failReason != null && feedback.Count < MaxFeedbackOptions)
                {
                    feedback.Add(DisabledOption(def, failThing?.LabelShort, failReason));
                }

                string label = def.label.NullOrEmpty() ? def.defName : def.label.CapitalizeFirst();

                WorkGiverDef capturedDef = def;
                LocalTargetInfo capturedTarget = target;
                Map capturedMap = map;
                options.Add(new FloatMenuOption(
                    "* " + label + " until done",
                    () =>
                    {
                        SweepManager mgr = capturedMap.GetComponent<SweepManager>();
                        if (mgr == null)
                        {
                            Logger.Warning("no SweepManager on map, sweep ignored");
                            return;
                        }
                        mgr.BeginSweep(eligiblePawns, capturedTarget, capturedDef);
                    },
                    MenuOptionPriority.Low));
            }

            options.AddRange(feedback);

            // eating off a burning tile is the same bad idea as everything
            // else on it
            if (!fireHere)
            {
                AddConsumeOption(pawns, map, thingsHere, options);
            }

            // We only owe a move order when we are the reason the menu is
            // opening at all. Single-select already gets vanilla's own
            // "Go here"; ChoicesAtForMultiSelect never builds one, so on that
            // path an empty-turned-non-empty list means we just swallowed the
            // player's squad move.
            if (multiSelect && vanillaOptionCount == 0 && options.Count > 0)
            {
                FloatMenuOption move = MoveHereOption(pawns, map, cell);
                if (move != null)
                {
                    options.Insert(0, move);
                }
            }
        }

        // CleanFilth and CleanClearSnow in vanilla, plus anything a mod
        // files under the same work type. Read off the WorkTypeDef rather
        // than listing defNames so a modded cleaning giver comes along.
        private static bool IsCleaningWork(WorkGiverDef def)
        {
            return def?.workType?.defName == "Cleaning";
        }

        private static bool AllDrafted(List<Pawn> pawns)
        {
            foreach (Pawn p in pawns)
            {
                if (p != null && !p.Drafted)
                {
                    return false;
                }
            }

            return true;
        }

        // "Move here" for a multi-select menu that only exists because we put
        // something in it. Reproduces vanilla's squad move rather than
        // approximating it: BestOrderedGotoDestNear is the same destination
        // picker FloatMenuMakerMap.GotoLocationOption uses, and it is what
        // spreads a group across neighbouring cells instead of stacking them
        // all on the one that was clicked. PawnGotoAction is public static in
        // this build (checked by reflection on Assembly-CSharp, along with
        // BestOrderedGotoDestNear's three-argument shape).
        //
        // GoHere priority sorts this above our own Low entries, so the move
        // is the first thing in the menu - which is where a player who was
        // trying to move expects it.
        private static FloatMenuOption MoveHereOption(List<Pawn> pawns, Map map, IntVec3 cell)
        {
            // Resolved now rather than in the closure: an option offering a
            // move that no selected pawn can make is worse than no option.
            var movers = new List<Pawn>();
            foreach (Pawn p in pawns)
            {
                if (p == null || !p.Spawned || p.Map != map || p.Downed)
                {
                    continue;
                }

                // Danger.Deadly to match vanilla's goto check - a manual move
                // order is allowed to walk somewhere dangerous
                if (p.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                {
                    movers.Add(p);
                }
            }

            if (movers.Count == 0)
            {
                return null;
            }

            return new FloatMenuOption(
                "Move here",
                () =>
                {
                    foreach (Pawn p in movers)
                    {
                        IntVec3 dest = RCellFinder.BestOrderedGotoDestNear(cell, p, null);
                        FloatMenuMakerMap.PawnGotoAction(cell, p, dest);
                    }
                },
                MenuOptionPriority.GoHere);
        }

        // Greyed-out entry explaining why a sweep isn't on offer. Vanilla
        // does exactly this in AddJobGiverWorkOrders - a null action makes
        // the option unclickable, and Disabled greys it. Worth the noise:
        // "I ordered a haul and nothing happened" has cost two sessions
        // now, and both times the answer was a stockpile filter that
        // vanilla could have told us about all along.
        //
        // Reason text comes from JobFailReason, so it's the game's own
        // wording, already translated.
        private static FloatMenuOption DisabledOption(WorkGiverDef def, string subject, string reason)
        {
            string label = def.label.NullOrEmpty() ? def.defName : def.label.CapitalizeFirst();
            // subject is the thing that refused for target-side entries and
            // the pawn that refused for pawn-side ones
            string what = subject.NullOrEmpty() ? "" : subject + ": ";

            return new FloatMenuOption(
                "* " + label + " until done - " + what + reason,
                null,
                MenuOptionPriority.Low)
            {
                Disabled = true,
            };
        }

        // Eating/drugs aren't WorkGiver-based at all - there's no WorkGiverDef
        // for "ingest", it's a separate system (JobDefOf.Ingest), so this
        // doesn't go through SweepManager or the eligibleDefs loop above.
        // One dose/meal per eligible pawn, issued directly and immediately -
        // not an ongoing sweep. Mirrors what right-clicking "Eat/Smoke/Snort
        // X" does for a single pawn in vanilla, just fanned out to the whole
        // selection. Deliberately ignores Drafted (you can manually order a
        // drafted pawn to eat/take a combat drug in vanilla too) and doesn't
        // check hunger level (a manual order works regardless of need, same
        // as vanilla).
        private static void AddConsumeOption(List<Pawn> pawns, Map map, List<Thing> thingsHere, List<FloatMenuOption> options)
        {
            Thing ingestible = FindIngestibleThing(thingsHere);
            if (ingestible == null)
            {
                return;
            }

            var canEat = new List<Pawn>();
            foreach (Pawn pawn in pawns)
            {
                if (pawn != null && pawn.Map == map && CanConsume(pawn, ingestible))
                {
                    canEat.Add(pawn);
                }
            }
            if (canEat.Count == 0)
            {
                return;
            }

            string commandFormat = ingestible.def.ingestible.ingestCommandString;
            string label = commandFormat.NullOrEmpty()
                ? "Consume " + ingestible.LabelShort
                : string.Format(commandFormat, ingestible.LabelShort);

            Thing capturedThing = ingestible;
            options.Add(new FloatMenuOption(
                "* " + label,
                () => ConsumeAll(canEat, capturedThing),
                MenuOptionPriority.Low));
        }

        private static Thing FindIngestibleThing(List<Thing> thingsHere)
        {
            foreach (Thing thing in thingsHere)
            {
                if (thing?.def?.ingestible != null && thing.def.ingestible.showIngestFloatOption)
                {
                    return thing;
                }
            }
            return null;
        }

        private static bool CanConsume(Pawn pawn, Thing thing)
        {
            if (pawn.Dead || pawn.Downed || pawn.InMentalState)
            {
                return false;
            }
            if (pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
            {
                return false;
            }

            // WillEat is a food-appetite check (preferability/nutrition) and
            // rejects pure drugs outright - a smokeleaf joint or wake-up
            // isn't "food", so every drug was failing this and only
            // unrelated Hauling-category options were left to show. Route
            // drugs through their own, much simpler check instead.
            if (thing.def.ingestible.drugCategory != DrugCategory.None)
            {
                // vanilla *does* let you force-feed a Teetotaler a drug (see
                // the "forced to take drugs" thought - it's allowed, just
                // gives a mood hit) but skip them here rather than force it
                bool isTeetotaler = pawn.story?.traits != null && pawn.story.traits.HasTrait(TraitDefOf.DrugDesire, -1);
                return !isTeetotaler;
            }

            return FoodUtility.WillEat(pawn, thing, pawn);
        }

        // one dose each, taken directly from the clicked stack - not looped
        // like an area sweep, and not tracked by SweepManager (nothing to
        // interrupt or chain, the job either succeeds or it doesn't)
        private static void ConsumeAll(List<Pawn> pawns, Thing thing)
        {
            foreach (Pawn pawn in pawns)
            {
                if (thing.Destroyed || thing.stackCount <= 0)
                {
                    break;
                }

                float nutrition = thing.GetStatValue(StatDefOf.Nutrition);
                Job job = JobMaker.MakeJob(JobDefOf.Ingest, thing);
                job.count = Mathf.Clamp(FoodUtility.WillIngestStackCountOf(pawn, thing.def, nutrition), 1, thing.stackCount);
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
        }

        private static List<WorkGiverDef> EligibleDefs()
        {
            if (eligibleDefs != null)
            {
                return eligibleDefs;
            }

            eligibleDefs = new List<WorkGiverDef>();
            foreach (WorkGiverDef def in DefDatabase<WorkGiverDef>.AllDefsListForReading)
            {
                // a broken mod def can throw straight out of the Worker
                // getter - don't let one bad def kill the whole list
                try
                {
                    if (IsSweepEligible(def))
                    {
                        eligibleDefs.Add(def);
                    }
                }
                catch (Exception e)
                {
                    Logger.Warning($"skipping WorkGiverDef {def?.defName}: {e.Message}");
                }
            }

            return eligibleDefs;
        }

        private static bool IsSweepEligible(WorkGiverDef def)
        {
            if (def == null || ExcludedDefNames.Contains(def.defName))
            {
                return false;
            }
            // firefighting jumps the directOrderable check below - it's the
            // one non-orderable def we deliberately want, see FireCompat
            if (FireCompat.IsFirefighting(def))
            {
                return def.Worker is WorkGiver_Scanner;
            }
            // directOrderable defaults true and is only set false on defs
            // vanilla deliberately keeps out of the player's hands - e.g.
            // FightFires (emergency-only, auto-taken by any idle pawn
            // regardless of orders; that's why there's no vanilla "put out
            // fire" menu option either). Respecting this generally is safer
            // than denylisting each one we happen to trip over.
            if (!def.directOrderable)
            {
                return false;
            }
            if (!(def.Worker is WorkGiver_Scanner scanner))
            {
                return false;
            }
            if (scanner is WorkGiver_DoBill)
            {
                return true;
            }
            return def.workType != null && SupportedWorkTypeDefNames.Contains(def.workType.defName);
        }

        // vanilla only shows the base option when some selected pawn can
        // actually do the job on the clicked target - mirror that here so
        // the * option only shows up where the normal one would have.
        // scanCells defs (e.g. GrowerSow - sow crops) have no Thing to
        // check: the target IS the clicked cell itself, empty or not, so
        // that branch runs regardless of what's in thingsHere.
        //
        // forced:true throughout - this is a manually-issued player order,
        // same as vanilla's own float-menu building. WorkGiver_GrowerSow's
        // JobOnCell threads forced straight into its reservation check
        // (ignoreOtherReservations), so leaving it false was silently
        // failing sow/harvest on perfectly valid cells.
        //
        // failReason/failThing come back set when no target was found but
        // some WorkGiver had something to say about why - that's what the
        // greyed-out feedback entries are built from. Only filled in for
        // the scanThings branch: a cell-scanned def refusing an ordinary
        // empty tile is the normal case, not an error worth reporting, and
        // "* Sow until done - not a growing zone" on every click of bare
        // ground would be pure noise.
        private static LocalTargetInfo FindTargetWithJob(List<Pawn> pawns, WorkGiverDef def, WorkGiver_Scanner scanner, IntVec3 cell, List<Thing> thingsHere, out string failReason, out Thing failThing)
        {
            failReason = null;
            failThing = null;

            if (def.scanCells)
            {
                foreach (Pawn pawn in pawns)
                {
                    try
                    {
                        // sow-only zone gates (allow sow, hydroponics power)
                        // that live in ExtraRequirements and never run here
                        if (!GrowerCompat.SowSettingsAllow(scanner, cell, pawn.Map))
                        {
                            continue;
                        }

                        // clears WorkGiver_Grower's shared static so the
                        // wanted plant is recomputed for the clicked cell -
                        // without it the menu shows or hides the option based
                        // on whatever unrelated scan ran last
                        GrowerCompat.ResetWantedPlantDef(scanner);

                        if (scanner.HasJobOnCell(pawn, cell, true))
                        {
                            return cell;
                        }
                    }
                    catch
                    {
                        // swallow - this def just doesn't offer a sweep here
                    }
                }
            }

            if (def.scanThings)
            {
                bool firefighting = FireCompat.IsFirefighting(def);
                ThingRequest req = scanner.PotentialWorkThingRequest;

                foreach (Thing thing in thingsHere)
                {
                    foreach (Pawn pawn in pawns)
                    {
                        // HasJobOnThing on a modded scanner can throw on odd
                        // targets - one bad def shouldn't eat the menu
                        try
                        {
                            // WorkGivers write their refusal into this
                            // static while answering - clear it first or we
                            // report whatever the previous def left behind
                            JobFailReason.Clear();

                            // firefighting goes through our own predicate,
                            // which is vanilla's minus the home-area gate
                            bool hasJob = firefighting
                                ? FireCompat.HasFireJob(pawn, thing, true)
                                : scanner.HasJobOnThing(pawn, thing, true);

                            if (hasJob)
                            {
                                failReason = null;
                                failThing = null;
                                return thing;
                            }

                            // req.Accepts is the scoping that keeps this
                            // sane - without it every def in the game gets
                            // to explain itself about every click. Vanilla
                            // scopes its own version the same way.
                            if (failReason == null
                                && JobFailReason.HaveReason
                                && !JobFailReason.Silent
                                && !req.IsUndefined
                                && req.Accepts(thing))
                            {
                                failReason = JobFailReason.Reason;
                                failThing = thing;
                            }
                            else if (failReason == null && FilthCompat.IsFilthCleaning(def))
                            {
                                // WorkGiver_CleanFilth writes no
                                // JobFailReason at all - it just returns
                                // false - so its two gates are invisible
                                // unless we name them ourselves. The
                                // home-area one is why firefoam sprayed
                                // outside the home area offers nothing to
                                // anybody, vanilla's own menu included.
                                failReason = FilthCompat.RefusalReason(thing);
                                if (failReason != null)
                                {
                                    failThing = thing;
                                }
                            }
                            else if (failReason == null && firefighting && thing is Fire)
                            {
                                // HasFireJob is ours, so nothing ever writes
                                // JobFailReason on this path - and fire
                                // suppresses every other option, so with no
                                // reason here the list comes back empty and
                                // the float menu never opens at all. Looks
                                // exactly like a dead right-click. Hardcoded
                                // because vanilla has no string for an option
                                // it never offers.
                                failReason = "unreachable, or already being dealt with";
                                failThing = thing;
                            }
                        }
                        catch
                        {
                            // swallow
                        }
                    }
                }
            }

            return LocalTargetInfo.Invalid;
        }

        // Scoping for the pawn-side feedback. Without it every eligible def
        // in the game gets to complain about every click - the target-side
        // entries dodge that by only speaking when a WorkGiver actually
        // wrote a reason, and this is the same idea one step earlier.
        // Cell-scanned defs (sow) are left out for the same reason
        // FindTargetWithJob leaves them out: bare ground is the normal case,
        // not something worth explaining.
        private static bool WantsSomethingHere(WorkGiverDef def, List<Thing> thingsHere, bool fireHere)
        {
            // on a burning cell fire is all we offer, so "why can't they
            // fight it" is always the question the player is asking
            if (fireHere)
            {
                return true;
            }

            try
            {
                ThingRequest req = ((WorkGiver_Scanner)def.Worker).PotentialWorkThingRequest;
                if (req.IsUndefined)
                {
                    return false;
                }
                foreach (Thing thing in thingsHere)
                {
                    if (req.Accepts(thing))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // swallow - a def that can't say what it wants doesn't
                // get to explain itself either
            }

            return false;
        }

        // First pawn in the selection with something to say, not a tally of
        // all of them - a group is usually refused for one shared reason and
        // naming one of them is enough to explain the click.
        private static string FirstRefusal(List<Pawn> pawns, Map map, WorkGiverDef def, out Pawn who)
        {
            foreach (Pawn pawn in pawns)
            {
                if (pawn == null || pawn.Map != map)
                {
                    continue;
                }

                string reason = PawnValidator.RefusalReason(pawn, def);
                if (reason != null)
                {
                    who = pawn;
                    return reason;
                }
            }

            who = null;
            return null;
        }

        private static List<Pawn> EligiblePawns(List<Pawn> pawns, Map map, WorkGiverDef def)
        {
            var result = new List<Pawn>();
            foreach (Pawn pawn in pawns)
            {
                // same map only - BeginSweep runs against one map's component
                if (pawn == null || pawn.Map != map)
                {
                    continue;
                }
                if (PawnValidator.CanSweep(pawn, def))
                {
                    result.Add(pawn);
                }
            }
            return result;
        }
    }
}
