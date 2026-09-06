using RimWorld;
using Verse;
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

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % CheckIntervalTicks != 0)
            {
                return;
            }

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
                    // already pending on a need from an earlier tick -
                    // don't re-pause every 60 ticks while they sleep it off
                    if (sweepManager.IsPaused(pawn))
                    {
                        continue;
                    }

                    if (!NeedIsCritical(pawn, threshold, moodThreshold, restThreshold))
                    {
                        continue;
                    }

                    sweepManager.PauseForNeed(pawn);
                    Logger.Message($"{pawn.LabelShort} paused from sweep: need at/below threshold. Will resume once addressed.");
                }
            }
        }

        // Rest and mood each have their own threshold; hunger and
        // recreation share needThreshold. Rest was split out 2026-09-06 - see
        // the comment on DoNotBeLazySettings.restThreshold.
        private static bool NeedIsCritical(Pawn pawn, float threshold, float moodThreshold, float restThreshold)
        {
            return NeedIsCritical(pawn.needs?.food, threshold)
                || NeedIsCritical(pawn.needs?.joy, threshold)
                || NeedIsCritical(pawn.needs?.rest, restThreshold)
                || NeedIsCritical(pawn.needs?.mood, moodThreshold);
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
    }
}
