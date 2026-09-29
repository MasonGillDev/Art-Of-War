namespace Sim.Core.Logistics;

// Composite intent: walk hauler to source, pick up, walk to destination,
// deposit. One submission = one trip. Multi-resource hauls = multiple intents.
//
// Resolution validates the plan is well-formed *at submission time*:
//   - Hauler exists, is Idle.
//   - Source and dest tiles are in-bounds.
//   - Source and dest both currently have structures.
//   - Resource is meaningful (not None).
//
// CargoCapacity is derived from Role via UnitCargoCatalog — every role
// has a positive capacity, so there's no "no capacity" reject path.
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
    // M36 — the most this trip picks up; 0 = a full load. The haul queue
    // sizes the last trip to the remaining need (docs/hauling-queue-and-routes.md).
    public int Amount { get; }
    // M36 — the haul-queue job this trip serves (0 = a manual haul). Must be
    // the submitter's own job, so no one can credit deliveries to another's.
    public int JobId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public HaulIntent(int haulerId, TileCoord sourceTile, TileCoord destTile, Resource resource,
        int amount = 0, int jobId = 0)
    {
        JobId = jobId;
        HaulerId = haulerId;
        SourceTile = sourceTile;
        DestTile = destTile;
        Resource = resource;
        Amount = amount;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(HaulerId, out var hauler))
            return IntentOutcome.Reject($"hauler {HaulerId} does not exist");
        if (hauler.OwnerId != PlayerId)
            return IntentOutcome.Reject($"hauler {HaulerId} not owned by player {PlayerId}");
        if (hauler.GroupId is not null)
            return IntentOutcome.Reject($"hauler {HaulerId} is in group {hauler.GroupId}");
        if (hauler.IsEmbarked)
            return IntentOutcome.Reject($"hauler {HaulerId} is embarked on boat {hauler.EmbarkedOn}");
        if (hauler.Activity != Activity.Idle)
            return IntentOutcome.Reject("hauler is not Idle");
        // A hauler must start empty — picking up would overwrite (and destroy)
        // an existing load. Unload it first (UnloadCargoIntent).
        if (hauler.CargoAmount > 0)
            return IntentOutcome.Reject(
                $"hauler {HaulerId} is carrying {hauler.CargoAmount} {hauler.CargoResource} — unload first");
        // M40 — a salvage trip names no resource (it takes everything).
        var salvage = JobId != 0 && world.HaulJobs.TryGetValue(JobId, out var sj)
            && sj.Kind == Sim.Core.Hauling.HaulJobKind.Salvage;
        if (Resource == Resource.None && !salvage)
            return IntentOutcome.Reject("resource is None");
        if (Amount < 0)
            return IntentOutcome.Reject($"amount {Amount} is negative");
        if (JobId != 0
            && (!world.HaulJobs.TryGetValue(JobId, out var job) || job.OwnerId != PlayerId))
            return IntentOutcome.Reject($"haul job {JobId} is not one of player {PlayerId}'s jobs");
        // M36 — a QUEUE trip takes only a genuinely free body: not marching, not
        // claimed by an automation order, not on a route, not bound to a goal.
        // The queue driver and the substrate think in the same tick and neither
        // sees the other's pending claims, so both can pick one hauler; checked
        // here, at resolution, whichever resolves second is refused cleanly
        // instead of the two tearing the unit between them. A manual haul
        // (JobId 0) is the player's word and keeps the plain Idle rule.
        if (JobId != 0 && (!Sim.Core.Automation.ClaimLedger.IsDormant(world, hauler) || hauler.Goal is not null))
            return IntentOutcome.Reject($"hauler {HaulerId} is spoken for (claimed, marching, or on another errand)");
        if (!world.Grid.InBounds(SourceTile))
            return IntentOutcome.Reject($"source {SourceTile.X},{SourceTile.Y} out of bounds");
        if (!world.Grid.InBounds(DestTile))
            return IntentOutcome.Reject($"dest {DestTile.X},{DestTile.Y} out of bounds");
        // M7: source can be either a Structure OR a ground pile (capture
        // economy — cargo dropped on the tile by a dying laden unit).
        var hasStructureSource = world.Structures.ContainsKey(SourceTile);
        var hasGroundSource = world.GroundResources.TryGetValue(SourceTile, out var srcPile)
            && srcPile.ContainsKey(Resource);
        // M40 — a salvage trip goes to LOOK: whether anything is still there is
        // learned on arrival (knowledge from presence), not checked here.
        if (!salvage && !hasStructureSource && !hasGroundSource)
            return IntentOutcome.Reject(
                $"no source for {Resource} at {SourceTile.X},{SourceTile.Y} (no structure, no ground pile)");
        if (!world.Structures.ContainsKey(DestTile))
            return IntentOutcome.Reject($"no structure at dest {DestTile.X},{DestTile.Y}");

        // M28 — BOAT FREIGHT: a Water-traversal hauler serves DOCKS only
        // (the sole land/water cargo interface — docs/boats.md update). It
        // sails to each dock's SLIP; the cargo moves against the dock. The
        // MoveTarget probe doubles as the dock check.
        if (hauler.Traversal == Traversal.Water)
        {
            if (HaulStops.MoveTarget(world, hauler, SourceTile) is null)
                return IntentOutcome.Reject(
                    $"boat freight needs a Dock source; {SourceTile.X},{SourceTile.Y} has none");
            if (HaulStops.MoveTarget(world, hauler, DestTile) is null)
                return IntentOutcome.Reject(
                    $"boat freight needs a Dock dest; {DestTile.X},{DestTile.Y} has none");
        }

        hauler.TrySetActivity(Activity.Hauling);

        // M4 Phase A: state-anchored haul orchestration. HaulPlan carries the
        // route shape; MoveArrivalEvent dispatches pickup/deposit on final
        // arrival by reading the plan. No OnFinalArrival event field.
        hauler.HaulPlan = new HaulPlan(SourceTile, DestTile, Resource, HaulPhase.ToSource, Amount, JobId);

        if (HaulStops.AtStop(world, hauler, SourceTile))
        {
            // Already at the stop — go straight to pickup.
            sim.Schedule(sim.Now,
                new HaulPickupEvent(HaulerId, SourceTile, DestTile, Resource, hauler.AssignmentEpoch));
        }
        else
        {
            MoveIntent.BeginMove(sim, hauler, HaulStops.MoveTarget(world, hauler, SourceTile)!.Value);
            // FAIL CLEAN when no route exists (a boat whose source dock sits
            // on another lake; a walled-off yard): without this the hauler
            // stays Hauling forever with no arrival scheduled — a zombie
            // every selector ignores.
            if (hauler.NextArrivalSeq is null)
            {
                hauler.HaulPlan = null;
                hauler.TrySetActivity(Activity.Idle);
                return IntentOutcome.Reject(
                    $"no route from {hauler.Position.X},{hauler.Position.Y} to the {Resource} source");
            }
        }

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"HaulIntent(hauler={HaulerId}, {SourceTile.X},{SourceTile.Y} -> {DestTile.X},{DestTile.Y}, {Resource})";
}
