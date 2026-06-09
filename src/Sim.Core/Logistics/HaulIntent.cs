namespace Sim.Core.Logistics;

// Composite intent: walk hauler to source, pick up, walk to destination,
// deposit. One submission = one trip. Multi-resource hauls = multiple intents.
//
// Resolution validates the plan is well-formed *at submission time*:
//   - Hauler exists, is Idle, has CargoCapacity > 0.
//   - Source and dest tiles are in-bounds.
//   - Source and dest both currently have structures.
//   - Resource is meaningful (not None).
//
// Runtime correctness (source actually has the resource when we arrive,
// dest still exists when we get there) is re-checked by HaulPickupEvent
// and HaulDepositEvent — per docs/intent-validation.md, submission checks
// are advisory and the world may have moved on by the time each step fires.
public sealed class HaulIntent : Intent
{
    public int HaulerId { get; }
    public TileCoord SourceTile { get; }
    public TileCoord DestTile { get; }
    public Resource Resource { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public HaulIntent(int haulerId, TileCoord sourceTile, TileCoord destTile, Resource resource)
    {
        HaulerId = haulerId;
        SourceTile = sourceTile;
        DestTile = destTile;
        Resource = resource;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(HaulerId, out var hauler))
            return IntentOutcome.Reject($"hauler {HaulerId} does not exist");
        if (hauler.GroupId is not null)
            return IntentOutcome.Reject($"hauler {HaulerId} is in group {hauler.GroupId}");
        if (hauler.IsEmbarked)
            return IntentOutcome.Reject($"hauler {HaulerId} is embarked on boat {hauler.EmbarkedOn}");
        if (hauler.Activity != Activity.Idle)
            return IntentOutcome.Reject("hauler is not Idle");
        if (hauler.CargoCapacity <= 0)
            return IntentOutcome.Reject("hauler has no cargo capacity");
        if (Resource == Resource.None)
            return IntentOutcome.Reject("resource is None");
        if (!world.Grid.InBounds(SourceTile))
            return IntentOutcome.Reject($"source {SourceTile.X},{SourceTile.Y} out of bounds");
        if (!world.Grid.InBounds(DestTile))
            return IntentOutcome.Reject($"dest {DestTile.X},{DestTile.Y} out of bounds");
        // M7: source can be either a Structure OR a ground pile (capture
        // economy — cargo dropped on the tile by a dying laden unit).
        var hasStructureSource = world.Structures.ContainsKey(SourceTile);
        var hasGroundSource = world.GroundResources.TryGetValue(SourceTile, out var srcPile)
            && srcPile.ContainsKey(Resource);
        if (!hasStructureSource && !hasGroundSource)
            return IntentOutcome.Reject(
                $"no source for {Resource} at {SourceTile.X},{SourceTile.Y} (no structure, no ground pile)");
        if (!world.Structures.ContainsKey(DestTile))
            return IntentOutcome.Reject($"no structure at dest {DestTile.X},{DestTile.Y}");

        hauler.TrySetActivity(Activity.Hauling);

        // M4 Phase A: state-anchored haul orchestration. HaulPlan carries the
        // route shape; MoveArrivalEvent dispatches pickup/deposit on final
        // arrival by reading the plan. No OnFinalArrival event field.
        hauler.HaulPlan = new HaulPlan(SourceTile, DestTile, Resource, HaulPhase.ToSource);

        if (hauler.Position == SourceTile)
        {
            // Already at source — go straight to pickup.
            sim.Schedule(sim.Now,
                new HaulPickupEvent(HaulerId, SourceTile, DestTile, Resource, hauler.AssignmentEpoch));
        }
        else
        {
            MoveIntent.BeginMove(sim, hauler, SourceTile);
        }

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"HaulIntent(hauler={HaulerId}, {SourceTile.X},{SourceTile.Y} -> {DestTile.X},{DestTile.Y}, {Resource})";
}
