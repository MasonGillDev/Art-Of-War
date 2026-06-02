namespace Sim.Core.World;

public sealed class TileGrid
{
    public int Width { get; }
    public int Height { get; }

    // Biome is the tile's identity. Movement cost and (eventually) which
    // resource an extractor on this tile produces both derive from it.
    private readonly Biome[] _biome;

    public TileGrid(int width, int height, Biome defaultBiome = Biome.Grassland)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException();
        Width = width;
        Height = height;
        _biome = new Biome[width * height];
        Array.Fill(_biome, defaultBiome);
    }

    public bool InBounds(TileCoord c) =>
        c.X >= 0 && c.X < Width && c.Y >= 0 && c.Y < Height;

    private int Idx(TileCoord c) => c.Y * Width + c.X;

    public Biome BiomeAt(TileCoord c) => _biome[Idx(c)];
    public void SetBiome(TileCoord c, Biome b) => _biome[Idx(c)] = b;

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
