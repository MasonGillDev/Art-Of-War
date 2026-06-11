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
    // Explicit SMALL-SCALE config: these tests pin the catch-up MATH
    // (snaps, clamps, observation independence), which is scale-free —
    // the production default carries the gameplay pacing (a ×100 point
    // space with hourly periods) and is free to retune without touching
    // these contracts. Same pattern as CombatResolutionTests' test-sized
    // CombatConfig.
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
        DegradePeriod:      10,
        DegradeRadius:       2);

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
        // Forest baseline 100. Drive 800 ticks of -1/10 degrade. With the
        // band-crossing step penalty (§step-penalty in docs/biome-
        // degradation.md), this crosses BOTH thresholds and the tile lands
        // in deep Desert:
        //   t = 0..259  : Forest band, dev 0 → -25 (fert 75 at t=250)
        //   t = 260     : crosses 75 → snap to GrasslandBaseline (50), dev=-50
        //   t = 261..519: Grassland band, dev -50 → -75 (fert 25 at t=510)
        //   t = 520     : crosses 25 → snap to DesertBaseline (10), dev=-90
        //   t = 521..800: Desert band, dev continues from -90 downward
        // With 800 ticks (80 periods) total: 26 spent on F→G, 26 on G→D,
        // 28 remaining apply smoothly with the [-baseline, 0] floor clamp.
        // dev = -90 - 28 = -118 → clamped to -100.
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(1, 1);
        BiomeDegradation.CatchUpWithRate(world, tile, 800, ratePerPeriod: -1, ratePeriod: 10, Cfg);
        var fert = world.Fertility[tile];
        Assert.Equal(-100, fert.Deviation);   // hit the degrade floor
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 800, Cfg));

        // Now drive a long stretch of recovery time (no extractor; DeriveRate
        // would normally apply recovery if not latched). Latch must hold:
        // deviation stays at -100, biome stays Desert.
        BiomeDegradation.CatchUp(world, tile, now: 10_000_000, Cfg);
        Assert.Equal(-100, world.Fertility[tile].Deviation);
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
        // The reversible side of the ladder: Forest tile, degraded across the
        // F→G threshold (which snaps to GrasslandBaseline 50), then recover
        // → climbs smoothly back to Forest. The recovery is smooth — no step
        // bonus on the upward crossing — so the climb takes proportionally
        // longer than the snap "cost" the tile paid on the way down. That's
        // the design intent ("easy to cut, hard to regrow").
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(1, 1);

        // Degrade: 400 ticks × -1/10 = 40 periods. F→G crossing at period 26
        // → snap to dev=-50. Remaining 14 periods of smooth degrade →
        // dev=-50-14 = -64. Fert=36 → Grassland.
        BiomeDegradation.CatchUpWithRate(world, tile, 400, ratePerPeriod: -1, ratePeriod: 10, Cfg);
        Assert.Equal(-64, world.Fertility[tile].Deviation);
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, tile, 400, Cfg));

        // Recovery: +1 per 30 ticks. To climb the 64-deviation gap back to 0
        // we need 64 periods × 30 ticks = 1920 ticks of rest. Run a full
        // recovery cycle and verify the entry is sparse-removed at baseline.
        BiomeDegradation.CatchUpWithRate(world, tile, 400 + 1920, ratePerPeriod: 1, ratePeriod: 30, Cfg);
        Assert.False(world.Fertility.ContainsKey(tile));     // sparse: at baseline
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, tile, 400 + 1920, Cfg));
    }

    // ====================================================================
    // Band-crossing step penalty (degrade only)
    //
    // When degrade pushes fertility across ForestThreshold (75 → 74), the
    // tile SNAPS to GrasslandBaseline (50) — NOT the threshold-minus-one
    // value. Same for the G→D crossing: snap to DesertBaseline (10). This
    // makes the biome flip a durable loss instead of a 1-tick blip that
    // recovery walks back in 30 ticks. See docs/biome-degradation.md
    // §step-penalty.
    // ====================================================================

    [Fact]
    public void Degrade_OnForestGrasslandCrossing_SnapsToGrasslandBaseline()
    {
        // Forest baseline 100. Rate -1/10. The crossing happens at period 26
        // (current fert reaches 74 — just below ForestThreshold 75). Instead
        // of stopping at fert=74, fertility snaps DOWN to GrasslandBaseline=50.
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(1, 1);

        // BEFORE the crossing: smooth math (still Forest).
        BiomeDegradation.CatchUpWithRate(world, tile, 250, ratePerPeriod: -1, ratePeriod: 10, Cfg);
        Assert.Equal(-25, world.Fertility[tile].Deviation);          // fert=75, still Forest
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, tile, 250, Cfg));

        // AT the crossing (period 26): fert would be 74 → snaps to 50.
        // Reset and drive to exactly the crossing.
        world.Fertility.Clear();
        BiomeDegradation.CatchUpWithRate(world, tile, 260, ratePerPeriod: -1, ratePeriod: 10, Cfg);
        Assert.Equal(-50, world.Fertility[tile].Deviation);          // snapped, not -26
        Assert.Equal(50, BiomeDegradation.FertilityAt(world, tile, 260, Cfg));
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, tile, 260, Cfg));
    }

    [Fact]
    public void Degrade_OnGrasslandDesertCrossing_SnapsToDesertBaseline()
    {
        // Pre-seed a tile in the Grassland band on a Forest-baseline tile
        // (post F→G snap state: dev=-50). Drive 26 more periods under rate
        // -1/10 to cross the G→D threshold. Snap target: DesertBaseline=10,
        // which is dev = 10 - 100 = -90 relative to worldgen Forest.
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(1, 1);
        SeedFertility(world, tile, deviation: -50, lastUpdate: 0);

        BiomeDegradation.CatchUpWithRate(world, tile, 260, ratePerPeriod: -1, ratePeriod: 10, Cfg);

        Assert.Equal(-90, world.Fertility[tile].Deviation);          // snapped, not -76
        Assert.Equal(10, BiomeDegradation.FertilityAt(world, tile, 260, Cfg));
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 260, Cfg));
    }

    [Fact]
    public void Degrade_AcrossBothCrossings_InOnePass_AppliesBothSnaps()
    {
        // Single catch-up that traverses BOTH crossings + further degrade in
        // Desert. Starting at Forest baseline (dev=0), drive 100 periods at
        // -1/10. Stages:
        //   period 0..25  : Forest (fert 100 → 75)
        //   period 26     : F→G snap → dev=-50 (fert=50)
        //   period 27..51 : Grassland (fert 50 → 25, but lands at 25 exactly
        //                  at period 51; the crossing fires at period 52
        //                  because 50→24 needs 26 more periods)
        //   period 52     : G→D snap → dev=-90 (fert=10)
        //   period 53..100: Desert (fert 10 → 0, clamped at floor)
        // 100 - 26 - 26 = 48 remaining smooth periods. dev = -90 - 48 = -138
        // → clamped at -100 (the [-baseline, 0] floor).
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(1, 1);

        BiomeDegradation.CatchUpWithRate(world, tile, 1000, ratePerPeriod: -1, ratePeriod: 10, Cfg);

        Assert.Equal(-100, world.Fertility[tile].Deviation);
        Assert.Equal(0, BiomeDegradation.FertilityAt(world, tile, 1000, Cfg));
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, tile, 1000, Cfg));
    }

    [Fact]
    public void Degrade_SnapMath_IsObservationIndependent_AcrossCrossings()
    {
        // The observation-independence contract must extend across the snap.
        // Two scenarios: catch up once at T=1000, vs catch up at many
        // intermediate ticks. Final stored state identical.
        const long T = 1000;
        var tile = new TileCoord(1, 1);

        var w1 = MakeWorld(Biome.Forest);
        BiomeDegradation.CatchUpWithRate(w1, tile, T, ratePerPeriod: -1, ratePeriod: 10, Cfg);

        var w2 = MakeWorld(Biome.Forest);
        foreach (var t in new long[] { 100, 250, 260, 300, 500, 519, 520, 700, T })
            BiomeDegradation.CatchUpWithRate(w2, tile, t, ratePerPeriod: -1, ratePeriod: 10, Cfg);

        var f1 = w1.Fertility[tile];
        var f2 = w2.Fertility[tile];
        Assert.Equal(f1.Deviation, f2.Deviation);
        Assert.Equal(f1.LastUpdateTick, f2.LastUpdateTick);
    }

    [Fact]
    public void Recovery_AcrossGrasslandForestThreshold_DoesNotSnapUp()
    {
        // Asymmetry contract: degrade snaps DOWN at band crossings, but
        // recovery climbs SMOOTHLY through the upward crossing. A tile
        // started deep in Grassland recovers tick-by-tick (no upward bonus
        // snap to ForestBaseline).
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(1, 1);
        SeedFertility(world, tile, deviation: -50, lastUpdate: 0);   // mid-Grassland (fert 50)

        // Drive recovery at +1/30 for 750 ticks = 25 periods. dev = -50 + 25 = -25.
        // Fert = 75 → at the ForestThreshold; the Band function returns Forest
        // (inclusive threshold). The intermediate value (75) is what a smooth
        // climb produces, NOT a snap to 100.
        BiomeDegradationConfig cfg = Cfg;  // explicit to silence shadowing nag
        // 25 recovery periods: dev -50 → -25 (fert 75, the ForestThreshold).
        // Window derives from cfg.RecoveryPeriod so retuning the period is safe.
        var t1 = 25 * cfg.RecoveryPeriod;
        BiomeDegradation.CatchUpWithRate(world, tile, t1, ratePerPeriod: cfg.RecoveryAmount, ratePeriod: cfg.RecoveryPeriod, cfg);
        Assert.Equal(-25, world.Fertility[tile].Deviation);
        Assert.Equal(75, BiomeDegradation.FertilityAt(world, tile, t1, cfg));
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, tile, t1, cfg));   // band inclusive at 75

        // 25 more recovery periods → dev=0 → sparse remove.
        var t2 = 50 * cfg.RecoveryPeriod;
        BiomeDegradation.CatchUpWithRate(world, tile, t2, ratePerPeriod: cfg.RecoveryAmount, ratePeriod: cfg.RecoveryPeriod, cfg);
        Assert.False(world.Fertility.ContainsKey(tile));
    }

    [Fact]
    public void Snap_RecoveryFromSnapBaseline_TakesManyTicks_TheHeadline()
    {
        // The gameplay reason the step penalty exists: after a tile flips
        // Forest → Grassland, recovery back to Forest is SLOW. Without the
        // snap, dev was -26 and only 30 ticks of recovery returned the tile
        // to Forest band. With the snap to GrasslandBaseline (50), dev is
        // -50 and recovery to Forest needs the full climb of 25 fertility
        // points = 25 periods × 30 ticks/period = 750 ticks of rest. Almost
        // 25× the previous recovery time.
        var world = MakeWorld(Biome.Forest);
        var tile = new TileCoord(1, 1);

        // Drive across the F→G crossing (period 26).
        BiomeDegradation.CatchUpWithRate(world, tile, 260, ratePerPeriod: -1, ratePeriod: 10, Cfg);
        Assert.Equal(-50, world.Fertility[tile].Deviation);
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, tile, 260, Cfg));

        // 1 recovery period: NOT enough to leave Grassland — with the snap,
        // +1 → dev=-49 → fert=51 → still Grassland.
        var r1 = 260 + Cfg.RecoveryPeriod;
        BiomeDegradation.CatchUpWithRate(world, tile, r1, ratePerPeriod: Cfg.RecoveryAmount, ratePeriod: Cfg.RecoveryPeriod, Cfg);
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, tile, r1, Cfg));

        // 25 recovery periods total (+25). dev=-25, fert=75 → back to Forest.
        var r2 = 260 + 25 * Cfg.RecoveryPeriod;
        BiomeDegradation.CatchUpWithRate(world, tile, r2, ratePerPeriod: Cfg.RecoveryAmount, ratePeriod: Cfg.RecoveryPeriod, Cfg);
        Assert.Equal(-25, world.Fertility[tile].Deviation);
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, tile, r2, Cfg));
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
