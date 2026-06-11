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
    // sim-event setup. Used by the pure-read tests where we want to bypass
    // the sim driver and just read fertility at various ticks.
    // M15: a raw TickArmed=true extractor with NO claims degrades nothing —
    // fill the claim the same way the lazy arm would (deterministic
    // AutoSelect) so the fixture stays honest.
    private static (GameWorld world, Extractor ext) MakeArmedExtractor(
        TileCoord at, StructureKind kind, Biome biome = Biome.Forest, int gridSize = 8)
    {
        var grid = new TileGrid(gridSize, gridSize, biome);
        var world = MakeWorld(grid);
        var ext = new Extractor(kind, at);
        world.AddStructure(ext);
        var claims = Claims.AutoSelect(world, at, ext.Spec, now: 0);
        Assert.NotNull(claims);   // fixture worlds are full-biome — must fill
        ext.ClaimTiles.AddRange(claims!);
        ext.TickArmed = true;   // mark "actively producing" without sim wiring
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
    public void LumberCamp_DegradesClaimTiles_OwnTileUntouched()
    {
        // M15: the degradation footprint IS the claim. A claimed tile walks
        // the full F→G→D ladder at the camp's catalog amount; the building's
        // own tile never degrades (it's the building, not a field).
        var (world, ext) = MakeArmedExtractor(new TileCoord(4, 4), StructureKind.LumberCamp);
        var claimed = ext.ClaimTiles[0];
        var own = ext.At;

        var P = Cfg.DegradePeriod;
        var amt = ext.Spec.DegradeAmount;
        var crossF = PeriodsFor(PointsToLeaveForest(Cfg), amt);
        var crossD = crossF + PeriodsFor(PointsToDesert(Cfg), amt);
        Assert.Equal(Biome.Forest,    BiomeDegradation.BiomeAt(world, claimed, 0,                Cfg));
        Assert.Equal(Biome.Forest,    BiomeDegradation.BiomeAt(world, claimed, (crossF - 1) * P, Cfg));
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, claimed, crossF * P,       Cfg));
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, claimed, (crossD - 1) * P, Cfg));
        Assert.Equal(Biome.Desert,    BiomeDegradation.BiomeAt(world, claimed, crossD * P,       Cfg));
        // The own tile sits inside the old radius box and right next to the
        // claims — and stays pristine at any horizon.
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, own, crossD * P * 10, Cfg));
    }

    [Fact]
    public void LumberCamp_DegradesAllClaimTiles_InLockstep()
    {
        // Every claimed tile shares the single claimant rate, so the whole
        // claim crosses F→G at the same derived tick; an unclaimed in-range
        // neighbor is untouched (the edge-exploit fix, observable).
        var center = new TileCoord(7, 7);
        var (world, ext) = MakeArmedExtractor(center, StructureKind.LumberCamp, gridSize: 16);
        var atCrossing = PeriodsFor(PointsToLeaveForest(Cfg), ext.Spec.DegradeAmount) * Cfg.DegradePeriod;

        foreach (var t in ext.ClaimTiles)
            Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(world, t, atCrossing, Cfg));
        // An in-range tile the camp did NOT claim stays Forest.
        var unclaimed = Enumerable.Range(-Cfg.DegradeRadius, 2 * Cfg.DegradeRadius + 1)
            .SelectMany(dy => Enumerable.Range(-Cfg.DegradeRadius, 2 * Cfg.DegradeRadius + 1)
                .Select(dx => new TileCoord(center.X + dx, center.Y + dy)))
            .First(t => t != center && !ext.ClaimTiles.Contains(t));
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, unclaimed, atCrossing, Cfg));
    }

    [Fact]
    public void LumberCamp_DoesNotDegradeOutsideClaims()
    {
        // A tile far outside any claim stays Forest forever — finite,
        // targeted extraction.
        var center = new TileCoord(7, 7);
        var (world, ext) = MakeArmedExtractor(center, StructureKind.LumberCamp, gridSize: 16);
        var outside = new TileCoord(center.X + ext.Spec.ClaimRange + 1, center.Y);

        Assert.DoesNotContain(outside, ext.ClaimTiles);
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(world, outside, 100_000, Cfg));
    }

    // ====================================================================
    // The MAX-NOT-SUM contract (the M9 promise)
    // ====================================================================

    // (M15) The two MAX-not-sum overlap tests are gone: overlapping
    // footprints are structurally impossible now — one claimant per tile,
    // enforced at placement (ClaimExclusionTests). The defensive MAX fold
    // inside Claims.ClaimantDegradeAmount is pinned by its own helper tests.

    // ====================================================================
    // Phase B integration: ArmIfDormant + ProductionTick honour the
    // pre-change-rate-then-flip contract
    // ====================================================================

    [Fact]
    public void ArmIfDormant_CatchesUpClaims_WithPreStartRate_AnchorsAtArmTime()
    {
        // First arm: lazy auto-claim fills the claim, then the pre-start
        // catch-up (rate 0 — nothing was producing) ANCHORS lastUpdate=now
        // on each CLAIMED tile — this is what makes a deferred-arm scenario
        // correct (without the anchor, a later read would over-apply the
        // post-arm rate over elapsed = now - 0 instead of now - armTime).
        var (sim, ext) = MakeSimWithArmedLumberCamp(new TileCoord(7, 7), gridSize: 16);
        var camp = (Extractor)sim.World.Structures[ext.At];

        // Exactly the claim's tiles get anchor entries — not a radius box.
        Assert.Equal(camp.Spec.ClaimCount, camp.ClaimTiles.Count);
        Assert.Equal(camp.ClaimTiles.Count, sim.World.Fertility.Count);
        foreach (var t in camp.ClaimTiles)
        {
            var f = sim.World.Fertility[t];
            Assert.Equal(0, f.Deviation);
            Assert.Equal(0, f.LastUpdateTick);
        }

        // A pure read after 10 periods on a CLAIMED tile sees the live
        // degrade at the camp's catalog amount; the camp's own tile has no
        // entry at all. Window sits before the F→G crossing (linear math).
        var amt = camp.Spec.DegradeAmount;
        Assert.True(10 * amt < PointsToLeaveForest(Cfg), "window must sit before the crossing");
        Assert.Equal(Cfg.ForestBaseline - 10 * amt,
            BiomeDegradation.FertilityAt(sim.World, camp.ClaimTiles[0], 10 * Cfg.DegradePeriod, Cfg));
        Assert.False(sim.World.Fertility.ContainsKey(camp.At), "own tile never degrades");
    }

    [Fact]
    public void ProductionTickStop_CatchesUpClaims_WithPreStopRate()
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
        // (-DegradeAmount / DegradePeriod, including this camp) over the
        // CLAIMED tiles. Deviation = completed degrade periods × amount at
        // sim.Now (integer division drops the remainder under the
        // transition discipline); lastUpdate anchors to sim.Now. All derive
        // from config / catalog / sim.Now so the test survives retuning.
        var claimed = sim.World.Fertility[camp.ClaimTiles[0]];
        Assert.Equal(-(int)(sim.Now / Cfg.DegradePeriod) * camp.Spec.DegradeAmount, claimed.Deviation);
        Assert.Equal(sim.Now, claimed.LastUpdateTick);
    }

    [Fact]
    public void Transition_WritesEntries_OnlyForClaimTiles()
    {
        // M15: the catch-up scope IS the claim — a transition writes
        // fertility entries for exactly the claimed tiles, nothing else
        // (no radius box, no own tile). Off-ladder tiles can never be in a
        // claim (validation requires the kind's RequiredBiome), so the old
        // off-ladder-in-radius guard scenario is structurally impossible.
        var (world, ext) = MakeArmedExtractor(new TileCoord(4, 4), StructureKind.LumberCamp);

        BiomeDegradation.OnProductionTransition(world, ext, 500, Cfg);

        Assert.Equal(ext.ClaimTiles.Count, world.Fertility.Count);
        foreach (var t in ext.ClaimTiles)
            Assert.True(world.Fertility.ContainsKey(t));
        Assert.False(world.Fertility.ContainsKey(ext.At));
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
        var claimed = ext.ClaimTiles[0];
        var amt = ext.Spec.DegradeAmount;
        Assert.True(10 * amt < PointsToLeaveForest(Cfg), "stay below the crossing — linear math");

        // Stop transition after 10 degrade periods: deviation 0 → -10×amt.
        var stop = 10 * Cfg.DegradePeriod;
        BiomeDegradation.OnProductionTransition(world, ext, stop, Cfg);
        ext.TickArmed = false;
        Assert.Equal(-10 * amt, world.Fertility[claimed].Deviation);
        Assert.Equal(stop, world.Fertility[claimed].LastUpdateTick);

        // Start transition 6 recovery periods later: recovery applies since
        // deviation < 0 and not latched. +RecoveryAmount per period × 6.
        // lastUpdate anchors to the transition tick. Windows derive from config.
        var start = stop + 6 * Cfg.RecoveryPeriod;
        BiomeDegradation.OnProductionTransition(world, ext, start, Cfg);
        ext.TickArmed = true;
        Assert.Equal(-10 * amt + 6 * Cfg.RecoveryAmount, world.Fertility[claimed].Deviation);
        Assert.Equal(start, world.Fertility[claimed].LastUpdateTick);
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
        var camp = (Extractor)sim.World.Structures[ext.At];
        var claimed = camp.ClaimTiles[0];
        var stopTick = sim.World.Fertility[claimed].LastUpdateTick;
        var pts = (int)(stopTick / Cfg.DegradePeriod) * camp.Spec.DegradeAmount;  // points lost at stop
        Assert.True(pts > 0 && pts < PointsToLeaveForest(Cfg), "must stop wounded but still Forest");

        // Stored fertility right after stop = baseline - pts (no snap yet).
        Assert.Equal(Cfg.ForestBaseline - pts, BiomeDegradation.FertilityAt(sim.World, claimed, stopTick, Cfg));

        // Lazy recovery via pure read: climbs back to baseline over pts
        // recovery periods → ForestBaseline → Forest. Windows derive from config.
        var recovered = stopTick + pts * Cfg.RecoveryPeriod;
        Assert.Equal(Cfg.ForestBaseline, BiomeDegradation.FertilityAt(sim.World, claimed, recovered, Cfg));
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(sim.World, claimed, recovered, Cfg));

        // STORED state unchanged — pure reads never write (Phase A contract);
        // proof through the integration path.
        Assert.Equal(-pts, sim.World.Fertility[claimed].Deviation);
        Assert.Equal(stopTick, sim.World.Fertility[claimed].LastUpdateTick);
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
        // M15: only a transition of the CLAIMANT touches its claim tiles
        // (a different extractor can't even claim them), so the
        // materializing transition is the same camp's own re-arm.
        var (sim, ext) = MakeSimWithArmedLumberCamp(new TileCoord(4, 4));
        sim.Run();   // dormant (buffer full)
        var camp = (Extractor)sim.World.Structures[ext.At];
        var claimed = camp.ClaimTiles[0];
        var startDev = sim.World.Fertility[claimed].Deviation;
        var stopTick = sim.World.Fertility[claimed].LastUpdateTick;
        Assert.True(startDev < 0, "camp must have wounded its claim before dormancy");

        // Fire the camp's own (dormant: TickArmed=false → recovery-rate)
        // transition 3 recovery periods after the stop; the catch-up
        // materializes the accumulated recovery into stored state.
        var transitionTick = stopTick + 3 * Cfg.RecoveryPeriod;
        BiomeDegradation.OnProductionTransition(sim.World, camp, transitionTick, Cfg);

        var stored = sim.World.Fertility[claimed];
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
        var claimed = ext.ClaimTiles[0];

        var t = 90 * Cfg.DegradePeriod;   // 90 periods: through both crossings → clamps to -baseline
        BiomeDegradation.OnProductionTransition(world, ext, t, Cfg);
        ext.TickArmed = false;
        Assert.Equal(-Cfg.ForestBaseline, world.Fertility[claimed].Deviation);
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, claimed, t, Cfg));

        // Far-future pure reads: latch holds, fertility unchanged, biome stays
        // Desert. Stored entry untouched (no transition fired between).
        Assert.Equal(0, BiomeDegradation.FertilityAt(world, claimed, 1_000_000, Cfg));
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, claimed, 1_000_000, Cfg));
        Assert.Equal(-Cfg.ForestBaseline, world.Fertility[claimed].Deviation);

        // Trigger another transition while the latch holds. Catch-up under
        // the rate-since-latch (= 0, since the extractor is dormant) leaves
        // stored state unchanged.
        BiomeDegradation.OnProductionTransition(world, ext, 1_000_000, Cfg);
        Assert.Equal(-Cfg.ForestBaseline, world.Fertility[claimed].Deviation);
        Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(world, claimed, 1_000_000, Cfg));
    }

    // ====================================================================
    // Phase D — extractor self-regulation. THE M9 HEADLINE: a producing
    // extractor exhausts its land and goes dormant; no infinite single-tile
    // extraction.
    // ====================================================================

    [Fact]
    public void LumberCamp_ExhaustsItsClaim_ThenStops_TheHeadline()
    {
        // The keystone behaviour, restated over claims. A LumberCamp
        // produces until its CLAIMED tiles all degrade out of Forest — at
        // which point the claim-exhausted guard rejects the next tick and
        // the camp goes dormant (and ArmIfDormant declines to re-arm it).
        // The building's own tile stays Forest throughout.
        //
        // Setup: a Forest world with the camp and ONE Lumberjack. The camp
        // alternates buffer-full dormancy and re-arm (we drain by hand);
        // the claim crosses out of Forest at the config-derived tick.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = MakeWorld(grid);
        var ext = new Extractor(StructureKind.LumberCamp, new TileCoord(4, 4));
        world.AddStructure(ext);
        var sim = new Simulation(world, seed: 1);
        var u = world.AddUnit(new Unit(1, ext.At) { Role = UnitRole.Lumberjack });
        ext.Workers.Add(u.Id);
        ext.ArmIfDormant(sim);   // lazy auto-claim fills here
        Assert.Equal(ext.Spec.ClaimCount, ext.ClaimTiles.Count);

        // Deadline: the F→G crossing tick (config + catalog derived) plus a
        // few production periods of slack for the exhausted check to fire.
        var crossTick = PeriodsFor(PointsToLeaveForest(Cfg), ext.Spec.DegradeAmount) * Cfg.DegradePeriod;
        var deadline = crossTick + 10 * ext.Spec.ProductionPeriodTicks;

        // The simplest way to keep the camp pumping past the flip without
        // the haul machinery: manually clear the buffer between runs.
        var camp = (Extractor)sim.World.Structures[ext.At];
        var totalProduced = 0;
        while (sim.Now < deadline)
        {
            sim.Run();
            // If the camp went dormant via claim-exhaustion, the buffer
            // might not be full — and ArmIfDormant declines to re-arm.
            // Break: that's the headline.
            if (!camp.TickArmed && !camp.BufferFull()) break;
            // Otherwise drain the buffer (simulating a haul) and re-arm.
            totalProduced += camp.Buffer;
            camp.Buffer = 0;
            camp.ArmIfDormant(sim);
        }

        // The headline: dormant with the buffer NOT full → claim exhausted.
        Assert.False(camp.TickArmed);
        Assert.False(camp.BufferFull());
        // Every claimed tile crossed out of Forest; the own tile did not.
        foreach (var t in camp.ClaimTiles)
            Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(sim.World, t, sim.Now, Cfg));
        Assert.Equal(Biome.Forest, BiomeDegradation.BiomeAt(sim.World, camp.At, sim.Now, Cfg));
        // Production was BOUNDED — not infinite. (The exact number depends on
        // the rate tuning; the contract is "finite total." This is the
        // original complaint, fixed — and the fix survives the claims model.)
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

        // Dormant via claim-exhaustion (NOT buffer-full).
        Assert.False(farm.TickArmed);
        Assert.False(farm.BufferFull());
        // Every CLAIMED tile is now Desert AND permanent (latched); the
        // farm's own tile never degraded and is still Grassland.
        foreach (var t in farm.ClaimTiles)
        {
            Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(sim.World, t, sim.Now, Cfg));
            // Far-future check: still Desert. Latch is permanent.
            Assert.Equal(Biome.Desert, BiomeDegradation.BiomeAt(sim.World, t, sim.Now + 10_000_000, Cfg));
        }
        Assert.Equal(Biome.Grassland, BiomeDegradation.BiomeAt(sim.World, farm.At, sim.Now, Cfg));
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
        // M15: the Farm must also CLAIM ClaimCount Grassland-band tiles in
        // range, so degrade a patch (the site tile + enough neighbors),
        // derived from the spec.
        var grid = new TileGrid(8, 8, Biome.Forest);
        var world = MakeWorld(grid);
        var tile = new TileCoord(4, 4);
        var farmSpec = StructureCatalog.Spec(StructureKind.Farm);
        var toGrassland = Cfg.GrasslandBaseline - Cfg.ForestBaseline;   // mid-Grassland deviation
        world.Fertility[tile] = new Fertility(toGrassland, lastUpdateTick: 0);
        for (var i = 0; i < farmSpec.ClaimCount; i++)
        {
            // Neighbors at Chebyshev 1: (3,4), (5,4), (4,3), (4,5), ...
            var t = (i % 4) switch
            {
                0 => new TileCoord(tile.X - 1 - i / 4, tile.Y),
                1 => new TileCoord(tile.X + 1 + i / 4, tile.Y),
                2 => new TileCoord(tile.X, tile.Y - 1 - i / 4),
                _ => new TileCoord(tile.X, tile.Y + 1 + i / 4),
            };
            world.Fertility[t] = new Fertility(toGrassland, lastUpdateTick: 0);
        }
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
