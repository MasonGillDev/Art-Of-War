using Sim.Core.Battlefields;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Roads;
using Sim.Core.World;

namespace Sim.Tests;

// Phase A of M2: EffectiveCost reads hand-set road condition smoothly,
// floored, and never mutates. Decay is NOT yet applied (Phase B).
// M43: a road is a LINK between two adjacent subtiles (docs/subtile-movement.md); the
// priced step is From -> To and the link is the one between them. The base cost is the
// step's, a quarter of the tile's hop: the tests derive it from the same rule.
public class RoadCostTests
{
    // Two neighbouring subtiles inside tile (0, 1) and tile (1, 1): the step crosses a tile edge.
    private static readonly WorldSubtile From = new(3, 5);
    private static readonly WorldSubtile To = new(4, 5);
    private static readonly SubtileLink Arc = SubtileLink.Between(From, To);

    // The plain cost of that step on this ground: a quarter of the hop, rounded up.
    private static int Plain(GameWorld world) => (int)SubtileStepRules.StepTicks(world, To.Tile);

    private static int Cost(GameWorld world, long now = 0) =>
        Road.EffectiveCost(world, From, To, now, Plain(world));

    private static GameWorld GrasslandWorld(int w = 4, int h = 4)
    {
        var grid = new TileGrid(w, h, Biome.Grassland);
        return new GameWorld(grid);
    }

    // Expected cost on a maxed-out road, derived from the same constants the
    // production formula reads — so biome-cost retunes never touch this file.
    // (At cap: reduction = biomeCost × MAX_REDUCTION_PERCENT %, floored at MIN_COST.)
    private static int CapCost(Biome b)
    {
        var c = (Biomes.MoveCost(b) + Subtile.Size - 1) / Subtile.Size;
        var reduced = c - (int)((long)c * RoadConstants.MAX_REDUCTION_PERCENT / 100L);
        return reduced < RoadConstants.MIN_COST ? RoadConstants.MIN_COST : reduced;
    }

    [Fact]
    public void NoRoad_ReturnsBiomeCost()
    {
        var world = GrasslandWorld();
        Assert.Equal(Plain(world), Cost(world));
    }

    [Fact]
    public void CostDecreasesSmoothly_AsConditionRises()
    {
        var world = GrasslandWorld();
        var biomeCost = Plain(world);
        var prev = biomeCost + 1;

        for (var condition = 0; condition <= RoadConstants.CONDITION_MAX; condition += 50)
        {
            world.Roads[Arc] = new RoadState(condition, 0);
            var cost = Cost(world);
            Assert.True(cost <= prev,
                $"Cost increased between condition steps: prev={prev}, cur={cost} at condition={condition}");
            prev = cost;
        }
    }

    [Fact]
    public void Cost_NeverDropsBelow_MIN_COST()
    {
        // Grassland at cap: reduced by MAX_REDUCTION_PERCENT, never below
        // MIN_COST. Expected derives from the constants.
        var world = GrasslandWorld();

        world.Roads[Arc] = new RoadState(RoadConstants.CONDITION_MAX, 0);
        var cost = Cost(world);
        Assert.True(cost >= RoadConstants.MIN_COST,
            $"Cost {cost} below MIN_COST {RoadConstants.MIN_COST}");

        Assert.Equal(CapCost(Biome.Grassland), cost);
    }

    [Fact]
    public void ForestRoad_AtCap_ProportionallyReduced()
    {
        // Proportional reduction makes the road actually useful on
        // expensive terrain (the old flat-8 model barely dented forest).
        // Expected derives from constants; the relational assert pins the
        // "meaningful speedup" shape under any tuning.
        var grid = new TileGrid(4, 4, Biome.Forest);
        var world = new GameWorld(grid);
        world.Roads[Arc] = new RoadState(RoadConstants.CONDITION_MAX, 0);
        var cost = Cost(world);
        Assert.Equal(CapCost(Biome.Forest), cost);
        Assert.True(cost * 2 < (Biomes.MoveCost(Biome.Forest) + Subtile.Size - 1) / Subtile.Size,
            "a maxed road should at least halve forest cost");
    }

    [Fact]
    public void MountainRoad_AtCap_ProportionallyReduced()
    {
        // The load-bearing case for proportional roads: a maxed mountain
        // road gets the same ~3x speedup as every other biome (the old
        // flat-8 model gave mountain a useless 1.22x). Derived from
        // constants; the relational assert pins the proportionality.
        var grid = new TileGrid(4, 4, Biome.Mountain);
        var world = new GameWorld(grid);
        world.Roads[Arc] = new RoadState(RoadConstants.CONDITION_MAX, 0);
        var cost = Cost(world);
        Assert.Equal(CapCost(Biome.Mountain), cost);
        Assert.True(cost * 2 < (Biomes.MoveCost(Biome.Mountain) + Subtile.Size - 1) / Subtile.Size,
            "a maxed road should at least halve mountain cost");
    }

    [Fact]
    public void ZeroCondition_TreatedAsNoRoad()
    {
        // A stale RoadState with Condition=0 should give plain biome cost
        // (defensive: real code removes such entries, but the read must not
        // double-reduce by reading a sentinel).
        var world = GrasslandWorld();
        world.Roads[Arc] = new RoadState(0, 0);
        Assert.Equal(Plain(world), Cost(world));
    }

    [Fact]
    public void EffectiveCost_IsPureRead_NoMutation()
    {
        // Set up a varied road set, hash the sim, call EffectiveCost 100 times
        // against different tiles, hash again — must match.
        var world = GrasslandWorld(8, 8);
        var sim = new Simulation(world, seed: 1);
        for (var i = 0; i < 5; i++)
            world.Roads[SubtileLink.FromOwner(new WorldSubtile(i, i), SubtileLink.Axis.East)] = new RoadState(200 + 100 * i, 7);
        var beforeHash = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
        {
            for (var x = 0; x < 7; x++)
                for (var y = 0; y < 8; y++)
                    Road.EffectiveCost(world, new WorldSubtile(x, y), new WorldSubtile(x + 1, y), 0, 8);
        }

        Assert.Equal(beforeHash, Snapshot.Hash(sim));
    }

    [Fact]
    public void ConditionAt_IsPureRead_NoMutation()
    {
        var world = GrasslandWorld();
        var sim = new Simulation(world, seed: 1);
        var arc = SubtileLink.FromOwner(new WorldSubtile(0, 0), SubtileLink.Axis.East);
        world.Roads[arc] = new RoadState(500, 7);
        var beforeHash = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
            Road.ConditionAt(world, arc, now: 0);

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
        world.Roads[Arc] = new RoadState(condition: 50, lastDecayTick: 0);

        // 10000 ticks at decay-per-period=1, period=100 → 100 decay total →
        // 50 - 100 = clamped to 0 via ConditionAt(now=10000).
        var sim = new Simulation(world, seed: 1);
        // Advance Now via a no-op event.
        sim.Schedule(10_000, new NoOp());
        sim.Run();

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);
        Assert.False(restored.World.Roads.ContainsKey(Arc),
            "fully-decayed-but-untouched road arc should not round-trip");
    }

    private sealed class NoOp : ScheduledEvent
    {
        public override void Apply(Simulation sim) { }
    }

    [Fact]
    public void Snapshot_RoundTripsRoadSet()
    {
        var world = GrasslandWorld(8, 8);
        var a = SubtileLink.FromOwner(new WorldSubtile(1, 2), SubtileLink.Axis.East);
        var b = SubtileLink.FromOwner(new WorldSubtile(1, 2), SubtileLink.Axis.South);   // same owner, other axis
        var c = SubtileLink.FromOwner(new WorldSubtile(3, 6), SubtileLink.Axis.South);
        world.Roads[a] = new RoadState(400, 50);
        world.Roads[b] = new RoadState(1000, 0);
        world.Roads[c] = new RoadState(75, 1234);
        var sim = new Simulation(world, seed: 1);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(3, restored.World.Roads.Count);
        Assert.Equal(400, restored.World.Roads[a].Condition);
        Assert.Equal(50,  restored.World.Roads[a].LastDecayTick);
        Assert.Equal(1000, restored.World.Roads[b].Condition);
        Assert.Equal(75, restored.World.Roads[c].Condition);
    }
}
