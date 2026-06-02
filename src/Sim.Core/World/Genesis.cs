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

    // Default owner for the Castle and all spawned units when their UnitSpawn
    // doesn't specify one. Single-player scenarios stay at 0; multi-player
    // scenarios override per-unit.
    public int CastleOwnerId { get; init; } = 0;
    public required TileCoord CastlePosition { get; init; }
    public IReadOnlyDictionary<Resource, int> StartingHoldings { get; init; } =
        new SortedDictionary<Resource, int>();

    public IReadOnlyList<UnitSpawn> Units { get; init; } = Array.Empty<UnitSpawn>();

    // Players to seed into the registry. Defaults to a single player 0;
    // multi-player scenarios pass a richer list. The CastleOwnerId / per-unit
    // OwnerId must reference players in this list.
    public IReadOnlyList<int> PlayerIds { get; init; } = new[] { 0 };
}

public sealed record UnitSpawn(
    int Id,
    TileCoord Position,
    UnitRole Role = UnitRole.None,
    int CargoCapacity = 1,
    int OwnerId = 0);

public static class Genesis
{
    public static GameWorld Build(GenesisSpec spec)
    {
        var grid = new TileGrid(spec.Width, spec.Height, spec.DefaultBiome);
        foreach (var (coord, biome) in spec.Biomes)
            grid.SetBiome(coord, biome);

        var world = new GameWorld(grid);

        foreach (var pid in spec.PlayerIds)
            world.Players[pid] = new Player(pid);

        var castle = world.AddStructure(new Castle(spec.CastlePosition) { OwnerId = spec.CastleOwnerId });
        foreach (var (r, n) in spec.StartingHoldings)
        {
            var accepted = castle.Deposit(r, n);
            if (accepted != n)
                throw new InvalidOperationException(
                    $"Castle capacity ({castle.Capacity}) too small for starting holdings.");
        }
        // M3 Phase B: the castle is a vision source; reveal its area.
        Sight.Reveal(world, castle.OwnerId, castle.At, Sight.RadiusFor(StructureKind.Castle));

        foreach (var u in spec.Units)
        {
            var unit = world.AddUnit(new Unit(u.Id, u.Position) {
                Role = u.Role,
                CargoCapacity = u.CargoCapacity,
                OwnerId = u.OwnerId,
            });
            // M3 Phase B: each spawned unit reveals around its spawn tile.
            Sight.Reveal(world, unit.OwnerId, unit.Position, Sight.RadiusFor(unit.Role));
        }

        return world;
    }
}
