namespace Sim.Core.Logistics;

// Fires when a hauler arrives at the destination tile of a haul. Transfers
// what it can from the hauler's cargo into the dest structure, then returns
// the hauler to Idle.
//
// Cross-system hook: if the destination is a ConstructionSite and conditions
// become newly met by this deposit, fires StartOrResume on the site
// (Phase C). This is the first real (non-test) caller of that path.
//
// Fail-clean per docs/intent-validation.md: any precondition miss leaves the
// world unchanged except that the hauler is returned to Idle (otherwise
// they'd be stuck Hauling forever holding cargo).
public sealed class HaulDepositEvent : ScheduledEvent
{
    public int HaulerId { get; }
    public TileCoord DestTile { get; }

    public HaulDepositEvent(int haulerId, TileCoord destTile)
    {
        HaulerId = haulerId;
        DestTile = destTile;
    }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(HaulerId, out var hauler))
        {
            Outcome = IntentOutcome.Reject($"hauler {HaulerId} does not exist");
            return;
        }
        if (hauler.Position != DestTile)
        {
            hauler.TrySetActivity(Activity.Idle);
            Outcome = IntentOutcome.Reject($"hauler not on dest {DestTile.X},{DestTile.Y}");
            return;
        }
        if (hauler.Activity != Activity.Hauling)
        {
            Outcome = IntentOutcome.Reject("hauler is not Hauling");
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
        // start construction. ConstructionSite.ConditionsMet also checks
        // builders-present — if no builders are on the tile yet, this is a
        // no-op and the next AssignBuilders or arrival will trigger the start.
        if (dest is ConstructionSite site && !site.IsActive && site.ConditionsMet(world))
        {
            site.StartOrResume(sim);
        }

        hauler.TrySetActivity(Activity.Idle);
    }

    public override string Describe() =>
        $"HaulDeposit(hauler={HaulerId} @ {DestTile.X},{DestTile.Y})";
}
