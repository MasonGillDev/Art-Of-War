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
// Cargo state stays on the unit (CargoResource / CargoAmount); HaulPlan is
// the orchestration anchor, not the cargo store.
public sealed class HaulPlan
{
    public TileCoord SourceTile { get; init; }
    public TileCoord DestTile   { get; init; }
    public Resource  Resource   { get; init; }
    public HaulPhase Phase      { get; set;  }

    public HaulPlan(TileCoord sourceTile, TileCoord destTile, Resource resource, HaulPhase phase)
    {
        SourceTile = sourceTile;
        DestTile   = destTile;
        Resource   = resource;
        Phase      = phase;
    }
}
