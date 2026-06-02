namespace Sim.Core.Logistics;

// Fires when a hauler arrives at the destination tile of a haul. Transfers
// what it can from the hauler's cargo into the dest structure, then returns
// the hauler to Idle.
//
// Cross-system hook: if the destination is a ConstructionSite and conditions
// become newly met by this deposit, fires StartOrResume on the site
// (Phase C). This is the first real (non-test) caller of that path.
//
// Fencing: ExpectedEpoch is captured at schedule time (from the pickup
// event). If the hauler's AssignmentEpoch differs on fire, the unit was
// retasked between scheduling and now — this event is stale, no-op without
// mutation.
//
// Fail-clean per docs/intent-validation.md: any non-fencing precondition
// miss leaves the world unchanged except that the hauler is returned to
// Idle (otherwise they'd be stuck Hauling forever holding cargo).
public sealed class HaulDepositEvent : ScheduledEvent
{
    public int HaulerId { get; }
    public TileCoord DestTile { get; }
    public byte ExpectedEpoch { get; }

    public HaulDepositEvent(int haulerId, TileCoord destTile, byte expectedEpoch)
    {
        HaulerId = haulerId;
        DestTile = destTile;
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

        // Fencing: stale event from a previous task; no-op.
        if (hauler.AssignmentEpoch != ExpectedEpoch)
        {
            Outcome = IntentOutcome.Reject("stale (epoch mismatch)");
            return;
        }

        if (hauler.Activity != Activity.Hauling)
        {
            Outcome = IntentOutcome.Reject("hauler is not Hauling");
            return;
        }
        if (hauler.Position != DestTile)
        {
            hauler.TrySetActivity(Activity.Idle);
            Outcome = IntentOutcome.Reject($"hauler not on dest {DestTile.X},{DestTile.Y}");
            return;
        }
        if (hauler.CargoAmount == 0 || hauler.CargoResource == Resource.None)
        {
            hauler.TrySetActivity(Activity.Idle);
            Outcome = IntentOutcome.Reject("hauler has no cargo");
            return;
        }
        if (!world.Structures.TryGetValue(DestTile, out var dest))
        {
            hauler.TrySetActivity(Activity.Idle);
            Outcome = IntentOutcome.Reject($"no structure at dest {DestTile.X},{DestTile.Y}");
            return;
        }

        var resource = hauler.CargoResource;
        var amount = hauler.CargoAmount;
        var deposited = dest switch
        {
            StorageStructure ss => ss.Deposit(resource, amount),
            ConstructionSite c => c.Deposit(resource, amount),
            _ => 0,
        };

        hauler.CargoAmount -= deposited;
        if (hauler.CargoAmount == 0) hauler.CargoResource = Resource.None;

        // Phase-C hook. If the deposit just made the build's conditions met,
        // start construction.
        if (dest is ConstructionSite site && !site.IsActive && site.ConditionsMet(world))
        {
            site.StartOrResume(sim);
        }

        hauler.TrySetActivity(Activity.Idle);
    }

    public override string Describe() =>
        $"HaulDeposit(hauler={HaulerId} @ {DestTile.X},{DestTile.Y})";
}
