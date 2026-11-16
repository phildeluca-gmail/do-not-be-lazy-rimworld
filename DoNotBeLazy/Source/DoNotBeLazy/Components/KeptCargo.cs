using System.Collections.Generic;
using System.Linq;
using DoNotBeLazy.Comps;
using DoNotBeLazy.Jobs;
using RimWorld;
using Verse;
using Verse.AI;
using Logger = DoNotBeLazy.Core.Logger;

namespace DoNotBeLazy.Components
{
    // Q44(b), 2026-11-15, user: "fix b) if it means the carried item eventually
    // leaves inventory, yes". When a stuffing job is interrupted, the cargo in
    // the pack is kept (still registered as DNBL cargo) so the next job can
    // deliver it. This component is the guarantee that it does leave the pack:
    //   after DeliverAfterTicks: a pawn with no active order and no forced job
    //     is sent to storage with a cargo-only stuffing job (the same job
    //     SweepManager.TryStartCargoDelivery gives a passed-over pawn; a pawn
    //     inside an order is left to that path);
    //   after DropAfterTicks: whatever is still held is unregistered and
    //     dropped beside the pawn.
    // One line when cargo is kept (written by the stuffing driver), one when
    // a delivery is started, one for the outcome. Checked once a second.
    // Not saved: a load re-arms every pawn that still holds registered cargo.
    public class KeptCargo : GameComponent
    {
        internal const int DeliverAfterTicks = 600;
        internal const int DropAfterTicks = 1800;
        private const int CheckIntervalTicks = 60;

        private static readonly Dictionary<Pawn, int> keptSince = new Dictionary<Pawn, int>();
        private static readonly HashSet<Pawn> deliveryStarted = new HashSet<Pawn>();
        private static readonly HashSet<Pawn> dropFailLogged = new HashSet<Pawn>();

        public KeptCargo(Game game)
        {
            keptSince.Clear();
            deliveryStarted.Clear();
            dropFailLogged.Clear();
        }

        internal static void Keep(Pawn pawn, int stacks, string condition)
        {
            if (pawn == null)
            {
                return;
            }

            if (!keptSince.ContainsKey(pawn))
            {
                keptSince[pawn] = Find.TickManager.TicksGame;
            }

            Logger.Message($"{pawn.LabelShort}: stuffing job interrupted ({condition}); {stacks} stack(s) of cargo stay in the pack for the next job to deliver - sent to storage after {DeliverAfterTicks} ticks if still held, dropped after {DropAfterTicks}");
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            foreach (Pawn pawn in PawnsFinder.AllMapsCaravansAndTravelingTransportPods_Alive_Colonists.ToList())
            {
                if (HeldCargo(pawn).Count > 0 && !keptSince.ContainsKey(pawn))
                {
                    keptSince[pawn] = Find.TickManager.TicksGame;
                    Logger.Message($"{pawn.LabelShort}: holds registered cargo after a load; sent to storage after {DeliverAfterTicks} ticks if still held, dropped after {DropAfterTicks}");
                }
            }
        }

        private static List<Thing> HeldCargo(Pawn pawn)
        {
            var held = new List<Thing>();
            CompDnblCargo comp = pawn?.GetComp<CompDnblCargo>();
            if (comp == null || pawn.inventory == null)
            {
                return held;
            }

            foreach (Thing thing in comp.Carrying())
            {
                if (pawn.inventory.innerContainer.Contains(thing))
                {
                    held.Add(thing);
                }
            }

            return held;
        }

        public override void GameComponentTick()
        {
            if (keptSince.Count == 0 || Find.TickManager.TicksGame % CheckIntervalTicks != 0)
            {
                return;
            }

            foreach (Pawn pawn in keptSince.Keys.ToList())
            {
                try
                {
                    Check(pawn, Find.TickManager.TicksGame - keptSince[pawn]);
                }
                catch (System.Exception ex)
                {
                    keptSince.Remove(pawn);
                    Logger.Warning($"KeptCargo check for {pawn?.LabelShort} threw {ex.GetType().Name}: {ex.Message}; no longer tracked");
                }
            }
        }

        private static void Check(Pawn pawn, int age)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead)
            {
                keptSince.Remove(pawn);
                return;
            }

            List<Thing> held = HeldCargo(pawn);
            if (held.Count == 0)
            {
                Logger.Message($"{pawn.LabelShort}: kept cargo has left the pack after {age} ticks (delivered or put down by a job)");
                End(pawn);
                return;
            }

            Job cur = pawn.jobs?.curJob;
            if (cur != null && cur.def == DnblJobDefOf.StuffAndHaul && JobDriver_StuffAndHaul.IsCargoDeliveryJob(pawn, cur))
            {
                return; // the delivery is under way
            }

            if (age >= DropAfterTicks && pawn.Spawned && pawn.Map != null)
            {
                string names = string.Join(", ", held.Select(t => t.LabelCap));
                CompDnblCargo comp = pawn.GetComp<CompDnblCargo>();
                int dropped = 0;
                foreach (Thing thing in held)
                {
                    ItemLedger.Ensure(pawn, thing);
                    if (pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, out _))
                    {
                        comp.Unregister(thing);
                        dropped++;
                        ItemLedger.Dropped(pawn, thing, $"kept cargo not delivered within {DropAfterTicks} ticks", true);
                    }
                }

                if (dropped == held.Count)
                {
                    Logger.Message($"{pawn.LabelShort}: kept cargo ({names}) was not delivered within {DropAfterTicks} ticks - dropped {dropped} of {held.Count} stack(s) beside the pawn and unregistered them");
                    End(pawn);
                }
                else if (dropFailLogged.Add(pawn) || dropped > 0)
                {
                    Logger.Message($"{pawn.LabelShort}: kept cargo ({names}) was not delivered within {DropAfterTicks} ticks - dropped {dropped} of {held.Count} stack(s); the rest could not be dropped and are retried every second (this line once per pawn)");
                }
                return;
            }

            if (age < DeliverAfterTicks || deliveryStarted.Contains(pawn))
            {
                return;
            }

            if (!pawn.Spawned || pawn.Downed || pawn.Drafted || (cur != null && cur.playerForced))
            {
                return; // try again next check; the drop deadline still applies
            }

            if (pawn.Map?.GetComponent<SweepManager>() is SweepManager sweeps && sweeps.TryGetActiveSweep(pawn, out _))
            {
                return; // inside an order, SweepManager.TryStartCargoDelivery is the delivery path
            }

            Job job = JobMaker.MakeJob(DnblJobDefOf.StuffAndHaul, held[0]);
            job.count = held[0].stackCount;
            job.haulMode = HaulMode.ToCellStorage;
            job.playerForced = true;
            deliveryStarted.Add(pawn);
            bool taken = pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            Logger.Message($"{pawn.LabelShort}: kept cargo ({held.Count} stack(s)) still held {age} ticks after the interrupt - " + (taken ? "sent to storage with a cargo-only stuffing job" : "the cargo-only stuffing job was refused; it is dropped at the deadline"));
        }

        private static void End(Pawn pawn)
        {
            keptSince.Remove(pawn);
            deliveryStarted.Remove(pawn);
            dropFailLogged.Remove(pawn);
        }
    }
}
