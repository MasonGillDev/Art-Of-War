using Sim.Core.Battlefields;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Tests;

// M43 (docs/m43-status.md, "the test migration"): tests say "N x the step time", never a
// hard-coded tick. A tile order is a walk of subtile steps, so the time it takes is the sum
// of the step costs along the pathfinder's path, from where the unit stands to the free
// subtile nearest the tile's centre. This derives it from the same rules the sim walks by,
// so a retune of terrain, roads or the step rule never touches a test.
public static class TestMarch
{
    // The path a tile order gives `u` right now (the walk's steps), or null if it has none.
    public static List<WorldSubtile>? PathTo(GameWorld world, Unit u, TileCoord dest)
    {
        var mover = StepMover.Of(u);
        var rules = new SubtileStepRules(world, mover, null);
        if (Walk.PickGoal(world, u, rules, dest, null) is not { } goal) return null;
        var start = WorldSubtile.Of(u.Position, u.Subtile ?? new Subtile(1, 1));
        return SubtilePathfinder.Find(world, mover, start, goal);
    }

    // Ticks the walk takes from the moment it starts, on the ground as it is now.
    public static long TicksFor(GameWorld world, Unit u, TileCoord dest, long now = 0)
    {
        var path = PathTo(world, u, dest) ?? throw new InvalidOperationException($"no walk from {u.Position} to {dest}");
        var at = WorldSubtile.Of(u.Position, u.Subtile ?? new Subtile(1, 1));
        long total = 0;
        foreach (var step in path)
        {
            total += SubtileRoutes.StepCost(world, u, at, step, now + total);
            at = step;
        }
        return total;
    }

    // The steps of a walk (its length in subtiles).
    public static int StepsFor(GameWorld world, Unit u, TileCoord dest) =>
        PathTo(world, u, dest)?.Count ?? throw new InvalidOperationException($"no walk from {u.Position} to {dest}");

    // Run until `unit` is no longer walking (or `limit` ticks pass), one tick at a time: a walk
    // is steps of a few ticks, and Simulation.Run(until) doesn't advance Now past the last event.
    public static long RunWalk(Simulation sim, Unit unit, long limit = 100_000)
    {
        var end = sim.Now + limit;
        for (var t = sim.Now + 1; unit.IsWalking && t <= end; t++) sim.Run(until: t);
        return sim.Now;
    }
}
