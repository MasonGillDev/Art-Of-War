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
        // M28 — a boat's stop is the source dock's SLIP, not the dock tile.
        if (!HaulStops.AtStop(world, hauler, SourceTile))
        {
            // FAIL CLEAN (see the dest-leg paths at the bottom of this file):
            // clearing the anchor is what makes the hauler usable again.
            hauler.HaulPlan = null;
            hauler.TrySetActivity(Activity.Idle);
            Outcome = IntentOutcome.Reject($"hauler not at source {SourceTile.X},{SourceTile.Y}");
            return;
        }
        // M7 — three possible sources, checked in this order:
        //   1) Structure on tile (Storage or matching Extractor).
        //   2) Ground pile on tile (loose cargo from a capture-on-death drop).
        //   3) Nothing → fail clean.
        Structure? source = null;
        world.Structures.TryGetValue(SourceTile, out source);

        // M40 — a salvage trip takes EVERYTHING that fits, from the cache and the
        // pile, mixed. Finding nothing is how a salvage learns it is over: the
        // job ends here, on arrival, and the hauler goes idle.
        if (Resource == Resource.None)
        {
            if (Sim.Core.Hauling.Salvage.TakeAll(sim, hauler, SourceTile) == 0)
            {
                if (hauler.HaulPlan is { JobId: > 0 } done
                    && world.HaulJobs.TryGetValue(done.JobId, out var job)
                    && job.Kind == Sim.Core.Hauling.HaulJobKind.Salvage
                    && job.OwnerId == hauler.OwnerId)
                    world.HaulJobs.Remove(job.JobId);
                hauler.HaulPlan = null;
                hauler.TrySetActivity(Activity.Idle);
                Outcome = IntentOutcome.Reject($"nothing left to salvage at {SourceTile.X},{SourceTile.Y}");
                return;
            }
            SecondLeg(sim, hauler);
            return;
        }

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

        var pickup = Math.Min(hauler.CargoCapacity - hauler.CargoAmount, available);
        // M36 — a sized trip (the queue's last load) takes no more than asked.
        if (hauler.HaulPlan is { Amount: > 0 } sized)
            pickup = Math.Min(pickup, sized.Amount);
        if (pickup == 0)
        {
            // FAIL CLEAN — the same discipline the dest-leg paths below already
            // apply, back-applied to the first leg where it was missed.
            //
            // Arriving at a source that has run dry is ORDINARY: a farm whose
            // buffer emptied while the hauler walked over is a normal race, not
            // a corrupt world. Leaving HaulPlan set turned that into a
            // permanent brick — the hauler sits Idle forever holding a dead
            // anchor, and every consumer that asks "is this unit free?" (the
            // automation driver's IsFreeForWork, the selectors) reads it as
            // busy for the rest of the game. One empty pickup silently retired
            // the unit AND wedged the order that named it.
            //
            // There is no cargo to strand here (pickup == 0), so dropping the
            // anchor is unambiguous: the trip simply did not happen.
            hauler.HaulPlan = null;
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
            Sim.Core.Caches.CacheLooting.RemoveIfEmptied(sim, source);
        }
        else
        {
            // M7 — pickup from the tile's ground pile (capture economy).
            var pile = world.GroundResources[SourceTile];
            var remaining = pile[Resource] - pickup;
            if (remaining <= 0) pile.Remove(Resource); else pile[Resource] = remaining;
            if (pile.Count == 0) world.GroundResources.Remove(SourceTile);
        }

        hauler.Cargo.Add(Resource, pickup);
        SecondLeg(sim, hauler);
    }

    // The trip's second leg: to the destination, depositing on arrival.
    private void SecondLeg(Simulation sim, Unit hauler)
    {
        var world = sim.World;

        // M4 Phase A: switch the on-unit haul anchor from "going to source"
        // to "going to dest". MoveArrivalEvent will see Phase == ToDest at
        // final arrival and dispatch the HaulDepositEvent — no event payload
        // to chain.
        if (hauler.HaulPlan is { } plan)
            plan.Phase = HaulPhase.ToDest;

        // Second leg: travel to the destination stop (the dock's slip for a
        // boat — M28). On arrival, MoveArrivalEvent's DispatchOnFinalArrival
        // reads HaulPlan and schedules the deposit.
        if (HaulStops.AtStop(world, hauler, DestTile))
        {
            sim.Schedule(sim.Now,
                new HaulDepositEvent(HaulerId, DestTile, hauler.AssignmentEpoch));
        }
        else if (HaulStops.MoveTarget(world, hauler, DestTile) is { } stop)
        {
            MoveIntent.BeginMove(sim, hauler, stop);
            // FAIL CLEAN on no route (M28 hardening): a laden hauler left
            // Hauling with no arrival scheduled is a zombie every selector
            // ignores. Idle it WITH its cargo — UnloadCargoIntent (which
            // now empties a slip-parked boat into its quay) is the recovery.
            if (hauler.NextArrivalSeq is null)
            {
                hauler.HaulPlan = null;
                hauler.TrySetActivity(Activity.Idle);
                Outcome = IntentOutcome.Reject(
                    $"no route to dest {DestTile.X},{DestTile.Y} — cargo stays aboard");
            }
        }
        else
        {
            // The dest dock vanished mid-trip (razed) — fail clean, laden.
            hauler.HaulPlan = null;
            hauler.TrySetActivity(Activity.Idle);
            Outcome = IntentOutcome.Reject(
                $"dest {DestTile.X},{DestTile.Y} is no longer water-servable");
        }
    }

    public override string Describe() =>
        $"HaulPickup(hauler={HaulerId} @ {SourceTile.X},{SourceTile.Y} -> {DestTile.X},{DestTile.Y}, {Resource})";
}
