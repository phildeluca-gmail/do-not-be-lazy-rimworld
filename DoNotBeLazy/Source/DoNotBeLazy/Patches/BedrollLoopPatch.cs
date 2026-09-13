using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DoNotBeLazy.Components;
using HarmonyLib;
using Verse;
using Verse.AI;
using Logger = DoNotBeLazy.Core.Logger;

namespace DoNotBeLazy.Patches
{
    // Postfix on UseBedrolls.JobGiver_TakeBedBack.TryGiveJob - the second
    // Harmony patch in this mod aimed at another mod's type, after
    // VehicleMenuPatch, and the first one that refuses a job rather than
    // adding to a menu.
    //
    // WHAT IS BROKEN. Read off the shipped 1.5 UseBedrolls.dll by reflection
    // on 2026-09-11, not remembered:
    //
    //     internal class UseBedrolls.JobGiver_TakeBedBack : ThinkNode_JobGiver
    //     {
    //         public override float GetPriority(Pawn pawn)
    //         protected override Job TryGiveJob(Pawn pawn)
    //         {
    //             if (!pawn.IsColonistPlayerControlled) return null;
    //             if (pawn.Map.GetComponent<PlacedBedsMapComponent>()
    //                     .placedBeds.TryGetValue(pawn, out var value)
    //                 && pawn.CanReserve(value, 1, -1, null, false))
    //                 return new Job(JobDefOf.TakeBedroll, value)
    //                        { ignoreForbidden = true };
    //             return null;
    //         }
    //     }
    //
    // The dictionary is UseBedrolls.PlacedBedsMapComponent.placedBeds, a
    // public Dictionary<Verse.Pawn, RimWorld.Building_Bed> on a
    // Verse.MapComponent. Nothing in that job giver asks whether the bed is
    // still on the map. Jerbear has an entry pointing at Bedroll9043738,
    // which is neither spawned nor held by anything spawned, and
    // Pawn.CanReserve says yes to it anyway - so the job is handed out, ends
    // at once, and is handed out again on the next think. RimWorld stops the
    // pawn at ten jobs in one tick, prints "started 10 jobs in one tick", and
    // throws away that pawn's whole job queue with it: 1,440 queued
    // vehicle-loading jobs died that way in twenty minutes on 2026-09-10.
    //
    // WHAT THIS DOES. Two things, and they are decided by two DIFFERENT
    // tests - see dnbl-architecture.md, "Refusing on the wrong test -
    // corrected 2026-09-13", which is the authority here:
    //
    //   1. REFUSE, whenever the recorded bed is not itself standing on a
    //      map: bed.Destroyed || !bed.Spawned. A TakeBedroll job aimed at a
    //      thing that is not on the map ends the tick it starts, whatever
    //      is holding it, so this covers destroyed, in an inventory, in a
    //      carry tracker, minified, in a caravan and in a container alike.
    //      __result is set to null. Every other case - a live bedroll, a
    //      null result, Use Bedrolls not installed - is left exactly as it
    //      was.
    //   2. CLEAR that pawn's entry from placedBeds, by reflection, in two
    //      cases only: the bed no longer exists anywhere (Destroyed, or
    //      !SpawnedOrAnyParentSpawned), or THIS pawn is already holding it.
    //      PlacedBedsMapComponent.ExposeData saves that dictionary, so the
    //      removal outlives the session once the game is saved.
    //
    // Anything ELSE holding the bedroll - a hauler carrying it to install
    // it, a caravan, a shelf - is refused but NOT cleared. That record is
    // still true and the bedroll will be spawned again shortly; Use
    // Bedrolls only writes the record when the owner places the bedroll, so
    // a record thrown away here is never written again and the pawn loses
    // their bed for good. Refusing costs a few reference comparisons and
    // starts no job, so there is no loop to pay for while the carry lasts.
    //
    // The 2026-09-11 build used !SpawnedOrAnyParentSpawned as the refusal
    // test. A bedroll in a pawn's inventory has a spawned parent - the pawn
    // - so that test passed and the job was allowed. Javelin looped on
    // Bedroll9044091 over 170 times on 2026-09-13, and each burst ended ten
    // of his queued LoadVehicle jobs as Incompletable.
    //
    // Everything is soft, the same way VehicleCompat and VehicleMenuPatch
    // are soft about Vehicle Framework: no UseBedrolls.dll in lib\, no type
    // from it named at compile time, every handle resolving to null when the
    // mod is absent, and TryPatch doing nothing at all in that case.
    //
    // JobLoopWatch is NOT involved. It stays a pure diagnostic that reports
    // loops and never alters a job; this is the only place in the mod that
    // intervenes in one.
    public static class BedrollLoopPatch
    {
        private const string JobGiverTypeName = "UseBedrolls.JobGiver_TakeBedBack";
        private const string ComponentTypeName = "UseBedrolls.PlacedBedsMapComponent";
        private const string PlacedBedsFieldName = "placedBeds";

        // How far up the chain of holders above a bedroll to walk before
        // giving up. Bounded on purpose: the chain belongs to code we do not
        // own and an unbounded walk up it is a hang waiting to happen. The
        // real chains here are two or three links long - a bedroll in an
        // inventory is Pawn_InventoryTracker then Pawn, and a minified one
        // adds a MinifiedThing in between.
        private const int MaxHolderSteps = 16;

        private static readonly Type JobGiverType = AccessTools.TypeByName(JobGiverTypeName);
        private static readonly Type ComponentType = AccessTools.TypeByName(ComponentTypeName);
        private static readonly FieldInfo PlacedBedsField = BuildPlacedBedsField();

        // One log line per pawn-and-bed pair, keyed by their thing ids. The
        // refusal itself can fire on every think until the record is cleared,
        // and a line per call is the exact shape the standing rule forbids.
        private static readonly HashSet<string> announced = new HashSet<string>();

        // Same reason, for the two failure paths below: a throw inside a
        // think node would otherwise print on every think, forever.
        private static bool postfixThrowReported;
        private static bool removalThrowReported;

        // Called by hand from the mod constructor after PatchAll, because
        // PatchAll cannot resolve a type this assembly does not reference.
        public static void TryPatch(Harmony harmony)
        {
            if (JobGiverType == null)
            {
                // Use Bedrolls is not installed. Nothing to say - most
                // players do not have it.
                return;
            }

            // TryGiveJob is protected and is DECLARED on
            // JobGiver_TakeBedBack, which overrides
            // ThinkNode_JobGiver.TryGiveJob. DeclaredOnly is what keeps this
            // off the base class: Harmony patches the exact MethodInfo it is
            // handed, and patching ThinkNode_JobGiver instead would catch
            // every job giver in the game while missing this one. That
            // mistake shipped Standard Cargo for Vehicles with no user
            // interface and it failed silently, which is why the success
            // line below exists.
            MethodInfo target = JobGiverType.GetMethod(
                "TryGiveJob",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            if (target == null)
            {
                Logger.Warning($"{JobGiverTypeName}.TryGiveJob not found - a colonist whose placed bedroll has left the map will keep being sent to fetch it, ten jobs a tick, and will lose any queued work.");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(BedrollLoopPatch), nameof(Postfix)));
                Logger.Message("Use Bedrolls detected - patched UseBedrolls.JobGiver_TakeBedBack.TryGiveJob so a bedroll that has left the map cannot loop a pawn.");
            }
            catch (Exception e)
            {
                // A failed patch here costs the bedroll loop fix and nothing
                // else. It must never take the mod down with it.
                Logger.Error("could not patch UseBedrolls.JobGiver_TakeBedBack.TryGiveJob, a stale bedroll record will still loop a pawn: " + e);
            }
        }

        // The parameter name matches the job giver's own (pawn), which is how
        // Harmony binds it. __result is the Job that Use Bedrolls decided on.
        //
        // This runs on a think node, so it is called for every colonist that
        // reaches this giver, several times a second. It must never throw and
        // must never log on the ordinary path.
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            try
            {
                if (__result == null || pawn == null)
                {
                    return;
                }

                // LocalTargetInfo.Thing is null when the target is a cell.
                // Use Bedrolls only ever aims this job at a Building_Bed, so
                // a null here is not our case and is left alone.
                Thing bed = __result.targetA.Thing;
                if (bed == null)
                {
                    return;
                }

                // The working case, and by far the common one: the bedroll is
                // standing on a map, where the pawn can walk to it and pick
                // it up. Nothing to do.
                if (!bed.Destroyed && bed.Spawned)
                {
                    return;
                }

                // Refuse first, and in every case below. The job cannot
                // complete against a thing that is not on the map, whatever
                // is holding it; only the clearing depends on what that is.
                __result = null;

                bool heldByThisPawn;
                Thing spawnedHolder;
                FindHolder(bed, pawn, out heldByThisPawn, out spawnedHolder);

                // "Gone" is the 2026-09-11 test, kept, and now only deciding
                // whether to clear rather than whether to refuse.
                bool gone = bed.Destroyed || (!heldByThisPawn && !bed.SpawnedOrAnyParentSpawned);
                bool cleared = false;

                if (gone || heldByThisPawn)
                {
                    cleared = ForgetPlacedBed(pawn, bed);
                }

                string key = pawn.ThingID + "/" + bed.ThingID;
                if (!announced.Add(key))
                {
                    return;
                }

                // ONE line per pawn and bed, on the first refusal only.
                // JobLoopWatch.DescribeThing gives the id and the whereabouts
                // in the same form the loop warnings already use, so the two
                // can be read together. What that form cannot say is WHO is
                // holding the bedroll, and that is the one fact needed to
                // tell a stuck record from a bedroll in transit, so this line
                // says it.
                string why;
                string outcome;

                if (gone)
                {
                    why = "The bedroll no longer exists - it is destroyed, or it is inside something that is not on the map itself.";
                    outcome = cleared
                        ? "Cleared that pawn's entry from PlacedBedsMapComponent.placedBeds, so it will not be offered again."
                        : "Could NOT clear the entry from PlacedBedsMapComponent.placedBeds, so the job will keep being offered and refused.";
                }
                else if (heldByThisPawn)
                {
                    why = $"The bedroll is being carried by {pawn.LabelShort} already, so there is nothing to fetch.";
                    outcome = cleared
                        ? "Cleared that pawn's entry from PlacedBedsMapComponent.placedBeds, so it will not be offered again."
                        : "Could NOT clear the entry from PlacedBedsMapComponent.placedBeds, so the job will keep being offered and refused.";
                }
                else
                {
                    why = $"The bedroll is being carried by {DescribeHolder(spawnedHolder)}, so there is nothing to fetch yet.";
                    outcome = "Kept that pawn's entry in PlacedBedsMapComponent.placedBeds on purpose - it is still true and the bedroll should be back on the map shortly. The job stays refused until it is.";
                }

                Logger.Warning($"refused a Use Bedrolls TakeBedroll job for {pawn.LabelShort}: " +
                    $"{JobLoopWatch.DescribeThing(bed)}. {why} {outcome}");
            }
            catch (Exception e)
            {
                if (postfixThrowReported)
                {
                    return;
                }

                postfixThrowReported = true;
                Logger.Error("the Use Bedrolls bedroll loop postfix threw and is being ignored from here on: " + e);
            }
        }

        // Walks the chain of holders above a thing and answers two questions
        // at once: is the pawn the job was offered to somewhere in it, and
        // what is the first thing in it that is actually on the map.
        //
        // Verse.Thing.ParentHolder is a Verse.IThingHolder, and
        // Verse.IThingHolder has a ParentHolder of its own - both read off
        // lib\Assembly-CSharp.dll on 2026-09-13, not remembered. Verse.Pawn
        // implements IThingHolder, and both Pawn_InventoryTracker and
        // Pawn_CarryTracker report the pawn as their ParentHolder, so a
        // bedroll in either is found in two steps. A MinifiedThing in
        // between is walked straight through.
        //
        // Verse.ThingOwnerUtility.GetFirstSpawnedParentThing would answer the
        // second question in one call and it does exist in this build. It is
        // not used because its body was not read, and a signature you could
        // not read is not a signature you may assume.
        private static void FindHolder(Thing thing, Pawn pawn, out bool heldByPawn, out Thing spawnedHolder)
        {
            heldByPawn = false;
            spawnedHolder = null;

            IThingHolder holder = thing.ParentHolder;

            for (int step = 0; step < MaxHolderSteps && holder != null; step++)
            {
                if (ReferenceEquals(holder, pawn))
                {
                    heldByPawn = true;
                }

                Thing holderThing = holder as Thing;
                if (spawnedHolder == null && holderThing != null && holderThing.Spawned)
                {
                    spawnedHolder = holderThing;
                }

                holder = holder.ParentHolder;
            }
        }

        // Names whatever is holding the bedroll, for the one warning line.
        // Null when the walk found nothing on the map - a caravan or a world
        // object holds things through something that is not a Thing at all.
        private static string DescribeHolder(Thing holder)
        {
            return holder == null
                ? "something that is not on this map"
                : holder.LabelShort + " [" + holder.ThingID + "]";
        }

        // Removes this pawn's stale record from
        // UseBedrolls.PlacedBedsMapComponent.placedBeds. Returns whether an
        // entry was actually taken out.
        //
        // Reached through the non-generic Verse.Map.GetComponent(Type), which
        // exists alongside the generic one and takes a Type this assembly
        // does not reference. Dictionary<Pawn, Building_Bed> implements
        // System.Collections.IDictionary, so Contains, the indexer and Remove
        // all work without naming either type parameter.
        private static bool ForgetPlacedBed(Pawn pawn, Thing bed)
        {
            if (ComponentType == null || PlacedBedsField == null || pawn == null || pawn.Map == null)
            {
                return false;
            }

            try
            {
                MapComponent component = pawn.Map.GetComponent(ComponentType);
                if (component == null)
                {
                    return false;
                }

                IDictionary placedBeds = PlacedBedsField.GetValue(component) as IDictionary;
                if (placedBeds == null || !placedBeds.Contains(pawn))
                {
                    return false;
                }

                // Only remove the record we just refused a job for. If it
                // points somewhere else, something changed it between the
                // job giver reading it and this postfix running, and taking
                // it out would destroy a record that may be sound.
                if (!ReferenceEquals(placedBeds[pawn], bed))
                {
                    return false;
                }

                placedBeds.Remove(pawn);
                return true;
            }
            catch (Exception e)
            {
                if (!removalThrowReported)
                {
                    removalThrowReported = true;
                    Logger.Warning($"could not clear a stale entry from {ComponentTypeName}.{PlacedBedsFieldName} - the job will be refused every time instead of stopping at the source: " + e.Message);
                }

                return false;
            }
        }

        private static FieldInfo BuildPlacedBedsField()
        {
            if (ComponentType == null)
            {
                return null;
            }

            FieldInfo field = AccessTools.Field(ComponentType, PlacedBedsFieldName);
            if (field == null)
            {
                Logger.Warning($"{ComponentTypeName}.{PlacedBedsFieldName} not found - a stale bedroll job can still be refused, but the record behind it cannot be cleared.");
            }

            return field;
        }
    }
}
