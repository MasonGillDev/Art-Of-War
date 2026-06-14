using System.Collections.Generic;
using System.Linq;
using Sim.Core.World;

namespace Sim.Server.Scouting;

// M20 Phase 2 — the claims compiler's output. A CLAIM is one atomic,
// honest, natural-language statement about the journey: truth lives here,
// embellishment does not. The compiler (the truth oracle) decides what
// claims exist and renders each to one canonical sentence; the LLM (Phase 4)
// may only restyle those sentences. Because the claims ARE the LLM's entire
// world, the worst a hallucinating model can do is embellish badly — it
// cannot lie about what was seen.
//
// This list is ALSO the player's raw fallback / sketch-map view (Anchor is
// the map pin). One artifact, two consumers — see
// docs/m20-scouting-reports-spec.md.

// The epistemic status of a claim — the SAW-vs-GUESS axis the prompt's
// absolute rules turn on. Kept separate from ClaimKind (what the claim is
// about) so the LLM gets a clean fact/impression boundary.
public enum ClaimCertainty : byte
{
    Observed,    // seen plainly; a fact
    Estimated,   // seen, but counted/measured under poor vision; a band
    Impression,  // a scout's read of the situation; a guess, marked as such
    NotObserved, // explicitly NOT seen; a named gap in the report
}

public enum ClaimKind : byte
{
    EmptyGround, // a stretch of country with nothing on it
    Force,       // a body of armed men
    Structure,   // a building or works
    Impression,  // a tagged read (entrenching, on the march away, ...)
    Unknown,     // a closure-rule unknown (their intent, true strength, purpose)
    NotObserved, // a coverage gap (the land beyond the turnaround)
}

public sealed class Claim
{
    public int Sequence { get; init; }
    public ClaimKind Kind { get; init; }
    public ClaimCertainty Certainty { get; init; }

    // The canonical sentence — flat, plain, complete. The LLM input and the
    // fallback view both read this.
    public string Text { get; init; } = "";

    // Map pin for the sketch view; null for unlocated claims (pure unknowns).
    public TileCoord? Anchor { get; init; }

    // Provenance, surfaced so the player (and the prompt) can weigh the claim.
    public long? ObservedTick { get; init; }
    public int? RangeTiles { get; init; }       // how far off it was seen
    public int? VisionQualityPct { get; init; } // 0..100; lower = less sure

    // False = the player already knows this (own territory). The compiler
    // marks foreign content novel and own content known; the report spends
    // its words on what is new.
    public bool Novel { get; init; } = true;
}

public sealed class ScoutReport
{
    public int ScoutUnitId { get; init; }
    public int OwnerId { get; init; }
    public long DispatchTick { get; init; }
    public long ReturnTick { get; init; }
    public IReadOnlyList<Claim> Claims { get; init; } = new List<Claim>();

    // The ordered canonical sentences — the raw fallback view, and (Phase 4)
    // the OBSERVATIONS block handed to the scout-persona prompt verbatim.
    public IReadOnlyList<string> CanonicalSentences =>
        Claims.Select(c => c.Text).ToList();
}
