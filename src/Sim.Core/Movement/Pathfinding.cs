namespace Sim.Core.Movement;

public static class Pathfinding
{
    // Grid A* over 4-neighborhood with terrain cost paid on entering a tile.
    // Priority tuple is (f, x, y) so same-f tiles break ties deterministically.
    // Heuristic: Manhattan — admissible as long as every terrain cost is >= 1.
    public static List<TileCoord>? FindPath(TileGrid grid, TileCoord start, TileCoord goal)
    {
        if (!grid.InBounds(start) || !grid.InBounds(goal)) return null;
        if (start == goal) return new List<TileCoord> { start };

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
                var tentative = currentG + grid.TerrainCost(n);
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
