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

        // Does this WorkGiver have anything at all to do inside the radius?
        // Asked at menu-build time, where the answer decides whether an entry
        // is offered - so it stops at the first hit rather than building a
        // pool nobody has asked for yet.
        public static bool HasTargetInRadius(IntVec3 center, int radius, Map map, WorkGiverDef workGiverDef, Pawn forPawn)
        {
            return FindTargets(center, radius, map, workGiverDef, forPawn, 1).Count > 0;
        }

        // limit > 0 stops the scan as soon as that many targets exist. Zero -
        // the sweep's own call - means "everything", which is what a pool
        // needs.
        public static List<LocalTargetInfo> FindTargets(IntVec3 center, int radius, Map map, WorkGiverDef workGiverDef, Pawn forPawn, int limit = 0)
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
                ScanThings(center, radiusSquared, map, workGiverDef, scanner, forPawn, allowedArea, results, limit);
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

                if (!map.reservationManager.CanReserve(forPawn, cell))
                {
                    continue;
                }

                if (TargetIsBurning(cell, map))
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

                if (!GrowerCompat.CanReachTarget(forPawn, cell, scanner))
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

                results.Add(cell);
            }
        }

        // PotentialWorkThingsGlobal returns NULL on the base class (its IL
        // is literally ldnull/ret) and most WorkGivers never override it -
        // construction and bills included. Iterating that straight was an
        // NRE. Vanilla's JobGiver_Work does the same thing we do here: fall
        // back to the thing lister.
        private static void ScanThings(IntVec3 center, float radiusSquared, Map map, WorkGiverDef workGiverDef, WorkGiver_Scanner scanner, Pawn forPawn, Area allowedArea, List<LocalTargetInfo> results, int limit)
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
