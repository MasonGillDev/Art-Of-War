using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;
using Sim.Core.WorldGen;

namespace Sim.Tests;

// M35 Phases A/B — the environmental fertility baseline
// (docs/environmental-fertility.md). Pins:
//   * strength 0 is the identity (the tile-aware baseline == the band baseline);
//   * the derivations are pure, box-bounded reads over the WORLDGEN grid;
//   * river banks are water (distance 0) for the latch lift too;
//   * the per-band clamp keeps every band identity what worldgen said;
//   * the step-penalty snap carries the tile's offset;
//   * the knobs survive a snapshot round-trip and twin-run on a generated
//     world with rivers and a non-zero gradient.
public class EnvironmentalFertilityTests
{
    // Same small-scale config family as BiomeDegradationTests / WaterRestorationTests:
    // baselines 100/50/10, thresholds 75/25. Strength 0 (the production default shape).
    private static readonly BiomeDegradationConfig Flat = new(
        ForestBaseline:    100,
        GrasslandBaseline:  50,
        DesertBaseline:     10,
        HillsBaseline:      30,
        MountainBaseline:   60,
        WaterBaseline:       0,
        ForestThreshold:    75,
        DesertThreshold:    25,
        RecoveryAmount:      1,
        RecoveryPeriod:     30,
        DegradePeriod:      10,
        DegradeRadius:       2,
        WaterRecoveryRadius: 2,
        WaterRecoveryAmount: 1);

    // A gradient: +40 on the bank tapering over radius 3, -5 on dry land,
    // +6 per ring of forest depth up to 3.
    private static readonly BiomeDegradationConfig Graded = Flat with
    {
        WaterFertilityRadius = 3,
        WaterFertilityBonus = 40,
        DryEdgePenalty = 5,
        ForestDepthRings = 3,
        ForestDepthBonusPerRing = 6,
    };

    private static GameWorld MakeWorld(TileGrid grid, BiomeDegradationConfig cfg) => new(
        grid, new Sim.Core.Diplomacy.DiplomacyConfig(),
        new Sim.Core.Combat.CombatConfig(), new Sim.Core.Population.PopulationConfig(),
        cfg);

    private static TileGrid RiverGrid(int size = 8, Biome biome = Biome.Grassland)
    {
        // A river running north-south between columns 2 and 3.
        var grid = new TileGrid(size, size, biome);
        for (var y = 0; y < size; y++)
        {
            grid.SetRiverEdges(new TileCoord(2, y), RiverEdge.East);
            grid.SetRiverEdges(new TileCoord(3, y), RiverEdge.West);
        }
        return grid;
    }

    // ====================================================================
    // Strength 0 is the identity
    // ====================================================================

    [Fact]
    public void Strength0_TileBaseline_EqualsBandBaseline_Everywhere()
    {
        var grid = RiverGrid(size: 10);
        grid.SetBiome(new TileCoord(7, 7), Biome.Forest);
        grid.SetBiome(new TileCoord(0, 9), Biome.Water);
        grid.SetBiome(new TileCoord(9, 0), Biome.Desert);
        grid.SetBiome(new TileCoord(5, 5), Biome.Hills);
        var world = MakeWorld(grid, Flat);
        for (var y = 0; y < 10; y++)
            for (var x = 0; x < 10; x++)
            {
                var t = new TileCoord(x, y);
                Assert.Equal(0, EnvironmentalFertility.RawOffset(world, t, Flat));
                Assert.Equal(
                    BiomeDegradation.BaselineFertility(grid.BiomeAt(t), Flat),
                    BiomeDegradation.BaselineFertility(world, t, Flat));
            }
    }

    // ====================================================================
    // Pure, box-bounded reads
    // ====================================================================

    [Fact]
    public void RawOffset_IsPureRead_NoMutation()
    {
        var grid = RiverGrid();
        grid.SetBiome(new TileCoord(6, 6), Biome.Forest);
        var world = MakeWorld(grid, Graded);
        world.Fertility[new TileCoord(1, 1)] = new Fertility(-20, 0);
        var sim = new Simulation(world, seed: 1);
        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++)
        {
            EnvironmentalFertility.RawOffset(world, new TileCoord(1, 1), Graded);
            EnvironmentalFertility.Baseline(world, new TileCoord(6, 6), Graded);
            BiomeDegradation.FertilityAt(world, new TileCoord(1, 1), 5_000, Graded);
            ForestDepth.At(world, new TileCoord(6, 6), 3);
            WaterProximity.DistanceToWater(world, new TileCoord(7, 7), 3);
        }
        Assert.Equal(before, Snapshot.Hash(sim));
    }

    [Fact]
    public void RawOffset_IgnoresTerrainBeyondItsRadius()
    {
        // Box-bounded by construction: a change further than every radius
        // away cannot move the offset. (The chunk-local == global property.)
        var grid = new TileGrid(12, 12, Biome.Grassland);
        var world = MakeWorld(grid, Graded);
        var tile = new TileCoord(2, 2);
        var before = EnvironmentalFertility.RawOffset(world, tile, Graded);
        grid.SetBiome(new TileCoord(6, 6), Biome.Water);   // Chebyshev 4 > radius 3
        grid.SetBiome(new TileCoord(2, 6), Biome.Forest);
        Assert.Equal(before, EnvironmentalFertility.RawOffset(world, tile, Graded));
        grid.SetBiome(new TileCoord(5, 5), Biome.Water);   // Chebyshev 3: in range
        Assert.NotEqual(before, EnvironmentalFertility.RawOffset(world, tile, Graded));
    }

    // ====================================================================
    // Water distance: lakes, canals AND river banks
    // ====================================================================

    [Fact]
    public void DistanceToWater_RiverBanksAreDistanceZero()
    {
        var world = MakeWorld(RiverGrid(), Flat);
        Assert.Equal(0, WaterProximity.DistanceToWater(world, new TileCoord(2, 4), 3));
        Assert.Equal(0, WaterProximity.DistanceToWater(world, new TileCoord(3, 4), 3));
        Assert.Equal(1, WaterProximity.DistanceToWater(world, new TileCoord(1, 4), 3));
        Assert.Equal(2, WaterProximity.DistanceToWater(world, new TileCoord(5, 4), 3));
        Assert.Equal(3, WaterProximity.DistanceToWater(world, new TileCoord(6, 4), 3));
        Assert.Equal(4, WaterProximity.DistanceToWater(world, new TileCoord(7, 4), 3)); // maxR + 1
    }

    [Fact]
    public void DistanceToWater_SeesWaterTiles_AndReportsBeyondRadius()
    {
        var grid = new TileGrid(10, 10, Biome.Grassland);
        grid.SetBiome(new TileCoord(0, 0), Biome.Water);
        var world = MakeWorld(grid, Flat);
        Assert.Equal(0, WaterProximity.DistanceToWater(world, new TileCoord(0, 0), 3));
        Assert.Equal(2, WaterProximity.DistanceToWater(world, new TileCoord(2, 1), 3));
        Assert.Equal(4, WaterProximity.DistanceToWater(world, new TileCoord(9, 9), 3));
        Assert.True(WaterProximity.IsNearWater(world, new TileCoord(3, 3), 3));
        Assert.False(WaterProximity.IsNearWater(world, new TileCoord(4, 4), 3));
    }

    [Fact]
    public void RiverBank_LiftsTheDesertLatch()
    {
        // The M21 latch lift now sees rivers: a degraded tile beside a river
        // recovers; the same tile in a dry world stays latched forever.
        var wet = MakeWorld(RiverGrid(), Flat);
        var dry = MakeWorld(new TileGrid(8, 8, Biome.Grassland), Flat);
        var tile = new TileCoord(1, 4);                       // distance 1 from the bank
        wet.Fertility[tile] = new Fertility(-30, 0);          // fert 20 < 25: latched in M9
        dry.Fertility[tile] = new Fertility(-30, 0);

        Assert.Equal(20, BiomeDegradation.FertilityAt(dry, tile, 3_000, Flat));
        Assert.Equal(50, BiomeDegradation.FertilityAt(wet, tile, 3_000, Flat)); // 100 periods of 30, clamped at baseline
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(wet, tile, 3_000, Flat));
    }

    // ====================================================================
    // Forest depth
    // ====================================================================

    [Fact]
    public void ForestDepth_CountsAllForestRings_MapEdgeIsAnEdge()
    {
        var grid = new TileGrid(9, 9, Biome.Forest);
        var world = MakeWorld(grid, Flat);
        Assert.Equal(3, ForestDepth.At(world, new TileCoord(4, 4), 3));   // capped
        Assert.Equal(2, ForestDepth.At(world, new TileCoord(4, 4), 2));
        Assert.Equal(1, ForestDepth.At(world, new TileCoord(1, 1), 3));   // ring 2 leaves the map
        Assert.Equal(0, ForestDepth.At(world, new TileCoord(0, 4), 3));   // on the map edge
        grid.SetBiome(new TileCoord(5, 4), Biome.Grassland);
        Assert.Equal(0, ForestDepth.At(world, new TileCoord(4, 4), 3));   // now an edge tile
        Assert.Equal(0, ForestDepth.At(world, new TileCoord(5, 4), 3));   // non-forest: 0
    }

    // ====================================================================
    // The offset and the per-band clamp
    // ====================================================================

    [Fact]
    public void WaterBonus_TapersWithDistance_DryLandTakesThePenalty()
    {
        var grid = new TileGrid(10, 10, Biome.Grassland);
        grid.SetBiome(new TileCoord(0, 0), Biome.Water);
        var world = MakeWorld(grid, Graded);
        // bonus * (R + 1 - d) / (R + 1) with bonus 40, R 3.
        Assert.Equal(30, EnvironmentalFertility.RawOffset(world, new TileCoord(1, 1), Graded)); // d 1
        Assert.Equal(20, EnvironmentalFertility.RawOffset(world, new TileCoord(2, 2), Graded)); // d 2
        Assert.Equal(10, EnvironmentalFertility.RawOffset(world, new TileCoord(3, 3), Graded)); // d 3
        Assert.Equal(-5, EnvironmentalFertility.RawOffset(world, new TileCoord(4, 4), Graded)); // dry
        Assert.Equal(0,  EnvironmentalFertility.RawOffset(world, new TileCoord(0, 0), Graded)); // off-ladder
    }

    [Fact]
    public void ForestDepthBonus_OnlyOnForestTiles()
    {
        var grid = new TileGrid(9, 9, Biome.Forest);
        var cfg = Flat with { ForestDepthRings = 3, ForestDepthBonusPerRing = 6 };
        var world = MakeWorld(grid, cfg);
        Assert.Equal(18, EnvironmentalFertility.RawOffset(world, new TileCoord(4, 4), cfg));
        Assert.Equal(0,  EnvironmentalFertility.RawOffset(world, new TileCoord(0, 0), cfg));
        grid.SetBiome(new TileCoord(4, 4), Biome.Grassland);
        Assert.Equal(0,  EnvironmentalFertility.RawOffset(world, new TileCoord(4, 4), cfg));
    }

    [Fact]
    public void BaselineFor_ClampsStrictlyInsideEachBand()
    {
        // Grassland 50 lives in [25, 74]; Desert 10 in [0, 24]; Forest 100 in [75, inf).
        Assert.Equal(74, EnvironmentalFertility.BaselineFor(Biome.Grassland,  40, Flat));
        Assert.Equal(25, EnvironmentalFertility.BaselineFor(Biome.Grassland, -40, Flat));
        Assert.Equal(60, EnvironmentalFertility.BaselineFor(Biome.Grassland,  10, Flat));
        Assert.Equal(24, EnvironmentalFertility.BaselineFor(Biome.Desert,     30, Flat));
        Assert.Equal(0,  EnvironmentalFertility.BaselineFor(Biome.Desert,    -20, Flat));
        Assert.Equal(75, EnvironmentalFertility.BaselineFor(Biome.Forest,    -40, Flat));
        Assert.Equal(140, EnvironmentalFertility.BaselineFor(Biome.Forest,    40, Flat));
        // Off-ladder bands ignore the offset.
        Assert.Equal(30, EnvironmentalFertility.BaselineFor(Biome.Hills, 40, Flat));
    }

    [Fact]
    public void RawDesertNearWater_StaysLatched()
    {
        // The clamp is the "no greening raw desert" guard (docs/canals.md).
        var grid = new TileGrid(8, 8, Biome.Desert);
        grid.SetBiome(new TileCoord(0, 0), Biome.Water);
        var world = MakeWorld(grid, Graded);
        var tile = new TileCoord(1, 1);
        Assert.Equal(24, BiomeDegradation.FertilityAt(world, tile, 100_000, Graded)); // 10 + 30 clamped to 24
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 100_000, Graded));
    }

    // ====================================================================
    // The snap carries the offset
    // ====================================================================

    [Fact]
    public void Degrade_SnapsToTheNextBandBaseline_PlusTheTileOffset()
    {
        // Forest tile at distance 1 from water with bonus 40, R 1: offset 40*(1+1-1)/2 = 20.
        var grid = new TileGrid(8, 8, Biome.Forest);
        grid.SetBiome(new TileCoord(0, 0), Biome.Water);
        var cfg = Flat with { WaterFertilityRadius = 1, WaterFertilityBonus = 40 };
        var world = MakeWorld(grid, cfg);
        var tile = new TileCoord(1, 1);
        Assert.Equal(120, BiomeDegradation.BaselineFertility(world, tile, cfg));

        // Degrade 1 per 10 ticks. Gap to ForestThreshold 75 is 45 -> the 46th
        // period crosses; snap to Grassland 50 + 20 = 70, then 4 more periods.
        BiomeDegradation.CatchUpWithRate(world, tile, now: 500, ratePerPeriod: -1, ratePeriod: 10, cfg);
        Assert.Equal(70 - 4, 120 + world.Fertility[tile].Deviation);

        // The same tile under the flat config: baseline 100, gap 25 -> the
        // 26th period crosses, snap to the plain 50, 24 periods left -> 26.
        var flatWorld = MakeWorld(grid, Flat);
        BiomeDegradation.CatchUpWithRate(flatWorld, tile, now: 500, ratePerPeriod: -1, ratePeriod: 10, Flat);
        Assert.Equal(26, 100 + flatWorld.Fertility[tile].Deviation);
    }

    // ====================================================================
    // Persistence and determinism with a real gradient
    // ====================================================================

    [Fact]
    public void Snapshot_RoundTrips_TheKnobs()
    {
        var world = MakeWorld(RiverGrid(), Graded);
        var sim = new Simulation(world, seed: 7);
        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 7);
        Assert.Equal(Graded, restored.World.BiomeDegradationConfig);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    private static (Simulation sim, GeneratedMap map) BuildGeneratedSim(BiomeDegradationConfig cfg, int seed = 4242)
    {
        var map = MapGenerator.Build(new GenerationConfig { Seed = seed, Width = 64, Height = 64 });
        var spec = new GenesisSpec
        {
            Width = map.Width,
            Height = map.Height,
            Biomes = MapGenerator.ToBiomeOverrides(map),
            Rivers = MapGenerator.ToRiverOverrides(map),
            BiomeDegradation = cfg,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = map.Start,
                    CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Wood] = 20 },
                    UnitSpawns = new[] { new UnitSpawn(1, map.Start, UnitRole.Builder) },
                },
            },
        };
        return (new Simulation(Genesis.Build(spec), seed: 0xABCD), map);
    }

    [Fact]
    public void GeneratedWorld_WithGradient_TwinRunsHashEqual_AndRoundTrip()
    {
        var graded = new BiomeDegradationConfig() with
        {
            WaterFertilityBonus = 1500, DryEdgePenalty = 300, ForestDepthBonusPerRing = 400,
        };
        var (a, mapA) = BuildGeneratedSim(graded);
        var (b, _) = BuildGeneratedSim(graded);
        var target = new TileCoord(mapA.Width - 1, mapA.Height - 1);
        a.SubmitIntent(0, new MoveIntent(1, target));
        b.SubmitIntent(0, new MoveIntent(1, target));
        a.Run();
        b.Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));

        var restored = Snapshot.Restore(Snapshot.Serialize(a), seed: 0xABCD);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(restored));

        // The gradient is real on a generated map: some ladder tile sits
        // above its band baseline and some below.
        var world = a.World;
        var above = false; var below = false;
        for (var y = 0; y < mapA.Height; y++)
            for (var x = 0; x < mapA.Width; x++)
            {
                var t = new TileCoord(x, y);
                var wg = world.Grid.BiomeAt(t);
                if (!BiomeDegradation.IsOnLadder(wg)) continue;
                var band = BiomeDegradation.BaselineFertility(wg, graded);
                var tile = BiomeDegradation.BaselineFertility(world, t, graded);
                if (tile > band) above = true;
                if (tile < band) below = true;
            }
        Assert.True(above && below);
    }
}
