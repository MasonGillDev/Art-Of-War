using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// M21 Phase A — water lifts the permanent desert latch on degraded land.
// A tile within WaterRecoveryRadius of any Water tile (worldgen lake/sea OR a
// built canal) escapes the M9 "Desert is forever" latch and recovers toward
// its original biome. The softening is asymmetric and DEGRADED-ONLY:
//   * degraded Grassland/Forest near water climbs back across DesertThreshold;
//   * raw worldgen Desert (deviation 0) does NOT green — nothing to recover.
// The new transition entry point OnWaterProximityChanged anchors recovery at
// the moment water appears, never retroactively (the canal phases depend on
// this; it is the M9/M15 rate-transition discipline applied to a new event).
public class WaterRestorationTests
{
    // Small-scale test config (the production default carries gameplay pacing
    // and is free to retune). Same shape as BiomeDegradationTests so the math
    // is easy to derive: baselines 100/50/10, thresholds 75/25, recovery
    // 1 point per 30 ticks, water-recovery radius 2.
    private static readonly BiomeDegradationConfig Cfg = new(
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
        DegradePeriod:      40,
        DegradeRadius:       2,
        WaterRecoveryRadius: 2);

    private static GameWorld MakeWorld(TileGrid grid) => new(
        grid, new Sim.Core.Diplomacy.DiplomacyConfig(),
        new Sim.Core.Combat.CombatConfig(), new Sim.Core.Population.PopulationConfig(),
        Cfg);

    // Grassland world (baseline 50) with one Water tile 2 tiles from `tile`
    // (inside WaterRecoveryRadius), and `tile` pre-degraded to `dev` (negative).
    private static (GameWorld world, TileCoord tile) DegradedGrasslandNearWater(int dev)
    {
        var grid = new TileGrid(12, 12, Biome.Grassland);
        var tile = new TileCoord(5, 5);
        grid.SetBiome(new TileCoord(5, 7), Biome.Water);   // Chebyshev 2 → in range
        var world = MakeWorld(grid);
        world.Fertility[tile] = new Fertility(dev, 0);
        return (world, tile);
    }

    // ====================================================================
    // Degraded land near water recovers (the headline softening)
    // ====================================================================

    [Fact]
    public void DegradedGrassland_NearWater_RecoversAcrossDesertThreshold()
    {
        // dev -30 → fertility 20 < DesertThreshold 25: permanently latched
        // Desert in plain M9. Near water it recovers at 1 point / 30 ticks.
        var (world, tile) = DegradedGrasslandNearWater(dev: -30);

        Assert.Equal(Biome.Desert,    BiomeDegradation.BiomeAt(world, tile, 0,   Cfg)); // fert 20
        Assert.Equal(Biome.Desert,    BiomeDegradation.BiomeAt(world, tile, 120, Cfg)); // fert 24
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, tile, 150, Cfg)); // fert 25
        // Climbs to baseline 50 by 900 ticks; never overshoots into Forest.
        Assert.Equal(50, BiomeDegradation.FertilityAt(world, tile, 900,    Cfg));
        Assert.Equal(50, BiomeDegradation.FertilityAt(world, tile, 100000, Cfg));
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, tile, 100000, Cfg));
    }

    [Fact]
    public void DegradedForest_NearWater_RecoversToForest_Slowly_NoUpwardSnap()
    {
        // Forest baseline 100, degraded to fertility 20 (latched Desert).
        var grid = new TileGrid(12, 12, Biome.Forest);
        var tile = new TileCoord(5, 5);
        grid.SetBiome(new TileCoord(5, 6), Biome.Water);   // adjacent
        var world = MakeWorld(grid);
        world.Fertility[tile] = new Fertility(-80, 0);

        Assert.Equal(Biome.Desert,    BiomeDegradation.BiomeAt(world, tile, 0,   Cfg));
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, tile, 150, Cfg)); // fert 25
        // Asymmetric: recovery is SMOOTH — no snap up to ForestBaseline. It is
        // still Grassland at fertility 74 (1620 ticks) and only flips to Forest
        // at fertility 75 (1650 ticks). The long climb is the "easy to cut,
        // hard to regrow" intent — water makes it possible, not instant.
        Assert.Equal(74, BiomeDegradation.FertilityAt(world, tile, 1620, Cfg));
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, tile, 1620, Cfg));
        Assert.Equal(Biome.Forest,    BiomeDegradation.BiomeAt(world, tile, 1650, Cfg));
        Assert.Equal(100, BiomeDegradation.FertilityAt(world, tile, 100000, Cfg)); // no overshoot
    }

    // ====================================================================
    // The DEGRADED-ONLY guarantee: raw desert stays barren near water
    // ====================================================================

    [Fact]
    public void RawDesert_NearWater_DoesNotBloom()
    {
        // Worldgen Desert (baseline 10, deviation 0) sits below DesertThreshold
        // but has nothing to recover — the storedDev<0 guard returns rate 0.
        // Surrounding it with water must NOT terraform it into Grassland.
        var grid = new TileGrid(12, 12, Biome.Desert);
        var tile = new TileCoord(5, 5);
        grid.SetBiome(new TileCoord(5, 4), Biome.Water);
        grid.SetBiome(new TileCoord(5, 6), Biome.Water);
        grid.SetBiome(new TileCoord(4, 5), Biome.Water);
        grid.SetBiome(new TileCoord(6, 5), Biome.Water);
        var world = MakeWorld(grid);

        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 0,         Cfg));
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 1_000_000, Cfg));
        Assert.Equal(10, BiomeDegradation.FertilityAt(world, tile, 1_000_000, Cfg));
        Assert.Empty(world.Fertility); // pure reads created no entry
    }

    [Fact]
    public void DegradedGrassland_FarFromWater_StaysPermanentlyLatched()
    {
        // Water exists but is 7 tiles away (> WaterRecoveryRadius 2): the latch
        // still holds. Inland land keeps its hard desert floor.
        var grid = new TileGrid(12, 12, Biome.Grassland);
        var tile = new TileCoord(2, 2);
        grid.SetBiome(new TileCoord(9, 9), Biome.Water);
        var world = MakeWorld(grid);
        world.Fertility[tile] = new Fertility(-30, 0); // fert 20, latched

        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 0,         Cfg));
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 1_000_000, Cfg));
        Assert.Equal(20, BiomeDegradation.FertilityAt(world, tile, 1_000_000, Cfg)); // never moves
    }

    // ====================================================================
    // Transition discipline — recovery anchors WHEN water appears
    // ====================================================================

    [Fact]
    public void OnWaterProximityChanged_AnchorsRecoveryAtTransition_NotRetroactively()
    {
        // A tile sits latched and dead for 600 ticks with no water nearby.
        // Then a canal is dug 2 tiles away. OnWaterProximityChanged catches the
        // tile up under the OLD (rate-0, latched) rate and anchors
        // lastUpdate=600 BEFORE the grid floods, so recovery starts at 600.
        var grid = new TileGrid(12, 12, Biome.Grassland);
        var tile = new TileCoord(5, 5);
        var canal = new TileCoord(5, 7); // becomes water; Chebyshev 2 from tile
        var world = MakeWorld(grid);
        world.Fertility[tile] = new Fertility(-30, 0); // fert 20, latched, lastUpdate 0

        // Latched for the whole pre-canal window.
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 600, Cfg));
        Assert.Equal(20, BiomeDegradation.FertilityAt(world, tile, 600, Cfg));

        // The canal-completion transition: catch up under the pre-water rate,
        // THEN flood (the order the BuildCompleteEvent branch will use).
        BiomeDegradation.OnWaterProximityChanged(world, new[] { canal }, now: 600, Cfg);
        grid.SetBiome(canal, Biome.Water);
        world.Fertility.Remove(canal); // flooded tile leaves the ladder

        // Anchored at 600, deviation unchanged.
        Assert.Equal(600, world.Fertility[tile].LastUpdateTick);
        Assert.Equal(-30, world.Fertility[tile].Deviation);

        // Recovery runs from 600: +5 points by 600+150 → fertility 25.
        // (A retroactive bug — lastUpdate left at 0 — would read fertility 45.)
        Assert.Equal(25, BiomeDegradation.FertilityAt(world, tile, 750, Cfg));
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, tile, 750, Cfg));
    }

    // ====================================================================
    // Pure-read wall + persistence
    // ====================================================================

    [Fact]
    public void NearWaterRecovery_FertilityAt_IsPureRead_NoMutation()
    {
        // The latch-lift path now does a water scan and takes the recovery
        // branch — it must still never mutate sim state.
        var (world, tile) = DegradedGrasslandNearWater(dev: -30);
        var sim = new Simulation(world, seed: 1);
        var hashBefore = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
        {
            BiomeDegradation.FertilityAt(world, tile, 450, Cfg);
            BiomeDegradation.BiomeAt(world, tile, 450, Cfg);
        }

        Assert.Equal(hashBefore, Snapshot.Hash(sim));
    }

    [Fact]
    public void SnapshotRoundTrip_PreservesWaterRecoveryRadius()
    {
        var cfg = Cfg with { WaterRecoveryRadius = 3 };
        var grid = new TileGrid(4, 4, Biome.Grassland);
        var world = new GameWorld(grid, new Sim.Core.Diplomacy.DiplomacyConfig(),
            new Sim.Core.Combat.CombatConfig(), new Sim.Core.Population.PopulationConfig(),
            cfg);
        var sim = new Simulation(world, seed: 1);

        var sim2 = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);

        Assert.Equal(3, sim2.World.BiomeDegradationConfig.WaterRecoveryRadius);
        Assert.Equal(cfg, sim2.World.BiomeDegradationConfig);
    }
}
