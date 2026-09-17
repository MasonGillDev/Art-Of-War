using Sim.Core.World;

namespace Sim.Core.Rivers;

// River reads. ALL PURE — called from A* (many times per query), from the
// hop scheduler and from views. Nothing here writes; the mask is set once at
// genesis and restored by Snapshot. See docs/rivers.md.
public static class River
{
    // The edge of `from` that a hop to `to` crosses, or None if the two are
    // not 4-adjacent (same tile, diagonal, far apart). Non-adjacent pairs are
    // "no edge" rather than an error because PlanCost is also asked to price
    // a tile in isolation (tests, tooling) with from == to.
    public static RiverEdge EdgeBetween(TileCoord from, TileCoord to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        if (dx == 0 && dy == -1) return RiverEdge.North;
        if (dx == 1 && dy == 0)  return RiverEdge.East;
        if (dx == 0 && dy == 1)  return RiverEdge.South;
        if (dx == -1 && dy == 0) return RiverEdge.West;
        return RiverEdge.None;
    }

    // The same edge seen from the other tile.
    public static RiverEdge Opposite(RiverEdge e) => e switch
    {
        RiverEdge.North => RiverEdge.South,
        RiverEdge.South => RiverEdge.North,
        RiverEdge.East  => RiverEdge.West,
        RiverEdge.West  => RiverEdge.East,
        _ => RiverEdge.None,
    };

    // Does the hop from → to cross a river? True iff the shared edge carries
    // one. Reads `from`'s mask; the symmetry invariant means `to`'s would say
    // the same. PURE READ.
    public static bool Crosses(TileGrid grid, TileCoord from, TileCoord to)
    {
        var edge = EdgeBetween(from, to);
        if (edge == RiverEdge.None) return false;
        if (!grid.InBounds(from)) return false;
        return (grid.RiverEdgesAt(from) & edge) != 0;
    }

    // The crossing surcharge for a Foot hop. The one seam future bridges and
    // fords plug into (a bridge returns 0, a ford something smaller).
    public static int CrossingCostFor(TileGrid grid, TileCoord from, TileCoord to) =>
        Crosses(grid, from, to) ? RiverConstants.CrossingCost : 0;

    // Count of river tiles (any bit set). Tooling / smoke output only.
    public static int CountRiverTiles(TileGrid grid)
    {
        var n = 0;
        for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
                if (grid.RiverEdgesAt(new TileCoord(x, y)) != RiverEdge.None) n++;
        return n;
    }
}
