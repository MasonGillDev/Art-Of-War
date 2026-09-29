namespace Sim.Core.World;

// The canonical tile order, (y, x): the same order every grid scan and every
// snapshot section walks. For sorted collections keyed by tile.
public sealed class TileOrder : IComparer<TileCoord>
{
    public static readonly TileOrder Instance = new();

    public int Compare(TileCoord a, TileCoord b)
    {
        var c = a.Y.CompareTo(b.Y);
        return c != 0 ? c : a.X.CompareTo(b.X);
    }
}
