namespace Sim.Core.Battlefields;

// M42 phase 3 — walk one of the player's units along a drawn chain of subtiles
// (docs/subtile-movement.md, "Subtile moves"). The unit takes the steps exactly as
// drawn, at quarter-hop pace, within the 3×3 block of tiles around where it
// starts. An EMPTY route cancels the unit's current route (how a player clears a
// blocked one before redrawing it).
//
// It retasks the unit the way MoveIntent does: a walk in flight is dropped,
// a job is released, a goal or a haul plan is countermanded. A unit on an open
// battlefield the route becomes its battle route, walked one subtile a turn.
public sealed class SubtileRouteIntent : Intent
{
    public int UnitId { get; }
    public IReadOnlyList<WorldSubtile> Route { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SubtileRouteIntent(int unitId, IReadOnlyList<WorldSubtile>? route)
    {
        UnitId = unitId;
        Route = route ?? Array.Empty<WorldSubtile>();
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        if (Sim.Core.Intents.Retask.Refusal(sim, unit) is { } refusal)
            return IntentOutcome.Reject(refusal);
        if (unit.Traversal == Traversal.Water)
            return IntentOutcome.Reject($"unit {UnitId} is a boat: boats don't take subtile routes");

        if (Route.Count == 0)
        {
            SubtileRoutes.Cancel(unit);
            return IntentOutcome.Applied;
        }
        if (SubtileRoutes.Check(world, unit, Route) is { } why)
            return IntentOutcome.Reject($"route refused: {why}");

        // Retask, as MoveIntent does (Sim.Core.Intents.Retask explains each step).
        Sim.Core.Intents.Retask.Release(sim, unit);

        // A drawn route replaces the unit's walk: it has no errand (PathFinalDest null).
        Walk.Stop(unit);

        if (unit.Board is not null)
        {
            // On an open board the same route is walked one subtile a turn, on the
            // beat: it becomes the unit's battle route.
            unit.SubtileRoute = Route.ToList();
            Battlefields.AdoptRoute(unit);
            if (Battlefields.TryGet(sim.World, unit, out var bf)) Battlefields.Wake(sim, bf);
            return IntentOutcome.Applied;
        }
        SubtileRoutes.Begin(sim, unit, Route);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"SubtileRouteIntent(unit={UnitId}, {Route.Count} steps)";
}
