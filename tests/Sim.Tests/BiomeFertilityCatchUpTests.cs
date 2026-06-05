using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// M9 Phase A: lazy fertility catch-up math in isolation. The headline test is
// CatchUpWithRate_IsObservationIndependent — the property that catches the
// silent remainder-drop desync. Same shape as M2 RoadDecayTests.
//
// Phase A drives the rate explicitly (no extractors wired yet). Phase B+ will
// add the extractor-driven CatchUp wrapper and test cross-transition
// observation independence over real production-state changes.
public class BiomeFertilityCatchUpTests
{
    private static readonly BiomeDegradationConfig Cfg = new();

    private static GameWorld MakeWorld(Biome biome = Biome.Forest, int width = 4, int height = 4)
    {
        var grid = new TileGrid(width, height, biome);
        return new GameWorld(grid);
    }

    // Seed a fertility entry at the given tile. Used by tests that need to
    // start the math from a non-default state.
    private static void SeedFertility(GameWorld world, TileCoord tile, int deviation, long lastUpdate)
    {
        world.Fertility[tile] = new Fertility(deviation, lastUpdate);
    }

    // ====================================================================
    // THE headline test. If lazy fertility is wrong, this is what catches it.
    // ====================================================================

    [Fact]
    public void CatchUpWithRate_IsObservationIndependent()
    {
        // Two worlds, identical starting state. One catches up once at tick T;
        // the other catches up at many intermediate ticks along the way to T.
        // The rate is the same throughout (a within-segment test — cross-
        // transition observation independence lands in Phase B).
        //
        // Degrade rate: -1 per 10 ticks. T = 1234. Starting deviation = 0 from
        // baseline 100 (a Forest tile).
        const long T = 1234;
        const int rateAmount = -1;
        const long ratePeriod = 10;
        var tile = new TileCoord(1, 1);

        var w1 = MakeWorld(Biome.Forest);
        BiomeDegradation.CatchUpWithRate(w1, tile, T, rateAmount, ratePeriod, Cfg);

        var w2 = MakeWorld(Biome.Forest);
        foreach (var t in new long[] { 7, 50, 137, 250, 400, 550, 700, 900, 1100, T })
            BiomeDegradation.CatchUpWithRate(w2, tile, t, rateAmount, ratePeriod, Cfg);

        var f1 = w1.Fertility.TryGetValue(tile, out var a) ? a : null;
        var f2 = w2.Fertility.TryGetValue(tile, out var b) ? b : null;
        Assert.NotNull(f1);
        Assert.NotNull(f2);
        Assert.Equal(f1!.Deviation, f2!.Deviation);
        Assert.Equal(f1.LastUpdateTick, f2.LastUpdateTick);
    }

    [Fact]
    public void CatchUpWithRate_TickByTickMatchesSingleJump()
    {
        // Most torturous version: catch up at every single tick from 1 to T.
        // The remainder carry must keep this identical to a single jump.
        const long T = 1234;
        const int rateAmount = -1;
        const long ratePeriod = 10;
        var tile = new TileCoord(1, 1);

        var w1 = MakeWorld(Biome.Forest);
        BiomeDegradation.CatchUpWithRate(w1, tile, T, rateAmount, ratePeriod, Cfg);

        var w2 = MakeWorld(Biome.Forest);
        for (var t = 1L; t <= T; t++)
            BiomeDegradation.CatchUpWithRate(w2, tile, t, rateAmount, ratePeriod, Cfg);

        var f1 = w1.Fertility[tile];
        var f2 = w2.Fertility[tile];
        Assert.Equal(f1.Deviation, f2.Deviation);
        Assert.Equal(f1.LastUpdateTick, f2.LastUpdateTick);
    }

    // ====================================================================
    // Pure-read / write-path agreement
    // ====================================================================

    [Fact]
    public void FertilityAt_IsPureRead_NoMutation()
    {
        var world = MakeWorld(Biome.Forest);
        // Seed a deviating tile so the math path actually runs (recovery would
        // be applied to it). FertilityAt must NOT write anything regardless.
        SeedFertility(world, new TileCoord(1, 1), deviation: -30, lastUpdate: 0);
        var sim = new Simulation(world, seed: 1);
        var hashBefore = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
        {
            BiomeDegradation.FertilityAt(world, new TileCoord(1, 1), 5_000, Cfg);
            BiomeDegradation.FertilityAt(world, new TileCoord(0, 0), 5_000, Cfg);  // absent tile
            BiomeDegradation.FertilityAt(world, new TileCoord(2, 2), 100_000, Cfg);
        }

        Assert.Equal(hashBefore, Snapshot.Hash(sim));
    }

    [Fact]
    public void BiomeAt_IsPureRead_NoMutation()
    {
        var world = MakeWorld(Biome.Forest);
        SeedFertility(world, new TileCoord(1, 1), deviation: -30, lastUpdate: 0);
        var sim = new Simulation(world, seed: 1);
        var hashBefore = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
            BiomeDegradation.BiomeAt(world, new TileCoord(1, 1), 5_000, Cfg);

        Assert.Equal(hashBefore, Snapshot.Hash(sim));
    }

    // ====================================================================
    // Band transitions
    // ====================================================================

    [Fact]
    public void Band_TransitionsAtThresholds()
    {
        // Default config: ForestThreshold=75, DesertThreshold=25.
        Assert.Equal(Biome.Forest, BiomeDegradation.Band(Cfg.ForestThreshold,     Cfg));
        Assert.Equal(Biome.Forest, BiomeDegradation.Band(Cfg.ForestThreshold + 1, Cfg));
        Assert.Equal(Biome.Grassland, BiomeDegradation.Band(Cfg.ForestThreshold - 1, Cfg));
        Assert.Equal(Biome.Grassland, BiomeDegradation.Band(Cfg.DesertThreshold,     Cfg));
        Assert.Equal(Biome.Desert,    BiomeDegradation.Band(Cfg.DesertThreshold - 1, Cfg));
        Assert.Equal(Biome.Desert,    BiomeDegradation.Band(0, Cfg));
    }

    [Fact]
    public void BiomeAt_OffLadderTiles_AlwaysReturnWorldgenBiome()
    {
        // Hills / Mountain / Water are NOT on the F/G/D ladder. Even with a
        // stored fertility deviation (which shouldn't happen in practice, but
        // the contract must hold defensively), BiomeAt returns the worldgen
        // biome unchanged.
        foreach (var b in new[] { Biome.Hills, Biome.Mountain, Biome.Water })
        {
            var world = MakeWorld(b);
            var tile = new TileCoord(1, 1);
            SeedFertility(world, tile, deviation: -50, lastUpdate: 0);
            Assert.Equal(b, BiomeDegradation.BiomeAt(world, tile, 1_000_000, Cfg));
        }
    }

    // ====================================================================
    // Implicit desert latch (the contract: once below DesertThreshold,
    // recovery is OFF, permanent)
    // ====================================================================

    [Fact]
    public void DesertLatch_OncePushedBelowThreshold_RecoveryNoOps()
    {
        // Forest baseline 100. Drive a long degrade so deviation goes deep
        // negative (current fertility well below DesertThreshold 25).
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(1, 1);
        // Manual degrade to current fertility = 20 (< DesertThreshold).
        BiomeDegradation.CatchUpWithRate(world, tile, 800, ratePerPeriod: -1, ratePeriod: 10, Cfg);
        // 800 / 10 = 80 periods × -1 = -80 deviation; current = 100 - 80 = 20 < 25 → latched.
        var fert = world.Fertility[tile];
        Assert.Equal(-80, fert.Deviation);
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 800, Cfg));

        // Now drive recovery (no extractor in range → CatchUp would normally
        // apply recovery). Latch must hold: deviation stays at -80.
        BiomeDegradation.CatchUp(world, tile, now: 10_000_000, Cfg);
        Assert.Equal(-80, world.Fertility[tile].Deviation);
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 10_000_000, Cfg));
    }

    [Fact]
    public void GeneratedDesert_IsImplicitlyLatched_NeverRecovers()
    {
        // A tile whose worldgen biome is Desert has baseline 10 < threshold 25
        // → implicit latch from t=0, even with deviation == 0. No recovery
        // can ever bring it to Grassland.
        var world = MakeWorld(Biome.Desert);
        var tile = new TileCoord(2, 2);

        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 0, Cfg));

        // Run CatchUp at a far-future tick; latch must hold. CatchUp at a
        // transition writes an anchor entry (deviation=0, lastUpdate=now);
        // the biome is still Desert because baseline < DesertThreshold.
        BiomeDegradation.CatchUp(world, tile, now: 10_000_000, Cfg);
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 10_000_000, Cfg));
        var anchored = world.Fertility[tile];
        Assert.Equal(0, anchored.Deviation);                 // still at baseline (desert)
        Assert.Equal(10_000_000, anchored.LastUpdateTick);   // anchored at catch-up time
    }

    [Fact]
    public void ForestGrassland_IsReversible_ViaRecovery()
    {
        // The reversible side of the ladder: Forest tile, degraded to
        // Grassland (current fertility between thresholds), then recover →
        // climbs back to Forest. The implicit latch does NOT engage above
        // DesertThreshold.
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(1, 1);

        // Degrade: 400 ticks × -1/10 = -40 deviation. Current 60 ∈ [25, 75) → Grassland.
        BiomeDegradation.CatchUpWithRate(world, tile, 400, ratePerPeriod: -1, ratePeriod: 10, Cfg);
        Assert.Equal(-40, world.Fertility[tile].Deviation);
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, tile, 400, Cfg));

        // Recover with explicit rate (no extractor in Phase A): apply enough
        // periods to climb the 40 back. RecoveryAmount=1 per RecoveryPeriod=30
        // → 1200 ticks should suffice. Use 30-tick-period recovery directly.
        BiomeDegradation.CatchUpWithRate(world, tile, 400 + 1200, ratePerPeriod: 1, ratePeriod: 30, Cfg);
        // 1200 / 30 = 40 periods × +1 = +40; deviation = -40 + 40 = 0 → sparse removal.
        Assert.False(world.Fertility.ContainsKey(tile));
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, tile, 400 + 1200, Cfg));
    }

    // ====================================================================
    // Clamps
    // ====================================================================

    [Fact]
    public void Recovery_ClampsAtBaseline_DoesNotOvershoot()
    {
        // Seed a tile at deviation -5 (near baseline). Drive a huge recovery
        // jump that would overshoot to positive deviation; the clamp must
        // hold at 0 and the entry must be removed (sparse).
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(1, 1);
        SeedFertility(world, tile, deviation: -5, lastUpdate: 0);
        BiomeDegradation.CatchUpWithRate(world, tile, 1_000_000, ratePerPeriod: 1, ratePeriod: 30, Cfg);

        Assert.False(world.Fertility.ContainsKey(tile));
        Assert.Equal(Cfg.ForestBaseline, BiomeDegradation.FertilityAt(world, tile, 1_000_000, Cfg));
    }

    [Fact]
    public void Degrade_FloorsAtNegativeBaseline_NoUnderflow()
    {
        // Seed a Forest tile and drive degrade far past what would zero its
        // current fertility. The clamp must hold deviation at -baseline.
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(1, 1);
        // Degrade for 10_000_000 ticks at -1 per 10 → would be -1_000_000.
        // Clamp at -ForestBaseline (-100).
        BiomeDegradation.CatchUpWithRate(world, tile, 10_000_000, ratePerPeriod: -1, ratePeriod: 10, Cfg);
        Assert.Equal(-Cfg.ForestBaseline, world.Fertility[tile].Deviation);
        Assert.Equal(0, BiomeDegradation.FertilityAt(world, tile, 10_000_000, Cfg));
        // Current fertility 0 is well below DesertThreshold → biome = Desert.
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 10_000_000, Cfg));
    }

    // ====================================================================
    // Sub-period / boundary / no-op
    // ====================================================================

    [Fact]
    public void SubPeriodCatchUp_DoesNotAdvanceState_RemainderBanked()
    {
        // 7 ticks elapsed with ratePeriod 10 → 0 completed periods → no change.
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(1, 1);
        SeedFertility(world, tile, deviation: -10, lastUpdate: 0);

        BiomeDegradation.CatchUpWithRate(world, tile, 7, ratePerPeriod: -1, ratePeriod: 10, Cfg);

        var f = world.Fertility[tile];
        Assert.Equal(-10, f.Deviation);
        Assert.Equal(0, f.LastUpdateTick);   // remainder banked
    }

    [Fact]
    public void BoundaryCrossed_AdvancesByCompletedPeriodsOnly()
    {
        // 237 ticks at ratePeriod 10 → 23 completed periods (230 ticks),
        // 7-tick remainder banked.
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(1, 1);
        // Start at deviation 0 so the math is clean.
        BiomeDegradation.CatchUpWithRate(world, tile, 237, ratePerPeriod: -1, ratePeriod: 10, Cfg);

        var f = world.Fertility[tile];
        Assert.Equal(-23, f.Deviation);
        Assert.Equal(230, f.LastUpdateTick);
    }

    [Fact]
    public void CatchUp_OffLadderTile_NoOp()
    {
        // Hills tile: CatchUp returns immediately without touching Fertility.
        var world = MakeWorld(Biome.Hills);
        var sim = new Simulation(world, seed: 1);
        var hashBefore = Snapshot.Hash(sim);

        BiomeDegradation.CatchUp(world, new TileCoord(1, 1), now: 1_000_000, Cfg);
        BiomeDegradation.CatchUpWithRate(world, new TileCoord(1, 1), 1_000_000, -1, 10, Cfg);

        Assert.Equal(hashBefore, Snapshot.Hash(sim));
        Assert.Empty(world.Fertility);
    }

    [Fact]
    public void CatchUp_NoStoredEntry_NoExtractor_WritesAnchorAtBaseline()
    {
        // A Forest tile with no stored entry and no producing extractor → math
        // is a no-op (rate=0, no deviation change). CatchUp still WRITES an
        // anchor entry (deviation=0, lastUpdate=now) at a transition — that's
        // the "anchor discipline" that fixes the lastUpdate=0 over-degrade
        // bug for a tile whose post-transition rate will be non-zero. (Here
        // the post-transition rate happens to stay 0; the anchor is a small
        // storage cost we accept for the simpler, uniformly-correct model.)
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(2, 2);

        BiomeDegradation.CatchUp(world, tile, now: 1_000_000, Cfg);

        var anchored = world.Fertility[tile];
        Assert.Equal(0, anchored.Deviation);
        Assert.Equal(1_000_000, anchored.LastUpdateTick);
        // BiomeAt remains Forest because the anchor doesn't shift fertility.
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, tile, 1_000_000, Cfg));
    }

    // ====================================================================
    // Snapshot round-trip
    // ====================================================================

    [Fact]
    public void SnapshotRoundTrip_PreservesFertilityState()
    {
        // Build a world with a mix of deviating tiles, snapshot, restore, hash
        // must match.
        var world = MakeWorld(Biome.Forest, width: 6, height: 6);
        SeedFertility(world, new TileCoord(1, 1), deviation: -20, lastUpdate: 150);
        SeedFertility(world, new TileCoord(3, 4), deviation: -80, lastUpdate: 700);
        SeedFertility(world, new TileCoord(0, 5), deviation: -5,  lastUpdate: 99);
        var sim = new Simulation(world, seed: 1);

        var bytes = Snapshot.Serialize(sim);
        var hashBefore = Snapshot.Hash(sim);

        var sim2 = Snapshot.Restore(bytes, seed: 1);
        var hashAfter = Snapshot.Hash(sim2);

        Assert.Equal(hashBefore, hashAfter);
        Assert.Equal(3, sim2.World.Fertility.Count);
        var restored = sim2.World.Fertility[new TileCoord(3, 4)];
        Assert.Equal(-80, restored.Deviation);
        Assert.Equal(700, restored.LastUpdateTick);
    }

    [Fact]
    public void SnapshotRoundTrip_PreservesDegradationConfig()
    {
        // Non-default config values must round-trip through Snapshot.
        var customCfg = new BiomeDegradationConfig(
            ForestBaseline:    150,
            GrasslandBaseline:  70,
            DesertBaseline:     12,
            HillsBaseline:      40,
            MountainBaseline:   80,
            WaterBaseline:       3,
            ForestThreshold:   100,
            DesertThreshold:    20,
            RecoveryAmount:      2,
            RecoveryPeriod:     50,
            DegradePeriod:      15,
            DegradeRadius:       2);
        var grid = new TileGrid(4, 4, Biome.Forest);
        var world = new GameWorld(grid, new Sim.Core.Diplomacy.DiplomacyConfig(),
            new Sim.Core.Combat.CombatConfig(), new Sim.Core.Population.PopulationConfig(),
            customCfg);
        var sim = new Simulation(world, seed: 1);

        var bytes = Snapshot.Serialize(sim);
        var sim2 = Snapshot.Restore(bytes, seed: 1);

        Assert.Equal(customCfg, sim2.World.BiomeDegradationConfig);
    }
}
