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
        // M7 — three possible sources, checked in this order:
        //   1) Structure on tile (Storage or matching Extractor).
        //   2) Ground pile on tile (loose cargo from a capture-on-death drop).
        //   3) Nothing → fail clean.
        Structure? source = null;
        world.Structures.TryGetValue(SourceTile, out source);

        // M19 — taking FOOD out of a FOOD HOME shifts its dry-out: catch
        // up FIRST so `available` reflects what the lazy clock already
        // ate (no phantom food), and re-evaluate after the withdraw so
        // the queued FamineCheck doesn't keep predicting from stock that
        // left. On a 100-cap house at rate 1 a stale 25-food withdrawal
        // back-dates famine onset by DAYS — past the grace window. (The
        // castle tolerated this gap only because its larder dwarfs any
        // single haul.)
        var foodHome = source is Sim.Core.Food.IFoodHome fh && Resource == Resource.Food
            ? fh : null;
        if (foodHome is not null)
            Sim.Core.Food.FoodConsumption.CatchUp(foodHome, sim, sim.Now);

        var availableFromStructure = source switch
        {
            StorageStructure ss => ss.AmountOf(Resource),
            Extractor ex when ex.Spec.OutputResource == Resource => ex.Buffer,
            _ => 0,
        };
        var availableFromGround = 0;
        if (availableFromStructure == 0
            && world.GroundResources.TryGetValue(SourceTile, out var groundPile)
            && groundPile.TryGetValue(Resource, out var groundAmount))
        {
            availableFromGround = groundAmount;
        }
        var available = availableFromStructure > 0 ? availableFromStructure : availableFromGround;

        var pickup = Math.Min(hauler.CargoCapacity, available);
        if (pickup == 0)
        {
            hauler.TrySetActivity(Activity.Idle);
            Outcome = IntentOutcome.Reject($"nothing to pick up (no {Resource} available)");
            return;
        }

        // Withdraw from whichever source had stock.
        if (availableFromStructure > 0)
        {
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
            if (foodHome is not null)
                Sim.Core.Food.FoodConsumption.OnRateOrFoodChanged(foodHome, sim);
        }
        else
        {
            // M7 — pickup from the tile's ground pile (capture economy).
            var pile = world.GroundResources[SourceTile];
            var remaining = pile[Resource] - pickup;
            if (remaining <= 0) pile.Remove(Resource); else pile[Resource] = remaining;
            if (pile.Count == 0) world.GroundResources.Remove(SourceTile);
        }

        hauler.CargoResource = Resource;
        hauler.CargoAmount = pickup;

        // M4 Phase A: switch the on-unit haul anchor from "going to source"
        // to "going to dest". MoveArrivalEvent will see Phase == ToDest at
        // final arrival and dispatch the HaulDepositEvent — no event payload
        // to chain.
        if (hauler.HaulPlan is { } plan)
            plan.Phase = HaulPhase.ToDest;

        // Second leg: walk to destination. On arrival, MoveArrivalEvent's
        // DispatchOnFinalArrival reads HaulPlan and schedules deposit.
        if (hauler.Position == DestTile)
        {
            sim.Schedule(sim.Now,
                new HaulDepositEvent(HaulerId, DestTile, hauler.AssignmentEpoch));
        }
        else
        {
            MoveIntent.BeginMove(sim, hauler, DestTile);
        }
    }

    public override string Describe() =>
        $"HaulPickup(hauler={HaulerId} @ {SourceTile.X},{SourceTile.Y} -> {DestTile.X},{DestTile.Y}, {Resource})";
}
