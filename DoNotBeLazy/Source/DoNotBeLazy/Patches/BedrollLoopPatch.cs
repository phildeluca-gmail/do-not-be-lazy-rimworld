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
    // WHAT THIS DOES. Two things, in this order, and only when the bed the
    // job is aimed at is gone:
    //
    //   1. Sets __result to null, so the pawn is not sent to fetch a bedroll
    //      that does not exist. Every other case - a live bedroll, a null
    //      result, Use Bedrolls not installed - is left exactly as it was.
    //   2. Removes that pawn's entry from placedBeds, by reflection, so the
    //      job giver stops producing the job at all rather than being
    //      refused forever. PlacedBedsMapComponent.ExposeData saves that
    //      dictionary, so the removal outlives the session once the game is
    //      saved.
    //
    // "Gone" is Thing.Destroyed, or !Thing.SpawnedOrAnyParentSpawned - the
    // second covers a thing that was never destroyed but is no longer
    // anywhere a pawn can walk to. A bedroll in a pawn's inventory or in a
    // caravan is held by something spawned and is NOT touched; that is the
    // ordinary, working case Use Bedrolls exists for.
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
                // on the map, or is inside something that is. Nothing to do.
                if (!bed.Destroyed && bed.SpawnedOrAnyParentSpawned)
                {
                    return;
                }

                __result = null;
                bool cleared = ForgetPlacedBed(pawn, bed);

                string key = pawn.ThingID + "/" + bed.ThingID;
                if (!announced.Add(key))
                {
                    return;
                }

                // ONE line per pawn and bed, on the first refusal only.
                // JobLoopWatch.DescribeThing gives the id and the whereabouts
                // in the same form the loop warnings already use, so the two
                // can be read together.
                Logger.Warning($"refused a Use Bedrolls TakeBedroll job for {pawn.LabelShort}: " +
                    $"{JobLoopWatch.DescribeThing(bed)} is destroyed or off the map. " +
                    (cleared
                        ? "Cleared that pawn's entry from PlacedBedsMapComponent.placedBeds, so it will not be offered again."
                        : "Could NOT clear the entry from PlacedBedsMapComponent.placedBeds, so the job will keep being offered and refused."));
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
