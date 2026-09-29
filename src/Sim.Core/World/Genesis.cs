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

    // Rivers: which edges of each tile carry one (docs/rivers.md). Sparse —
    // only river tiles need an entry. Build validates the symmetry invariant
    // (a river on my North edge is a river on my northern neighbour's South
    // edge) and throws on a mismatch: an asymmetric mask would make the
    // crossing fee depend on which way you walked.
    public IReadOnlyDictionary<TileCoord, RiverEdge> Rivers { get; init; } =
        new Dictionary<TileCoord, RiverEdge>();

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

    // M23: loot-cache scatter (count + loot table). Defaulted to Count 0 (no
    // caches), so existing scenarios are unaffected; scenarios opt in. The
    // scatter runs in the Simulation spec-ctor with the seeded Rng; only the
    // resulting Cache structures persist. See docs/loot-caches.md.
    public Sim.Core.Caches.CacheConfig Caches { get; init; } = new();
    // M38 — idols scattered in the fog, and their grade table.
    public Sim.Core.Scouting.IdolConfig Idols { get; init; } = new();
    // M39 — bandit camp knobs.
    public Sim.Core.Bandits.CampConfig Camps { get; init; } = new();
    // Two-act pacing — the landing (day X). Default = no landing, a one-act
    // world (docs/two-act-pacing.md).
    public Sim.Core.Landing.LandingConfig Landing { get; init; }

    // M31: world-level dynasty configuration (aura radius/bonus, majority
    // age). Defaulted; scenarios override, same as every config above.
    public Sim.Core.Royalty.RoyaltyConfig Royalty { get; init; } = new();
    // M37 — progression knobs (docs/progression.md). Only enrolled factions
    // (FactionStartSpec.Progression) are affected.
    public Sim.Core.Progression.ProgressionConfig Progression { get; init; } = new();

    // M41 — pairs of factions at war from tick 0 (the battle test bed's
    // scenarios; docs/m41-status.md). Empty for every ordinary world, where
    // war comes only by declaration.
    public IReadOnlyList<(int A, int B)> StartingWars { get; init; } = Array.Empty<(int, int)>();

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

    // M31 — which UnitSpawn.Id wears the crown from tick zero. Every realm
    // starts with a king (docs/king-and-dynasty.md): stakes before the first
    // enemy appears, and a one-unit tutorial for the game's deepest loop.
    //
    // Nullable, and validated at Build: a kingless faction has to be an
    // EXPLICIT choice (minimal test worlds, the bandit faction) rather than an
    // accident, because a realm without a king is quietly at a permanent
    // disadvantage against every realm that has one.
    public int? KingUnitId { get; init; }

    // God mode (docs/god-mode.md): this faction places structures for free
    // and they stand instantly. A test-harness switch (--god on the host),
    // off for every ordinary and AI faction.
    public bool GodMode { get; init; }

    // M37 — this faction takes part in progression (docs/progression.md):
    // counters, hidden milestones, omens. Human seats only; AI factions are
    // placeholders and never enrol.
    public bool Progression { get; init; }
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
            // M31 — the crown must name one of THIS faction's own spawns.
            if (fs.KingUnitId is { } kid)
            {
                var found = false;
                foreach (var u in fs.UnitSpawns)
                    if (u.Id == kid) { found = true; break; }
                if (!found)
                    throw new InvalidOperationException(
                        $"FactionStartSpec.KingUnitId {kid} is not among faction {fs.OwnerId}'s UnitSpawns.");
            }
        }

        var grid = new TileGrid(spec.Width, spec.Height, spec.DefaultBiome);
        foreach (var (coord, biome) in spec.Biomes)
            grid.SetBiome(coord, biome);
        foreach (var (coord, edges) in spec.Rivers)
        {
            if (!grid.InBounds(coord))
                throw new InvalidOperationException($"GenesisSpec.Rivers: {coord.X},{coord.Y} is out of bounds.");
            grid.SetRiverEdges(coord, edges);
        }
        ValidateRiverSymmetry(grid);

        var world = new GameWorld(grid, spec.Diplomacy, spec.Combat, spec.Population, spec.BiomeDegradation);
        world.RestoreRoyaltyConfig(spec.Royalty);   // M31 — genesis-set, then immutable
        world.RestoreProgressionConfig(spec.Progression);   // M37 — same
        world.RestoreIdolConfig(spec.Idols);                 // M38 — same
        world.RestoreCampConfig(spec.Camps);                 // M39 — same
        world.RestoreLandingConfig(spec.Landing);            // two-act pacing — same

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
            world.Players[fs.OwnerId] = new Player(fs.OwnerId)
            {
                GodMode = fs.GodMode,
                Progress = fs.Progression ? new Sim.Core.Progression.ProgressLedger() : null,
            };

            var castle = world.AddStructure(new Castle(fs.CastlePosition) { OwnerId = fs.OwnerId });
            // Its gate faces a side you can walk out of (docs/structure-footprints.md):
            // north if that neighbour is walkable land, else east, south, west.
            castle.Facing = GateFacing(world, fs.CastlePosition);
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

            // M31 — crown the founder. Genesis kings have no parents (their
            // ParentAId/ParentBId stay null), so the line starts one
            // generation deep and grows downward, exactly as the narrow-line
            // rule intends.
            if (fs.KingUnitId is { } kingId)
                world.Players[fs.OwnerId].KingUnitId = kingId;
        }

        // M41 — wars in force from the start (scenarios only).
        foreach (var (a, b) in spec.StartingWars)
            world.Diplomacy.SetState(Diplomacy.FactionPair.Of(a, b), Diplomacy.RelationshipState.Enemy);

        // M8: seed the monotonic unit-id counter so BirthEvent allocates
        // ids that don't collide with any spawned unit.
        world.NextUnitId = world.Units.Count == 0 ? 1 : world.Units.Keys.Max() + 1;

        return world;
    }

    // Every river edge must be seen from both sides (docs/rivers.md). A
    // one-sided mask is a spec bug, not a world: fail loudly here rather
    // than let River.Crosses answer differently for A→B and B→A.
    private static void ValidateRiverSymmetry(TileGrid grid)
    {
        for (var y = 0; y < grid.Height; y++)
        for (var x = 0; x < grid.Width; x++)
        {
            var tile = new TileCoord(x, y);
            var mask = grid.RiverEdgesAt(tile);
            if (mask == RiverEdge.None) continue;
            foreach (var n in grid.Neighbors(tile))
            {
                var edge = TileEdge.SideBetween(tile, n);
                var mine = (mask & edge) != 0;
                var theirs = (grid.RiverEdgesAt(n) & TileEdge.Opposite(edge)) != 0;
                if (mine != theirs)
                    throw new InvalidOperationException(
                        $"GenesisSpec.Rivers is asymmetric between {x},{y} and {n.X},{n.Y} ({edge}).");
            }
            // A river on a map-edge side has no neighbour to agree with it.
            if (((mask & RiverEdge.North) != 0 && y == 0) ||
                ((mask & RiverEdge.West)  != 0 && x == 0) ||
                ((mask & RiverEdge.South) != 0 && y == grid.Height - 1) ||
                ((mask & RiverEdge.East)  != 0 && x == grid.Width - 1))
                throw new InvalidOperationException(
                    $"GenesisSpec.Rivers: {x},{y} carries a river on the map border.");
        }
    }

    // The first of N, E, S, W whose neighbour is in bounds, dry and walkable.
    private static Sim.Core.Battlefields.Heading GateFacing(GameWorld world, TileCoord at)
    {
        foreach (var h in Sim.Core.Battlefields.Headings.All)
        {
            var n = new TileCoord(at.X + Sim.Core.Battlefields.Headings.Dx(h), at.Y + Sim.Core.Battlefields.Headings.Dy(h));
            if (world.Grid.InBounds(n) && world.Grid.BiomeAt(n) != Biome.Water
                && world.Grid.TerrainCost(n) < Biomes.Impassable)
                return h;
        }
        return Sim.Core.Battlefields.Heading.North;
    }
}
