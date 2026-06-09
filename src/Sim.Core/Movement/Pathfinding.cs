namespace Sim.Core.Movement;

public static class Pathfinding
{
    // Grid A* over 4-neighborhood with cost paid on entering a tile.
    // Priority tuple is (f, x, y) so same-f tiles break ties deterministically.
    // Heuristic: Manhattan — admissible as long as every cost is >= 1.
    //
    // The optional costFn lets the caller supply a richer cost source — e.g.
    // Road.EffectiveCost(world, tile, now) to make A* prefer high-condition
    // roads. Defaults to plain biome cost when omitted, so M0-era callers
    // and tests that don't care about roads still work unchanged.
    //
    // costFn MUST be a pure read — A* will call it many times per tile in a
    // single query. A costFn that mutated state would inject nondeterminism
    // straight into the hash via path queries. See docs/persistence-model.md
    // and Roads/Road.cs (pure-read wall).
    public static List<TileCoord>? FindPath(
        TileGrid grid,
        TileCoord start,
        TileCoord goal,
        Func<TileCoord, int>? costFn = null)
    {
        if (!grid.InBounds(start) || !grid.InBounds(goal)) return null;
        if (start == goal) return new List<TileCoord> { start };

        costFn ??= tile => grid.TerrainCost(tile);

        var open = new PriorityQueue<TileCoord, (int f, int x, int y)>();
        var gScore = new Dictionary<TileCoord, int> { [start] = 0 };
        var cameFrom = new Dictionary<TileCoord, TileCoord>();
        open.Enqueue(start, (Heuristic(start, goal), start.X, start.Y));

        while (open.Count > 0)
        {
            var current = open.Dequeue();
            if (current == goal) return Reconstruct(cameFrom, current);

            var currentG = gScore[current];
            foreach (var n in grid.Neighbors(current))
            {
                var step = costFn(n);
                // Impassable tile — skip. Without this, `currentG + step`
                // overflows int and wraps to a negative "better" gScore,
                // and A* re-explores forever. (M12: BoatMovementCost
                // returns Impassable on every land biome, so this guard
                // is now reachable in normal play.)
                if (step >= Sim.Core.World.Biomes.Impassable) continue;
                var tentative = currentG + step;
                if (!gScore.TryGetValue(n, out var existing) || tentative < existing)
                {
                    gScore[n] = tentative;
                    cameFrom[n] = current;
                    var f = tentative + Heuristic(n, goal);
                    open.Enqueue(n, (f, n.X, n.Y));
                }
            }
        }
        return null;
    }

    private static int Heuristic(TileCoord a, TileCoord b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static List<TileCoord> Reconstruct(Dictionary<TileCoord, TileCoord> cameFrom, TileCoord end)
    {
        var path = new List<TileCoord> { end };
        while (cameFrom.TryGetValue(end, out var prev))
        {
            end = prev;
            path.Add(end);
        }
        path.Reverse();
        return path;
    }
}
