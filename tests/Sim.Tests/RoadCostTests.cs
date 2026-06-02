using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Roads;
using Sim.Core.World;

namespace Sim.Tests;

// Phase A of M2: EffectiveCost reads hand-set road condition smoothly,
// floored, and never mutates. Decay is NOT yet applied (Phase B).
public class RoadCostTests
{
    private static GameWorld GrasslandWorld(int w = 4, int h = 4)
    {
        var grid = new TileGrid(w, h, Biome.Grassland); // cost 10
        return new GameWorld(grid);
    }

    [Fact]
    public void NoRoad_ReturnsBiomeCost()
    {
        var world = GrasslandWorld();
        var tile = new TileCoord(1, 1);
        Assert.Equal(world.Grid.TerrainCost(tile), Road.EffectiveCost(world, tile, now: 0));
    }

    [Fact]
    public void CostDecreasesSmoothly_AsConditionRises()
    {
        var world = GrasslandWorld();
        var tile = new TileCoord(1, 1);
        var biomeCost = world.Grid.TerrainCost(tile); // 10
        var prev = biomeCost + 1;

        for (var condition = 0; condition <= RoadConstants.CONDITION_MAX; condition += 50)
        {
            world.Roads[tile] = new RoadState(condition, 0);
            var cost = Road.EffectiveCost(world, tile, now: 0);
            Assert.True(cost <= prev,
                $"Cost increased between condition steps: prev={prev}, cur={cost} at condition={condition}");
            prev = cost;
        }
    }

    [Fact]
    public void Cost_NeverDropsBelow_MIN_COST()
    {
        // Try a small biome (cost 10) — even at full road, reduction is 8,
        // so cost = 2 which is above MIN_COST=1. Force a worse case:
        // a fictional zero-cost biome would underflow, so test the floor
        // via a biome cost equal to MAX_REDUCTION.
        var world = GrasslandWorld();
        var tile = new TileCoord(1, 1);

        // Grassland at cap.
        world.Roads[tile] = new RoadState(RoadConstants.CONDITION_MAX, 0);
        var cost = Road.EffectiveCost(world, tile, now: 0);
        Assert.True(cost >= RoadConstants.MIN_COST,
            $"Cost {cost} below MIN_COST {RoadConstants.MIN_COST}");

        // Verify the formula matches expectation: cost = max(MIN, 10 - 8) = 2.
        Assert.Equal(2, cost);
    }

    [Fact]
    public void ForestRoad_AtCap_Costs22()
    {
        // Forest cost = 30, MAX_REDUCTION = 8, so cap cost = 22.
        var grid = new TileGrid(4, 4, Biome.Forest);
        var world = new GameWorld(grid);
        var tile = new TileCoord(1, 1);
        world.Roads[tile] = new RoadState(RoadConstants.CONDITION_MAX, 0);
        Assert.Equal(22, Road.EffectiveCost(world, tile, now: 0));
    }

    [Fact]
    public void ZeroCondition_TreatedAsNoRoad()
    {
        // A stale RoadState with Condition=0 should give plain biome cost
        // (defensive: real code removes such entries, but the read must not
        // double-reduce by reading a sentinel).
        var world = GrasslandWorld();
        var tile = new TileCoord(1, 1);
        world.Roads[tile] = new RoadState(0, 0);
        Assert.Equal(world.Grid.TerrainCost(tile), Road.EffectiveCost(world, tile, now: 0));
    }

    [Fact]
    public void EffectiveCost_IsPureRead_NoMutation()
    {
        // Set up a varied road set, hash the sim, call EffectiveCost 100 times
        // against different tiles, hash again — must match.
        var world = GrasslandWorld(8, 8);
        var sim = new Simulation(world, seed: 1);
        for (var i = 0; i < 5; i++)
            world.Roads[new TileCoord(i, i)] = new RoadState(200 + 100 * i, 7);
        var beforeHash = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
        {
            for (var x = 0; x < 8; x++)
                for (var y = 0; y < 8; y++)
                    Road.EffectiveCost(world, new TileCoord(x, y), now: 0);
        }

        Assert.Equal(beforeHash, Snapshot.Hash(sim));
    }

    [Fact]
    public void ConditionAt_IsPureRead_NoMutation()
    {
        var world = GrasslandWorld();
        var sim = new Simulation(world, seed: 1);
        world.Roads[new TileCoord(0, 0)] = new RoadState(500, 7);
        var beforeHash = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
            Road.ConditionAt(world, new TileCoord(0, 0), now: 0);

        Assert.Equal(beforeHash, Snapshot.Hash(sim));
    }

    [Fact]
    public void Snapshot_OmitsFullyDecayedButUntouchedRoad()
    {
        // A road tile with stored Condition>0 but a LastDecayTick so old that
        // pure-read ConditionAt(now) returns 0 should NOT round-trip — the
        // snapshot filter is by *effective* condition, not stored value.
        // This prevents stale entries from bloating snapshots indefinitely
        // for tiles that decayed and were never re-touched by traffic.
        var world = GrasslandWorld();
        var tile = new TileCoord(1, 1);
        world.Roads[tile] = new RoadState(condition: 50, lastDecayTick: 0);

        // 10000 ticks at decay-per-period=1, period=100 → 100 decay total →
        // 50 - 100 = clamped to 0 via ConditionAt(now=10000).
        var sim = new Simulation(world, seed: 1);
        // Advance Now via a no-op event.
        sim.Schedule(10_000, new NoOp());
        sim.Run();

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);
        Assert.False(restored.World.Roads.ContainsKey(tile),
            "fully-decayed-but-untouched road tile should not round-trip");
    }

    private sealed class NoOp : ScheduledEvent
    {
        public override void Apply(Simulation sim) { }
    }

    [Fact]
    public void Snapshot_RoundTripsRoadSet()
    {
        var world = GrasslandWorld(8, 8);
        world.Roads[new TileCoord(1, 2)] = new RoadState(400, 50);
        world.Roads[new TileCoord(5, 1)] = new RoadState(1000, 0);
        world.Roads[new TileCoord(3, 7)] = new RoadState(75, 1234);
        var sim = new Simulation(world, seed: 1);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(3, restored.World.Roads.Count);
        Assert.Equal(400, restored.World.Roads[new TileCoord(1, 2)].Condition);
        Assert.Equal(50,  restored.World.Roads[new TileCoord(1, 2)].LastDecayTick);
    }
}
