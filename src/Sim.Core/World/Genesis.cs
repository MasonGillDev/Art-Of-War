namespace Sim.Core.World;

// Initial-world factory. Centralizes the "what does a fresh game look like"
// setup so test scenarios and the host don't duplicate boilerplate, and so
// snapshot determinism tests have a deterministic starting state to compare
// against.
public sealed record GenesisSpec
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    // Default biome for every tile. Override per-tile via Biomes.
    public Biome DefaultBiome { get; init; } = Biome.Grassland;
    public IReadOnlyDictionary<TileCoord, Biome> Biomes { get; init; } =
        new Dictionary<TileCoord, Biome>();

    public required TileCoord CastlePosition { get; init; }
    public IReadOnlyDictionary<Resource, int> StartingHoldings { get; init; } =
        new SortedDictionary<Resource, int>();

    public IReadOnlyList<UnitSpawn> Units { get; init; } = Array.Empty<UnitSpawn>();
}

public sealed record UnitSpawn(int Id, TileCoord Position, UnitRole Role = UnitRole.None, int CargoCapacity = 1);

public static class Genesis
{
    public static GameWorld Build(GenesisSpec spec)
    {
        var grid = new TileGrid(spec.Width, spec.Height, spec.DefaultBiome);
        foreach (var (coord, biome) in spec.Biomes)
            grid.SetBiome(coord, biome);

        var world = new GameWorld(grid);

        var castle = world.AddStructure(new Castle(spec.CastlePosition));
        foreach (var (r, n) in spec.StartingHoldings)
        {
            var accepted = castle.Deposit(r, n);
            if (accepted != n)
                throw new InvalidOperationException(
                    $"Castle capacity ({castle.Capacity}) too small for starting holdings.");
        }

        foreach (var u in spec.Units)
            world.AddUnit(new Unit(u.Id, u.Position) { Role = u.Role, CargoCapacity = u.CargoCapacity });

        return world;
    }
}
