using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Utility
{
    // Finds candidate task targets for an area sweep. SweepManager calls
    // FindTargetsForGroup once per order, with every eligible pawn, and
    // FindTargets for one pawn whenever that pawn runs its pool dry.
    //
    // THE RULE, in the user's words: "The group should drop the pawns who
    // cannot but keep the pawns who can do a certain thing." Until
    // 2026-09-13 the whole order's pool was built off eligiblePawns[0] on
    // the claim that the checks "don't vary by which pawn asked". Every
    // check after the radius does vary - allowed area, IsForbidden(pawn),
    // CanReserve(pawn), CanReach, and the WorkGiver's own HasJobOn* for that
    // pawn (Pick Up And Haul's reads that one pawn's carrying room). On
    // 2026-09-13 "scan HaulToInventory r=50 ... for Abi: 0 targets" killed
    // the order for the whole selection. Architecture doc section 9.
    //
    // So a scan is split by whether a check reads the pawn. Candidates -
    // bounds, radius, the player's filter, sow settings, fire - are worked
    // out once. Only the pawn checks run per pawn, and see AnyoneCan for how
    // few of them that is.
    //
    // Caveat: a handful of WorkGiver_Scanner subclasses override the
    // Potential*Global methods with custom iteration that may not respect
    // the pawn-scoping assumptions here. Worst case for those is an empty
    // result (the * command finds nothing), not a crash - verify in-game
    // per WorkGiverDef as sweep types are added.
    public static class TaskScanner
    {
        // One selected pawn, as the scan sees it. The allowed area is read
        // once per scan rather than once per candidate, and HasWork records
        // that this pawn can do at least one target found so far - which is
        // both the answer the caller wants and what lets AnyoneCan stop
        // asking it early.
        private sealed class GroupMember
        {
            public Pawn Pawn;
            public Area AllowedArea;
            public bool HasWork;
        }

        // Everything a per-pawn check needs, gathered once per scan.
        private sealed class ScanContext
        {
            public Map Map;
            public WorkGiverDef Def;
            public WorkGiver_Scanner Scanner;
            public bool Firefighting;
            public List<GroupMember> Members;

            // how many members still have HasWork false
            public int WithoutWork;
        }

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

        // One pawn's scan - the rescan in AssignNextTask. A group of one runs
        // exactly the checks the old single-pawn scan ran, in the same order.
        //
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
            if (forPawn == null)
            {
                return new List<LocalTargetInfo>();
            }

            return FindTargetsForGroup(center, radius, map, workGiverDef, new List<Pawn> { forPawn }, out _, limit, filter);
        }

        // The first scan of an order. Returns every target at least ONE of
        // these pawns can do, and hands back in `able` the pawns who can do
        // at least one of them, in the order they were given. A pawn left
        // out of `able` can do nothing in the pool and is left out of the
        // order. Added 2026-09-13 - architecture doc section 9.
        //
        // `able` is only complete when limit is 0: a limited scan stops
        // before every pawn has been given the chance to find work.
        //
        // No log line. It used to log "scan <def> ... for <pawn>" on every
        // unlimited call, and the rescan in AssignNextTask made that once per
        // pawn every time a pool ran dry. The caller writes one line per
        // order instead.
        public static List<LocalTargetInfo> FindTargetsForGroup(IntVec3 center, int radius, Map map, WorkGiverDef workGiverDef, List<Pawn> pawns, out List<Pawn> able, int limit = 0, Predicate<Thing> filter = null)
        {
            var results = new List<LocalTargetInfo>();
            able = new List<Pawn>();

            if (map == null || pawns == null)
            {
                return results;
            }

            if (!(workGiverDef?.Worker is WorkGiver_Scanner scanner))
            {
                return results;
            }

            var context = new ScanContext
            {
                Map = map,
                Def = workGiverDef,
                Scanner = scanner,
                Firefighting = FireCompat.IsFirefighting(workGiverDef),
                Members = new List<GroupMember>(pawns.Count)
            };

            foreach (Pawn pawn in pawns)
            {
                // a pawn not standing on this map cannot reach, reserve or
                // work anything on it - the menu was built a moment ago and
                // the selection may have changed since
                if (pawn == null || !pawn.Spawned || pawn.Map != map)
                {
                    continue;
                }

                context.Members.Add(new GroupMember
                {
                    Pawn = pawn,
                    AllowedArea = pawn.playerSettings?.AreaRestrictionInPawnCurrentMap
                });
            }

            context.WithoutWork = context.Members.Count;
            if (context.Members.Count == 0)
            {
                return results;
            }

            float radiusSquared = radius * radius;

            // scanCells and scanThings aren't mutually exclusive on the def,
            // so both branches can contribute to the same pool
            if (workGiverDef.scanCells)
            {
                ScanCells(center, radius, radiusSquared, context, results, limit);
            }

            if (workGiverDef.scanThings && (limit <= 0 || results.Count < limit))
            {
                ScanThings(center, radiusSquared, context, results, limit, filter);
            }

            foreach (GroupMember member in context.Members)
            {
                if (member.HasWork)
                {
                    able.Add(member.Pawn);
                }
            }

            return results;
        }

        // Can anybody in the group do this target? Two passes, and the split
        // is the whole performance story (architecture doc section 9):
        //
        // 1. Every pawn NOT yet known to have work is asked, and each one
        //    that says yes is marked. Not stopping at the first yes is
        //    deliberate - stopping there drops a pawn who could do the work
        //    just because someone earlier in the list said yes first, which
        //    is the rule broken the other way.
        // 2. Only if none of those can, the pawns already known to have work
        //    are asked, stopping at the first yes - one pawn is all a target
        //    needs to belong in the pool.
        //
        // Once every pawn that can work is known, a workable target costs
        // about one pawn's checks. A target nobody can do costs one check per
        // pawn, which is the unavoidable price of saying "nobody".
        private static bool AnyoneCan(ScanContext context, LocalTargetInfo target)
        {
            bool anyone = false;

            if (context.WithoutWork > 0)
            {
                foreach (GroupMember member in context.Members)
                {
                    if (member.HasWork || !PawnCanDo(context, member, target))
                    {
                        continue;
                    }

                    member.HasWork = true;
                    context.WithoutWork--;
                    anyone = true;
                }

                if (anyone)
                {
                    return true;
                }
            }

            foreach (GroupMember member in context.Members)
            {
                if (member.HasWork && PawnCanDo(context, member, target))
                {
                    return true;
                }
            }

            return false;
        }

        // The checks that read the pawn, and only those. Everything that does
        // not - bounds, radius, filter, sow settings, fire - was settled
        // before the target got here. Within each kind the order is the one
        // the single-pawn scan already used; it is one conjunction, so the
        // order changes the cost of a rejection and never the answer.
        private static bool PawnCanDo(ScanContext context, GroupMember member, LocalTargetInfo target)
        {
            Pawn pawn = member.Pawn;
            Map map = context.Map;
            WorkGiver_Scanner scanner = context.Scanner;

            if (!target.HasThing)
            {
                IntVec3 cell = target.Cell;

                if (member.AllowedArea != null && !member.AllowedArea[cell])
                {
                    return false;
                }

                // must precede HasJobOnCell - clears WorkGiver_Grower's
                // shared static so vanilla recomputes the wanted plant for
                // THIS cell (which is also what rejects unzoned cells)
                GrowerCompat.ResetWantedPlantDef(scanner);

                // forced:true - matches how a manually-issued order behaves
                // in vanilla (e.g. bypasses ignoreOtherReservations-style
                // conflicts that only block the AI's own automatic scan)
                if (!scanner.HasJobOnCell(pawn, cell, true))
                {
                    return false;
                }

                // Reservation and reachability come last on purpose: they
                // are the expensive pair - CanReach walks regions - and for
                // the scanCells defs that matter they are also the least
                // selective, because HasJobOnCell for BuildRoofs,
                // RemoveRoofs, ConstructRemoveFloors, ConstructSmoothFloors,
                // ConstructSmoothWalls and CleanClearSnow is a designation or
                // snow-depth lookup that rejects nearly every cell for
                // nothing. Reordered 2026-09-02; paying for reachability on
                // ~1,800 cells first was the difference between a hitch and a
                // freeze, and it matters more now that a cell can be asked of
                // every pawn in the group.
                if (!map.reservationManager.CanReserve(pawn, cell))
                {
                    return false;
                }

                return GrowerCompat.CanReachTarget(pawn, cell, scanner);
            }

            Thing thing = target.Thing;

            if (member.AllowedArea != null && !member.AllowedArea[thing.Position])
            {
                return false;
            }

            if (thing.IsForbidden(pawn))
            {
                return false;
            }

            if (!map.reservationManager.CanReserve(pawn, thing))
            {
                return false;
            }

            if (!GrowerCompat.CanReachTarget(pawn, thing, scanner))
            {
                return false;
            }

            // HasFireJob is vanilla's own check minus the home-area gate -
            // see FireCompat
            return context.Firefighting
                ? FireCompat.HasFireJob(pawn, thing, true)
                : scanner.HasJobOnThing(pawn, thing, true);
        }

        // No global lister for "empty farmable cells" the way listerThings
        // covers things, so this is a real radial scan - GrowerSow (sow
        // crops) is the case that needs it: scanCells=true, scanThings=false,
        // and the target cell has nothing on it (that's the whole point -
        // it's empty farmland waiting for a seed).
        private static void ScanCells(IntVec3 center, int radius, float radiusSquared, ScanContext context, List<LocalTargetInfo> results, int limit)
        {
            Map map = context.Map;

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

                // zone "allow sow" / hydroponics power - lives in
                // WorkGiver_GrowerSow.ExtraRequirements, which only
                // PotentialWorkCellsGlobal calls, so we apply it ourselves.
                // Reads no pawn, so it is asked once here rather than once per
                // pawn - and for GrowerSow it throws out every cell outside a
                // growing zone before any pawn is asked anything.
                if (!GrowerCompat.SowSettingsAllow(context.Scanner, cell, map))
                {
                    continue;
                }

                if (TargetIsBurning(cell, map))
                {
                    continue;
                }

                if (AnyoneCan(context, cell))
                {
                    results.Add(cell);
                }
            }
        }

        // PotentialWorkThingsGlobal returns NULL on the base class (its IL
        // is literally ldnull/ret) and most WorkGivers never override it -
        // construction and bills included. Iterating that straight was an
        // NRE. Vanilla's JobGiver_Work does the same thing we do here: fall
        // back to the thing lister.
        private static void ScanThings(IntVec3 center, float radiusSquared, ScanContext context, List<LocalTargetInfo> results, int limit, Predicate<Thing> filter = null)
        {
            Map map = context.Map;
            WorkGiver_Scanner scanner = context.Scanner;

            // PotentialWorkThingsGlobal takes a pawn, so it is asked once per
            // pawn and the answers merged. Most WorkGivers hand every pawn the
            // same list object off a map lister, and a list already walked is
            // skipped by reference, so the usual case walks one list rather
            // than one per pawn. A thing is considered once however many
            // lists it is in.
            var walkedLists = new List<IEnumerable<Thing>>();
            var seen = new HashSet<Thing>();
            IEnumerable<Thing> listerFallback = null;
            bool fallbackResolved = false;

            foreach (GroupMember member in context.Members)
            {
                IEnumerable<Thing> candidates = scanner.PotentialWorkThingsGlobal(member.Pawn);
                if (candidates == null)
                {
                    if (!fallbackResolved)
                    {
                        fallbackResolved = true;
                        ThingRequest req = scanner.PotentialWorkThingRequest;
                        // ThingsMatching throws on an undefined request
                        if (!req.IsUndefined)
                        {
                            listerFallback = map.listerThings.ThingsMatching(req);
                        }
                    }

                    candidates = listerFallback;
                }

                if (candidates == null || AlreadyWalked(walkedLists, candidates))
                {
                    continue;
                }

                walkedLists.Add(candidates);

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

                    // the burning-target filter would throw out every single
                    // candidate when firefighting - the target IS the fire
                    if (!context.Firefighting && TargetIsBurning(thing, map))
                    {
                        continue;
                    }

                    // after the cheap pawn-free tests, so the set only ever
                    // holds things inside the radius
                    if (!seen.Add(thing))
                    {
                        continue;
                    }

                    if (AnyoneCan(context, thing))
                    {
                        results.Add(thing);
                    }
                }
            }
        }

        private static bool AlreadyWalked(List<IEnumerable<Thing>> walkedLists, IEnumerable<Thing> candidates)
        {
            foreach (IEnumerable<Thing> walked in walkedLists)
            {
                if (ReferenceEquals(walked, candidates))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
