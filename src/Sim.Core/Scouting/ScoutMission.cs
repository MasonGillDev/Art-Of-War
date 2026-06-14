namespace Sim.Core.Scouting;

// M20 — a dispatched scout's reconnaissance mission and the fog-honest
// OBSERVATION LOG it accumulates as it travels (docs/m20-scouting-reports-spec.md).
//
// The log is the CANONICAL artifact: deterministic sim data, snapshot-
// serialized (and therefore hashed), regenerated bit-for-bit on replay.
// The prose report is a presentation-only VIEW of it produced outside the
// sim wall by the server-side claims compiler + LLM — none of that touches
// this state. Replays regenerate logs, never prose.
//
// MUTATION CONTRACT (docs/determinism-audit.md):
//   * Legs are appended ONLY by ScoutObservation.Capture, which is called
//     ONLY from MoveArrivalEvent.Apply — the same single-write-site
//     discipline as Sight.Reveal (the disc it records is the disc Reveal
//     just revealed). Phase 1 ships capture; dispatch + lifecycle
//     transitions (Active → Returned) arrive with DispatchScoutIntent and
//     the return-rule driver in Phase 3.
//   * Keyed by the scout's own unit id in GameWorld.ScoutMissions — one
//     slot per scout. A fresh dispatch replaces the prior record (the
//     report store, not the sim, keeps history).
public sealed class ScoutMission
{
    public int ScoutUnitId { get; init; }
    public int OwnerId { get; init; }
    public long DispatchTick { get; init; }
    public ScoutMissionState State { get; set; } = ScoutMissionState.Active;

    // The order's plan + recall rule, set at dispatch and immutable after.
    public TileCoord HomeTile { get; init; }            // where to return
    public List<TileCoord> Waypoints { get; init; } = new();
    public ScoutReturnRule ReturnRule { get; init; } = ScoutReturnRule.WaypointsExhausted;
    public long ElapsedLimitTicks { get; init; }        // for the ElapsedTicks rule

    // Index of the waypoint the scout is currently heading to / has last
    // reached. Mutated only by ScoutMissionRunner (the in-sim driver).
    public int WaypointCursor { get; set; }

    // Appended in arrival order — "in order along the path", the structure the
    // claims compiler walks to narrate the journey. Capture appends on every
    // arrival while the mission is not yet Returned (so the homeward leg is
    // observed too).
    public List<ObservationLeg> Legs { get; } = new();
}

// Append-only enum (serialized).
public enum ScoutMissionState : byte
{
    Active    = 1, // outbound; Capture appends to the log
    Returned  = 2, // home again; log is final, report being written
    Returning = 3, // recall fired; heading home, still observing
}

// Append-only enum (serialized). When does the scout turn for home?
// WaypointsExhausted is always the backstop — the others recall EARLIER.
public enum ScoutReturnRule : byte
{
    WaypointsExhausted = 1, // visit every waypoint, then return
    ElapsedTicks       = 2, // return after a time budget (or waypoints, first)
    HostileSighted     = 3, // return the moment a hostile force is seen
}

// One arrival's worth of observation: the vision disc swept (Center +
// Radius => the COVERAGE, reconstructed by the compiler) and the CONTENT
// sighted within it. Coverage minus content = "observed-empty"; tiles in no
// leg's disc = "never observed". That distinction — recorded-empty vs
// no-record — is the spine of the honest NOT-OBSERVED claims, and it is
// structural here, not reconstructed later.
public sealed class ObservationLeg
{
    public long Tick { get; init; }
    public TileCoord Center { get; init; }
    public int Radius { get; init; }

    // Only non-empty tiles, in canonical (y, x) then unit-id order. Empty
    // tiles inside the disc are deliberately absent (observed-empty).
    public List<Sighting> Sightings { get; } = new();
}

// What the scout saw on one tile, at the tick of the owning leg. Stored as
// observed (not recomputed at read time) so the world may change behind the
// fog — the staleness that makes intelligence intelligence.
public sealed class Sighting
{
    public TileCoord Tile { get; init; }
    public Biome Biome { get; init; }

    // Units present, in unit-id order, excluding the scout itself. Empty
    // when the tile held only a structure.
    public List<SightedUnit> Units { get; init; } = new();

    // The structure on this tile, or null. At most one per tile.
    public SightedStructure? Structure { get; init; }
}

// A unit as the scout saw it. Ground truth (true id/owner/role/activity) —
// the log is sim-internal and never reaches the LLM. Uncertainty bands and
// fog novelty are applied later by the compiler, which is the only thing
// that crosses the presentation wall. Activity is the observed posture
// (Moving = on the march, otherwise at a halt) the compiler reads for
// fact-vs-impression separation.
public readonly record struct SightedUnit(int UnitId, int OwnerId, UnitRole Role, Activity Activity);

// A structure as the scout saw it. For a ConstructionSite, TargetKind names
// what is being raised and (ProgressTicks / BuildDurationTicks) is its
// integer-exact completeness — the compiler turns that into "~40% built,
// two days' work done". For a finished structure, Kind == TargetKind and
// the tick fields are 0.
public sealed class SightedStructure
{
    public StructureKind Kind { get; init; }
    public StructureKind TargetKind { get; init; }
    public int OwnerId { get; init; }
    public long ProgressTicks { get; init; }
    public long BuildDurationTicks { get; init; }
}
