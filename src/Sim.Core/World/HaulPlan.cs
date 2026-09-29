namespace Sim.Core.World;

// Append-only enum (serialized into snapshots).
public enum HaulPhase : byte
{
    ToSource = 1,
    ToDest   = 2,
}

// State-side haul orchestration anchor (M4 Phase A). Lives on Unit. Carries
// the haul's destination shape so that when a movement chain reaches its
// final tile, MoveArrivalEvent can dispatch to the right next step
// (HaulPickupEvent for ToSource, HaulDepositEvent for ToDest) — without an
// OnFinalArrival event field carried on the move events.
//
// Cargo state stays on the unit (Unit.Cargo); HaulPlan is the orchestration
// anchor, not the cargo store.
public sealed class HaulPlan
{
    public TileCoord SourceTile { get; init; }
    public TileCoord DestTile   { get; init; }
    public Resource  Resource   { get; init; }
    public HaulPhase Phase      { get; set;  }
    // M36 — the most this trip picks up; 0 = as much as the hauler can
    // carry. A queued job sizes its last trip to the remaining need so it
    // does not overshoot (docs/hauling-queue-and-routes.md).
    public int       Amount     { get; init; }
    // M36 — the haul-queue job this trip serves; 0 = none (a manual haul).
    // The driver reads it to count what is already on the way for a job,
    // and HaulDepositEvent to credit a Once job's delivered total.
    public int       JobId      { get; init; }

    public HaulPlan(TileCoord sourceTile, TileCoord destTile, Resource resource, HaulPhase phase,
        int amount = 0, int jobId = 0)
    {
        SourceTile = sourceTile;
        DestTile   = destTile;
        Resource   = resource;
        Phase      = phase;
        Amount     = amount;
        JobId      = jobId;
    }
}
