namespace Sim.Core.World;

// Initial-world factory. Centralizes the "what does a fresh game look like"
// setup so test scenarios and the host don't duplicate boilerplate, and so
// snapshot determinism tests have a deterministic starting state to compare
// against.
//
// M6: a world is born with one or more factions, each with their own start
// (castle position, holdings, unit spawns). Pass a single-element
// FactionStarts for the classic single-player case; pass multiple for the
// multi-faction case combat (M7) builds on.
public sealed record GenesisSpec
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    // Default biome for every tile. Override per-tile via Biomes.
    public Biome DefaultBiome { get; init; } = Biome.Grassland;
    public IReadOnlyDictionary<TileCoord, Biome> Biomes { get; init; } =
        new Dictionary<TileCoord, Biome>();

    // M6: per-faction starts. Each entry registers a Player and seeds that
    // faction's castle + holdings + units. OwnerIds must be unique within the
    // list. Iterated in OwnerId order at Build time for deterministic placement.
    public required IReadOnlyList<FactionStartSpec> FactionStarts { get; init; }

    // M6: world-level diplomacy configuration (Delay, ProposalExpiryTicks).
    // Defaulted; tests and the host can override at world-build time.
    public Diplomacy.DiplomacyConfig Diplomacy { get; init; } = new();

    public int FactionCount => FactionStarts.Count;
}

// One faction's spawn-time loadout. Used by Genesis.Build to set up each
// faction's castle (with starting holdings) and units. OwnerId scopes the
// castle and any UnitSpawn that doesn't override its own OwnerId.
public sealed record FactionStartSpec
{
    public int OwnerId { get; init; } = 0;
    public required TileCoord CastlePosition { get; init; }
    public IReadOnlyDictionary<Resource, int> CastleHoldings { get; init; } =
        new SortedDictionary<Resource, int>();
    public IReadOnlyList<UnitSpawn> UnitSpawns { get; init; } = Array.Empty<UnitSpawn>();
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
        if (spec.FactionStarts.Count == 0)
            throw new InvalidOperationException(
                "GenesisSpec.FactionStarts must contain at least one FactionStartSpec.");
        var seenOwners = new HashSet<int>();
        foreach (var fs in spec.FactionStarts)
            if (!seenOwners.Add(fs.OwnerId))
                throw new InvalidOperationException(
                    $"GenesisSpec.FactionStarts has duplicate OwnerId {fs.OwnerId}.");

        var grid = new TileGrid(spec.Width, spec.Height, spec.DefaultBiome);
        foreach (var (coord, biome) in spec.Biomes)
            grid.SetBiome(coord, biome);

        var world = new GameWorld(grid, spec.Diplomacy);

        // Iterate factions in OwnerId order — deterministic placement,
        // matches the snapshot canonical Players order (sorted-by-id).
        foreach (var fs in spec.FactionStarts.OrderBy(f => f.OwnerId))
        {
            world.Players[fs.OwnerId] = new Player(fs.OwnerId);

            var castle = world.AddStructure(new Castle(fs.CastlePosition) { OwnerId = fs.OwnerId });
            foreach (var (r, n) in fs.CastleHoldings)
            {
                var accepted = castle.Deposit(r, n);
                if (accepted != n)
                    throw new InvalidOperationException(
                        $"Castle capacity ({castle.Capacity}) too small for starting holdings.");
            }
            // M3 Phase B: the castle is a vision source; reveal its area.
            Sight.Reveal(world, castle.OwnerId, castle.At, Sight.RadiusFor(StructureKind.Castle));

            foreach (var u in fs.UnitSpawns)
            {
                var unit = world.AddUnit(new Unit(u.Id, u.Position) {
                    Role = u.Role,
                    CargoCapacity = u.CargoCapacity,
                    OwnerId = u.OwnerId,
                });
                // M3 Phase B: each spawned unit reveals around its spawn tile.
                Sight.Reveal(world, unit.OwnerId, unit.Position, Sight.RadiusFor(unit.Role));
            }
        }

        return world;
    }
}
