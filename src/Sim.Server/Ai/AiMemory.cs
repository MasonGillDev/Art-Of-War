namespace Sim.Server.Ai;

// Droppable hints only — everything else is re-derived from the view.
public sealed class AiMemory
{
    public int ScoutLeg;
    // Site order awaiting confirmation + tiles where placement was
    // observed to fail (rejected server-side; skip them next search).
    public (Sim.Core.World.TileCoord Tile, long OrderedAt)? PendingSite;
    public HashSet<(int X, int Y)> BlacklistedTiles { get; } = new();
    // Claim-exhaustion inference: consecutive zero-buffer observations per
    // staffed extractor; past the threshold the extractor is treated as
    // dead (crew released, dropped from supply counts). Droppable like the
    // rest — a restart re-observes for 12 thinks and re-concludes.
    public Dictionary<(int X, int Y), int> ZeroBufferThinks { get; } = new();
    public HashSet<(int X, int Y)> DeadExtractors { get; } = new();
    // Set when the known-land inventory drops below the bank floor —
    // re-opens scouting past its budget. ForestStarved is Build's
    // distress flag (no known forest for a replacement camp); Eat ORs it
    // into LandStarved each think.
    public bool LandStarved;
    public bool ForestStarved;
    // First think each extractor was observed — farm mortality accounting
    // (age ≈ working life; replacements pre-build before the cliff).
    public Dictionary<(int X, int Y), long> FirstSeen { get; } = new();
    // The apprentice en route to the School — same ownership rule as
    // parents (cleared when they graduate to Farmer or die).
    public int? DesignatedTrainee;
    // The recruit en route to the Barracks (Muster rung) — same
    // ownership rule (cleared on graduation to Soldier or death).
    public int? DesignatedRecruit;
    // THREAT MEMORY (Defend rung): last-known hostile sightings —
    // tile → (tick seen, headcount). Refreshed on sight, cleared when
    // the tile is re-observed empty, expired after ThreatMemoryTicks.
    // Droppable like the rest: a restart re-spots anything still
    // prowling the moment it enters sight.
    public Dictionary<(int X, int Y), (long Tick, int Count)> SightedHostiles { get; } = new();
    // Cross-think OWNERSHIP of breeding candidates (arbitration lesson #6):
    // per-think reservations can't stop Eat from re-staffing a freed parent
    // the think after Grow freed them. Designation persists until the
    // breeding starts; every other selector skips designated units.
    public HashSet<int> DesignatedParents { get; } = new();
}
