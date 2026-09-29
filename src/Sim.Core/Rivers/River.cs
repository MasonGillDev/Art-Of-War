using Sim.Core.World;

namespace Sim.Core.Rivers;

// River reads. ALL PURE — called from A* (many times per query), from the
// hop scheduler and from views. Nothing here writes; the mask is set once at
// genesis and restored by Snapshot. See docs/rivers.md.
public static class River
{
    // The side of `from` that a hop to `to` crosses. The geometry lives on
    // TileEdge (docs/roads-on-edges.md: one edge vocabulary for rivers,
    // bridges and roads); these forward so river callers read naturally.
    public static RiverEdge EdgeBetween(TileCoord from, TileCoord to) => TileEdge.SideBetween(from, to);
    public static RiverEdge Opposite(RiverEdge e) => TileEdge.Opposite(e);

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

    // Same question asked of an edge value (shared with roads and bridges).
    public static bool Crosses(TileGrid grid, TileEdge edge) => Crosses(grid, edge.A, edge.B);

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
