namespace Sim.Core.Battlefields;

// M42 — a subtile named on the whole map: the world tile it is in and its place
// on that tile's 4×4 board, as one pair of integers (tile × 4 + subtile). This is
// how a subtile route names its steps, so a route can cross a tile edge without
// any special case: the step from (3, y) of one tile to (0, y) of the next is just
// X going from 4t + 3 to 4t + 4.
public readonly record struct WorldSubtile(int X, int Y)
{
    public TileCoord Tile => new(FloorDiv(X, Subtile.Size), FloorDiv(Y, Subtile.Size));

    public Subtile Sub => new(Mod(X, Subtile.Size), Mod(Y, Subtile.Size));

    public static WorldSubtile Of(TileCoord tile, Subtile sub) =>
        new(tile.X * Subtile.Size + sub.X, tile.Y * Subtile.Size + sub.Y);

    public bool IsAdjacentTo(WorldSubtile other) => Math.Abs(X - other.X) + Math.Abs(Y - other.Y) == 1;

    // The heading of a one-step move this → other, or null if they aren't 4-adjacent.
    public Heading? HeadingTo(WorldSubtile other)
    {
        if (!IsAdjacentTo(other)) return null;
        if (other.X > X) return Heading.East;
        if (other.X < X) return Heading.West;
        return other.Y > Y ? Heading.South : Heading.North;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    private static int Mod(int a, int b) => a - FloorDiv(a, b) * b;

    public override string ToString() => $"[{X},{Y}]";
}
