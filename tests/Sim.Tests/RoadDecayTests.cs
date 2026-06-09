using Sim.Core.Persistence;
using Sim.Core.Roads;
using Sim.Core.World;
using Sim.Core.Engine;

namespace Sim.Tests;

// Phase B of M2: lazy decay catch-up. The headline test is
// CatchUpDecay_IsObservationIndependent — the property that catches the
// silent remainder-drop desync. Everything else here is structural.
public class RoadDecayTests
{
    private static GameWorld MakeRoadWorld(int condition, long lastDecayTick)
    {
        var grid = new TileGrid(4, 4, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Roads[new TileCoord(1, 1)] = new RoadState(condition, lastDecayTick);
        return world;
    }

    // ====================================================================
    // THE headline test. If lazy decay is wrong, this is what catches it.
    // ====================================================================

    [Fact]
    public void CatchUpDecay_IsObservationIndependent()
    {
        // Two worlds with identical starting state. One catches up once at
        // tick T; the other catches up at many intermediate ticks along the
        // way to T. Final stored condition AND LastDecayTick must be identical.
        const int startCondition = 500;
        const long T = 1234;
        var tile = new TileCoord(1, 1);

        var w1 = MakeRoadWorld(startCondition, lastDecayTick: 0);
        Road.CatchUpDecay(w1, tile, T);

        var w2 = MakeRoadWorld(startCondition, lastDecayTick: 0);
        // Touch at irregular intervals — including sub-period times (50, 137)
        // that should NOT advance the decay clock past the boundary they
        // bracket. The carry is what makes this work.
        foreach (var t in new long[] { 50, 137, 250, 400, 550, 700, 900, 1100, T })
            Road.CatchUpDecay(w2, tile, t);

        var c1 = w1.Roads.TryGetValue(tile, out var r1) ? r1.Condition : 0;
        var c2 = w2.Roads.TryGetValue(tile, out var r2) ? r2.Condition : 0;
        var l1 = r1?.LastDecayTick ?? 0;
        var l2 = r2?.LastDecayTick ?? 0;
        Assert.Equal(c1, c2);
        Assert.Equal(l1, l2);
    }

    [Fact]
    public void CatchUpDecay_TickByTickMatchesSingleJump()
    {
        // Most torturous version: catch up at every single tick from 1 to T.
        // The remainder carry must keep this identical to a single jump.
        const int startCondition = 500;
        const long T = 1234;
        var tile = new TileCoord(1, 1);

        var w1 = MakeRoadWorld(startCondition, lastDecayTick: 0);
        Road.CatchUpDecay(w1, tile, T);

        var w2 = MakeRoadWorld(startCondition, lastDecayTick: 0);
        for (var t = 1L; t <= T; t++) Road.CatchUpDecay(w2, tile, t);

        var c1 = w1.Roads.TryGetValue(tile, out var r1) ? r1.Condition : 0;
        var c2 = w2.Roads.TryGetValue(tile, out var r2) ? r2.Condition : 0;
        var l1 = r1?.LastDecayTick ?? 0;
        var l2 = r2?.LastDecayTick ?? 0;
        Assert.Equal(c1, c2);
        Assert.Equal(l1, l2);
    }

    // ====================================================================
    // Pure-read / write-path agreement
    // ====================================================================

    [Fact]
    public void ConditionAt_MatchesPostWriteCondition_ForSameNow()
    {
        // For any (start, now), the pure read ConditionAt(now) must equal
        // the value that CatchUpDecay would have written at the same now.
        var tile = new TileCoord(1, 1);
        foreach (var now in new long[] { 0, 50, 99, 100, 101, 500, 999, 1000, 1234, 5000 })
        {
            var wRead  = MakeRoadWorld(condition: 500, lastDecayTick: 0);
            var wWrite = MakeRoadWorld(condition: 500, lastDecayTick: 0);

            var read = Road.ConditionAt(wRead, tile, now);
            Road.CatchUpDecay(wWrite, tile, now);
            var written = wWrite.Roads.TryGetValue(tile, out var r) ? r.Condition : 0;

            Assert.Equal(written, read);
        }
    }

    [Fact]
    public void ConditionAt_IsPureRead_NoMutationEvenWithDecayPending()
    {
        var world = MakeRoadWorld(condition: 500, lastDecayTick: 0);
        var sim = new Simulation(world, seed: 1);
        var hashBefore = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
            Road.ConditionAt(world, new TileCoord(1, 1), now: 100_000);

        Assert.Equal(hashBefore, Snapshot.Hash(sim));
    }

    [Fact]
    public void EffectiveCost_AppliesDecay_BeforeCostReduction()
    {
        // Tile starts at full road. After decay-equivalent ticks, effective
        // cost should reflect the decayed condition.
        var world = MakeRoadWorld(RoadConstants.CONDITION_MAX, lastDecayTick: 0);
        var tile = new TileCoord(1, 1);

        // No decay yet: reduction = 10 * 66 * 1000 / (100 * 1000) = 6, cost = 4.
        Assert.Equal(4, Road.EffectiveCost(world, tile, now: 0));

        // After enough decay for condition to reach 0: cost back to 10.
        // Decay rate = 1 per 100 ticks; CONDITION_MAX = 1000; so 100_000 ticks
        // brings it to 0.
        var fullyDecayedAt = (long)RoadConstants.CONDITION_MAX
                             * RoadConstants.DECAY_PERIOD
                             / RoadConstants.DECAY_PER_PERIOD;
        Assert.Equal(world.Grid.TerrainCost(tile),
                     Road.EffectiveCost(world, tile, now: fullyDecayedAt));
    }

    // ====================================================================
    // Removal on hitting 0
    // ====================================================================

    [Fact]
    public void DecayToZero_RemovesTileFromRoadSet()
    {
        var world = MakeRoadWorld(condition: 10, lastDecayTick: 0);
        var tile = new TileCoord(1, 1);
        Assert.Single(world.Roads);

        // Enough decay to wipe: 10 condition / 1 per period = 10 periods = 1000 ticks.
        Road.CatchUpDecay(world, tile, now: 1000);

        Assert.Empty(world.Roads);
    }

    [Fact]
    public void DecayPastZero_RemovesTile_NoUnderflow()
    {
        var world = MakeRoadWorld(condition: 5, lastDecayTick: 0);
        var tile = new TileCoord(1, 1);

        Road.CatchUpDecay(world, tile, now: 1_000_000_000);

        Assert.Empty(world.Roads);
    }

    [Fact]
    public void ConditionAt_ReturnsZero_PastFullDecay()
    {
        var world = MakeRoadWorld(condition: 5, lastDecayTick: 0);
        Assert.Equal(0, Road.ConditionAt(world, new TileCoord(1, 1), now: 1_000_000));
    }

    // ====================================================================
    // Sub-period catch-up carries the remainder
    // ====================================================================

    [Fact]
    public void CatchUpDecay_SubPeriod_LeavesConditionUnchanged_AdvancesNothing()
    {
        var world = MakeRoadWorld(condition: 100, lastDecayTick: 0);
        var tile = new TileCoord(1, 1);
        // 50 ticks elapsed; one period = 100 ticks; no boundary crossed.
        Road.CatchUpDecay(world, tile, now: 50);

        var r = world.Roads[tile];
        Assert.Equal(100, r.Condition);
        Assert.Equal(0, r.LastDecayTick);   // remainder banked
    }

    [Fact]
    public void CatchUpDecay_BoundaryCrossed_AdvancesByCompletedPeriodsOnly()
    {
        var world = MakeRoadWorld(condition: 100, lastDecayTick: 0);
        var tile = new TileCoord(1, 1);
        // 237 ticks elapsed → 2 completed periods (200 ticks), 37 remainder.
        Road.CatchUpDecay(world, tile, now: 237);

        var r = world.Roads[tile];
        Assert.Equal(98, r.Condition);                              // -2
        Assert.Equal(200, r.LastDecayTick);                         // advanced by 2 * 100
    }

    [Fact]
    public void CatchUpDecay_NoRoad_NoOp()
    {
        var world = new GameWorld(new TileGrid(4, 4, Biome.Grassland));
        var sim = new Simulation(world, seed: 1);
        var hashBefore = Snapshot.Hash(sim);

        Road.CatchUpDecay(world, new TileCoord(1, 1), now: 1000);

        Assert.Equal(hashBefore, Snapshot.Hash(sim));
        Assert.Empty(world.Roads);
    }
}
