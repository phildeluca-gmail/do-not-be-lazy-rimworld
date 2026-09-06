using UnityEngine;
using Verse;

namespace DoNotBeLazy.Core
{
    // Per architecture doc 3.4: sweep radius, need-interrupt threshold,
    // and whether to draw the radius overlay on hover.
    public class DoNotBeLazySettings : ModSettings
    {
        public int sweepRadius = 16;

        // Which end of the sweep the work starts from. True is centre-out
        // (rank by distance from the clicked cell), false is the pre-08-27
        // rule (rank by distance from the pawn). Defaults true so an
        // existing save keeps the behaviour it has.
        public bool centerOutOrder = true;

        public float needThreshold = 0.05f;
        public float moodThreshold = 0.10f;

        // Rest gets its own, higher threshold for the same reason mood does:
        // 5% rest is not "getting tired", it is a pawn about to collapse
        // where they stand. Added 2026-09-06 after a `* PackVehicle` sweep
        // ran a pawn through fourteen consecutive LoadVehicle jobs with sleep
        // under 10% - the sweep was behaving exactly as configured, and the
        // configuration was wrong.
        //
        // This matters more for a sweep than for ordinary work: a swept pawn
        // is on a FORCED job, and a forced job does not let the think tree
        // send them to bed. NeedMonitor is the only thing that will.
        public float restThreshold = 0.10f;
        public bool showSweepOverlay = true;
        public bool verboseLogging = false;
        public bool jobDiagnostics = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref sweepRadius, "sweepRadius", 16);
            Scribe_Values.Look(ref centerOutOrder, "centerOutOrder", true);
            Scribe_Values.Look(ref needThreshold, "needThreshold", 0.05f);
            Scribe_Values.Look(ref moodThreshold, "moodThreshold", 0.10f);
            Scribe_Values.Look(ref restThreshold, "restThreshold", 0.10f);
            Scribe_Values.Look(ref showSweepOverlay, "showSweepOverlay", true);
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref jobDiagnostics, "jobDiagnostics", false);

            // the toggle only exists to drive this - keep them in sync on
            // load as well as on change, or a saved "on" reads as off until
            // the settings window is opened
            Logger.VerboseLogging = verboseLogging;
            Logger.JobDiagnostics = jobDiagnostics;
        }

        public void DoWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label($"Sweep radius: {sweepRadius} tiles");
            sweepRadius = Mathf.RoundToInt(listing.Slider(sweepRadius, 1f, 50f));

            listing.Gap();
            listing.Label("Sweep works outward from:");

            // Listing_Standard.RadioButton returns true on the click, not the
            // current state, so each one only writes when it's the one picked -
            // no need to clear the other. Verified signature:
            // RadioButton(string label, bool active, float tabIn, string tooltip, float? delay)
            //
            // Both tooltips say "order, not area" on purpose. "Nearest to
            // pawn" reads like it re-centres the sweep on the pawn, and it
            // doesn't - the radius is measured from the click either way.
            if (listing.RadioButton("The click (pawns clear rings outward from where you clicked)",
                centerOutOrder, 8f,
                "Targets nearest the clicked cell are taken first, whichever pawn is closest. A pawn may walk past work beside them to reach work nearer your click. Changes the order work is done in, not which work is found - the sweep radius is measured from the click either way."))
            {
                centerOutOrder = true;
            }

            if (listing.RadioButton("Each pawn (every pawn takes the work nearest itself)",
                !centerOutOrder, 8f,
                "Each pawn takes whichever target is closest to it and ignores where you clicked. Less walking, but a group spreads out and the spot you pointed at is cleared whenever. Changes the order work is done in, not which work is found - the sweep radius is measured from the click either way."))
            {
                centerOutOrder = false;
            }

            // stamped onto the order at click time, so this is honest rather
            // than pedantic - a sweep already running keeps the rule it started with
            listing.Gap(4f);
            Text.Font = GameFont.Tiny;
            listing.Label("Applies to sweeps started after the change; running sweeps keep the setting they began with.");
            Text.Font = GameFont.Small;

            listing.Gap();
            listing.Label($"Need interrupt threshold (hunger, recreation): {needThreshold:P0}");
            needThreshold = listing.Slider(needThreshold, 0.01f, 0.20f);

            listing.Gap();
            listing.Label($"Sleep interrupt threshold: {restThreshold:P0}");
            restThreshold = listing.Slider(restThreshold, 0.01f, 0.30f);

            listing.Gap();
            listing.Label($"Mood interrupt threshold: {moodThreshold:P0}");
            moodThreshold = listing.Slider(moodThreshold, 0.01f, 0.30f);

            listing.Gap();
            listing.CheckboxLabeled("Show sweep radius overlay on hover", ref showSweepOverlay);

            listing.Gap();
            listing.CheckboxLabeled("Verbose logging (for bug reports)", ref verboseLogging,
                "Writes a [DoNotBeLazy] trace of every sweep decision to the log. Leave off for normal play.");
            Logger.VerboseLogging = verboseLogging;

            listing.Gap();
            bool wasDiagnostics = jobDiagnostics;
            listing.CheckboxLabeled("Job diagnostics (why is this pawn standing still)", ref jobDiagnostics,
                "Traces idle jobs, who issued them, and which mods have patched the job pipeline. Separate from verbose logging. Leave off for normal play.");
            Logger.JobDiagnostics = jobDiagnostics;

            // census normally runs at startup, when this was probably off -
            // re-run it on the way in so you don't have to restart the game
            // just to see who's patching what
            if (jobDiagnostics && !wasDiagnostics)
            {
                Utility.PipelineCensus.Run();
            }

            listing.End();
        }
    }
}
