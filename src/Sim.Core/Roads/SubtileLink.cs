using Sim.Core.Battlefields;

namespace Sim.Core.Roads;

// The connection between two 4-adjacent SUBTILES, as a value (M43 step 3,
// docs/subtile-movement.md: "roads live on subtiles"). A road is a set of worn
// LINKS: the step a unit takes between two neighbouring subtiles. Traffic wears the
// steps it actually takes and a worn step is cheaper, so trails form where people
// really walk, right up to a building's entrance. A road runs straight or bends at
// 90° because a link only ever joins subtiles that share a side.
//
// Canonical form: A is the west subtile (horizontal link, Axis.East) or the north
// subtile (vertical link, Axis.South). Between(a, b) == Between(b, a), so a
// dictionary keyed by SubtileLink has exactly one owner per link. Subtiles are named
// on the whole map (WorldSubtile: tile × 4 + subtile), so a link across a tile edge
// is just another link.
//
// This replaces the M34 model, where a road was an arc between tile centres
// (TileEdge) and could only join world tiles (docs/roads-on-edges.md).
public readonly record struct SubtileLink(WorldSubtile A, WorldSubtile B)
{
    public enum Axis : byte { East = 0, South = 1 }

    // East when B is east of A; South when B is south of A.
    public Axis Direction => B.X == A.X ? Axis.South : Axis.East;

    public static bool TryBetween(WorldSubtile from, WorldSubtile to, out SubtileLink link)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        if (dx == 1 && dy == 0)  { link = new SubtileLink(from, to); return true; }
        if (dx == -1 && dy == 0) { link = new SubtileLink(to, from); return true; }
        if (dx == 0 && dy == 1)  { link = new SubtileLink(from, to); return true; }
        if (dx == 0 && dy == -1) { link = new SubtileLink(to, from); return true; }
        link = default;
        return false;
    }

    // Throws on a non-adjacent pair; use TryBetween where from == to is legal.
    public static SubtileLink Between(WorldSubtile from, WorldSubtile to) =>
        TryBetween(from, to, out var l)
            ? l
            : throw new ArgumentException($"{from} and {to} are not 4-adjacent.");

    // The owner subtile's link in axis form: (owner, East) or (owner, South).
    public static SubtileLink FromOwner(WorldSubtile owner, Axis axis) =>
        axis == Axis.East
            ? new SubtileLink(owner, new WorldSubtile(owner.X + 1, owner.Y))
            : new SubtileLink(owner, new WorldSubtile(owner.X, owner.Y + 1));

    // The tile(s) the link touches: one, or two when it crosses a tile edge.
    public bool Touches(TileCoord tile) => A.Tile == tile || B.Tile == tile;
}
