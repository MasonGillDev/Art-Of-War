namespace Sim.Core.Movement;

public sealed class MoveIntent : Intent
{
    public int UnitId { get; }
    public TileCoord Destination { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public MoveIntent(int unitId, TileCoord destination)
    {
        UnitId = unitId;
        Destination = destination;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        if (!sim.World.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.GroupId is not null)
            return IntentOutcome.Reject($"unit {UnitId} is in group {unit.GroupId}");
        // M12 — embarked units are off-tile passengers; solo intents
        // are blocked until the boat disembarks them. (Note: boats
        // themselves use MoveIntent for water travel; their Traversal
        // selects the BoatMovementCost table inside BeginMove. MoveBoat
        // is not a separate intent type.)
        if (unit.IsEmbarked)
            return IntentOutcome.Reject($"unit {UnitId} is embarked on boat {unit.EmbarkedOn}");
        // M8 follow-up: breeding is a commitment, not a retaskable assignment.
        // A parent in an active breeding cycle is locked to the house until
        // BirthEvent (or stop-on-removal via combat / aging death) frees them.
        // No cancel — the player can't back out. This is the one case where
        // MoveIntent is NOT authoritative.
        if (Sim.Core.Population.Population.GetActiveBreedingFor(sim.World, UnitId) is { } breedingHouse)
            return IntentOutcome.Reject(
                $"unit {UnitId} is breeding at house ({breedingHouse.At.X},{breedingHouse.At.Y}) and cannot be moved");
        if (!sim.World.Grid.InBounds(Destination))
            return IntentOutcome.Reject($"destination {Destination.X},{Destination.Y} out of bounds");

        // Move-on-busy: a MoveIntent is authoritative — the player has retasked
        // this unit, and any structure depending on them gets cleaned up.
        // Cargo on a Hauling unit stays with them (they walk holding it; the
        // player can issue a new haul later). Pending per-unit events from the
        // old task fence on AssignmentEpoch and no-op when they fire.
        if (unit.Activity != Activity.Idle)
            CleanUpAssignment(sim, unit);   // bumps epoch via TrySetActivity(Idle)
        else
            unit.BumpEpoch();                // explicit bump for Idle→Idle move so
                                             // any prior move chain's MoveArrivalEvents fence out

        BeginMove(sim, unit, Destination);
        return IntentOutcome.Applied;
    }

    private static void CleanUpAssignment(Simulation sim, Unit unit)
    {
        var prevAssignment = unit.Assignment;
        var prevActivity = unit.Activity;

        // Idle the unit FIRST so the structure-side check below sees the
        // updated builder count / worker count.
        unit.TrySetActivity(Activity.Idle);

        if (prevAssignment is not TileCoord at) return;
        if (!sim.World.Structures.TryGetValue(at, out var s)) return;

        switch (prevActivity)
        {
            case Activity.Working when s is Extractor ex:
                ex.Workers.Remove(unit.Id);
                // Production goes dormant naturally on the next ProductionTick fire
                // (it sees Workers.Count and decides). No active intervention needed.
                break;
            case Activity.Building when s is ConstructionSite site:
                // If this builder leaving drops the site below requirement,
                // pause the build. The previously-scheduled BuildCompleteEvent
                // will fence via site.ScheduledCompletion when it fires.
                if (site.IsActive && site.BuildersPresent(sim.World) < site.RequiredBuilderCount)
                    site.Pause(sim.Now);
                break;
        }
    }

    // M4 Phase A: start a new movement chain to `finalDest`. Computes the full
    // committed path once, stores it on the unit, and schedules the first
    // MoveArrivalEvent. Called by:
    //   * MoveIntent.Resolve (player-issued move)
    //   * HaulIntent.Resolve (move to source)
    //   * HaulPickupEvent.Apply (after pickup, move to dest)
    //
    // The path is STORED on the unit (Unit.PathRemaining) rather than
    // recomputed per hop or per restore. Recomputing against current road
    // conditions could yield a different path than the live sim took,
    // breaking determinism.
    internal static void BeginMove(Simulation sim, Unit unit, TileCoord finalDest)
    {
        if (unit.Position == finalDest)
        {
            // Already at destination — clear any stale path anchors.
            unit.PathRemaining = null;
            unit.PathFinalDest = null;
            unit.NextArrivalTick = null;
            unit.NextArrivalSeq  = null;
            return;
        }

        var world = sim.World;
        var now = sim.Now;
        // FOG-AWARE PLANNING: the cost the planner sees on each tile is what
        // the OWNING PLAYER could see — own units always counted, non-own
        // only on currently-visible tiles. A* will route around visible
        // crowds but cannot see through the fog. The unit may stumble into
        // hidden congestion (paying ground-truth ExecutionCost) — that's the
        // "cost of ignorance" gameplay loop. See docs/movement-cost.md.
        var visibleTiles = View.VisibleTiles(world, unit.OwnerId);
        var trav = unit.Traversal;
        var path = Pathfinding.FindPath(
            world.Grid,
            unit.Position,
            finalDest,
            tile => MovementCost.PlanCost(world, tile, unit.OwnerId, visibleTiles, now, trav));
        if (path is null || path.Count < 2)
        {
            unit.PathRemaining = null;
            unit.PathFinalDest = null;
            unit.NextArrivalTick = null;
            unit.NextArrivalSeq  = null;
            return;
        }

        // path[0] == unit.Position; the rest is the committed itinerary.
        unit.PathRemaining = path.Skip(1).ToList();
        unit.PathFinalDest = finalDest;
        ScheduleNextHop(sim, unit);
    }

    // Pop nothing — just schedule the arrival for PathRemaining[0]. Used by
    // BeginMove (initial schedule) and MoveArrivalEvent.Apply (per-hop
    // continuation after popping the head).
    internal static void ScheduleNextHop(Simulation sim, Unit unit)
    {
        if (unit.PathRemaining is null || unit.PathRemaining.Count == 0 || unit.PathFinalDest is null)
            return;
        var next = unit.PathRemaining[0];
        var world = sim.World;
        // GROUND-TRUTH HOP COST: includes BOTH source and destination
        // crowding (whichever is more crowded), regardless of fog. The
        // unit pays the real cost of this hop even if it differs from
        // what the plan assumed. See MovementCost.ExecutionCost.
        var arrival = sim.Now + MovementCost.ExecutionCost(world, unit.Position, next, sim.Now, unit.Traversal);
        unit.NextArrivalTick = arrival;
        unit.NextArrivalSeq  = sim.Schedule(arrival,
            new MoveArrivalEvent(unit.Id, next, unit.PathFinalDest.Value, unit.AssignmentEpoch));
    }

    public override string Describe() =>
        $"MoveIntent(unit={UnitId} -> {Destination.X},{Destination.Y})";
}
