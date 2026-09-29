using Sim.Core.Battlefields;

namespace Sim.Core.Movement;

public static class Pathfinding
{
    // Grid A* over 4-neighborhood with cost paid PER HOP: costFn(from, to).
    // Priority tuple is (f, x, y) so same-f tiles break ties deterministically.
    // Heuristic: Manhattan — admissible as long as every cost is >= 1.
    //
    // The cost is an EDGE cost since rivers (docs/rivers.md): fording is a
    // property of the pair of tiles, not of the tile entered. Most of the
    // cost still comes from the destination (biome, road, crowding); the
    // edge term is what a per-tile delegate could not express.
    //
    // costFn MUST be a pure read — A* will call it many times per tile in a
    // single query. A costFn that mutated state would inject nondeterminism
    // straight into the hash via path queries. See docs/persistence-model.md
    // and Roads/Road.cs (pure-read wall).
    public static List<TileCoord>? FindPath(
        TileGrid grid,
        TileCoord start,
        TileCoord goal,
        Func<TileCoord, TileCoord, int> costFn) => FindPath(grid, start, goal, costFn, null, null);

    // With a tile's SHAPE (docs/structure-footprints.md, "World movement"): on a
    // tile whose footprint separates its edges (a castle, a canal), which edge
    // the path came in by decides where it can go next, so the search state is
    // (tile, entry edge) there. Everywhere else the entry edge doesn't matter
    // and the state is the tile alone ("any"), so open ground costs what it
    // always did. `startEntry` is the edge the mover came onto `start` by (null
    // = unknown: born or placed there, any way out it could have come in by).
    // The goal must be a tile the path can stop on from the edge it arrives by.
    public static List<TileCoord>? FindPath(
        TileGrid grid,
        TileCoord start,
        TileCoord goal,
        Func<TileCoord, TileCoord, int> costFn,
        CrossingRule? rule,
        Sim.Core.Battlefields.Heading? startEntry)
    {
        if (!grid.InBounds(start) || !grid.InBounds(goal)) return null;
        if (start == goal) return new List<TileCoord> { start };

        const int Any = -1;
        int EntryOn(TileCoord t, int entry) => rule is not null && rule.Restricted(t) ? entry : Any;
        Sim.Core.Battlefields.Heading? AsHeading(int e) => e == Any ? null : (Sim.Core.Battlefields.Heading)e;

        var first = (start, EntryOn(start, startEntry is { } se ? (int)se : Any));
        var open = new PriorityQueue<(TileCoord T, int E), (int f, int x, int y, int e)>();
        var gScore = new Dictionary<(TileCoord T, int E), int> { [first] = 0 };
        var cameFrom = new Dictionary<(TileCoord T, int E), (TileCoord T, int E)>();
        open.Enqueue(first, (Heuristic(start, goal), start.X, start.Y, first.Item2));

        while (open.Count > 0)
        {
            var current = open.Dequeue();
            var (tile, entry) = current;
            if (tile == goal)
            {
                if (rule is null || !rule.Restricted(tile) || rule.CanStay(tile, AsHeading(entry)))
                    return Reconstruct(cameFrom, current);
                continue;
            }

            var currentG = gScore[current];
            foreach (var n in grid.Neighbors(tile))
            {
                var step = costFn(tile, n);
                // Impassable tile — skip. Without this, `currentG + step`
                // overflows int and wraps to a negative "better" gScore,
                // and A* re-explores forever. (M12: BoatMovementCost
                // returns Impassable on every land biome, so this guard
                // is now reachable in normal play.)
                if (step >= Sim.Core.World.Biomes.Impassable) continue;
                var nEntry = Any;
                if (rule is not null)
                {
                    var h = Sim.Core.Battlefields.Battlefields.EdgeToward(tile, n)!.Value;
                    if (rule.Restricted(tile) && !rule.CanPass(tile, AsHeading(entry), h)) continue;
                    var into = h.Opposite();
                    if (!rule.CanEnter(n, into)) continue;
                    nEntry = EntryOn(n, (int)into);
                }
                var next = (n, nEntry);
                var tentative = currentG + step;
                if (!gScore.TryGetValue(next, out var existing) || tentative < existing)
                {
                    gScore[next] = tentative;
                    cameFrom[next] = current;
                    var f = tentative + Heuristic(n, goal);
                    open.Enqueue(next, (f, n.X, n.Y, nEntry));
                }
            }
        }
        return null;
    }

    // Tile-cost convenience: for callers that genuinely price TILES
    // (reachability checks, tests, the M0-era default of plain biome cost).
    // Wraps the edge form; there is one A*.
    public static List<TileCoord>? FindPath(
        TileGrid grid,
        TileCoord start,
        TileCoord goal,
        Func<TileCoord, int>? costFn = null)
    {
        costFn ??= tile => grid.TerrainCost(tile);
        return FindPath(grid, start, goal, (_, to) => costFn(to));
    }

    private static int Heuristic(TileCoord a, TileCoord b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static List<TileCoord> Reconstruct(Dictionary<(TileCoord T, int E), (TileCoord T, int E)> cameFrom, (TileCoord T, int E) end)
    {
        var path = new List<TileCoord> { end.T };
        while (cameFrom.TryGetValue(end, out var prev))
        {
            end = prev;
            path.Add(end.T);
        }
        path.Reverse();
        return path;
    }
}
