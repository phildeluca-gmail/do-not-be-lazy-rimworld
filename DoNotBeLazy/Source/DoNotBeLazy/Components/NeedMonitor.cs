using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using DoNotBeLazy.Core;

namespace DoNotBeLazy.Components
{
    // GameComponent: every 60 ticks, checks pawns currently in an active
    // sweep for critical needs. Hunger, recreation, mood and sleep each have their own
    // threshold (hungerThreshold, recreationThreshold, moodThreshold,
    // restThreshold; hunger and recreation were one slider until 2026-10-22,
    // section 28), because 5% of either is not "getting low" - it is a
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
            // Section 28 review 2026-10-22: the safety net keeps static state
            // (pass tick, quiet timers, Pawn keys). A new or loaded game
            // constructs this component, so start it clean.
            SafetyNet.Reset();
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

            float hungerThreshold = DoNotBeLazyMod.Settings.safetyHungerThreshold;
            float recreationThreshold = DoNotBeLazyMod.Settings.safetyRecreationThreshold;
            float moodThreshold = DoNotBeLazyMod.Settings.moodThreshold;
            float restThreshold = DoNotBeLazyMod.Settings.safetySleepThreshold;

            // Section 28: colonists not on a * order, at most once per
            // SafetyNet.PassIntervalTicks.
            SafetyNet.Run();

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

                        if (!NeedIsCritical(pawn, hungerThreshold, recreationThreshold, moodThreshold, restThreshold))
                        {
                            continue;
                        }

                        string need = CriticalNeedLabel(pawn, hungerThreshold, recreationThreshold, moodThreshold, restThreshold);
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

        // Each need has its own threshold. Rest was split out 2026-09-06 - see
        // the comment on DoNotBeLazySettings.safetySleepThreshold; hunger and
        // recreation were split 2026-10-22 (section 28).
        private static bool NeedIsCritical(Pawn pawn, float hungerThreshold, float recreationThreshold, float moodThreshold, float restThreshold)
        {
            return CriticalNeedLabel(pawn, hungerThreshold, recreationThreshold, moodThreshold, restThreshold) != null;
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
        public static string CriticalNeedLabel(Pawn pawn, float hungerThreshold, float recreationThreshold, float moodThreshold, float restThreshold)
        {
            if (NeedIsCritical(pawn?.needs?.food, hungerThreshold))
            {
                return Describe("Food", pawn.needs.food);
            }

            if (NeedIsCritical(pawn?.needs?.rest, restThreshold))
            {
                return Describe("Rest", pawn.needs.rest);
            }

            if (NeedIsCritical(pawn?.needs?.joy, recreationThreshold))
            {
                return Describe("Recreation", pawn.needs.joy);
            }

            if (NeedIsCritical(pawn?.needs?.mood, moodThreshold, false))
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

            float hungerThreshold = ResumeAt(DoNotBeLazyMod.Settings.safetyHungerThreshold);
            float recreationThreshold = ResumeAt(DoNotBeLazyMod.Settings.safetyRecreationThreshold);
            float moodThreshold = DoNotBeLazyMod.Settings.moodThreshold + ResumeMargin;
            float restThreshold = ResumeAt(DoNotBeLazyMod.Settings.safetySleepThreshold);

            var parts = new System.Collections.Generic.List<string>();
            if (NeedIsCritical(needs.food, hungerThreshold)) parts.Add(Describe("Food", needs.food));
            if (NeedIsCritical(needs.rest, restThreshold)) parts.Add(Describe("Rest", needs.rest));
            if (NeedIsCritical(needs.joy, recreationThreshold)) parts.Add(Describe("Recreation", needs.joy));
            if (NeedIsCritical(needs.mood, moodThreshold, false)) parts.Add(Describe("Mood", needs.mood));
            return string.Join(", ", parts.ToArray());
        }

        // Recreation alone, against the same resume threshold NeedsSatisfied
        // uses. Change C.
        public static bool JoySatisfied(Pawn pawn)
        {
            Need joy = pawn?.needs?.joy;
            return joy == null || !NeedIsCritical(joy, ResumeAt(DoNotBeLazyMod.Settings.safetyRecreationThreshold));
        }

        // Rest alone, against the same resume threshold NeedsSatisfied uses.
        // Added 2026-10-02 (change B).
        public static bool RestSatisfied(Pawn pawn)
        {
            Need rest = pawn?.needs?.rest;
            return rest == null || !NeedIsCritical(rest, ResumeAt(DoNotBeLazyMod.Settings.safetySleepThreshold));
        }

        // Food alone, against the same resume threshold NeedsSatisfied uses.
        public static bool FoodSatisfied(Pawn pawn)
        {
            Need food = pawn?.needs?.food;
            return food == null || !NeedIsCritical(food, ResumeAt(DoNotBeLazyMod.Settings.safetyHungerThreshold));
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
                DoNotBeLazyMod.Settings.safetyHungerThreshold,
                DoNotBeLazyMod.Settings.safetyRecreationThreshold,
                DoNotBeLazyMod.Settings.moodThreshold,
                DoNotBeLazyMod.Settings.safetySleepThreshold);
        }

        // What SweepManager asks before resuming a paused pawn. Same four
        // needs, thresholds raised by ResumeMargin - see the comment on it.
        public static bool NeedsSatisfied(Pawn pawn)
        {
            if (pawn?.needs == null)
            {
                return true;
            }

            float hungerThreshold = ResumeAt(DoNotBeLazyMod.Settings.safetyHungerThreshold);
            float recreationThreshold = ResumeAt(DoNotBeLazyMod.Settings.safetyRecreationThreshold);
            float moodThreshold = DoNotBeLazyMod.Settings.moodThreshold + ResumeMargin;
            float restThreshold = ResumeAt(DoNotBeLazyMod.Settings.safetySleepThreshold);

            return !NeedIsCritical(pawn, hungerThreshold, recreationThreshold, moodThreshold, restThreshold);
        }

        // Section 30 (2026-10-24): one threshold per need, the safety* settings,
        // for the order path and the safety net alike. A threshold of 0 means
        // that need is switched off on both paths (the safety net's Low() has
        // always read it so); ResumeAt keeps 0 at 0 so a switched-off need is
        // never "unsatisfied" either.
        // zeroIsOff is false for mood only: its 0 still means "pause at 0%".
        private static bool NeedIsCritical(Need need, float threshold, bool zeroIsOff = true)
        {
            return need != null && (threshold > 0f || !zeroIsOff) && need.CurLevelPercentage <= threshold;
        }

        private static float ResumeAt(float threshold)
        {
            return threshold > 0f ? threshold + ResumeMargin : 0f;
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
        // read verbatim off FoodUtility.cs. Within the best tier present,
        // highest mood wins; a tie (most often two plain meals with no
        // thoughts at all, both 0) goes to whichever is nearer.
        // Section 28: the near pick looks within 16 cells, straight-line.
        public const int NearMealCells = 16;

        // Section 28 tiers: 0 meal (cooked, not nutrient paste), 1 acceptable
        // raw (non-meal food whose eating carries no mood penalty), 2 nutrient
        // paste (the meal or the dispenser), 3 raw with a mood penalty.
        // Verified by reflection on lib\Assembly-CSharp.dll: FoodPreferability
        // runs ... RawTasty, MealTerrible, MealAwful, MealSimple, MealFine,
        // MealLavish; ThingDefOf.MealNutrientPaste is a ThingDef, not a preference.
        private static int FoodTier(Thing candidate, ThingDef def, float mood)
        {
            FoodPreferability pref = def.ingestible?.preferability ?? FoodPreferability.Undefined;
            if (candidate is Building_NutrientPasteDispenser || def == ThingDefOf.MealNutrientPaste)
            {
                return 2;
            }

            // Insect jelly (ordered 2026-10-22): a meal when this pawn's mood
            // effect from eating it is 0 or more, otherwise raw with penalty.
            if (def == ThingDefOf.InsectJelly)
            {
                return mood >= 0f ? 0 : 3;
            }

            if (pref >= FoodPreferability.MealTerrible)
            {
                return 0;
            }

            // Below RawBad (DesperateOnly etc., admitted only when Starving): last tier.
            if (pref < FoodPreferability.RawBad)
            {
                return 3;
            }

            return mood >= 0f ? 1 : 3;
        }

        private static string FoodTierName(int tier)
        {
            switch (tier)
            {
                case 0: return "meal";
                case 1: return "acceptable raw";
                case 2: return "nutrient paste";
                default: return "raw with penalty";
            }
        }

        // The decline reason without its live "(left out: ...)" counts, for
        // deduplicating decline log lines (the counts still print).
        public static string DeclineKey(string reason)
        {
            int i = reason?.IndexOf(" (left out: ") ?? -1;
            if (i < 0) { return reason; }
            int j = reason.IndexOf(')', i);
            return j < 0 ? reason.Substring(0, i) : reason.Substring(0, i) + reason.Substring(j + 1);
        }

        private enum FoodFilterResult { Ok, Rejected, Corpse, Drug, BelowFloor, Rotting }

        // The filter shared by the meal search (Consider) and the carried-food
        // check in FindMealDroppingCargo, so a carried item is dropped only if
        // the search would accept it. Order matches vanilla's: corpse/drug,
        // preferability floor, WillEat/forbidden, rot stage.
        private static FoodFilterResult FoodFilter(Pawn pawn, Thing candidate, ThingDef def, bool desperate, FoodPreferability minPref)
        {
            if (def == null || !def.IsNutritionGivingIngestible)
            {
                return FoodFilterResult.Rejected;
            }

            // Never drugs or corpses, whatever the hunger (vanilla's
            // allowDrug / allowCorpse are caller choices; ours are false).
            if (candidate is Corpse) { return FoodFilterResult.Corpse; }
            if (def.IsDrug) { return FoodFilterResult.Drug; }

            // A dispenser's own def has no ingestible; vanilla asks WillEat
            // about the paste it dispenses instead.
            bool isDispenser = candidate is Building_NutrientPasteDispenser;
            FoodPreferability pref = isDispenser
                ? ThingDefOf.MealNutrientPaste.ingestible.preferability
                : def.ingestible.preferability;
            if (pref < minPref)
            {
                return FoodFilterResult.BelowFloor;
            }

            bool willEat = isDispenser
                ? pawn.WillEat(ThingDefOf.MealNutrientPaste, pawn)
                : pawn.WillEat(candidate, pawn);
            if (!willEat || candidate.IsForbidden(pawn))
            {
                return FoodFilterResult.Rejected;
            }

            CompRottable rot = candidate.TryGetComp<CompRottable>();
            // Vanilla: Dessicated always refused; other rot stages only
            // refused when the pawn is not desperate (Starving).
            if (rot != null
                && (rot.Stage == RotStage.Dessicated || (rot.Stage != RotStage.Fresh && !desperate)))
            {
                return FoodFilterResult.Rotting;
            }

            return FoodFilterResult.Ok;
        }

        public static Job TryFindBestFoodJob(Pawn pawn, out int candidateCount, out float bestMoodEffect, out string declineReason, out string choiceNote, Thing skip = null)
        {
            candidateCount = 0;
            bestMoodEffect = 0f;
            declineReason = null;
            choiceNote = null;

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

            // Section 28 food choice (tiers ordered 2026-10-22): the best
            // (lowest-numbered) tier present anywhere on the map wins, so a
            // meal 40 cells away beats rice 15 cells away. Only within that
            // tier does distance apply: candidates within NearMealCells, best
            // mood then nearest; none within range, best mood then nearest
            // map-wide. Straight-line squared cell distance (no pathing per
            // candidate); inventory food is distance 0, so always near.
            const int TierCount = 4;
            Thing[] nearThing = new Thing[TierCount];
            ThingDef[] nearDef = new ThingDef[TierCount];
            float[] nearMood = new float[TierCount];
            int[] nearDist = new int[TierCount];
            Thing[] anyThing = new Thing[TierCount];
            ThingDef[] anyDef = new ThingDef[TierCount];
            float[] anyMood = new float[TierCount];
            int[] anyDist = new int[TierCount];
            int bestTier = int.MaxValue;
            int skipped = 0;
            int leftCorpses = 0;
            int leftDrugs = 0;
            int leftBelowFloor = 0;
            int leftRotting = 0;

            // Vanilla FoodUtility.BestFoodSourceOnMap floor: a Starving pawn is
            // "desperate" (floor DesperateOnly); otherwise the floor is RawBad,
            // which drops DesperateOnly and DesperateOnlyForHumanlikes (corpses,
            // chocolate, hay, etc). Vanilla's MealAwful floor
            // for a merely Hungry pawn is NOT used: it would remove every raw
            // food, and raw food is tiers 1 and 3 of section 28.
            bool desperate = pawn.needs.food.CurCategory == HungerCategory.Starving;
            FoodPreferability minPref = desperate ? FoodPreferability.DesperateOnly : FoodPreferability.RawBad;

            void Consider(Thing candidate, bool inInventory)
            {
                // Section 27: targets a vanilla Ingest stalled on.
                if (skip != null && skip == candidate)
                {
                    skipped++;
                    return;
                }

                ThingDef def = FoodUtility.GetFinalIngestibleDef(candidate);
                switch (FoodFilter(pawn, candidate, def, desperate, minPref))
                {
                    case FoodFilterResult.Corpse: leftCorpses++; return;
                    case FoodFilterResult.Drug: leftDrugs++; return;
                    case FoodFilterResult.BelowFloor: leftBelowFloor++; return;
                    case FoodFilterResult.Rotting: leftRotting++; return;
                    case FoodFilterResult.Rejected: return;
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
                    if ((dispenser.Faction != pawn.Faction && dispenser.Faction != pawn.HostFaction)
                        || dispenser.powerComp == null || !dispenser.powerComp.PowerOn
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
                int distance = inInventory ? 0 : (pawn.Position - candidate.Position).LengthHorizontalSquared;
                int tier = FoodTier(candidate, def, mood);
                if (anyThing[tier] == null
                    || mood > anyMood[tier]
                    || (mood == anyMood[tier] && distance < anyDist[tier]))
                {
                    anyThing[tier] = candidate;
                    anyDef[tier] = def;
                    anyMood[tier] = mood;
                    anyDist[tier] = distance;
                }

                if (distance > NearMealCells * NearMealCells)
                {
                    return;
                }

                if (nearThing[tier] == null
                    || mood > nearMood[tier]
                    || (mood == nearMood[tier] && distance < nearDist[tier]))
                {
                    nearThing[tier] = candidate;
                    nearDef[tier] = def;
                    nearMood[tier] = mood;
                    nearDist[tier] = distance;
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

            bool fellBackToBestTier = false;
            for (int t = 0; t < TierCount; t++)
            {
                if (anyThing[t] == null) { continue; }
                bestTier = t;
                if (nearThing[t] != null)
                {
                    bestThing = nearThing[t];
                    bestDef = nearDef[t];
                    bestScore = nearMood[t];
                    bestDistance = nearDist[t];
                }
                else
                {
                    bestThing = anyThing[t];
                    bestDef = anyDef[t];
                    bestScore = anyMood[t];
                    bestDistance = anyDist[t];
                    fellBackToBestTier = true;
                }
                break;
            }

            if (bestThing == null)
            {
                declineReason = candidates == 0
                    ? "no food this pawn will eat, reach or reserve"
                    : "no viable candidate scored";
                if (skipped > 0)
                {
                    declineReason += $" ({skipped} stalled target(s) skipped)";
                }
                declineReason += $" (left out: {leftCorpses} corpses, {leftDrugs} drugs, {leftBelowFloor} below floor, {leftRotting} rotting)";
                return null;
            }

            bestMoodEffect = bestScore;
            bool carried = bestThing.holdingOwner != null && bestThing.holdingOwner == pawn.inventory?.innerContainer;
            choiceNote = $"pawn at {pawn.Position}, food at {(carried ? "own inventory" : bestThing.Position.ToString())}"
                + (carried ? "" : $" ({Math.Sqrt(bestDistance):F0} cells straight-line)")
                + (fellBackToBestTier ? $", best tier on the map, none of it within {NearMealCells} cells so best mood, then nearest map-wide" : $", best tier on the map, then within {NearMealCells} cells best mood, then nearest")
                + ", tier: " + FoodTierName(bestTier);
            float nutrition = FoodUtility.GetNutrition(pawn, bestThing, bestDef);
            Job job = JobMaker.MakeJob(JobDefOf.Ingest, bestThing);
            job.count = FoodUtility.WillIngestStackCountOf(pawn, bestDef, nutrition);
            return job;
        }

        // True when the meal search would accept this carried item.
        private static bool CarriedFoodAcceptable(Pawn pawn, Thing carried)
        {
            bool desperate = pawn.needs?.food != null && pawn.needs.food.CurCategory == HungerCategory.Starving;
            FoodPreferability minPref = desperate ? FoodPreferability.DesperateOnly : FoodPreferability.RawBad;
            return FoodFilter(pawn, carried, FoodUtility.GetFinalIngestibleDef(carried), desperate, minPref) == FoodFilterResult.Ok;
        }

        // Section 28 (review 2026-10-22). The meal search both the order
        // pause and the safety net use. Differences from TryFindBestFoodJob:
        // (1) what the pawn is carrying is dropped only once a meal is found,
        // or when the carried thing is itself edible and the first search
        // found nothing (then it is searched again so the dropped meal is a
        // candidate); a carried Pawn is never dropped. (2) if the skipped
        // stalled meal was the only food, the search runs again without the
        // skip rather than leaving the pawn to starve.
        public static Job FindMealDroppingCargo(Pawn pawn, out int candidateCount, out float bestMoodEffect, out string declineReason, out string choiceNote, out string dropped, Thing skip = null)
        {
            dropped = "";

            // A warden or doctor carrying a meal to someone else: that meal
            // is never ours to drop or eat (section 28, doctoring exempt).
            JobDef curDef = pawn.jobs?.curJob?.def;
            if (curDef == JobDefOf.FeedPatient || curDef == JobDefOf.DeliverFood)
            {
                candidateCount = 0;
                bestMoodEffect = 0f;
                choiceNote = null;
                declineReason = "carrying a meal to someone else (" + curDef.defName + ")";
                return null;
            }

            Job job = TryFindBestFoodJob(pawn, out candidateCount, out bestMoodEffect, out declineReason, out choiceNote, skip);
            string retryNote = "";
            if (job == null && skip != null)
            {
                job = TryFindBestFoodJob(pawn, out candidateCount, out bestMoodEffect, out declineReason, out choiceNote);
                if (job != null)
                {
                    retryNote = "; the stalled meal was the only food, so it was tried again";
                }
            }

            Thing carried = pawn.carryTracker?.CarriedThing;
            if (carried != null && !(carried is Pawn))
            {
                if (job != null)
                {
                    dropped = SweepManager.DropCurrentJobCargo(pawn);
                }
                else if (carried.IngestibleNow && CarriedFoodAcceptable(pawn, carried))
                {
                    // the carried item is food itself; put it on the ground
                    // so the search can see it
                    dropped = SweepManager.DropCurrentJobCargo(pawn);
                    job = TryFindBestFoodJob(pawn, out candidateCount, out bestMoodEffect, out declineReason, out choiceNote);
                }
            }

            if (job != null && retryNote.Length > 0)
            {
                choiceNote += retryNote;
            }

            // cargo was put down but no meal job came of it: say so
            if (job == null && dropped.Length > 0)
            {
                declineReason += dropped;
            }

            return job;
        }
    }
}
