namespace Sim.Core.World;

// The boundary between two 4-adjacent tiles, as a value. One vocabulary for
// everything that lives BETWEEN tiles (docs/roads-on-edges.md):
//
//   BARRIERS  (rivers, bridges, later fords/fences) sit ON the boundary and
//             are CROSSED by the hop A -> B. Stored as bits on both tiles
//             (RiverEdge mask, symmetric by invariant), read from `from`.
//   CONNECTIONS (roads) are the lane WALKED by the hop A -> B. Stored once,
//             keyed by this struct, in canonical form.
//
// Canonical form: A is the west tile (horizontal edge, Axis.East) or the
// north tile (vertical edge, Axis.South). Between(a, b) == Between(b, a),
// so a dictionary keyed by TileEdge has exactly one owner per boundary and
// no symmetry invariant to validate. Non-adjacent pairs are not an edge:
// TryBetween returns false rather than throwing, because PlanCost is also
// asked to price a tile in isolation (from == to) by tests and tooling.
public readonly record struct TileEdge(TileCoord A, TileCoord B)
{
    public enum Axis : byte { East = 0, South = 1 }

    // East when B is east of A; South when B is south of A.
    public Axis Direction => B.X == A.X ? Axis.South : Axis.East;

    public static bool TryBetween(TileCoord from, TileCoord to, out TileEdge edge)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        if (dx == 1 && dy == 0)  { edge = new TileEdge(from, to); return true; }
        if (dx == -1 && dy == 0) { edge = new TileEdge(to, from); return true; }
        if (dx == 0 && dy == 1)  { edge = new TileEdge(from, to); return true; }
        if (dx == 0 && dy == -1) { edge = new TileEdge(to, from); return true; }
        edge = default;
        return false;
    }

    // Throws on a non-adjacent pair; use TryBetween where from == to is legal.
    public static TileEdge Between(TileCoord from, TileCoord to) =>
        TryBetween(from, to, out var e)
            ? e
            : throw new ArgumentException($"{from} and {to} are not 4-adjacent.");

    // The owner tile's edge in axis form: (owner, East) or (owner, South).
    public static TileEdge FromOwner(TileCoord owner, Axis axis) =>
        axis == Axis.East
            ? new TileEdge(owner, new TileCoord(owner.X + 1, owner.Y))
            : new TileEdge(owner, new TileCoord(owner.X, owner.Y + 1));

    // The four edges incident to a tile (for "remove every arc touching this
    // tile" when it floods). Order N, E, S, W for deterministic iteration.
    public static IEnumerable<TileEdge> Around(TileCoord t)
    {
        yield return new TileEdge(new TileCoord(t.X, t.Y - 1), t);
        yield return new TileEdge(t, new TileCoord(t.X + 1, t.Y));
        yield return new TileEdge(t, new TileCoord(t.X, t.Y + 1));
        yield return new TileEdge(new TileCoord(t.X - 1, t.Y), t);
    }

    // ---- the per-tile side view, shared with the river mask ------------

    // The side of `from` that the hop from -> to crosses, or None if the two
    // are not 4-adjacent (same tile, diagonal, far apart).
    public static RiverEdge SideBetween(TileCoord from, TileCoord to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        if (dx == 0 && dy == -1) return RiverEdge.North;
        if (dx == 1 && dy == 0)  return RiverEdge.East;
        if (dx == 0 && dy == 1)  return RiverEdge.South;
        if (dx == -1 && dy == 0) return RiverEdge.West;
        return RiverEdge.None;
    }

    // The same side seen from the other tile.
    public static RiverEdge Opposite(RiverEdge e) => e switch
    {
        RiverEdge.North => RiverEdge.South,
        RiverEdge.South => RiverEdge.North,
        RiverEdge.East  => RiverEdge.West,
        RiverEdge.West  => RiverEdge.East,
        _ => RiverEdge.None,
    };

    // This edge as seen from one of its two tiles.
    public RiverEdge SideOf(TileCoord tile) =>
        tile == A ? SideBetween(A, B) : tile == B ? SideBetween(B, A) : RiverEdge.None;
}
