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
    private static readonly BiomeDegradationConfig Cfg = new();

    // Construct a Forest-tiled world with an Extractor placed at `at` and
    // marked actively producing (TickArmed=true) WITHOUT going through the
    // sim-event setup. Used by the pure-read MAX/radius tests where we want to
    // bypass the sim driver and just read fertility at various ticks.
    private static (GameWorld world, Extractor ext) MakeArmedExtractor(
        TileCoord at, StructureKind kind, Biome biome = Biome.Forest, int gridSize = 8)
    {
        var grid = new TileGrid(gridSize, gridSize, biome);
        var world = new GameWorld(grid);
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
        var world = new GameWorld(grid);
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
        var (world, _) = MakeArmedExtractor(new TileCoord(4, 4), StructureKind.LumberCamp);
        var own = new TileCoord(4, 4);

        // ForestBaseline 100, ForestThreshold 75. With DegradeAmount=1 and
        // DegradePeriod=10, deviation reaches -25 at t=250 → current 75 still
        // Forest. At t=260 → deviation -26 → current 74 < 75 → Grassland.
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, own, 0,    Cfg));
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, own, 250,  Cfg));
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, own, 260, Cfg));
        // Eventually crosses the Desert threshold (deviation -76, t=760).
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, own, 760, Cfg));
    }

    [Fact]
    public void LumberCamp_DegradesRadius_OverTime()
    {
        // DegradeRadius=1 → 3×3 area including center. All 9 tiles should
        // degrade in lockstep (single rate source, identical elapsed time).
        var center = new TileCoord(4, 4);
        var (world, _) = MakeArmedExtractor(center, StructureKind.LumberCamp);

        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var t = new TileCoord(center.X + dx, center.Y + dy);
            Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, t, 500, Cfg));
        }
    }

    [Fact]
    public void LumberCamp_DoesNotDegradeOutsideRadius()
    {
        // A tile 2 steps away (Chebyshev) is outside radius=1 → stays Forest.
        var center = new TileCoord(4, 4);
        var (world, _) = MakeArmedExtractor(center, StructureKind.LumberCamp);
        var outside = new TileCoord(6, 4);   // Chebyshev = 2

        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, outside, 100_000, Cfg));
    }

    // ====================================================================
    // The MAX-NOT-SUM contract (the M9 promise)
    // ====================================================================

    [Fact]
    public void TwoOverlappingLumberCamps_DegradeAtMax_NotSum()
    {
        // Two LumberCamps at (3,3) and (5,3) (Chebyshev distance 2). Their
        // radii are 3×3 each; the tile (4,3) is in BOTH radii. With
        // MAX-not-sum, that tile degrades at LumberCamp.DegradeAmount=1 per
        // period — NOT 2 per period.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = new GameWorld(grid);
        var a = new Extractor(StructureKind.LumberCamp, new TileCoord(3, 3)) { TickArmed = true };
        var b = new Extractor(StructureKind.LumberCamp, new TileCoord(5, 3)) { TickArmed = true };
        world.AddStructure(a);
        world.AddStructure(b);
        var overlap = new TileCoord(4, 3);

        // At t=500: 50 periods × -1 (NOT -2) → deviation -50 → current 50 → Grassland.
        Assert.Equal(50, BiomeDegradation.FertilityAt(world, overlap, 500, Cfg));
        // If degrade were SUM (2 per period), deviation would be -100 →
        // current 0 → Desert. Anti-test:
        Assert.NotEqual(Biome.Desert, BiomeDegradation.BiomeAt(world, overlap, 500, Cfg));
    }

    [Fact]
    public void LumberCampPlusFarm_OverlappingTile_DegradesAtMaxOfTheTwoRates()
    {
        // LumberCamp.DegradeAmount = 1, Farm.DegradeAmount = 2. An overlap
        // tile should pick the MAX (= Farm's 2), not their sum (= 3).
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = new GameWorld(grid);
        var camp = new Extractor(StructureKind.LumberCamp, new TileCoord(3, 3)) { TickArmed = true };
        var farm = new Extractor(StructureKind.Farm,       new TileCoord(5, 3)) { TickArmed = true };
        world.AddStructure(camp);
        world.AddStructure(farm);
        var overlap = new TileCoord(4, 3);

        // At t=100: 10 periods × -MAX(1,2) = -2 each → deviation -20.
        // If SUM (3), deviation would be -30. Anti-test pins the contract.
        Assert.Equal(80, BiomeDegradation.FertilityAt(world, overlap, 100, Cfg));
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
        var (sim, _) = MakeSimWithArmedLumberCamp(new TileCoord(4, 4));

        // 3×3 anchor entries written at lastUpdate=0 (armed at sim.Now=0).
        Assert.Equal(9, sim.World.Fertility.Count);
        foreach (var (_, f) in sim.World.Fertility)
        {
            Assert.Equal(0, f.Deviation);
            Assert.Equal(0, f.LastUpdateTick);
        }

        // A pure read at t=200 sees the live degrade — and importantly, gets
        // the SAME answer as it would without the anchor (because armTime=0,
        // anchor==0). The anchor-vs-no-anchor difference shows up when arm
        // happens at t > 0; covered by Phase D's end-to-end tests.
        Assert.Equal(80, BiomeDegradation.FertilityAt(sim.World, new TileCoord(4, 4), 200, Cfg));
    }

    [Fact]
    public void ProductionTickStop_CatchesUpRadius_WithPreStopRate()
    {
        // Single Lumberjack on a LumberCamp. BufferCap=30, role bonus 2× →
        // 2 per production tick × ProductionPeriodTicks=10 = buffer fills at
        // sim.Now=150 (15 production ticks). At that tick the buffer-full
        // branch fires OnProductionTransition and goes dormant.
        var (sim, ext) = MakeSimWithArmedLumberCamp(new TileCoord(4, 4));

        sim.Run();   // drains the queue — last event is the buffer-fill stop
        var camp = (Extractor)sim.World.Structures[ext.At];
        Assert.False(camp.TickArmed);
        Assert.True(camp.BufferFull());
        Assert.Equal(150, sim.Now);

        // At the moment of dormancy, OnProductionTransition fired with
        // pre-stop rate (=-1/10, including this camp). 150 ticks / period 10
        // = 15 periods × -1 = -15 deviation. lastUpdate = 150.
        var ownTile = sim.World.Fertility[new TileCoord(4, 4)];
        Assert.Equal(-15, ownTile.Deviation);
        Assert.Equal(150, ownTile.LastUpdateTick);
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
        var world = new GameWorld(grid);
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
        //   t=0:   arm (TickArmed=true) — pre-arm rate was 0 so no-op catch-up
        //   t=100: stop transition — catch up using PRE-STOP rate (-1, includes us)
        //   t=300: start transition — catch up using PRE-START rate (recovery,
        //          this extractor's TickArmed is false; deviation was -10 → recovery
        //          pushes deviation back toward 0 at +1/30 over 200 ticks
        //          = +6 → new deviation = -4)
        var (world, ext) = MakeArmedExtractor(new TileCoord(3, 3), StructureKind.LumberCamp);
        var own = new TileCoord(3, 3);

        // t=100 stop transition: catch up under -1/10. Deviation goes 0 → -10.
        BiomeDegradation.OnProductionTransition(world, ext, 100, Cfg);
        ext.TickArmed = false;
        Assert.Equal(-10, world.Fertility[own].Deviation);
        Assert.Equal(100, world.Fertility[own].LastUpdateTick);

        // t=300 start transition: catch up under recovery rate (TickArmed=false
        // → MaxInRangeProducingDegradeAmount=0 → recovery applies because
        // stored deviation < 0 and not latched). Recovery: +1 per 30 ticks
        // over 200 ticks = 6 periods × +1 = +6. Deviation -10 + 6 = -4.
        // lastUpdate is ANCHORED to now=300 (the transition discipline drops
        // the 20-tick remainder under the old rate so the new rate starts
        // cleanly from t=300).
        BiomeDegradation.OnProductionTransition(world, ext, 300, Cfg);
        ext.TickArmed = true;
        Assert.Equal(-4, world.Fertility[own].Deviation);
        Assert.Equal(300, world.Fertility[own].LastUpdateTick);
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
            var world = new GameWorld(grid);
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
        // LumberCamp runs to dormancy; tiles in radius are stored at -15
        // deviation (= 85 fertility = Forest). Then time passes WITHOUT any
        // further transition. Pure FertilityAt reads must lazily apply
        // recovery and eventually return ForestBaseline.
        var (sim, _) = MakeSimWithArmedLumberCamp(new TileCoord(4, 4));
        sim.Run();  // run to dormancy at sim.Now=150; tile (4,4) stored at -15

        var own = new TileCoord(4, 4);
        Assert.Equal(85, BiomeDegradation.FertilityAt(sim.World, own, 150, Cfg));

        // Lazy recovery via pure read: deviation -15 climbs back to 0 at
        // +1 per 30 ticks. After 15 × 30 = 450 ticks, deviation = 0 →
        // current fertility = ForestBaseline.
        Assert.Equal(100, BiomeDegradation.FertilityAt(sim.World, own, 150 + 450, Cfg));
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(sim.World, own, 150 + 450, Cfg));

        // The STORED state is unchanged — no catch-up has fired since the
        // production-stop transition. Pure reads never write (Phase A
        // contract); this is the proof through the integration path.
        Assert.Equal(-15, sim.World.Fertility[own].Deviation);
        Assert.Equal(150, sim.World.Fertility[own].LastUpdateTick);
    }

    [Fact]
    public void Rotation_NewTransition_MaterializesAccumulatedRecovery()
    {
        // After the camp stops at t=150 with own-tile deviation -15, let
        // recovery climb lazily for 90 ticks (3 periods at +1/30 = +3
        // deviation). A new transition (a different extractor's production
        // change) at t=240 materializes the accumulated recovery into stored
        // state: -15 + 3 = -12 deviation, lastUpdate carries 150 + 3*30 = 240.
        //
        // We drive the transition directly via OnProductionTransition so the
        // test doesn't depend on advancing the event queue.
        var (sim, _) = MakeSimWithArmedLumberCamp(new TileCoord(4, 4));
        sim.Run();   // dormant at sim.Now=150 with own tile @ deviation -15

        // Place a second LumberCamp at (5,5) whose 3×3 radius covers (4,4).
        // Mark it dormant (TickArmed=false) — we are about to FIRE its
        // arm-transition manually at t=240, which is the moment it would
        // start producing.
        var newExt = new Extractor(StructureKind.LumberCamp, new TileCoord(5, 5));
        sim.World.AddStructure(newExt);
        // newExt.TickArmed is false by default → MaxInRangeProducingDegrade
        // sees no producer in range → catch-up applies recovery rate.
        BiomeDegradation.OnProductionTransition(sim.World, newExt, 240, Cfg);

        var own = new TileCoord(4, 4);
        var stored = sim.World.Fertility[own];
        Assert.Equal(-12, stored.Deviation);             // -15 + 3 × +1 (recovery)
        Assert.Equal(240, stored.LastUpdateTick);        // 150 + 3 × 30
    }

    [Fact]
    public void Latch_HoldsAcrossDormancy_DespiteRecoveryRateConfigured()
    {
        // Push the own-tile fertility below the desert threshold, then stop
        // the extractor and let arbitrary time pass. Recovery is configured
        // (RecoveryAmount=1, RecoveryPeriod=30), but the implicit latch
        // (storedFert < DesertThreshold) forces rate=0 so recovery never
        // fires — even via the lazy pure-read path.
        var (world, ext) = MakeArmedExtractor(new TileCoord(4, 4), StructureKind.LumberCamp);
        var own = new TileCoord(4, 4);

        // Stop at t=900 → -90 deviation → current 10 < threshold 25 → latched.
        BiomeDegradation.OnProductionTransition(world, ext, 900, Cfg);
        ext.TickArmed = false;
        Assert.Equal(-90, world.Fertility[own].Deviation);
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, own, 900, Cfg));

        // Far-future pure reads: latch holds, fertility unchanged, biome stays
        // Desert. The stored entry is also unchanged because no transition
        // fired to trigger a catch-up.
        Assert.Equal(10, BiomeDegradation.FertilityAt(world, own, 1_000_000, Cfg));
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, own, 1_000_000, Cfg));
        Assert.Equal(-90, world.Fertility[own].Deviation);  // stored, untouched

        // If we DO trigger another transition (some other extractor far away
        // arming, conceptually) and force a catch-up under the rate-since-
        // latch (= 0), the stored state must still not move.
        BiomeDegradation.OnProductionTransition(world, ext, 1_000_000, Cfg);
        Assert.Equal(-90, world.Fertility[own].Deviation);
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
        // Setup: enormous BufferCap so we don't dormancy-stop on buffer-full
        // before the biome flips. We hand-build a Forest world with the camp
        // and ONE Lumberjack. Production rate = 2/tick × 10 = 1 wood per 5
        // ticks. The own-tile degrades at -1/10 = 1 fertility per 10 ticks.
        // Forest threshold 75; baseline 100 → 250 ticks to fall to Grassland
        // (deviation -26, current 74). At the next PT after t=250, the biome
        // check fires.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = new GameWorld(grid);
        var ext = new Extractor(StructureKind.LumberCamp, new TileCoord(4, 4));
        world.AddStructure(ext);
        var sim = new Simulation(world, seed: 1);
        var u = world.AddUnit(new Unit(1, ext.At) { Role = UnitRole.Lumberjack });
        ext.Workers.Add(u.Id);
        ext.ArmIfDormant(sim);

        // Production drains into the buffer; the buffer caps at 30, but we
        // want to see the biome-mismatch path. The camp will hit buffer-full
        // at t=150 (15 PT ticks × 2 wood = 30, period 10). At that moment
        // its OWN tile deviation is -15 → current 85 → still Forest → buffer-
        // full path fires (not biome-mismatch). We then need a re-arm at
        // t=150-and-beyond by draining the buffer.
        //
        // The simplest way to keep the camp pumping past biome-flip without
        // the haul machinery: manually clear the buffer between runs.
        var camp = (Extractor)sim.World.Structures[ext.At];
        var totalProduced = 0;
        while (sim.Now < 1000)
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
        Assert.True(totalProduced < 1000, "production should be bounded by exhaustion");
    }

    [Fact]
    public void Farm_OnGrassland_DegradesToDesert_PermanentlyDead()
    {
        // Farms drive their tile into permanent Desert via the latch. After
        // dormancy, no relocation back to this tile is possible — Desert
        // never recovers. (Quarry / Mine relocation pressure is out of scope
        // for M9 — see the spec.)
        var grid = new TileGrid(8, 8, Biome.Grassland);
        var world = new GameWorld(grid);
        var ext = new Extractor(StructureKind.Farm, new TileCoord(4, 4));
        world.AddStructure(ext);
        var sim = new Simulation(world, seed: 1);
        var u = world.AddUnit(new Unit(1, ext.At) { Role = UnitRole.Farmer });
        ext.Workers.Add(u.Id);
        ext.ArmIfDormant(sim);

        var farm = (Extractor)sim.World.Structures[ext.At];
        while (sim.Now < 1000)
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
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = new GameWorld(grid);
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
        var world = new GameWorld(grid);
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
