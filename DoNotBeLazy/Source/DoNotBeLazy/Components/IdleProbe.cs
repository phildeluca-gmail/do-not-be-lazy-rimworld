using System;
using System.Text;
using RimWorld;
using Verse;
using DoNotBeLazy.Core;

namespace DoNotBeLazy.Components
{
    // Instrument 3 of the standing-still diagnostics: catch the silent case.
    //
    // Pawn_JobTracker.TryFindAndStartJob has two paths that produce a pawn
    // with no job and no log line at all - CanDoAnyJob() false, and
    // DetermineNextJob returning NoJob. Both leave curJob null. Nothing in
    // vanilla reports it, and in game it looks the same as a pawn who is
    // merely between jobs.
    //
    // Deliberately does NOT re-run WorkGivers to ask why. Calling ShouldSkip
    // or JobOnThing outside the think tree reads and writes the same static
    // state the real scan uses - this mod has been burned by that three
    // times now - and a probe that changes what it's measuring is worse than
    // no probe. This only reads pawn state.
    public class IdleProbe : MapComponent
    {
        // ~4 seconds at 1x. Often enough to see a pawn stuck across several
        // probes, rare enough that a genuinely idle colony doesn't drown the
        // log.
        private const int ProbeIntervalTicks = 250;

        public IdleProbe(Map map) : base(map)
        {
        }

        public override void MapComponentTick()
        {
            if (!Logger.JobDiagnostics || Find.TickManager.TicksGame % ProbeIntervalTicks != 0)
            {
                return;
            }

            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                // Wrapped per-pawn 2026-09-27 per user order: "Don't end all
                // pawn's activities because one pawn stopped." This probe is
                // read-only and diagnostic-only, but it is still a per-pawn
                // loop on the tick path - one pawn's bad state should not
                // stop the rest of the map from being probed.
                try
                {
                    if (pawn?.jobs == null || pawn.jobs.curJob != null)
                    {
                        continue;
                    }

                    Logger.Diag("idle " + pawn.LabelShort + ": no job - " + Describe(pawn));
                }
                catch (Exception ex)
                {
                    Logger.Diag("idle probe: exception describing " + (pawn?.LabelShort ?? "unknown pawn") + " - skipping: " + ex);
                }
            }
        }

        // Everything here is a candidate answer to "why is nothing being
        // handed to this pawn". Hunting is called out by name because that's
        // the work type in the report.
        private static string Describe(Pawn pawn)
        {
            var sb = new StringBuilder();

            sb.Append("drafted=").Append(pawn.Drafted);
            sb.Append(" downed=").Append(pawn.Downed);
            sb.Append(" mental=").Append(pawn.InMentalState ? pawn.MentalStateDef?.defName ?? "yes" : "no");
            sb.Append(" queued=").Append(pawn.jobs.jobQueue?.Count ?? 0);

            if (pawn.workSettings == null || !pawn.workSettings.EverWork)
            {
                sb.Append(" work=never");
            }
            else
            {
                WorkTypeDef hunting = WorkTypeDefOf.Hunting;
                sb.Append(" hunting=").Append(pawn.WorkTypeIsDisabled(hunting)
                    ? "disabled"
                    : pawn.workSettings.GetPriority(hunting).ToString());
            }

            // a think tree that never got built is its own explanation, and
            // cheap to rule out
            sb.Append(" thinker=").Append(pawn.thinker != null ? "ok" : "MISSING");

            return sb.ToString();
        }
    }
}
