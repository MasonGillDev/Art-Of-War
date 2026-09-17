namespace Sim.Core.World;

// Which EDGES of a tile carry a river. A river lies BETWEEN tiles, along the
// boundary, never through a tile's centre — so "crossing" is a property of a
// hop (from, to), not of a tile, and walking along the bank never pays. The
// mask on a tile and the mask on its neighbour agree by invariant: bit North
// on (x, y) ⇔ bit South on (x, y-1). Genesis validates and throws otherwise.
//
// APPEND-ONLY (serialized as a byte since snapshot v30). docs/rivers.md.
[Flags]
public enum RiverEdge : byte
{
    None  = 0,
    North = 1,   // the edge shared with (x, y-1)
    East  = 2,   // the edge shared with (x+1, y)
    South = 4,   // the edge shared with (x, y+1)
    West  = 8,   // the edge shared with (x-1, y)
}
