using System.Collections.Generic;
using DoNotBeLazy.Comps;
using Verse;
using Verse.AI;
using Logger = DoNotBeLazy.Core.Logger;

namespace DoNotBeLazy.Jobs
{
    // "Drop everything" gizmo - dnbl-architecture.md section 22 subsection
    // "Drop everything", built 2026-09-27. Vanilla has a queued job for
    // dropping worn apparel (RimWorld.JobDriver_RemoveApparel) and for
    // dropping the primary weapon (RimWorld.JobDriver_DropEquipment), but
    // nothing equivalent for a single stack in Pawn_InventoryTracker -
    // Patches/LoopGizmoPatch.cs queues one of these per inventory stack.
    //
    // Matches RimWorld.ITab_Pawn_Gear.InterfaceDrop's own inventory-item
    // branch (verified by ildasm against lib\Assembly-CSharp.dll):
    // `pawn.inventory.innerContainer.TryDrop(t, pawn.Position, pawn.Map,
    // ThingPlaceMode.Near, out _)` - dropped near the pawn. ThingOwner's
    // TryDrop takes no forbid parameter at all, and nothing here forbids
    // the result afterward either, so the dropped stack is left
    // unforbidden, the same as the gear tab's own drop button.
    public class JobDriver_DropInventoryStack : JobDriver
    {
        // Nothing to reserve - the target is already sitting in this
        // pawn's own inventory, not out on the map. Same reasoning as
        // RimWorld.JobDriver_RemoveApparel and RimWorld.JobDriver_DropEquipment,
        // both of which return true unconditionally here.
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            Toil toil = new Toil();
            toil.initAction = () =>
            {
                Thing thing = job.GetTarget(TargetIndex.A).Thing;
                if (thing == null || thing.Destroyed || !pawn.inventory.innerContainer.Contains(thing))
                {
                    // The stack was gone by the time its queued turn came -
                    // eaten from inventory, spoiled away, moved some other
                    // way. This is an expected race for a queued drop, not
                    // a defect - end quietly, logged once (this toil never
                    // repeats for the same job).
                    Logger.Message($"{pawn.LabelShort}: inventory stack for a queued drop was already gone, skipping it");
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, out Thing _);
                pawn.GetComp<CompDnblCargo>()?.Unregister(thing);
                Logger.Message($"{pawn.LabelShort} dropped {thing.LabelCap} from inventory (drop everything)");
                EndJobWith(JobCondition.Succeeded);
            };
            yield return toil;
        }
    }
}
