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

        // Widgets.TextFieldNumeric and Widgets.TextFieldPercent both take a
        // "ref string buffer" next to the value, and it is not optional. The
        // buffer is what is currently TYPED, which a float or an int cannot
        // hold: "0.", a lone minus sign, "12" on its way to "120", a number
        // briefly outside the range. The settings window redraws every frame
        // and Listing_Standard is rebuilt each time, so the buffer has to
        // live out here.
        //
        // Deliberately NOT in ExposeData - this is what is on screen, not
        // what is configured, and persisting it would save a half-typed
        // number. Added 2026-09-12 with the number boxes; see architecture
        // doc section 7.
        private string sweepRadiusBuffer;
        private string needThresholdBuffer;
        private string restThresholdBuffer;
        private string moodThresholdBuffer;

        // Row geometry for a slider with a number box beside it. 22f is the
        // height Listing_Standard.Slider itself uses - verified by reading
        // its IL, which is GetRect(22f, 1f) then Gap(verticalSpacing).
        //
        // The box is the same width for the percentages and for the radius,
        // which is wider than "50" needs, so that all four sliders end at
        // the same x and the four boxes line up down the screen.
        private const float SliderRowHeight = 22f;
        private const float NumberBoxWidth = 70f;
        private const float NumberBoxGap = 6f;

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
            sweepRadius = SliderWithIntBox(listing, sweepRadius, 1, 50, ref sweepRadiusBuffer);

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
            needThreshold = SliderWithPercentBox(listing, needThreshold, 0.01f, 0.20f, ref needThresholdBuffer);

            listing.Gap();
            listing.Label($"Sleep interrupt threshold: {restThreshold:P0}");
            restThreshold = SliderWithPercentBox(listing, restThreshold, 0.01f, 0.30f, ref restThresholdBuffer);

            listing.Gap();
            listing.Label($"Mood interrupt threshold: {moodThreshold:P0}");
            moodThreshold = SliderWithPercentBox(listing, moodThreshold, 0.01f, 0.30f, ref moodThresholdBuffer);

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

        // A slider and a typed box on one row, both editing the same stored
        // value, for a setting held as a FRACTION and shown as a percentage:
        // type 10 and mean 10%, stored as 0.10. Added 2026-09-12 - a slider
        // cannot be aimed, and the value in the config file the day this was
        // ordered read 0.10277. Architecture doc section 7.
        //
        // The row is built by hand rather than calling listing.Slider,
        // because that takes the whole width and leaves no room. Its IL is
        // GetRect(22f, 1f), Widgets.HorizontalSlider, Gap(verticalSpacing),
        // and this is the same three steps with the right-hand end carved
        // off for the box.
        private static float SliderWithPercentBox(Listing_Standard listing, float value, float min, float max, ref string buffer)
        {
            Rect row = listing.GetRect(SliderRowHeight);
            Rect sliderRect = new Rect(row.x, row.y, row.width - NumberBoxWidth - NumberBoxGap, row.height);
            Rect boxRect = new Rect(row.xMax - NumberBoxWidth, row.y, NumberBoxWidth, row.height);

            // The trailing arguments are HorizontalSlider's three optional
            // labels and its roundTo, and -1f means "do not round" - the
            // values listing.Slider passes.
            float dragged = Widgets.HorizontalSlider(sliderRect, value, min, max, false, null, null, null, -1f);

            // The drag wins over whatever is typed, and a null buffer is how
            // the box is told to re-read the value: TextFieldNumeric fills a
            // null buffer from the value it is handed. A tolerance rather
            // than an exact inequality, because a one-bit difference from a
            // slider nobody touched would delete a number mid-typing.
            if (Mathf.Abs(dragged - value) > 0.0001f)
            {
                buffer = null;
            }
            value = dragged;

            // Stores a fraction and shows a percentage: read off its IL, it
            // multiplies the value and both bounds by 100 on the way in,
            // divides by 100 on the way out, draws a "%" in the last 25
            // pixels of the rect, and clamps to max. The lower clamp is
            // Mathf.Clamp inside Widgets.ResolveParseNow, which runs when the
            // box loses keyboard focus - so a number is only corrected once
            // the player has finished typing it.
            Widgets.TextFieldPercent(boxRect, ref value, ref buffer, min, max);

            listing.Gap(listing.verticalSpacing);
            return value;
        }

        // The same row for a plain integer setting - the sweep radius, which
        // is tiles and not a percentage.
        private static int SliderWithIntBox(Listing_Standard listing, int value, int min, int max, ref string buffer)
        {
            Rect row = listing.GetRect(SliderRowHeight);
            Rect sliderRect = new Rect(row.x, row.y, row.width - NumberBoxWidth - NumberBoxGap, row.height);
            Rect boxRect = new Rect(row.xMax - NumberBoxWidth, row.y, NumberBoxWidth, row.height);

            int dragged = Mathf.RoundToInt(Widgets.HorizontalSlider(sliderRect, value, min, max, false, null, null, null, -1f));
            if (dragged != value)
            {
                buffer = null;
            }
            value = dragged;

            // The int form of the widget TextFieldPercent is built on. Same
            // clamp on focus loss, via the integer branch of
            // Widgets.ResolveParseNow - int and float are the only two types
            // it handles, and everything else logs an error.
            Widgets.TextFieldNumeric<int>(boxRect, ref value, ref buffer, min, max);

            listing.Gap(listing.verticalSpacing);
            return value;
        }
    }
}
