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
        if (!world.Structures.ContainsKey(SourceTile))
            return IntentOutcome.Reject($"no structure at source {SourceTile.X},{SourceTile.Y}");
        if (!world.Structures.ContainsKey(DestTile))
            return IntentOutcome.Reject($"no structure at dest {DestTile.X},{DestTile.Y}");

        hauler.TrySetActivity(Activity.Hauling);

        // Capture epoch AFTER the activity transition (which bumped it) so the
        // pickup event fences against any FUTURE retasking but not against the
        // bump we just did.
        var pickup = new HaulPickupEvent(HaulerId, SourceTile, DestTile, Resource, hauler.AssignmentEpoch);
        if (hauler.Position == SourceTile)
        {
            sim.Schedule(sim.Now, pickup);
        }
        else
        {
            MoveIntent.ScheduleNextStep(sim, hauler, SourceTile, onFinalArrival: pickup);
        }

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"HaulIntent(hauler={HaulerId}, {SourceTile.X},{SourceTile.Y} -> {DestTile.X},{DestTile.Y}, {Resource})";
}
