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

    // M7: world-level combat configuration (RoundIntervalTicks). Defaulted.
    public Combat.CombatConfig Combat { get; init; } = new();

    // M8: world-level population configuration (lifespan band, age gates,
    // gestation, food cost). Defaulted.
    public Population.PopulationConfig Population { get; init; } = new();

    // M9/M15: world-level biome-degradation configuration (fertility space,
    // periods, claim range default). Defaulted; demo/host scenarios override
    // for faster pacing, same as the other configs.
    public Sim.Core.Biomes.BiomeDegradationConfig BiomeDegradation { get; init; } = new();

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

    // M17 Phase 2 follow-up — the circular-lock fix (user decision
    // 2026-06-12, docs/m17-defender-spec.md): only Builders may raise a
    // site and only a School trains Builders, so a faction that loses
    // its last Builder before its first School stands is PERMANENTLY
    // locked out of construction — for humans and AI alike. Born with
    // the trainer, the lock is unreachable. Null = no school (test
    // scenarios keep their minimal worlds).
    public TileCoord? SchoolPosition { get; init; }

    // M8: per-faction default starting age (years) for spawned units that
    // don't override via UnitSpawn.StartingAgeYears. 30 = productive adult.
    public int StartingAgeYears { get; init; } = 30;
}

public sealed record UnitSpawn(
    int Id,
    TileCoord Position,
    UnitRole Role = UnitRole.None,
    int OwnerId = 0,
    // M8: optional per-unit starting-age override (null inherits faction).
    int? StartingAgeYears = null);

public static class Genesis
{
    public static GameWorld Build(GenesisSpec spec)
    {
        if (spec.FactionStarts.Count == 0)
            throw new InvalidOperationException(
                "GenesisSpec.FactionStarts must contain at least one FactionStartSpec.");
        var seenOwners = new HashSet<int>();
        foreach (var fs in spec.FactionStarts)
        {
            if (fs.OwnerId == Sim.Core.Bandits.BanditConstants.OwnerId)
                throw new InvalidOperationException(
                    $"OwnerId {fs.OwnerId} is reserved for the bandit faction (M16).");
            if (!seenOwners.Add(fs.OwnerId))
                throw new InvalidOperationException(
                    $"GenesisSpec.FactionStarts has duplicate OwnerId {fs.OwnerId}.");
        }

        var grid = new TileGrid(spec.Width, spec.Height, spec.DefaultBiome);
        foreach (var (coord, biome) in spec.Biomes)
            grid.SetBiome(coord, biome);

        var world = new GameWorld(grid, spec.Diplomacy, spec.Combat, spec.Population, spec.BiomeDegradation);

        // M16 — every world carries the bandit faction, usually empty: a
        // Player row with no castle, no holdings, no spawns. Registering it
        // here (not lazily at first spawn) keeps GameWorld.AddUnit's
        // population bookkeeping unconditional and the snapshot canonical.
        world.Players[Sim.Core.Bandits.BanditConstants.OwnerId] =
            new Player(Sim.Core.Bandits.BanditConstants.OwnerId);

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
            Sight.Reveal(world, castle.OwnerId, castle.At, Sight.RadiusFor(StructureKind.Castle), now: 0);

            // The genesis School (see SchoolPosition's doc) — a structure
            // like any other: snapshot round-trips it by kind, training
            // resolves on it from tick 0.
            if (fs.SchoolPosition is { } schoolAt)
            {
                var school = world.AddStructure(new School(schoolAt) { OwnerId = fs.OwnerId });
                Sight.Reveal(world, school.OwnerId, school.At,
                    Sight.RadiusFor(StructureKind.School), now: 0);
            }

            foreach (var u in fs.UnitSpawns)
            {
                // M8: BornTick = -StartingAgeYears * TicksPerYear (sim.Now
                // is 0 at genesis), so age = now - BornTick = startingAge
                // years at tick 0. Per-unit override wins over the
                // faction default.
                var startingAge = u.StartingAgeYears ?? fs.StartingAgeYears;
                var bornTick = -(long)startingAge * spec.Population.TicksPerYear;
                var unit = world.AddUnit(new Unit(u.Id, u.Position) {
                    Role = u.Role,
                    OwnerId = u.OwnerId,
                    BornTick = bornTick,
                });
                // M3 Phase B: each spawned unit reveals around its spawn tile.
                Sight.Reveal(world, unit.OwnerId, unit.Position, Sight.RadiusFor(unit.Role), now: 0);
            }
        }

        // M8: seed the monotonic unit-id counter so BirthEvent allocates
        // ids that don't collide with any spawned unit.
        world.NextUnitId = world.Units.Count == 0 ? 1 : world.Units.Keys.Max() + 1;

        return world;
    }
}
