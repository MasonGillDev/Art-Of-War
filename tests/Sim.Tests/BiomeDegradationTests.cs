using Sim.Core.Biomes;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// M9 Phase B — extractor-sourced degradation wired into the production
// system. Phase A pinned the math (BiomeFertilityCatchUpTests); these tests
// pin the wiring:
//   - LumberCamp + Farm contribute via StructureSpec.DegradeAmount
//   - MaxInRangeProducingDegradeAmount returns MAX, never sum
//   - ArmIfDormant catches up radius using PRE-START rate
//   - ProductionTick stop branches catch up using PRE-STOP rate
//   - The catch-up scope is radius-bounded (no global sweep)
public class BiomeDegradationTests
{
    // Explicit SMALL-SCALE test config (the production default carries the
    // gameplay pacing — a ×100 point space with hourly periods — and is
    // free to retune without touching these wiring contracts). The worlds
    // below are built WITH this config so the sim-driven paths
    // (ArmIfDormant / ProductionTickEvent) read the same numbers the
    // assertions derive from.
    //
    // DegradePeriod 40 is chosen so a single-Lumberjack camp fills its
    // buffer (450 ticks) BEFORE its tile crosses out of Forest: 11
    // completed periods × camp amount 2 = 22 points < the 26-point
    // crossing. Tests that want the crossing drive time explicitly.
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
        DegradeRadius:       2);

    private static GameWorld MakeWorld(TileGrid grid) => new(
        grid, new Sim.Core.Diplomacy.DiplomacyConfig(),
        new Sim.Core.Combat.CombatConfig(), new Sim.Core.Population.PopulationConfig(),
        Cfg);

    // Points to cross out of the top of a band (threshold is inclusive on
    // the upper side, so the crossing needs baseline − threshold + 1 points),
    // and the completed-period count an extractor of `amount` needs for it.
    private static int PointsToLeaveForest(BiomeDegradationConfig c) => c.ForestBaseline - c.ForestThreshold + 1;
    private static int PointsToDesert(BiomeDegradationConfig c) => c.GrasslandBaseline - c.DesertThreshold + 1;
    private static int PeriodsFor(int points, int amount) => (points + amount - 1) / amount;

    // Construct a Forest-tiled world with an Extractor placed at `at` and
    // marked actively producing (TickArmed=true) WITHOUT going through the
    // sim-event setup. Used by the pure-read MAX/radius tests where we want to
    // bypass the sim driver and just read fertility at various ticks.
    private static (GameWorld world, Extractor ext) MakeArmedExtractor(
        TileCoord at, StructureKind kind, Biome biome = Biome.Forest, int gridSize = 8)
    {
        var grid = new TileGrid(gridSize, gridSize, biome);
        var world = MakeWorld(grid);
        var ext = new Extractor(kind, at);
        ext.TickArmed = true;   // mark "actively producing" without sim wiring
        world.AddStructure(ext);
        return (world, ext);
    }

    // Construct a Forest-tiled world with a LumberCamp and N adult Lumberjacks
    // assigned to it; arm it via the real ArmIfDormant code path so the
    // pre-start catch-up runs through the M9 hook. Returns the sim so the
    // caller can advance the event queue.
    private static (Simulation sim, Extractor ext) MakeSimWithArmedLumberCamp(
        TileCoord at, int workers = 1, int gridSize = 8)
    {
        var grid = new TileGrid(gridSize, gridSize, Biome.Forest);
        var world = MakeWorld(grid);
        var ext = new Extractor(StructureKind.LumberCamp, at);
        world.AddStructure(ext);
        var sim = new Simulation(world, seed: 1);
        for (var i = 1; i <= workers; i++)
        {
            var u = world.AddUnit(new Unit(i, at) { Role = UnitRole.Lumberjack });
            // BornTick defaults to a deeply-negative sentinel so the unit is
            // an adult by default — no Population.CanTrain gate to fight here.
            ext.Workers.Add(u.Id);
        }
        ext.ArmIfDormant(sim);
        return (sim, ext);
    }

    // ====================================================================
    // Headline: producing LumberCamp degrades its OWN tile AND its radius
    // ====================================================================

    [Fact]
    public void LumberCamp_DegradesOwnTile_OverTime()
    {
        var (world, ext) = MakeArmedExtractor(new TileCoord(4, 4), StructureKind.LumberCamp);
        var own = new TileCoord(4, 4);

        // Crossing periods derive from config + the camp's catalog amount so
        // the test follows any pacing retune. The F→G crossing snaps to
        // GrasslandBaseline; from there the G→D crossing needs the
        // grassland headroom at the same rate.
        var P = Cfg.DegradePeriod;
        var amt = ext.Spec.DegradeAmount;
        var crossF = PeriodsFor(PointsToLeaveForest(Cfg), amt);
        var crossD = crossF + PeriodsFor(PointsToDesert(Cfg), amt);
        Assert.Equal(Biome.Forest,    BiomeDegradation.BiomeAt(world, own, 0,                  Cfg));
        Assert.Equal(Biome.Forest,    BiomeDegradation.BiomeAt(world, own, (crossF - 1) * P,   Cfg));
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, own, crossF * P,         Cfg));
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, own, (crossD - 1) * P,   Cfg));
        Assert.Equal(Biome.Desert,    BiomeDegradation.BiomeAt(world, own, crossD * P,         Cfg));
    }

    [Fact]
    public void LumberCamp_DegradesRadius_OverTime()
    {
        // The (2r+1)×(2r+1) Chebyshev box around the camp degrades in lockstep
        // — single rate source, identical elapsed time. Drives over Cfg.DegradeRadius
        // so the test stays correct under any radius tuning; the window is the
        // F→G crossing tick derived from config + catalog amount.
        var center = new TileCoord(7, 7);
        var (world, ext) = MakeArmedExtractor(center, StructureKind.LumberCamp, gridSize: 16);
        var r = Cfg.DegradeRadius;
        var atCrossing = PeriodsFor(PointsToLeaveForest(Cfg), ext.Spec.DegradeAmount) * Cfg.DegradePeriod;

        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            var t = new TileCoord(center.X + dx, center.Y + dy);
            Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, t, atCrossing, Cfg));
        }
    }

    [Fact]
    public void LumberCamp_DoesNotDegradeOutsideRadius()
    {
        // A tile just past the configured radius (Chebyshev = DegradeRadius+1)
        // is outside the camp's footprint → stays Forest. Uses Cfg.DegradeRadius
        // so the test follows the active tuning rather than hard-coding a radius.
        var center = new TileCoord(7, 7);
        var (world, _) = MakeArmedExtractor(center, StructureKind.LumberCamp, gridSize: 16);
        var outside = new TileCoord(center.X + Cfg.DegradeRadius + 1, center.Y);

        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, outside, 100_000, Cfg));
    }

    // ====================================================================
    // The MAX-NOT-SUM contract (the M9 promise)
    // ====================================================================

    [Fact]
    public void TwoOverlappingLumberCamps_DegradeAtMax_NotSum()
    {
        // Two LumberCamps at (3,3) and (5,3) (Chebyshev distance 2). Their
        // radii overlap on (4,3). With MAX-not-sum, that tile degrades at
        // LumberCamp's catalog amount per period — NOT double it.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = MakeWorld(grid);
        var a = new Extractor(StructureKind.LumberCamp, new TileCoord(3, 3)) { TickArmed = true };
        var b = new Extractor(StructureKind.LumberCamp, new TileCoord(5, 3)) { TickArmed = true };
        world.AddStructure(a);
        world.AddStructure(b);
        var overlap = new TileCoord(4, 3);

        // Read 10 periods in — before the MAX-rate crossing but past where a
        // SUM rate would have crossed. Under MAX = amt the tile is still
        // Forest at baseline − 10×amt; under SUM = 2×amt the F→G snap has
        // fired and the tile reads Grassland. Both the numeric and the band
        // contrast pin MAX-not-sum.
        var amt = a.Spec.DegradeAmount;
        Assert.True(10 * amt < PointsToLeaveForest(Cfg), "window must sit before the MAX crossing");
        Assert.True(10 * 2 * amt >= PointsToLeaveForest(Cfg), "window must sit past the SUM crossing");
        var t = 10 * Cfg.DegradePeriod;
        Assert.Equal(Cfg.ForestBaseline - 10 * amt, BiomeDegradation.FertilityAt(world, overlap, t, Cfg));
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, overlap, t, Cfg));
    }

    [Fact]
    public void LumberCampPlusFarm_OverlappingTile_DegradesAtMaxOfTheTwoRates()
    {
        // LumberCamp and Farm carry different catalog amounts (logging
        // strips land faster than farming exhausts it). The overlap tile
        // pays MAX of the two, never their sum.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = MakeWorld(grid);
        var camp = new Extractor(StructureKind.LumberCamp, new TileCoord(3, 3)) { TickArmed = true };
        var farm = new Extractor(StructureKind.Farm,       new TileCoord(5, 3)) { TickArmed = true };
        world.AddStructure(camp);
        world.AddStructure(farm);
        var overlap = new TileCoord(4, 3);

        var max = Math.Max(camp.Spec.DegradeAmount, farm.Spec.DegradeAmount);
        var sum = camp.Spec.DegradeAmount + farm.Spec.DegradeAmount;

        // Window: before the MAX-rate crossing but past where the SUM rate
        // would have crossed — so the numeric value AND the band contrast
        // (Forest vs Grassland) both pin MAX-not-sum.
        var periods = PeriodsFor(PointsToLeaveForest(Cfg), sum);
        Assert.True(periods * max < PointsToLeaveForest(Cfg), "window must sit before the MAX crossing");
        var t = periods * Cfg.DegradePeriod;
        Assert.Equal(Cfg.ForestBaseline - periods * max, BiomeDegradation.FertilityAt(world, overlap, t, Cfg));
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, overlap, t, Cfg));
    }

    // ====================================================================
    // Phase B integration: ArmIfDormant + ProductionTick honour the
    // pre-change-rate-then-flip contract
    // ====================================================================

    [Fact]
    public void ArmIfDormant_CatchesUpRadius_WithPreStartRate_AnchorsAtArmTime()
    {
        // First arm: pre-start rate is 0 (no extractor was producing). The
        // catch-up still ANCHORS lastUpdate=now for each tile in radius —
        // this is what makes a deferred-arm scenario correct (without the
        // anchor, a later read would over-apply post-arm rate over elapsed
        // = now - 0 instead of now - armTime).
        var (sim, _) = MakeSimWithArmedLumberCamp(new TileCoord(7, 7), gridSize: 16);

        // (2r+1)² anchor entries written at lastUpdate=0 (armed at sim.Now=0).
        // Counted off Cfg.DegradeRadius so the test follows the tuning.
        var expected = (2 * Cfg.DegradeRadius + 1) * (2 * Cfg.DegradeRadius + 1);
        Assert.Equal(expected, sim.World.Fertility.Count);
        foreach (var (_, f) in sim.World.Fertility)
        {
            Assert.Equal(0, f.Deviation);
            Assert.Equal(0, f.LastUpdateTick);
        }

        // A pure read after 10 periods on the camp's own tile (always inside the
        // radius regardless of tuning) sees the live degrade at the camp's
        // catalog amount. Window sits before the F→G crossing so the math is
        // linear. The anchor-vs-no-anchor difference shows up when arm happens
        // at t > 0; covered by Phase D tests.
        var amt = StructureCatalog.Spec(StructureKind.LumberCamp).DegradeAmount;
        Assert.True(10 * amt < PointsToLeaveForest(Cfg), "window must sit before the crossing");
        Assert.Equal(Cfg.ForestBaseline - 10 * amt,
            BiomeDegradation.FertilityAt(sim.World, new TileCoord(7, 7), 10 * Cfg.DegradePeriod, Cfg));
    }

    [Fact]
    public void ProductionTickStop_CatchesUpRadius_WithPreStopRate()
    {
        // Single Lumberjack on a LumberCamp. It produces until the buffer fills,
        // then the buffer-full branch fires OnProductionTransition and goes dormant.
        // (The exact dormancy tick depends on production tuning; we read it off
        // sim.Now rather than pinning it, so production retuning doesn't break us.)
        var (sim, ext) = MakeSimWithArmedLumberCamp(new TileCoord(4, 4));

        sim.Run();   // drains the queue — last event is the buffer-fill stop
        var camp = (Extractor)sim.World.Structures[ext.At];
        Assert.False(camp.TickArmed);
        Assert.True(camp.BufferFull());

        // At dormancy, OnProductionTransition fired with the pre-stop rate
        // (-DegradeAmount / DegradePeriod, including this camp). Deviation =
        // completed degrade periods × amount at sim.Now (integer division
        // drops the remainder under the transition discipline); lastUpdate
        // anchors to sim.Now. All derive from config / catalog / sim.Now so
        // the test survives retuning.
        var ownTile = sim.World.Fertility[new TileCoord(4, 4)];
        Assert.Equal(-(int)(sim.Now / Cfg.DegradePeriod) * camp.Spec.DegradeAmount, ownTile.Deviation);
        Assert.Equal(sim.Now, ownTile.LastUpdateTick);
    }

    [Fact]
    public void OffLadderTilesInRadius_NoFertilityEntryWritten()
    {
        // A LumberCamp at (0,0) has radius covering (-1..1, -1..1) — the
        // negative ones are clipped by bounds, and the diagonal sits at
        // (1,1). If we make the GRID Forest with one Hills patch at (1,1),
        // that tile is off-ladder and CatchUp must no-op on it. We exploit
        // this to verify the off-ladder gate fires inside the radius loop.
        var grid = new TileGrid(4, 4, Biome.Forest);
        grid.SetBiome(new TileCoord(1, 1), Biome.Hills);
        var world = MakeWorld(grid);
        var ext = new Extractor(StructureKind.LumberCamp, new TileCoord(0, 0)) { TickArmed = true };
        world.AddStructure(ext);

        // Pure-read at t=500 → tile (1,1) returns its worldgen biome (Hills),
        // not Grassland. And no entry should be written by CatchUp even if
        // we force one.
        Assert.Equal(Biome.Hills, BiomeDegradation.BiomeAt(world, new TileCoord(1, 1), 500, Cfg));

        // Internal: force a CatchUp on the Hills tile. The off-ladder guard
        // must short-circuit before any write.
        BiomeDegradation.OnProductionTransition(world, ext, 500, Cfg);
        Assert.False(world.Fertility.ContainsKey(new TileCoord(1, 1)));
    }

    // ====================================================================
    // Transition ordering — disciplined start/stop flips against the
    // OnProductionTransition contract.
    //
    // Cross-transition observation independence (pure reads between
    // transitions don't disturb final state) is implied by Phase A's
    // FertilityAt_IsPureRead_NoMutation contract plus the twin-run test
    // below — we don't need a separate test for it.
    // ====================================================================

    [Fact]
    public void OnProductionTransition_PreStopThenPreStart_GivesExpectedDeviation()
    {
        // Hand-orchestrate one stop + one start cycle:
        //   arm at t=0 (pre-arm rate was 0 so no-op catch-up), stop after 10
        //   degrade periods (catch up under the PRE-STOP rate, which includes
        //   us), then start again 6 recovery periods later (catch up under
        //   the PRE-START rate — recovery, since our TickArmed is false).
        var (world, ext) = MakeArmedExtractor(new TileCoord(3, 3), StructureKind.LumberCamp);
        var own = new TileCoord(3, 3);
        var amt = ext.Spec.DegradeAmount;
        Assert.True(10 * amt < PointsToLeaveForest(Cfg), "stay below the crossing — linear math");

        // Stop transition after 10 degrade periods: deviation 0 → -10×amt.
        var stop = 10 * Cfg.DegradePeriod;
        BiomeDegradation.OnProductionTransition(world, ext, stop, Cfg);
        ext.TickArmed = false;
        Assert.Equal(-10 * amt, world.Fertility[own].Deviation);
        Assert.Equal(stop, world.Fertility[own].LastUpdateTick);

        // Start transition 6 recovery periods later: recovery applies since
        // deviation < 0 and not latched. +RecoveryAmount per period × 6.
        // lastUpdate anchors to the transition tick. Windows derive from config.
        var start = stop + 6 * Cfg.RecoveryPeriod;
        BiomeDegradation.OnProductionTransition(world, ext, start, Cfg);
        ext.TickArmed = true;
        Assert.Equal(-10 * amt + 6 * Cfg.RecoveryAmount, world.Fertility[own].Deviation);
        Assert.Equal(start, world.Fertility[own].LastUpdateTick);
    }

    // ====================================================================
    // Twin run — full deterministic re-simulation
    // ====================================================================

    [Fact]
    public void Degradation_TwinRun_HashesMatch()
    {
        // Build two identical sims with a LumberCamp, run them to natural
        // dormancy (buffer fills, no hauls → camp goes dormant via the M9
        // stop branch), hashes must match. Broadest nondeterminism catch.
        Simulation Build()
        {
            var grid = new TileGrid(8, 8, Biome.Forest);
            var world = MakeWorld(grid);
            var ext = new Extractor(StructureKind.LumberCamp, new TileCoord(4, 4));
            world.AddStructure(ext);
            var sim = new Simulation(world, seed: 42);
            for (var i = 1; i <= 2; i++)
            {
                var u = world.AddUnit(new Unit(i, ext.At) { Role = UnitRole.Lumberjack });
                ext.Workers.Add(u.Id);
            }
            ext.ArmIfDormant(sim);
            sim.Run();   // drains the queue at dormancy
            return sim;
        }

        var sim1 = Build();
        var sim2 = Build();

        Assert.Equal(Snapshot.Hash(sim1), Snapshot.Hash(sim2));
    }

    // ====================================================================
    // Phase C — recovery + permanent latch through the integrated wiring
    // (the math/latch contracts are pinned in BiomeFertilityCatchUpTests;
    // these are end-to-end through ProductionTickEvent + ArmIfDormant)
    // ====================================================================

    [Fact]
    public void Recovery_AfterProductionStops_LazyReadsClimbBackToForest()
    {
        // LumberCamp runs to dormancy; tiles in radius are stored at -7
        // deviation (= 93 fertility = Forest) under DegradePeriod=20.
        // Then time passes WITHOUT any further transition. Pure FertilityAt
        // reads must lazily apply recovery and eventually return
        // ForestBaseline.
        var (sim, ext) = MakeSimWithArmedLumberCamp(new TileCoord(4, 4));
        sim.Run();  // run to dormancy
        var own = new TileCoord(4, 4);
        var stopTick = sim.World.Fertility[own].LastUpdateTick;
        var pts = (int)(stopTick / Cfg.DegradePeriod) * ext.Spec.DegradeAmount;  // points lost at stop
        Assert.True(pts > 0 && pts < PointsToLeaveForest(Cfg), "must stop wounded but still Forest");

        // Stored fertility right after stop = baseline - pts (no snap yet).
        Assert.Equal(Cfg.ForestBaseline - pts, BiomeDegradation.FertilityAt(sim.World, own, stopTick, Cfg));

        // Lazy recovery via pure read: climbs back to baseline over pts
        // recovery periods → ForestBaseline → Forest. Windows derive from config.
        var recovered = stopTick + pts * Cfg.RecoveryPeriod;
        Assert.Equal(Cfg.ForestBaseline, BiomeDegradation.FertilityAt(sim.World, own, recovered, Cfg));
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(sim.World, own, recovered, Cfg));

        // STORED state unchanged — pure reads never write (Phase A contract);
        // proof through the integration path.
        Assert.Equal(-pts, sim.World.Fertility[own].Deviation);
        Assert.Equal(stopTick, sim.World.Fertility[own].LastUpdateTick);
    }

    [Fact]
    public void Rotation_NewTransition_MaterializesAccumulatedRecovery()
    {
        // After the camp stops at t=150 with own-tile deviation -7
        // (DegradePeriod=20: 7 periods × -1 over the 150-tick run), let
        // recovery climb lazily for 90 ticks (3 periods at +1/30 = +3
        // deviation). A new transition (a different extractor's production
        // change) at t=240 materializes the accumulated recovery into stored
        // state: -7 + 3 = -4 deviation, lastUpdate carries 150 + 3*30 = 240.
        //
        // We drive the transition directly via OnProductionTransition so the
        // test doesn't depend on advancing the event queue.
        var (sim, _) = MakeSimWithArmedLumberCamp(new TileCoord(4, 4));
        sim.Run();   // dormant
        var own = new TileCoord(4, 4);
        var startDev = sim.World.Fertility[own].Deviation;
        var stopTick = sim.World.Fertility[own].LastUpdateTick;

        // Place a second LumberCamp at (5,5) whose radius covers (4,4). It's dormant
        // (TickArmed=false), so firing its arm-transition catches up under the
        // recovery rate. Fire it 3 recovery periods after the stop; the catch-up
        // materializes the accumulated recovery into stored state.
        var newExt = new Extractor(StructureKind.LumberCamp, new TileCoord(5, 5));
        sim.World.AddStructure(newExt);
        var transitionTick = stopTick + 3 * Cfg.RecoveryPeriod;
        BiomeDegradation.OnProductionTransition(sim.World, newExt, transitionTick, Cfg);

        var stored = sim.World.Fertility[own];
        Assert.Equal(startDev + 3 * Cfg.RecoveryAmount, stored.Deviation);  // 3 periods of recovery
        Assert.Equal(transitionTick, stored.LastUpdateTick);
    }

    [Fact]
    public void Latch_HoldsAcrossDormancy_DespiteRecoveryRateConfigured()
    {
        // Push the own-tile fertility below the desert threshold via a long
        // degrade window. With the band-crossing step penalty, 1800 ticks
        // under -1/20 sends the tile through BOTH crossings:
        //   F→G snap at period 26 (dev=-50)
        //   G→D snap at period 52 (dev=-90)
        //   Remaining 38 periods × -1 → dev=-128 → clamped to -baseline=-100.
        // Once the latch holds (storedFert < DesertThreshold), recovery is
        // forced to 0 even via the lazy pure-read path.
        var (world, ext) = MakeArmedExtractor(new TileCoord(4, 4), StructureKind.LumberCamp);
        var own = new TileCoord(4, 4);

        var t = 90 * Cfg.DegradePeriod;   // 90 periods: through both crossings → clamps to -100
        BiomeDegradation.OnProductionTransition(world, ext, t, Cfg);
        ext.TickArmed = false;
        Assert.Equal(-100, world.Fertility[own].Deviation);
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, own, t, Cfg));

        // Far-future pure reads: latch holds, fertility unchanged, biome stays
        // Desert. Stored entry untouched (no transition fired between).
        Assert.Equal(0, BiomeDegradation.FertilityAt(world, own, 1_000_000, Cfg));
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, own, 1_000_000, Cfg));
        Assert.Equal(-100, world.Fertility[own].Deviation);

        // Trigger another transition while the latch holds. Catch-up under
        // the rate-since-latch (= 0, since the extractor is dormant) leaves
        // stored state unchanged.
        BiomeDegradation.OnProductionTransition(world, ext, 1_000_000, Cfg);
        Assert.Equal(-100, world.Fertility[own].Deviation);
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, own, 1_000_000, Cfg));
    }

    // ====================================================================
    // Phase D — extractor self-regulation. THE M9 HEADLINE: a producing
    // extractor exhausts its land and goes dormant; no infinite single-tile
    // extraction.
    // ====================================================================

    [Fact]
    public void LumberCamp_OnForest_DegradesOwnTile_ThenStops_TheHeadline()
    {
        // The keystone behaviour. A LumberCamp on Forest produces until its
        // own tile degrades out of Forest (band-by-band into Grassland) — at
        // which point the M9 biome-mismatch guard in ProductionTickEvent
        // rejects the next tick and the camp goes dormant. Manual relocate
        // from there.
        //
        // Setup: a Forest world with the camp and ONE Lumberjack. The camp
        // alternates buffer-full dormancy and re-arm (we drain by hand); the
        // tile crosses out of Forest at the config-derived crossing tick and
        // the next production tick fires the biome-mismatch path.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = MakeWorld(grid);
        var ext = new Extractor(StructureKind.LumberCamp, new TileCoord(4, 4));
        world.AddStructure(ext);
        var sim = new Simulation(world, seed: 1);
        var u = world.AddUnit(new Unit(1, ext.At) { Role = UnitRole.Lumberjack });
        ext.Workers.Add(u.Id);
        ext.ArmIfDormant(sim);

        // Deadline: the F→G crossing tick (config + catalog derived) plus a
        // few production periods of slack for the mismatch check to fire.
        var crossTick = PeriodsFor(PointsToLeaveForest(Cfg), ext.Spec.DegradeAmount) * Cfg.DegradePeriod;
        var deadline = crossTick + 10 * ext.Spec.ProductionPeriodTicks;

        // The simplest way to keep the camp pumping past biome-flip without
        // the haul machinery: manually clear the buffer between runs.
        var camp = (Extractor)sim.World.Structures[ext.At];
        var totalProduced = 0;
        while (sim.Now < deadline)
        {
            sim.Run();
            // If the camp went dormant via biome-mismatch, the buffer might
            // not be full — and we won't re-arm (player has to relocate).
            // Break: that's the headline.
            if (!camp.TickArmed && !camp.BufferFull()) break;
            // Otherwise drain the buffer (simulating a haul) and re-arm.
            totalProduced += camp.Buffer;
            camp.Buffer = 0;
            camp.ArmIfDormant(sim);
        }

        // The headline: dormant, with the buffer NOT full → biome-mismatch path.
        Assert.False(camp.TickArmed);
        Assert.False(camp.BufferFull());
        // Own tile must be Grassland (degraded out of Forest).
        Assert.Equal(Biome.Grassland,
            BiomeDegradation.BiomeAt(sim.World, ext.At, sim.Now, Cfg));
        // Production was BOUNDED — not infinite. (The exact number depends on
        // the rate tuning; the contract is "finite total." This is the
        // original complaint, fixed.)
        Assert.True(totalProduced > 0, "expected SOME production before exhaustion");
        Assert.True(totalProduced < deadline, "production should be bounded by exhaustion");
    }

    [Fact]
    public void Farm_OnGrassland_DegradesToDesert_PermanentlyDead()
    {
        // Farms drive their tile into permanent Desert via the latch. After
        // dormancy, no relocation back to this tile is possible — Desert
        // never recovers. (Quarry / Mine relocation pressure is out of scope
        // for M9 — see the spec.)
        var grid = new TileGrid(8, 8, Biome.Grassland);
        var world = MakeWorld(grid);
        var ext = new Extractor(StructureKind.Farm, new TileCoord(4, 4));
        world.AddStructure(ext);
        var sim = new Simulation(world, seed: 1);
        var u = world.AddUnit(new Unit(1, ext.At) { Role = UnitRole.Farmer });
        ext.Workers.Add(u.Id);
        ext.ArmIfDormant(sim);

        // Deadline: the G→D crossing tick (config + catalog derived) plus
        // slack for the mismatch check to fire.
        var crossTick = PeriodsFor(PointsToDesert(Cfg), ext.Spec.DegradeAmount) * Cfg.DegradePeriod;
        var deadline = crossTick + 10 * ext.Spec.ProductionPeriodTicks;

        var farm = (Extractor)sim.World.Structures[ext.At];
        while (sim.Now < deadline)
        {
            sim.Run();
            if (!farm.TickArmed && !farm.BufferFull()) break;
            farm.Buffer = 0;
            farm.ArmIfDormant(sim);
        }

        // Dormant via biome-mismatch (NOT buffer-full).
        Assert.False(farm.TickArmed);
        Assert.False(farm.BufferFull());
        // Own tile is now Desert AND permanent (latched).
        Assert.Equal(Biome.Desert,
            BiomeDegradation.BiomeAt(sim.World, ext.At, sim.Now, Cfg));
        // Far-future check: still Desert. Latch is permanent.
        Assert.Equal(Biome.Desert,
            BiomeDegradation.BiomeAt(sim.World, ext.At, sim.Now + 10_000_000, Cfg));
    }

    [Fact]
    public void LumberCamp_CannotBePlaced_OnDegradedGrassland()
    {
        // A formerly-Forest tile that has degraded to Grassland rejects a new
        // LumberCamp placement — PlaceSiteIntent uses the DERIVED biome.
        // (World carries the test config: PlaceSiteIntent reads it internally,
        // and the seeded deviation is in the test scale.)
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = MakeWorld(grid);
        // Seed a stored fertility entry that puts the tile in Grassland band.
        var tile = new TileCoord(4, 4);
        world.Fertility[tile] = new Fertility(deviation: -40, lastUpdateTick: 0);
        // Sanity: BiomeAt(now) is Grassland (current 60, between 25 and 75).
        var sim = new Simulation(world, seed: 1);
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(sim.World, tile, sim.Now, Cfg));

        var intent = new PlaceSiteIntent(tile, StructureKind.LumberCamp);
        var outcome = intent.Resolve(sim);
        Assert.False(outcome.IsApplied);
        Assert.Contains("requires Forest", outcome.Reason);
    }

    [Fact]
    public void Farm_CanBePlaced_OnDegradedForestTurnedGrassland()
    {
        // Sanity: a Forest tile that degraded to Grassland now ACCEPTS a Farm.
        // Same tile, different intent — the derived biome matches Farm's req.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = MakeWorld(grid);
        var tile = new TileCoord(4, 4);
        world.Fertility[tile] = new Fertility(deviation: -40, lastUpdateTick: 0);
        var sim = new Simulation(world, seed: 1);
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(sim.World, tile, sim.Now, Cfg));

        var outcome = new PlaceSiteIntent(tile, StructureKind.Farm).Resolve(sim);
        Assert.True(outcome.IsApplied);
    }

    [Fact]
    public void Degradation_SnapshotMidRun_RoundTrips()
    {
        // Run a degradation scenario to dormancy (stored Fertility entries
        // exist), snapshot, restore, hash matches.
        var (sim, _) = MakeSimWithArmedLumberCamp(new TileCoord(4, 4));
        sim.Run();

        var hashBefore = Snapshot.Hash(sim);
        var bytes = Snapshot.Serialize(sim);
        var sim2 = Snapshot.Restore(bytes, seed: 1);
        var hashAfter = Snapshot.Hash(sim2);

        Assert.Equal(hashBefore, hashAfter);
        Assert.NotEmpty(sim2.World.Fertility);
    }
}
