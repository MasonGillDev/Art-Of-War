namespace Sim.Core.Battlefields;

// M41 — the battlefield grid (docs/battlefield-grid.md). When hostile units
// share a world tile, that tile opens as a 4×4 board of SUBTILES. Board
// positions exist only while the board is open; at world scale a unit is on
// a tile, never on a subtile.

// The four ways across a subtile edge. The order N, E, S, W is the tie-break
// order everywhere on the board (pathing, archer targets, withdraw edges),
// matching the spec's "ties broken N, E, S, W". North is y − 1, as on the
// world map (TileEdge.SideBetween).
public enum Heading : byte
{
    North = 0,
    East = 1,
    South = 2,
    West = 3,
}

public static class Headings
{
    // N, E, S, W: iterate this, never Enum.GetValues, so the order is the
    // spec's tie-break order by construction.
    public static readonly IReadOnlyList<Heading> All =
        new[] { Heading.North, Heading.East, Heading.South, Heading.West };

    public static int Dx(this Heading h) => h switch
    {
        Heading.East => 1,
        Heading.West => -1,
        _ => 0,
    };

    public static int Dy(this Heading h) => h switch
    {
        Heading.North => -1,
        Heading.South => 1,
        _ => 0,
    };

    public static Heading Opposite(this Heading h) => (Heading)(((int)h + 2) % 4);

    // The heading of a one-step move a → b, or null if they aren't 4-adjacent.
    public static Heading? Between(Subtile a, Subtile b)
    {
        foreach (var h in All)
            if (a.Step(h) == b) return h;
        return null;
    }
}

// A cell of the board. On the board X and Y are 0..3. A subtile one step
// outside an edge (X or Y of −1 or 4) is how the board names "across that
// edge": a unit waiting to come on stands there, a unit leaving steps there,
// and a unit that entered remembers it as the subtile it came from.
public readonly record struct Subtile(int X, int Y)
{
    public const int Size = 4;
    public const int Count = Size * Size;

    public bool IsOnBoard => X >= 0 && X < Size && Y >= 0 && Y < Size;

    // Row-major index, 0..15, for on-board subtiles only.
    public int Index => Y * Size + X;

    public Subtile Step(Heading h) => new(X + h.Dx(), Y + h.Dy());

    public static int Manhattan(Subtile a, Subtile b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    public bool IsAdjacentTo(Subtile other) => Manhattan(this, other) == 1;

    // True when this on-board subtile touches edge `h` (the row or column a
    // unit must stand on to step across that edge).
    public bool IsOnEdgeRow(Heading h) => h switch
    {
        Heading.North => Y == 0,
        Heading.South => Y == Size - 1,
        Heading.West => X == 0,
        _ => X == Size - 1,
    };

    // Steps from here to the edge row of `h` (0 when on it).
    public int DistanceToEdge(Heading h) => h switch
    {
        Heading.North => Y,
        Heading.South => Size - 1 - Y,
        Heading.West => X,
        _ => Size - 1 - X,
    };

    // For a subtile just outside the board, the edge it lies beyond; null for
    // on-board subtiles and for anything further out or diagonal.
    public Heading? EdgeBeyond
    {
        get
        {
            var inX = X >= 0 && X < Size;
            var inY = Y >= 0 && Y < Size;
            if (inX && Y == -1) return Heading.North;
            if (inX && Y == Size) return Heading.South;
            if (inY && X == -1) return Heading.West;
            if (inY && X == Size) return Heading.East;
            return null;
        }
    }

    // The subtile just outside edge `h` in front of lane `lane` (0..3, counted
    // west→east along a north/south edge and north→south along an east/west
    // edge). Where a unit waiting to come on in that lane stands.
    public static Subtile OutsideLane(Heading h, int lane) => h switch
    {
        Heading.North => new Subtile(lane, -1),
        Heading.South => new Subtile(lane, Size),
        Heading.West => new Subtile(-1, lane),
        _ => new Subtile(Size, lane),
    };

    // Every on-board subtile, row-major.
    public static IEnumerable<Subtile> All()
    {
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                yield return new Subtile(x, y);
    }

    public override string ToString() => $"({X},{Y})";
}
