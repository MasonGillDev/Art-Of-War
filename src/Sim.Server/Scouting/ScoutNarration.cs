using System.Threading;
using System.Threading.Tasks;

namespace Sim.Server.Scouting;

// M20 Phase 4 — the LLM narration boundary. A narrator turns the OBSERVATIONS
// block (the canonical claim sentences, the LLM's ENTIRE world) into a
// first-person scout's report. It is the ONLY thing that crosses the
// presentation wall, and it can ONLY restyle — the claims are its whole
// truth, so a hallucinating model can embellish badly but cannot lie about
// what was seen.
//
// Returns null on any failure (network, HTTP error, refusal, empty) — the
// caller falls back to the raw claims sheet. The sim never blocks on, and is
// bit-identical with or without, narration.
public interface IReportNarrator
{
    Task<string?> NarrateAsync(string systemPrompt, string observations, CancellationToken ct = default);
}

public enum ReportStatus : byte
{
    Narrated,     // an LLM produced prose that passed the lint
    RawFallback,  // narration disabled, failed, or failed the lint → raw claims
}

// The finished, player-facing report: the canonical claims (always present,
// the sketch-map / fallback source) plus the prose to show and how it was
// produced.
public sealed class NarratedReport
{
    public ScoutReport Report { get; init; } = null!;
    public string Prose { get; init; } = "";
    public ReportStatus Status { get; init; }
}
