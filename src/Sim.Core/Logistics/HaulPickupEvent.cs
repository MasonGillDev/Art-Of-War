namespace Sim.Core.Logistics;

// Fires when a hauler arrives at the source tile of a haul. Withdraws what it
// can into the hauler's cargo, then starts the second leg (move to dest, with
// HaulDepositEvent as the on-arrival hook).
//
// Cross-system hook: if the source is an Extractor and the withdraw frees
// buffer space, calls Extractor.ArmIfDormant (Phase D). This is the first
// real (non-test) caller of that path.
//
// Fencing: ExpectedEpoch is captured at schedule time. If the hauler's
// AssignmentEpoch differs on fire, the unit was retasked between scheduling
// and now (e.g. by a Move-on-busy intent) — this event is stale, no-op without
// mutation. Same fencing-token pattern as ConstructionSite.ScheduledCompletion.
//
// Fail-clean per docs/intent-validation.md. If the source is empty (or has
// nothing of the requested resource), the haul is aborted cleanly: hauler
// becomes Idle on the source tile, no second leg is scheduled.
public sealed class HaulPickupEvent : ScheduledEvent
{
    public int HaulerId { get; }
    public TileCoord SourceTile { get; }
    public TileCoord DestTile { get; }
    public Resource Resource { get; }
    public byte ExpectedEpoch { get; }

    public HaulPickupEvent(int haulerId, TileCoord sourceTile, TileCoord destTile, Resource resource, byte expectedEpoch)
    {
        HaulerId = haulerId;
        SourceTile = sourceTile;
        DestTile = destTile;
        Resource = resource;
        ExpectedEpoch = expectedEpoch;
    }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(HaulerId, out var hauler))
        {
            Outcome = IntentOutcome.Reject($"hauler {HaulerId} does not exist");
            return;
        }

        // Fencing: if epoch doesn't match, the unit was retasked. Silently no-op —
        // no mutation, no cleanup. The current task owns the unit's state.
        if (hauler.AssignmentEpoch != ExpectedEpoch)
        {
            Outcome = IntentOutcome.Reject("stale (epoch mismatch)");
            return;
        }

        // Epoch matched, so the unit is still on THIS haul. Now we can safely
        // clean them up on any precondition miss.
        if (hauler.Activity != Activity.Hauling)
        {
            Outcome = IntentOutcome.Reject("hauler is not Hauling");
            return;
        }
        if (hauler.Position != SourceTile)
        {
            hauler.TrySetActivity(Activity.Idle);
            Outcome = IntentOutcome.Reject($"hauler not on source {SourceTile.X},{SourceTile.Y}");
            return;
        }
        if (!world.Structures.TryGetValue(SourceTile, out var source))
        {
            hauler.TrySetActivity(Activity.Idle);
            Outcome = IntentOutcome.Reject($"no structure at source {SourceTile.X},{SourceTile.Y}");
            return;
        }

        // What's available?
        var available = source switch
        {
            StorageStructure ss => ss.AmountOf(Resource),
            Extractor ex when ex.Spec.OutputResource == Resource => ex.Buffer,
            _ => 0,
        };

        var pickup = Math.Min(hauler.CargoCapacity, available);
        if (pickup == 0)
        {
            hauler.TrySetActivity(Activity.Idle);
            Outcome = IntentOutcome.Reject($"nothing to pick up (no {Resource} available)");
            return;
        }

        // Actually withdraw. Two paths because StorageStructure and Extractor
        // have different storage APIs.
        switch (source)
        {
            case StorageStructure ss:
                ss.Withdraw(Resource, pickup);
                break;
            case Extractor ex:
                ex.Buffer -= pickup;
                // Phase-D hook: freeing buffer space may re-arm dormant production.
                ex.ArmIfDormant(sim);
                break;
        }

        hauler.CargoResource = Resource;
        hauler.CargoAmount = pickup;

        // Second leg: walk to destination, deposit on arrival. Capture the
        // hauler's *current* epoch so the deposit event fences against future
        // retasking too.
        var deposit = new HaulDepositEvent(HaulerId, DestTile, hauler.AssignmentEpoch);
        if (hauler.Position == DestTile)
        {
            sim.Schedule(sim.Now, deposit);
        }
        else
        {
            MoveIntent.ScheduleNextStep(sim, hauler, DestTile, onFinalArrival: deposit);
        }
    }

    public override string Describe() =>
        $"HaulPickup(hauler={HaulerId} @ {SourceTile.X},{SourceTile.Y} -> {DestTile.X},{DestTile.Y}, {Resource})";
}
