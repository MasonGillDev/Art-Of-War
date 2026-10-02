using Sim.Core.Battlefields;

namespace Sim.Core.Movement;

// What one search did, for tests and the benchmark.
public sealed class SubtilePathStats
{
    public int Expanded;          // subtiles taken off the open list, across every search made
    public int TileRouteLength;   // tiles in the shape-aware tile route, 0 if none was found
    public bool UsedCorridor;     // the corridor search found the path
    public bool FellBack;         // the corridor found nothing and the whole grid was searched
}

// M43 step 1 (docs/m43-status.md): the one pathfinder. A search over SUBTILES (the
// whole map is a grid of them, tile × 4), decided by the step-0 measurement:
//
//   1. Find the TILE route first, shape-aware: the crossing masks (CrossingRule) route a
//      wall line to a gate or round its end, a castle only by its gate, a canal only at a
//      bridge. Cheap: 1/16 the nodes.
//   2. A* over subtiles inside that route's tiles plus a one-tile margin (the corridor).
//      About 10× less work than the whole grid on long marches, and its cost barely grows
//      with distance.
//   3. If the corridor finds nothing (the tile route was wrong about a shape), the whole
//      grid is searched, so a path that exists is always found.
//
// Every step is judged by SubtileStepRules, the same rules the walk applies, so the plan
// can only be wrong about what the player couldn't see (fog: structures they can't see
// plan as open ground, and the per-step ground-truth check stops the unit when it meets
// them). The cost is a quarter of the tile's foot hop, rounded up, plus the ford at a
// river edge. Units don't cost anything here: moving friends pass through each other, and
// who is standing where is the walk's business.
//
// A pure read (architecture §2.2): it never writes, so path queries can't perturb the
// hash. Ties break by (f, subtile index), so the same query always returns the same path.
public static class SubtilePathfinder
{
    // The margin, in tiles, round the tile route that the corridor search may use.
    public const int CorridorMargin = 1;

    // A search that takes more than this many subtiles off the open list gives up. A
    // whole-grid search of an unreachable goal on a big map would otherwise run through
    // every subtile; this bounds it (a 252×252 map has 1,016,064).
    public const int MaxExpanded = 600_000;

    // The steps from `start` to `goal` (exclusive of `start`, inclusive of `goal`), or
    // null if the goal can't be reached or isn't a place the mover may stand.
    // `visible` = the tiles the planner can see (structures elsewhere plan as open
    // ground); null = ground truth, everything counts (tests, the AI's own reads).
    public static List<WorldSubtile>? Find(
        GameWorld world, StepMover mover, WorldSubtile start, WorldSubtile goal,
        HashSet<TileCoord>? visible = null, SubtilePathStats? stats = null, bool useCorridor = true, long now = 0)
    {
        var grid = world.Grid;
        if (!grid.InBounds(start.Tile) || !grid.InBounds(goal.Tile)) return null;
        if (start == goal) return new List<WorldSubtile>();

        var rules = new SubtileStepRules(world, mover, visible);
        if (!rules.CanStand(goal)) return null;

        if (useCorridor && TileRoute(world, rules, mover, start.Tile, goal.Tile, visible) is { } route)
        {
            if (stats is not null) stats.TileRouteLength = route.Count;
            var allowed = new HashSet<TileCoord>();
            foreach (var t in route)
                for (var dy = -CorridorMargin; dy <= CorridorMargin; dy++)
                    for (var dx = -CorridorMargin; dx <= CorridorMargin; dx++)
                    {
                        var c = new TileCoord(t.X + dx, t.Y + dy);
                        if (grid.InBounds(c)) allowed.Add(c);
                    }
            var found = Search(world, rules, mover, start, goal, allowed, stats, now);
            if (found is not null)
            {
                if (stats is not null) stats.UsedCorridor = true;
                return found;
            }
            if (stats is not null) stats.FellBack = true;
        }
        return Search(world, rules, mover, start, goal, null, stats, now);
    }

    // The shape-aware tile route. Null if there isn't one (the goal tile is walled off by
    // shape as the tile-level rule sees it), which sends the caller to the whole grid.
    private static List<TileCoord>? TileRoute(
        GameWorld world, SubtileStepRules rules, StepMover mover, TileCoord from, TileCoord to, HashSet<TileCoord>? visible)
    {
        // Boats have no shaped tiles: docks and the like are the water's business here.
        CrossingRule? shape = mover.Traversal == Traversal.Water ? null
            : visible is null ? CrossingRule.GroundTruth(world, mover.Owner) : CrossingRule.Planning(world, mover.Owner, visible);
        return Pathfinding.FindPath(world.Grid, from, to, (a, b) => rules.TileHopCost(a, b), shape, null);
    }

    private static readonly (int Dx, int Dy)[] Around = { (0, -1), (1, 0), (0, 1), (-1, 0) };   // N, E, S, W

    // A* over subtiles, indexed y * width + x on the whole map; only in `allowed` tiles
    // when given. Heuristic: Manhattan distance × the cheapest step in the search area,
    // admissible while no step is cheaper than that (roads, step 3, will make some
    // cheaper: they will lower the floor and the corridor keeps the search small).
    private static List<WorldSubtile>? Search(
        GameWorld world, SubtileStepRules rules, StepMover mover, WorldSubtile start, WorldSubtile goal,
        HashSet<TileCoord>? allowed, SubtilePathStats? stats, long now)
    {
        var w = world.Grid.Width * Subtile.Size;
        static int Index(WorldSubtile s, int width) => s.Y * width + s.X;
        WorldSubtile At(int i, int width) => new(i % width, i / width);

        var floor = SubtileStepRules.CheapestStep(world, MinStep(world, mover.Traversal, allowed));
        var goalIndex = Index(goal, w);
        int H(WorldSubtile s) => (Math.Abs(s.X - goal.X) + Math.Abs(s.Y - goal.Y)) * floor;

        var open = new PriorityQueue<int, (int F, int I)>();
        var g = new Dictionary<int, int>();
        var cameFrom = new Dictionary<int, int>();
        var closed = new HashSet<int>();
        var startIndex = Index(start, w);
        g[startIndex] = 0;
        open.Enqueue(startIndex, (H(start), startIndex));
        var expanded = 0;

        while (open.Count > 0)
        {
            var ci = open.Dequeue();
            if (!closed.Add(ci)) continue;
            expanded++;
            if (stats is not null) stats.Expanded++;
            if (ci == goalIndex) return Reconstruct(cameFrom, ci, startIndex, w);
            if (expanded > MaxExpanded) return null;

            var c = At(ci, w);
            var cg = g[ci];
            foreach (var (dx, dy) in Around)
            {
                var n = new WorldSubtile(c.X + dx, c.Y + dy);
                if (n.X < 0 || n.Y < 0 || n.X >= w || n.Y >= world.Grid.Height * Subtile.Size) continue;
                var ni = Index(n, w);
                if (closed.Contains(ni)) continue;
                if (allowed is not null && !allowed.Contains(n.Tile)) continue;
                if (!rules.Allows(c, n)) continue;
                var t = cg + SubtileStepRules.StepCost(world, mover.Traversal, c, n, now);
                if (!g.TryGetValue(ni, out var existing) || t < existing)
                {
                    g[ni] = t;
                    cameFrom[ni] = ci;
                    open.Enqueue(ni, (t + H(n), ni));
                }
            }
        }
        return null;
    }

    // The cheapest a single step can cost in the search area (never 0).
    private static int MinStep(GameWorld world, Traversal trav, HashSet<TileCoord>? allowed)
    {
        var best = int.MaxValue;
        if (allowed is not null)
        {
            foreach (var t in allowed) best = Math.Min(best, (int)SubtileStepRules.StepTicks(world, t, trav));
        }
        else
        {
            for (var y = 0; y < world.Grid.Height; y++)
                for (var x = 0; x < world.Grid.Width; x++)
                    best = Math.Min(best, (int)SubtileStepRules.StepTicks(world, new TileCoord(x, y), trav));
        }
        return best == int.MaxValue ? 1 : Math.Max(1, best);
    }

    private static List<WorldSubtile> Reconstruct(Dictionary<int, int> cameFrom, int end, int start, int width)
    {
        var path = new List<WorldSubtile>();
        for (var i = end; i != start; i = cameFrom[i]) path.Add(new WorldSubtile(i % width, i / width));
        path.Reverse();
        return path;
    }
}
