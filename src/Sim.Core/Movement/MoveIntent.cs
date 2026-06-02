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
        ScheduleNextStep(sim, unit, Destination);
        return IntentOutcome.Applied;
    }

    // Shared by MoveIntent.Resolve and MoveArrivalEvent.Apply: re-path from the
    // unit's current tile to the final destination, schedule the next arrival.
    // Internal so other movement-related code can chain steps without exposing
    // it as a public API.
    internal static void ScheduleNextStep(Simulation sim, Unit unit, TileCoord finalDest)
    {
        if (unit.Position == finalDest) return;
        var path = Pathfinding.FindPath(sim.World.Grid, unit.Position, finalDest);
        if (path is null || path.Count < 2) return;
        var next = path[1];
        var arrival = sim.Now + sim.World.Grid.TerrainCost(next);
        sim.Schedule(arrival, new MoveArrivalEvent(unit.Id, next, finalDest));
    }

    public override string Describe() =>
        $"MoveIntent(unit={UnitId} -> {Destination.X},{Destination.Y})";
}
