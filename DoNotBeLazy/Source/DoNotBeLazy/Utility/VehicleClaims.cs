using System.Collections.Generic;
using System.Linq;
using DoNotBeLazy.Jobs;
using Verse;
using Verse.AI;

namespace DoNotBeLazy.Utility
{
    // Over-fetch fix, dnbl-architecture.md section 45. Vehicle Framework's own
    // count of what other pawns are fetching only sees its own loading jobs,
    // never DNBL's StuffAndHaul, so 26 pawns were each handed a stack for a
    // 50-unit request. This tally records, per vehicle and per ThingDef, how
    // many units each pawn has been handed or is carrying for it, so the
    // hand-out and the pickup can subtract what the others already hold.
    //
    // Not saved: after a load nothing is in transit until jobs restart. A
    // claim is dropped when its job ends (ReleasePawn, from the driver's
    // finish action) and, as a backstop against a missed ending, when it
    // is read and its pawn is dead, gone, or no longer running a stuffing
    // job at that vehicle after a grace period (a job handed out but still
    // queued has no driver yet).
    public static class VehicleClaims
    {
        private class Claim
        {
            public int units;
            public int tick;
        }

        // Ticks a claim survives with no live stuffing job behind it: a
        // queued job has no driver until its turn.
        private const int GraceTicks = 1800;

        // vehicle id -> load-list entry (the matching TransferableOneWay, found the
        // way VehicleCompat.FindEntry matches, so stuff and quality are kept
        // apart) -> pawn -> claim
        private static readonly Dictionary<int, Dictionary<object, Dictionary<Pawn, Claim>>> table
            = new Dictionary<int, Dictionary<object, Dictionary<Pawn, Claim>>>();

        // Sets this pawn's claim for (vehicle, def) to `units`; 0 removes it.
        public static void Set(Pawn pawn, Thing vehicle, object def, int units)
        {
            if (pawn == null || vehicle == null || def == null)
            {
                return;
            }
            if (units <= 0)
            {
                Remove(pawn, vehicle.thingIDNumber, def);
                return;
            }
            if (!table.TryGetValue(vehicle.thingIDNumber, out var byDef))
            {
                byDef = new Dictionary<object, Dictionary<Pawn, Claim>>();
                table[vehicle.thingIDNumber] = byDef;
            }
            if (!byDef.TryGetValue(def, out var byPawn))
            {
                byPawn = new Dictionary<Pawn, Claim>();
                byDef[def] = byPawn;
            }
            byPawn[pawn] = new Claim { units = units, tick = Find.TickManager.TicksGame };
        }

        private static void Remove(Pawn pawn, int vehicleId, object def)
        {
            if (table.TryGetValue(vehicleId, out var byDef) && byDef.TryGetValue(def, out var byPawn))
            {
                byPawn.Remove(pawn);
                if (byPawn.Count == 0)
                {
                    byDef.Remove(def);
                }
                if (byDef.Count == 0)
                {
                    table.Remove(vehicleId);
                }
            }
        }

        // Set by SweepManager.WrapForVehicleStuffing: the last pawn it asked
        // got no job only because other pawns already carry what the vehicle
        // wants. A refusal like that is not a failure and never ends an order.
        public static bool LastRefusedByClaims;

        // Forgets everything; called on game load.
        public static void Clear()
        {
            table.Clear();
        }

        // Drops this pawn's claims on one vehicle only (its next queued job
        // may be for another). Returns the units released, for the log.
        public static int ReleasePawn(Pawn pawn, Thing vehicle)
        {
            int released = 0;
            if (vehicle == null || !table.TryGetValue(vehicle.thingIDNumber, out var byDef))
            {
                return 0;
            }
            foreach (object key in byDef.Keys.ToList())
            {
                if (byDef[key].TryGetValue(pawn, out Claim c))
                {
                    released += c.units;
                    Remove(pawn, vehicle.thingIDNumber, key);
                }
            }
            return released;
        }

        // Units of the entry `def` other pawns than `self` have been handed or carry
        // for the vehicle. Stale claims are pruned on the way.
        public static int OthersHold(Pawn self, Thing vehicle, object def)
        {
            if (vehicle == null || def == null
                || !table.TryGetValue(vehicle.thingIDNumber, out var byDef)
                || !byDef.TryGetValue(def, out var byPawn))
            {
                return 0;
            }

            int total = 0;
            int now = Find.TickManager.TicksGame;
            foreach (KeyValuePair<Pawn, Claim> pair in byPawn.ToList())
            {
                Pawn other = pair.Key;
                if (IsStale(other, vehicle, pair.Value, now))
                {
                    Remove(other, vehicle.thingIDNumber, def);
                    continue;
                }
                if (other != self)
                {
                    total += pair.Value.units;
                }
            }
            return total;
        }

        private static bool IsStale(Pawn other, Thing vehicle, Claim claim, int now)
        {
            if (other == null || other.Destroyed || other.Dead || other.Map != vehicle.Map)
            {
                return true;
            }
            // A tick count below the claim's means a different game was loaded.
            if (now < claim.tick)
            {
                return true;
            }
            Job cur = other.CurJob;
            bool running = cur != null && cur.def == DnblJobDefOf.StuffAndHaul && cur.targetB.Thing == vehicle;
            return !running && now - claim.tick > GraceTicks;
        }
    }
}
