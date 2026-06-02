namespace Sim.Core.Movement;

public sealed class MoveIntent : Intent
{
    public int UnitId { get; }
    public TileCoord Destination { get; }

    public MoveIntent(int unitId, TileCoord destination)
    {
        UnitId = unitId;
        Destination = destination;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        if (!sim.World.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
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

        ScheduleNextStep(sim, unit, Destination);
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

    // Shared by MoveIntent.Resolve, MoveArrivalEvent.Apply, and HaulIntent:
    // re-path from the unit's current tile to the final destination, schedule
    // the next arrival. The optional onFinalArrival hook is carried by every
    // intermediate MoveArrivalEvent so it fires when the chain reaches the end.
    internal static void ScheduleNextStep(
        Simulation sim,
        Unit unit,
        TileCoord finalDest,
        ScheduledEvent? onFinalArrival = null)
    {
        if (unit.Position == finalDest) return;
        // Road-aware cost: A* prefers high-condition routes when their
        // effective cost wins. Pure read — captures sim.World and sim.Now in
        // the closure but never mutates road state. See Roads/Road.cs.
        var world = sim.World;
        var now = sim.Now;
        var path = Pathfinding.FindPath(
            world.Grid,
            unit.Position,
            finalDest,
            tile => Road.EffectiveCost(world, tile, now));
        if (path is null || path.Count < 2) return;
        var next = path[1];
        // Per-step arrival time uses the same effective cost so the unit
        // actually moves faster along the road, not just chooses to walk it.
        var arrival = sim.Now + Road.EffectiveCost(world, next, now);
        sim.Schedule(arrival, new MoveArrivalEvent(unit.Id, next, finalDest, unit.AssignmentEpoch)
        {
            OnFinalArrival = onFinalArrival,
        });
    }

    public override string Describe() =>
        $"MoveIntent(unit={UnitId} -> {Destination.X},{Destination.Y})";
}
