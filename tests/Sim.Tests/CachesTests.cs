using Sim.Core.Caches;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Tests;

// M23 — loot caches: unowned treasure scattered into the genesis fog, never on
// a tile any player has seen, discovered by exploring and looted cargo-capped.
// See docs/loot-caches.md.
public class CachesTests
{
    // A bare sim (no genesis) for the loot / visibility unit tests.
    private static Simulation MakeBareSim(int size = 20)
    {
        var grid = new TileGrid(size, size, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        return new Simulation(world, seed: 1);
    }

    private static Cache PlaceCache(Simulation sim, TileCoord at, params (Resource r, int n)[] loot)
    {
        var cache = sim.World.AddStructure(new Cache(at) { OwnerId = CacheConstants.OwnerId });
        foreach (var (r, n) in loot) cache.Deposit(r, n);
        return cache;
    }

    private static Simulation MakeScatteredSim(int caches, ulong seed = 0xCAC4E, int size = 30,
        IReadOnlyDictionary<TileCoord, Biome>? biomes = null)
    {
        var spec = new GenesisSpec
        {
            Width = size, Height = size,
            Biomes = biomes ?? new Dictionary<TileCoord, Biome>(),
            Caches = new CacheConfig(Count: caches),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(2, 2) },
            },
        };
        return new Simulation(spec, seed);
    }

    private static List<Cache> CachesIn(Simulation sim) =>
        sim.World.Structures.Values.OfType<Cache>().ToList();

    // ====================================================================
    // Phase A — the cache structure + fog behavior (vanishes when unseen)
    // ====================================================================

    [Fact]
    public void Cache_OnVisibleTile_AppearsInView()
    {
        var sim = MakeBareSim();
        sim.World.AddStructure(new Castle(new TileCoord(2, 2)) { OwnerId = 0 }); // vision source
        PlaceCache(sim, new TileCoord(3, 2), (Resource.Wood, 40)); // adjacent → visible

        var view = View.BuildPlayerView(sim.World, 0);
        Assert.Contains(view.VisibleStructures,
            s => s.At == new TileCoord(3, 2) && s.Kind == StructureKind.Cache);
    }

    [Fact]
    public void Cache_OnFoggedTile_HiddenFromView()
    {
        var sim = MakeBareSim();
        sim.World.AddStructure(new Castle(new TileCoord(2, 2)) { OwnerId = 0 });
        PlaceCache(sim, new TileCoord(18, 18), (Resource.Stone, 30)); // far → fogged

        var view = View.BuildPlayerView(sim.World, 0);
        Assert.DoesNotContain(view.VisibleStructures, s => s.At == new TileCoord(18, 18));
        // Structures are not remembered — so a cache simply isn't there for a
        // player who can't currently see its tile (rush-or-lose).
        Assert.DoesNotContain(new TileCoord(18, 18), view.Explored);
    }

    [Fact]
    public void Cache_RoundTripsThroughSnapshot_WithLoot()
    {
        var sim = MakeBareSim();
        PlaceCache(sim, new TileCoord(5, 5), (Resource.Wood, 40), (Resource.Sword, 1));

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        var cache = Assert.IsType<Cache>(restored.World.Structures[new TileCoord(5, 5)]);
        Assert.Equal(CacheConstants.OwnerId, cache.OwnerId);
        Assert.Equal(40, cache.AmountOf(Resource.Wood));
        Assert.Equal(1, cache.AmountOf(Resource.Sword));
    }

    // ====================================================================
    // Phase B — looting (cargo-capped, remainder persists, removed when empty)
    // ====================================================================

    [Fact]
    public void LootCache_CargoCapped_TakesWhatFits_RemainderPersists()
    {
        var sim = MakeBareSim();
        var at = new TileCoord(3, 3);
        PlaceCache(sim, at, (Resource.Wood, 100));
        var hauler = sim.World.AddUnit(new Unit(1, at) { Role = UnitRole.Hauler, OwnerId = 0 });

        var outcome = new LootCacheIntent(1, Resource.Wood) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsApplied);
        Assert.Equal(Resource.Wood, hauler.CargoResource);
        Assert.Equal(25, hauler.CargoAmount); // Hauler capacity
        var cache = Assert.IsType<Cache>(sim.World.Structures[at]);
        Assert.Equal(75, cache.AmountOf(Resource.Wood)); // remainder persists, re-lootable
    }

    [Fact]
    public void LootCache_EmptyingIt_RemovesTheCache()
    {
        var sim = MakeBareSim();
        var at = new TileCoord(3, 3);
        PlaceCache(sim, at, (Resource.Wood, 20)); // < Hauler cap
        sim.World.AddUnit(new Unit(1, at) { Role = UnitRole.Hauler, OwnerId = 0 });

        Assert.True(new LootCacheIntent(1, Resource.Wood) { PlayerId = 0 }.Resolve(sim).IsApplied);

        Assert.False(sim.World.Structures.ContainsKey(at)); // consumed
        Assert.Equal(20, sim.World.Units[1].CargoAmount);
    }

    [Fact]
    public void LootCache_Gear_Works()
    {
        var sim = MakeBareSim();
        var at = new TileCoord(3, 3);
        PlaceCache(sim, at, (Resource.Sword, 2));
        sim.World.AddUnit(new Unit(1, at) { Role = UnitRole.Hauler, OwnerId = 0 });

        Assert.True(new LootCacheIntent(1, Resource.Sword) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Equal(Resource.Sword, sim.World.Units[1].CargoResource);
        Assert.Equal(2, sim.World.Units[1].CargoAmount);
    }

    [Fact]
    public void LootCache_ResourceNotInCache_Rejected()
    {
        var sim = MakeBareSim();
        var at = new TileCoord(3, 3);
        PlaceCache(sim, at, (Resource.Wood, 40));
        sim.World.AddUnit(new Unit(1, at) { Role = UnitRole.Hauler, OwnerId = 0 });

        Assert.True(new LootCacheIntent(1, Resource.Stone) { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.True(sim.World.Structures.ContainsKey(at)); // untouched
    }

    [Fact]
    public void LootCache_NotOnCacheTile_Rejected()
    {
        var sim = MakeBareSim();
        PlaceCache(sim, new TileCoord(3, 3), (Resource.Wood, 40));
        sim.World.AddUnit(new Unit(1, new TileCoord(5, 5)) { Role = UnitRole.Hauler, OwnerId = 0 });

        Assert.True(new LootCacheIntent(1, Resource.Wood) { PlayerId = 0 }.Resolve(sim).IsRejected);
    }

    [Fact]
    public void LootCache_FirstComeFirstServed_SecondLooterFindsItGone()
    {
        var sim = MakeBareSim();
        var at = new TileCoord(3, 3);
        PlaceCache(sim, at, (Resource.Wood, 20));
        sim.World.AddUnit(new Unit(1, at) { Role = UnitRole.Hauler, OwnerId = 0 });
        sim.World.AddUnit(new Unit(2, at) { Role = UnitRole.Hauler, OwnerId = 0 });

        Assert.True(new LootCacheIntent(1, Resource.Wood) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.True(new LootCacheIntent(2, Resource.Wood) { PlayerId = 0 }.Resolve(sim).IsRejected);
    }

    // ====================================================================
    // Phase C — genesis scatter (deterministic, fog-gated)
    // ====================================================================

    [Fact]
    public void Scatter_PlacesRequestedCount_AllUnowned_AllLooted()
    {
        var caches = CachesIn(MakeScatteredSim(caches: 8));
        Assert.Equal(8, caches.Count);
        Assert.All(caches, c => Assert.Equal(CacheConstants.OwnerId, c.OwnerId));
        Assert.All(caches, c => Assert.True(c.TotalHeld() > 0));
    }

    [Fact]
    public void Scatter_EveryCache_IsUnseenByAllPlayers_AtGenesis()
    {
        // THE constraint: a cache never spawns on a tile any player has seen.
        var sim = MakeScatteredSim(caches: 12);
        var seen = new HashSet<TileCoord>();
        foreach (var set in sim.World.Explored.Values) seen.UnionWith(set);

        Assert.NotEmpty(seen); // sanity: the starting vision was actually revealed
        foreach (var c in CachesIn(sim))
            Assert.DoesNotContain(c.At, seen);
    }

    [Fact]
    public void Scatter_AvoidsWaterTiles()
    {
        var biomes = new Dictionary<TileCoord, Biome> { [new TileCoord(15, 15)] = Biome.Water };
        var sim = MakeScatteredSim(caches: 60, biomes: biomes); // pack the map
        foreach (var c in CachesIn(sim))
            Assert.NotEqual(Biome.Water, sim.World.Grid.BiomeAt(c.At));
    }

    [Fact]
    public void Scatter_Count0_ProducesNoCaches()
    {
        Assert.Empty(CachesIn(MakeScatteredSim(caches: 0)));
    }

    [Fact]
    public void Scatter_TwinRun_IdenticalPlacementAndLoot()
    {
        var a = MakeScatteredSim(caches: 10);
        var b = MakeScatteredSim(caches: 10);

        List<Cache> Sorted(Simulation s) =>
            CachesIn(s).OrderBy(c => c.At.Y).ThenBy(c => c.At.X).ToList();
        var ca = Sorted(a);
        var cb = Sorted(b);
        Assert.Equal(ca.Select(c => c.At), cb.Select(c => c.At));
        Assert.Equal(ca.Select(c => c.TotalHeld()), cb.Select(c => c.TotalHeld()));
    }

    // ====================================================================
    // Phase D — headline determinism + persistence
    // ====================================================================

    [Fact]
    public void Caches_TwinRun_HashesMatch()
    {
        var a = MakeScatteredSim(caches: 15);
        var b = MakeScatteredSim(caches: 15);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
        Assert.NotEmpty(CachesIn(a)); // sanity: caches actually scattered
    }

    [Fact]
    public void Caches_GenesisScatter_RoundTripsThroughSnapshot()
    {
        var sim = MakeScatteredSim(caches: 10);
        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 0xCAC4E);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(CachesIn(sim).Count, CachesIn(restored).Count);
    }
}
