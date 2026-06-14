using System.Threading;
using System.Threading.Tasks;
using Sim.Core.Scouting;
using Sim.Core.World;

namespace Sim.Server.Scouting;

// M20 Phase 4 — orchestrates a returned mission into a player-facing report:
// compile claims (deterministic, the canonical artifact) → narrate (async,
// off the sim thread) → lint → retry once → fall back to raw claims. The
// claims are recomputed on demand from the snapshotted mission, so this is
// recovery-clean for free: a crash between return and narration loses only the
// (regenerable) prose, never the intel.
//
// Construct with a null narrator (or one whose options are disabled) to run
// fully offline — the report is the raw claims sheet, and the game is wholly
// playable without an API key.
public sealed class ScoutReportNarrationService
{
    private readonly IReportNarrator? _narrator;
    private readonly ScoutReportConstants _claimsConfig;

    public ScoutReportNarrationService(
        IReportNarrator? narrator = null, ScoutReportConstants? claimsConfig = null)
    {
        _narrator = narrator;
        _claimsConfig = claimsConfig ?? new ScoutReportConstants();
    }

    // Compile then narrate. Use when you have the mission in hand.
    public Task<NarratedReport> NarrateAsync(
        GameWorld world, ScoutMission mission, string scoutName = "your scout",
        CancellationToken ct = default)
        => NarrateReportAsync(ClaimsCompiler.Compile(world, mission, _claimsConfig), scoutName, ct);

    // Narrate an ALREADY-compiled report. The server compiles synchronously
    // under its sim lock (a fast pure read) and calls this off the lock for the
    // slow HTTP turn — so the clock thread never blocks on the network.
    public async Task<NarratedReport> NarrateReportAsync(
        ScoutReport report, string scoutName = "your scout", CancellationToken ct = default)
    {
        var raw = ReportText.RawFallback(report);
        if (_narrator is null)
            return new NarratedReport { Report = report, Prose = raw, Status = ReportStatus.RawFallback };

        var observations = ReportText.ObservationsBlock(report, scoutName);

        // Try twice: a transient failure or a lint miss earns one retry, then
        // the raw claims carry the intel.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var prose = await _narrator.NarrateAsync(ScoutPrompt.System, observations, ct).ConfigureAwait(false);
            if (prose is not null && ReportLinter.NumeralsTraceToGrounding(prose, observations))
                return new NarratedReport { Report = report, Prose = prose, Status = ReportStatus.Narrated };
        }

        return new NarratedReport { Report = report, Prose = raw, Status = ReportStatus.RawFallback };
    }
}
