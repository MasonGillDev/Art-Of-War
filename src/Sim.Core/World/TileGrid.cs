namespace Sim.Core.World;

public sealed class TileGrid
{
    public int Width { get; }
    public int Height { get; }

    // Biome is the tile's identity. Movement cost and (eventually) which
    // resource an extractor on this tile produces both derive from it.
    private readonly Biome[] _biome;

    // Which edges of each tile carry a river (docs/rivers.md). Dense rather
    // than sparse: one byte per tile is 63 KB on a 252² map, and the read
    // sits inside A*'s inner loop. Set at genesis (and by Snapshot restore),
    // never by the sim — rivers are terrain, like the biome grid itself.
    private readonly byte[] _river;

    public TileGrid(int width, int height, Biome defaultBiome = Biome.Grassland)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException();
        Width = width;
        Height = height;
        _biome = new Biome[width * height];
        Array.Fill(_biome, defaultBiome);
        _river = new byte[width * height];
    }

    public bool InBounds(TileCoord c) =>
        c.X >= 0 && c.X < Width && c.Y >= 0 && c.Y < Height;

    private int Idx(TileCoord c) => c.Y * Width + c.X;

    public Biome BiomeAt(TileCoord c) => _biome[Idx(c)];
    public void SetBiome(TileCoord c, Biome b) => _biome[Idx(c)] = b;

    public RiverEdge RiverEdgesAt(TileCoord c) => (RiverEdge)_river[Idx(c)];
    public void SetRiverEdges(TileCoord c, RiverEdge edges) => _river[Idx(c)] = (byte)edges;

    // Derived from biome — there is no per-tile cost override (yet).
    public int TerrainCost(TileCoord c) => Biomes.MoveCost(_biome[Idx(c)]);

    // 4-neighborhood, N E S W for deterministic A* expansion.
    public IEnumerable<TileCoord> Neighbors(TileCoord c)
    {
        if (c.Y > 0) yield return new TileCoord(c.X, c.Y - 1);
        if (c.X < Width - 1) yield return new TileCoord(c.X + 1, c.Y);
        if (c.Y < Height - 1) yield return new TileCoord(c.X, c.Y + 1);
        if (c.X > 0) yield return new TileCoord(c.X - 1, c.Y);
    }
}
