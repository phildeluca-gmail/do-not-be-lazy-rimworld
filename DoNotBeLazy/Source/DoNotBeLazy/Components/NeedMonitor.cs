using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using DoNotBeLazy.Core;

namespace DoNotBeLazy.Components
{
    // GameComponent: every 60 ticks, checks pawns currently in an active
    // sweep for critical needs. Hunger and recreation share needThreshold;
    // mood and sleep each have their own, higher one (moodThreshold,
    // restThreshold), because 5% of either is not "getting low" - it is a
    // mental break and a collapse respectively. Sleep was split out
    // 2026-09-06 after a pawn ran fourteen LoadVehicle jobs under 10% rest.
    //
    // A swept pawn is on a FORCED job, and a forced job does not let the
    // vanilla think tree send them to bed. This class is the only thing that
    // will, which is why its thresholds have to be generous rather than
    // last-ditch.
    // If any is critical, the pawn's forced job is ended so vanilla AI takes
    // over - EndCurrentJob defaults to startNewJob:true, so the pawn's
    // normal think tree picks a new job immediately, which for hunger/rest/
    // joy already means "go address it" without us issuing anything
    // explicit. Mood doesn't have a single fix-it job in vanilla; letting
    // the think tree take over is the only lever there too.
    //
    // The sweep itself is paused, not cancelled (SweepManager.PauseForNeed,
    // not RemoveSweep) - per explicit user request, once the pawn's own
    // need-driven job finishes on its own they resume the last-ordered
    // work. This reverses the original design (see architecture doc
    // section 2 history), which cancelled outright to avoid interrupt
    // loops.
    //
    // "resumption only happens on a real job-end event, not a repeated need
    // check, so the interrupt loop can't come back" - that was the old
    // comment here and it was wrong. EndCurrentJob starts a replacement job
    // immediately, so the very next job end is usually that replacement,
    // not the pawn eating. SweepManager resumed on it, we re-paused 60
    // ticks later, and the pawn never got a meal. Worse, once the pause
    // flag was spent the next genuine interrupt read as "something took
    // this pawn" and killed the sweep outright - the reported "they take a
    // break and never come back". Now the job end is only a trigger to
    // re-check: SweepManager asks NeedsSatisfied below and stays paused
    // until it's actually true.
    //
    // RimWorld auto-instantiates every non-abstract GameComponent subclass
    // with a (Game) constructor when a game is created/loaded, so this
    // needs no manual registration.
    public class NeedMonitor : GameComponent
    {
        private const int CheckIntervalTicks = 60;

        // Resume needs a bit more than the threshold that paused them, or a
        // need sitting right on the line thrashes: resume, drop a hair,
        // pause again, one job interrupt per cycle. Mood is the one that
        // actually does this - food and rest jump well clear once addressed.
        // 5 percentage points of CurLevelPercentage.
        public const float ResumeMargin = 0.05f;

        public NeedMonitor(Game game)
        {
        }

        // Throttle for GameComponentTickExceptionThrottled below - one line
        // per pawn per window rather than one per tick, same shape as
        // SweepManager's GiveJobFailedLogExpiry/missingSweepStateLogExpiry.
        // Added 2026-09-27 per user order: "Don't end all pawn's activities
        // because one pawn stopped."
        private const int GameComponentTickExceptionQuietTicks = 250;
        private static readonly Dictionary<Pawn, int> gameComponentTickExceptionLogExpiry = new Dictionary<Pawn, int>();

        private static void GameComponentTickExceptionThrottled(Pawn pawn, Exception ex)
        {
            int now = Find.TickManager.TicksGame;
            if (gameComponentTickExceptionLogExpiry.TryGetValue(pawn, out int expiry) && now < expiry)
            {
                return;
            }

            gameComponentTickExceptionLogExpiry[pawn] = now + GameComponentTickExceptionQuietTicks;
            Logger.Warning($"{pawn.LabelShort}: exception during this pawn's need check - skipping {pawn.LabelShort} this tick and continuing with the rest of the pass: {ex}");
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % CheckIntervalTicks != 0)
            {
                return;
            }

            // ends the ORDER lines of any Drop everything whose queue has
            // run out, 2026-10-02
            DoNotBeLazy.Patches.LoopGizmoPatch.PollDropOrders();

            float threshold = DoNotBeLazyMod.Settings.needThreshold;
            float moodThreshold = DoNotBeLazyMod.Settings.moodThreshold;
            float restThreshold = DoNotBeLazyMod.Settings.restThreshold;

            foreach (Map map in Find.Maps)
            {
                SweepManager sweepManager = map.GetComponent<SweepManager>();
                if (sweepManager == null)
                {
                    continue;
                }

                foreach (Pawn pawn in sweepManager.GetSweptPawns())
                {
                    // Wrapped per-pawn 2026-09-27 per user order: "Don't end
                    // all pawn's activities because one pawn stopped." An
                    // exception checking one pawn's needs used to abort the
                    // rest of this tick's pass for every other swept pawn on
                    // every map - see GameComponentTickExceptionThrottled
                    // above.
                    try
                    {
                        // already pending on a need from an earlier tick -
                        // don't re-pause every 60 ticks while they sleep it off.
                        // Still worth polling rather than skipping outright:
                        // TryForceRestIfStuck catches the case, found live
                        // 2026-09-26/27, where a Rest pause hands the pawn back
                        // to vanilla and vanilla's own JobGiver_GetRest declines
                        // anyway because the timetable says Work - see its
                        // comment in SweepManager.
                        if (sweepManager.IsPaused(pawn))
                        {
                            sweepManager.TryForceRestIfStuck(pawn);
                            // Section 21, added 2026-09-28: retries a stuck
                            // Food pause the same way TryForceRestIfStuck
                            // retries a stuck Rest pause - see its comment
                            // in SweepManager.
                            sweepManager.TryForceEatIfStuck(pawn);
                            // Change C, 2026-10-02: Recreation holding a
                            // pause is sent to recreation the way Rest is
                            // sent to bed.
                            sweepManager.TryForceJoyIfStuck(pawn);
                            sweepManager.WarnIfFoodStuck(pawn);
                            continue;
                        }

                        if (!NeedIsCritical(pawn, threshold, moodThreshold, restThreshold))
                        {
                            continue;
                        }

                        string need = CriticalNeedLabel(pawn, threshold, moodThreshold, restThreshold);
                        sweepManager.PauseForNeed(pawn, need);
                        Logger.Message($"{pawn.LabelShort} paused from sweep: {need} at/below threshold. Will resume once addressed.");
                    }
                    catch (Exception ex)
                    {
                        GameComponentTickExceptionThrottled(pawn, ex);
                    }
                }
            }
        }

        // Rest and mood each have their own threshold; hunger and
        // recreation share needThreshold. Rest was split out 2026-09-06 - see
        // the comment on DoNotBeLazySettings.restThreshold.
        private static bool NeedIsCritical(Pawn pawn, float threshold, float moodThreshold, float restThreshold)
        {
            return CriticalNeedLabel(pawn, threshold, moodThreshold, restThreshold) != null;
        }

        // Which need pushed the pawn under, and how far under it is.
        //
        // Added 2026-09-07 because the pause line said only "need at/below
        // threshold". A Jazzy report - assigned a deconstruct, pulled out one
        // second later, twice running - could not be answered from the log at
        // all: nothing said whether it was food, rest, joy or mood, so there
        // was no way to tell a working pause from a stuck one. The name is
        // the whole diagnostic value of the line.
        //
        // Order matters only for the report: food first because it is the
        // most common and the most actionable. Returns null when nothing is
        // under, which is what NeedIsCritical above tests.
        public static string CriticalNeedLabel(Pawn pawn, float threshold, float moodThreshold, float restThreshold)
        {
            if (NeedIsCritical(pawn?.needs?.food, threshold))
            {
                return Describe("Food", pawn.needs.food);
            }

            if (NeedIsCritical(pawn?.needs?.rest, restThreshold))
            {
                return Describe("Rest", pawn.needs.rest);
            }

            if (NeedIsCritical(pawn?.needs?.joy, threshold))
            {
                return Describe("Recreation", pawn.needs.joy);
            }

            if (NeedIsCritical(pawn?.needs?.mood, moodThreshold))
            {
                return Describe("Mood", pawn.needs.mood);
            }

            return null;
        }

        // The four levels at once, for the line written at each forced-meal
        // retry. Added 2026-10-02 - a pause records only the need that
        // started it, and that is not what keeps the pawn paused.
        public static string LevelsText(Pawn pawn)
        {
            Pawn_NeedsTracker needs = pawn?.needs;
            if (needs == null)
            {
                return "no needs";
            }

            return (needs.food != null ? Describe("Food", needs.food) : "Food n/a")
                + ", " + (needs.rest != null ? Describe("Rest", needs.rest) : "Rest n/a")
                + ", " + (needs.joy != null ? Describe("Recreation", needs.joy) : "Recreation n/a")
                + ", " + (needs.mood != null ? Describe("Mood", needs.mood) : "Mood n/a");
        }

        // Every need still under its resume threshold - what actually keeps
        // a paused pawn paused (NeedsSatisfied tests all four, not just the
        // one the pause started on). "" when none is.
        public static string UnsatisfiedNeedsText(Pawn pawn)
        {
            Pawn_NeedsTracker needs = pawn?.needs;
            if (needs == null)
            {
                return "";
            }

            float threshold = DoNotBeLazyMod.Settings.needThreshold + ResumeMargin;
            float moodThreshold = DoNotBeLazyMod.Settings.moodThreshold + ResumeMargin;
            float restThreshold = DoNotBeLazyMod.Settings.restThreshold + ResumeMargin;

            var parts = new System.Collections.Generic.List<string>();
            if (NeedIsCritical(needs.food, threshold)) parts.Add(Describe("Food", needs.food));
            if (NeedIsCritical(needs.rest, restThreshold)) parts.Add(Describe("Rest", needs.rest));
            if (NeedIsCritical(needs.joy, threshold)) parts.Add(Describe("Recreation", needs.joy));
            if (NeedIsCritical(needs.mood, moodThreshold)) parts.Add(Describe("Mood", needs.mood));
            return string.Join(", ", parts.ToArray());
        }

        // Recreation alone, against the same resume threshold NeedsSatisfied
        // uses (hunger and recreation share needThreshold). Change C.
        public static bool JoySatisfied(Pawn pawn)
        {
            Need joy = pawn?.needs?.joy;
            return joy == null || !NeedIsCritical(joy, DoNotBeLazyMod.Settings.needThreshold + ResumeMargin);
        }

        // Rest alone, against the same resume threshold NeedsSatisfied uses.
        // Added 2026-10-02 (change B).
        public static bool RestSatisfied(Pawn pawn)
        {
            Need rest = pawn?.needs?.rest;
            return rest == null || !NeedIsCritical(rest, DoNotBeLazyMod.Settings.restThreshold + ResumeMargin);
        }

        // Food alone, against the same resume threshold NeedsSatisfied uses.
        public static bool FoodSatisfied(Pawn pawn)
        {
            Need food = pawn?.needs?.food;
            return food == null || !NeedIsCritical(food, DoNotBeLazyMod.Settings.needThreshold + ResumeMargin);
        }

        // "Food 14%" - the level is what says whether a pause was marginal or
        // desperate, and it costs one call to read.
        private static string Describe(string label, Need need)
        {
            return label + " " + (need.CurLevelPercentage * 100f).ToString("F0") + "%";
        }

        // The same question GameComponentTick asks, but for a caller that
        // does not have the thresholds to hand - BeginAreaSweep, deciding
        // whether a pawn it is about to recruit is already under.
        //
        // Added 2026-09-07. Before it, sweep recruitment tested drafted,
        // capable and work-type and nothing else, so a new * order pulled in
        // a pawn at Rest 1% and set them hauling.
        public static string CriticalNeedLabelFor(Pawn pawn)
        {
            if (pawn?.needs == null || DoNotBeLazyMod.Settings == null)
            {
                return null;
            }

            return CriticalNeedLabel(pawn,
                DoNotBeLazyMod.Settings.needThreshold,
                DoNotBeLazyMod.Settings.moodThreshold,
                DoNotBeLazyMod.Settings.restThreshold);
        }

        // What SweepManager asks before resuming a paused pawn. Same four
        // needs, thresholds raised by ResumeMargin - see the comment on it.
        public static bool NeedsSatisfied(Pawn pawn)
        {
            if (pawn?.needs == null)
            {
                return true;
            }

            float threshold = DoNotBeLazyMod.Settings.needThreshold + ResumeMargin;
            float moodThreshold = DoNotBeLazyMod.Settings.moodThreshold + ResumeMargin;
            float restThreshold = DoNotBeLazyMod.Settings.restThreshold + ResumeMargin;

            return !NeedIsCritical(pawn, threshold, moodThreshold, restThreshold);
        }

        private static bool NeedIsCritical(Need need, float threshold)
        {
            return need != null && need.CurLevelPercentage <= threshold;
        }

        // Section 21, go-eat backstop - built 2026-09-27. Verbatim ask:
        // "Best mood buff to lowest." Called by SweepManager the instant a
        // pawn is paused for Food (revised the same day - see the section 21
        // note - not on a delay), so this has to pick a candidate on its own
        // rather than lean on FoodUtility.TryFindBestFoodSourceFor, which
        // picks by FoodOptimality (nutrition and distance), not by mood.
        //
        // Bounded on purpose - this runs once per pause, never per tick:
        // candidates come from Verse.ThingListGroupHelper's own maintained
        // list for ThingRequestGroup.FoodSourceNotPlantOrTree (confirmed by
        // decompile that this group already includes
        // Building_NutrientPasteDispenser, so dispensers are covered without
        // extra code) plus the pawn's own inventory - never a raw map scan.
        // Predator-hunt and harvest-a-plant food sources are deliberately
        // NOT replicated (out of scope for a hunger backstop); pack-animal
        // inventory is also skipped for the same reason.
        //
        // Verified against lib\Assembly-CSharp.dll:
        // - FoodUtility.WillEat(Thing) covers food policy (Pawn_FoodRestrictionTracker),
        //   ideology venerated-animal and royal title gates. Ordinary ideology
        //   moral reactions (AteHumanMeat, AteNonCannibalFood, etc.) are not a
        //   block - they show up as thoughts, which is exactly what
        //   FoodUtility.MoodFromIngesting sums, so the mood-based pick already
        //   steers away from them on its own.
        // - Forbidden: Thing.IsForbidden(Pawn) (RimWorld.ForbidUtility).
        // - Reachable + reservable: Pawn.CanReserveAndReach(LocalTargetInfo,
        //   PathEndMode, Danger) (Verse.AI.ReservationUtility) for a map item;
        //   a nutrient paste dispenser is checked by hand the same way
        //   FoodUtility.BestFoodSourceOnMap's own validator does (power,
        //   feedstock, InteractionCell reachable) since it is not reserved
        //   the normal way.
        // - Freshness: CompRottable.Stage != RotStage.Fresh is excluded -
        //   FoodUtility.IsNotFresh()/IsDessicated() are private helpers this
        //   build does not expose, so the rot stage is read directly instead.
        // - Job: JobDefOf.Ingest via JobMaker.MakeJob, count from
        //   FoodUtility.WillIngestStackCountOf(pawn, foodDef,
        //   FoodUtility.GetNutrition(...)) - the same two calls
        //   RimWorld.JobGiver_GetFood itself makes. Social propriety
        //   (Thing.IsSociallyProper, prisoners eating apart from colonists) is
        //   NOT checked - out of scope; note it if this ever reaches a
        //   prisoner.
        //
        // Mood is FoodUtility.MoodFromIngesting(pawn, candidate, foodDef) -
        // itself just ThoughtsFromIngesting(...).Sum(t => t.thought.stages[0].baseMoodEffect),
        // read verbatim off FoodUtility.cs. Highest mood wins; a tie (most
        // often two plain meals with no thoughts at all, both 0) goes to
        // whichever is nearer. Because the winner is simply the maximum,
        // negative-mood food is only ever picked when every candidate is
        // negative - no separate branch needed for that rule.
        public static Job TryFindBestFoodJob(Pawn pawn, out int candidateCount, out float bestMoodEffect, out string declineReason)
        {
            candidateCount = 0;
            bestMoodEffect = 0f;
            declineReason = null;

            if (pawn?.needs?.food == null || pawn.Map == null)
            {
                declineReason = "no food need, or not spawned on a map";
                return null;
            }

            Thing bestThing = null;
            ThingDef bestDef = null;
            float bestScore = float.NegativeInfinity;
            int bestDistance = int.MaxValue;
            int candidates = 0;

            void Consider(Thing candidate, bool inInventory)
            {
                ThingDef def = FoodUtility.GetFinalIngestibleDef(candidate);
                if (def == null || !def.IsNutritionGivingIngestible)
                {
                    return;
                }

                if (!pawn.WillEat(candidate, pawn) || candidate.IsForbidden(pawn))
                {
                    return;
                }

                CompRottable rot = candidate.TryGetComp<CompRottable>();
                if (rot != null && rot.Stage != RotStage.Fresh)
                {
                    return;
                }

                if (inInventory)
                {
                    if (!candidate.IngestibleNow)
                    {
                        return;
                    }
                }
                else if (candidate is Building_NutrientPasteDispenser dispenser)
                {
                    if (dispenser.powerComp == null || !dispenser.powerComp.PowerOn
                        || !dispenser.HasEnoughFeedstockInHoppers()
                        || !dispenser.InteractionCell.Standable(dispenser.Map)
                        || !pawn.CanReach(dispenser.InteractionCell, PathEndMode.OnCell, Danger.Some))
                    {
                        return;
                    }
                }
                else
                {
                    if (!candidate.IngestibleNow
                        || !pawn.CanReserveAndReach(candidate, PathEndMode.ClosestTouch, Danger.Some))
                    {
                        return;
                    }
                }

                candidates++;
                float mood = FoodUtility.MoodFromIngesting(pawn, candidate, def);
                int distance = inInventory ? 0 : (pawn.Position - candidate.Position).LengthManhattan;
                if (mood > bestScore || (mood == bestScore && distance < bestDistance))
                {
                    bestScore = mood;
                    bestDistance = distance;
                    bestThing = candidate;
                    bestDef = def;
                }
            }

            if (pawn.inventory?.innerContainer != null)
            {
                foreach (Thing item in pawn.inventory.innerContainer)
                {
                    Consider(item, true);
                }
            }

            foreach (Thing thing in pawn.Map.listerThings.ThingsMatching(ThingRequest.ForGroup(ThingRequestGroup.FoodSourceNotPlantOrTree)))
            {
                Consider(thing, false);
            }

            candidateCount = candidates;

            if (bestThing == null)
            {
                declineReason = candidates == 0
                    ? "no food this pawn will eat, reach or reserve"
                    : "no viable candidate scored";
                return null;
            }

            bestMoodEffect = bestScore;
            float nutrition = FoodUtility.GetNutrition(pawn, bestThing, bestDef);
            Job job = JobMaker.MakeJob(JobDefOf.Ingest, bestThing);
            job.count = FoodUtility.WillIngestStackCountOf(pawn, bestDef, nutrition);
            return job;
        }
    }
}
