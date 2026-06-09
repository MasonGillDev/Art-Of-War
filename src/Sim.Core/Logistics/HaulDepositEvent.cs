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

        // M13 — when depositing Food into a Castle, catch up consumption
        // FIRST so the new total reflects the correct pre-deposit Holdings.
        // This closes the constant-rate window before the deposit changes
        // the food level.
        var foodCastle = (dest is Castle c0 && resource == Resource.Food) ? c0 : null;
        if (foodCastle is not null)
            Sim.Core.Food.FoodConsumption.CatchUp(foodCastle, sim, sim.Now);

        var deposited = dest switch
        {
            StorageStructure ss => ss.Deposit(resource, amount),
            ConstructionSite c => c.Deposit(resource, amount),
            _ => 0,
        };

        hauler.CargoAmount -= deposited;
        if (hauler.CargoAmount == 0) hauler.CargoResource = Resource.None;

        // M13 Phase C — if a famine was active and the deposit brought
        // Holdings[Food] above 0, the famine ends. Always re-evaluate the
        // famine check so the predicted next dry-out reflects the new
        // food level.
        //
        // The scheduled StarvationDeathEvent is INTENTIONALLY left in
        // flight (anchor not cleared). If the deposit is large enough
        // that no new famine arises before the death tick, the event
        // fences harmlessly when it fires (StarvationDeathEvent.Apply
        // → FamineStartTick is null → fence + clear). If a new famine
        // starts before then, CatchUp's famine-trigger branch sees the
        // existing anchor and declines to reschedule — preserving the
        // original death cadence so a player can't reset the starvation
        // clock by trickling tiny deposits.
        if (foodCastle is not null)
        {
            if (foodCastle.FamineStartTick.HasValue
                && foodCastle.AmountOf(Resource.Food) > 0)
            {
                foodCastle.FamineStartTick = null;
            }
            Sim.Core.Food.FoodConsumption.OnRateOrFoodChanged(foodCastle, sim);
        }

        // Phase-C hook. If the deposit just made the build's conditions met,
        // start construction.
        if (dest is ConstructionSite site && !site.IsActive && site.ConditionsMet(world))
        {
            site.StartOrResume(sim);
        }

        // M4 Phase A: haul complete; clear the on-unit anchor.
        hauler.HaulPlan = null;
        hauler.TrySetActivity(Activity.Idle);
    }

    public override string Describe() =>
        $"HaulDeposit(hauler={HaulerId} @ {DestTile.X},{DestTile.Y})";
}
