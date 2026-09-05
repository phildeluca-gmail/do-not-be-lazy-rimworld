using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Utility
{
    // Finds candidate task targets for an area sweep. SweepManager calls
    // this once per sweep initiation and caches the result rather than
    // rescanning every tick.
    //
    // forPawn drives the scan: WorkGiver_Scanner.PotentialWorkThingsGlobal,
    // HasJobOnThing/HasJobOnCell are inherently pawn-scoped vanilla APIs.
    // Any single sweep-eligible pawn works as the driver for building the
    // shared candidate list; SweepManager still re-checks CanReserve per
    // assignee at the moment it actually claims a target for a *different*
    // pawn.
    //
    // Caveat: a handful of WorkGiver_Scanner subclasses override the
    // Potential*Global methods with custom iteration that may not respect
    // the pawn-scoping assumptions here. Worst case for those is an empty
    // result (the * command finds nothing), not a crash - verify in-game
    // per WorkGiverDef as sweep types are added.
    public static class TaskScanner
    {
        // FloatMenuPatch bails on the whole right-click if the clicked cell
        // is on fire, but that only covers the one cell the player aimed at -
        // a sweep radius routinely spans tiles the player never looked at.
        // Nothing else filters fire: GrowerSow/GrowerHarvest don't check it
        // (a scorched-but-mature plant, or a cell burnt back to bare ground,
        // still passes HasJobOnCell), and vanilla's own fire guards live in
        // PotentialWorkCellsGlobal, which this mod never calls.
        //
        // Deliberate divergence from vanilla: vanilla skips an *entire* grow
        // zone when it contains static fire (Zone_Growing.ContainsStaticFire).
        // Filtering per target instead means the unburnt part of a field is
        // still workable, which is better behaviour for an explicit player
        // order, and avoids re-walking every zone cell once per scanned cell.
        public static bool TargetIsBurning(LocalTargetInfo target, Map map)
        {
            if (target.HasThing)
            {
                Thing thing = target.Thing;
                return thing != null && (thing.IsBurning() || thing.Position.ContainsStaticFire(map));
            }

            return target.Cell.ContainsStaticFire(map);
        }

        // limit > 0 stops the scan as soon as that many targets exist. Zero -
        // the sweep's own call - means "everything", which is what a pool
        // needs.
        // filter, when given, narrows the pool to the specific order the
        // player gave rather than everything the WorkGiver can do - see
        // PlantCompat, where one def serves both "chop wood" and "cut
        // plants". Only ScanThings applies it: it is a question about a
        // Thing, and a scanCells def has no Thing to ask about.
        public static List<LocalTargetInfo> FindTargets(IntVec3 center, int radius, Map map, WorkGiverDef workGiverDef, Pawn forPawn, int limit = 0, Predicate<Thing> filter = null)
        {
            var results = new List<LocalTargetInfo>();

            if (map == null || forPawn == null)
            {
                return results;
            }

            if (!(workGiverDef?.Worker is WorkGiver_Scanner scanner))
            {
                return results;
            }

            float radiusSquared = radius * radius;
            Area allowedArea = forPawn.playerSettings?.AreaRestrictionInPawnCurrentMap;

            // one line per scan rather than per cell - a radius-16 scan
            // touches ~800 cells and per-cell logging would bury the signal
            int before = results.Count;

            // scanCells and scanThings aren't mutually exclusive on the def,
            // so both branches can contribute to the same pool
            if (workGiverDef.scanCells)
            {
                ScanCells(center, radius, radiusSquared, map, scanner, forPawn, allowedArea, results, limit);
            }

            if (workGiverDef.scanThings && (limit <= 0 || results.Count < limit))
            {
                ScanThings(center, radiusSquared, map, workGiverDef, scanner, forPawn, allowedArea, results, limit, filter);
            }

            // a limited scan is a menu probe, and one of those runs per
            // cleaning def per right-click - the caller logs the answer, not
            // the asking
            if (limit <= 0)
            {
                Core.Logger.Message($"scan {workGiverDef.defName} r={radius} at {center} for {forPawn.LabelShort}: {results.Count - before} targets");
            }

            return results;
        }

        // No global lister for "empty farmable cells" the way listerThings
        // covers things, so this is a real radial scan - GrowerSow (sow
        // crops) is the case that needs it: scanCells=true, scanThings=false,
        // and the target cell has nothing on it (that's the whole point -
        // it's empty farmland waiting for a seed).
        private static void ScanCells(IntVec3 center, int radius, float radiusSquared, Map map, WorkGiver_Scanner scanner, Pawn forPawn, Area allowedArea, List<LocalTargetInfo> results, int limit)
        {
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, radius, true))
            {
                if (limit > 0 && results.Count >= limit)
                {
                    return;
                }

                if (!cell.InBounds(map))
                {
                    continue;
                }

                if ((cell - center).LengthHorizontalSquared > radiusSquared)
                {
                    continue;
                }

                if (allowedArea != null && !allowedArea[cell])
                {
                    continue;
                }

                // zone "allow sow" / hydroponics power - lives in
                // WorkGiver_GrowerSow.ExtraRequirements, which only
                // PotentialWorkCellsGlobal calls, so we apply it ourselves
                if (!GrowerCompat.SowSettingsAllow(scanner, cell, map))
                {
                    continue;
                }

                if (TargetIsBurning(cell, map))
                {
                    continue;
                }

                // must precede HasJobOnCell - clears WorkGiver_Grower's
                // shared static so vanilla recomputes the wanted plant for
                // THIS cell (which is also what rejects unzoned cells)
                GrowerCompat.ResetWantedPlantDef(scanner);

                // forced:true - matches how a manually-issued order behaves
                // in vanilla (e.g. bypasses ignoreOtherReservations-style
                // conflicts that only block the AI's own automatic scan)
                if (!scanner.HasJobOnCell(forPawn, cell, true))
                {
                    continue;
                }

                // Reservation and reachability come last on purpose, and the
                // ordering above is otherwise cheapest-and-most-selective
                // first. Every check here is part of one conjunction, so the
                // result is identical whatever the order; what changes is how
                // much work a rejected cell costs. These two are the
                // expensive pair - CanReach walks regions - and for the
                // scanCells defs that matter they are also the least
                // selective, because HasJobOnCell for BuildRoofs,
                // RemoveRoofs, ConstructRemoveFloors, ConstructSmoothFloors,
                // ConstructSmoothWalls and CleanClearSnow is a designation or
                // snow-depth lookup that rejects nearly every cell for
                // nothing.
                //
                // Reordered 2026-09-02, when Construction joined the float
                // menu's radius probe: a right-click that finds nothing runs
                // this loop once per scanCells def over ~1,800 cells at the
                // configured radius of 24, and paying for reachability on all
                // of them first was the difference between a hitch and a
                // freeze.
                if (!map.reservationManager.CanReserve(forPawn, cell))
                {
                    continue;
                }

                if (!GrowerCompat.CanReachTarget(forPawn, cell, scanner))
                {
                    continue;
                }

                results.Add(cell);
            }
        }

        // PotentialWorkThingsGlobal returns NULL on the base class (its IL
        // is literally ldnull/ret) and most WorkGivers never override it -
        // construction and bills included. Iterating that straight was an
        // NRE. Vanilla's JobGiver_Work does the same thing we do here: fall
        // back to the thing lister.
        private static void ScanThings(IntVec3 center, float radiusSquared, Map map, WorkGiverDef workGiverDef, WorkGiver_Scanner scanner, Pawn forPawn, Area allowedArea, List<LocalTargetInfo> results, int limit, Predicate<Thing> filter = null)
        {
            // the burning-target filter below would throw out every single
            // candidate here - the target IS the fire
            bool firefighting = FireCompat.IsFirefighting(workGiverDef);

            IEnumerable<Thing> candidates = scanner.PotentialWorkThingsGlobal(forPawn);
            if (candidates == null)
            {
                ThingRequest req = scanner.PotentialWorkThingRequest;
                // ThingsMatching throws on an undefined request
                if (!req.IsUndefined)
                {
                    candidates = map.listerThings.ThingsMatching(req);
                }
            }

            if (candidates == null)
            {
                return;
            }

            foreach (Thing thing in candidates)
            {
                if (limit > 0 && results.Count >= limit)
                {
                    return;
                }

                if (thing == null || thing.Map != map)
                {
                    continue;
                }

                // asked before anything expensive: this is the player's own
                // order, so a thing that fails it is never a candidate no
                // matter what the WorkGiver thinks
                if (filter != null && !filter(thing))
                {
                    continue;
                }

                if ((thing.Position - center).LengthHorizontalSquared > radiusSquared)
                {
                    continue;
                }

                if (allowedArea != null && !allowedArea[thing.Position])
                {
                    continue;
                }

                if (thing.IsForbidden(forPawn))
                {
                    continue;
                }

                if (!firefighting && TargetIsBurning(thing, map))
                {
                    continue;
                }

                if (!map.reservationManager.CanReserve(forPawn, thing))
                {
                    continue;
                }

                if (!GrowerCompat.CanReachTarget(forPawn, thing, scanner))
                {
                    continue;
                }

                // HasFireJob is vanilla's own check minus the home-area
                // gate - see FireCompat
                bool hasJob = firefighting
                    ? FireCompat.HasFireJob(forPawn, thing, true)
                    : scanner.HasJobOnThing(forPawn, thing, true);
                if (!hasJob)
                {
                    continue;
                }

                results.Add(thing);
            }
        }
    }
}
