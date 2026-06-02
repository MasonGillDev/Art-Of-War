using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Roads;
using Sim.Core.World;

namespace Sim.Tests;

// Phase C of M2: traffic gain on traversal. Direct CreditTraffic tests
// (the integration with MoveArrivalEvent gets a smoke check at the end).
public class RoadTrafficTests
{
    private static (Simulation sim, GameWorld world) MakeWorld(int width = 5)
    {
        var grid = new TileGrid(width, 1, Biome.Grassland);
        var world = new GameWorld(grid);
        return (new Simulation(world, seed: 1), world);
    }

    [Fact]
    public void FirstCreditTraffic_CreatesRoad()
    {
        var (_, world) = MakeWorld();
        var tile = new TileCoord(2, 0);
        Assert.False(world.Roads.ContainsKey(tile));

        Road.CreditTraffic(world, tile, now: 0);

        Assert.True(world.Roads.ContainsKey(tile));
        Assert.Equal(RoadConstants.BASE_GAIN, world.Roads[tile].Condition);
        Assert.Equal(0L, world.Roads[tile].LastDecayTick);
    }

    [Fact]
    public void SustainedTraffic_BuildsConditionWithDiminishingReturns()
    {
        // Credit traffic at fast tempo (every 10 ticks — well faster than
        // decay can erase any of it). Condition should rise toward CAP,
        // each gain smaller than the last.
        var (_, world) = MakeWorld();
        var tile = new TileCoord(2, 0);
        var lastGain = int.MaxValue;
        var prevCondition = 0;

        for (var t = 0L; t < 5_000; t += 10)
        {
            Road.CreditTraffic(world, tile, t);
            var c = world.Roads[tile].Condition;
            var gain = c - prevCondition;
            // Allow equal (gain plateau at floor) but never an increase.
            Assert.True(gain <= lastGain,
                $"Diminishing returns violated at t={t}: gain={gain} > lastGain={lastGain}");
            lastGain = gain;
            prevCondition = c;
        }

        // After 500 traversals at this tempo, condition should be near cap.
        Assert.True(prevCondition >= RoadConstants.CONDITION_MAX * 9 / 10,
            $"Condition only reached {prevCondition}/{RoadConstants.CONDITION_MAX}");
    }

    [Fact]
    public void GainAtCap_EqualsFloor()
    {
        // Start at cap. Credit traffic. Decay is zero (same tick). Gain
        // should be exactly GAIN_FLOOR... but then clamped to not overshoot
        // cap. So condition stays at cap.
        var (_, world) = MakeWorld();
        var tile = new TileCoord(2, 0);
        world.Roads[tile] = new RoadState(RoadConstants.CONDITION_MAX, 0);

        Road.CreditTraffic(world, tile, now: 0);
        Assert.Equal(RoadConstants.CONDITION_MAX, world.Roads[tile].Condition);
    }

    [Fact]
    public void SingleTraversal_ThenLongSilence_DecaysToZero()
    {
        // One walker once: condition rises by a bit, then decay erases it.
        // Tile drops from world.Roads. This is the "one citizen can't make
        // a road" emergence property — no counter, just gain vs decay.
        var (_, world) = MakeWorld();
        var tile = new TileCoord(2, 0);

        Road.CreditTraffic(world, tile, now: 0);
        var initialCondition = world.Roads[tile].Condition;
        Assert.True(initialCondition > 0);

        // Decay to 0 takes initialCondition / DECAY_PER_PERIOD periods.
        var ticksToZero = (long)initialCondition * RoadConstants.DECAY_PERIOD
                          / RoadConstants.DECAY_PER_PERIOD;

        // Observe at that tick + a bit more, via CatchUpDecay to make the tile drop.
        Road.CatchUpDecay(world, tile, now: ticksToZero + 1);
        Assert.False(world.Roads.ContainsKey(tile));
    }

    [Fact]
    public void SameTickTraversals_Stack_DeterministicByOrder()
    {
        // Two CreditTraffic calls at the same tick on the same tile: the
        // second sees the first's gain (CatchUpDecay is a no-op because
        // LastDecayTick == now). Diminishing returns applies — the second
        // gain is smaller because condition is higher.
        var (_, world) = MakeWorld();
        var tile = new TileCoord(2, 0);

        Road.CreditTraffic(world, tile, now: 100);
        var afterFirst = world.Roads[tile].Condition;

        Road.CreditTraffic(world, tile, now: 100);
        var afterSecond = world.Roads[tile].Condition;

        var firstGain = afterFirst;             // started at 0
        var secondGain = afterSecond - afterFirst;
        Assert.True(secondGain < firstGain, "Second same-tick gain should be smaller (diminishing returns)");
        Assert.True(secondGain >= RoadConstants.GAIN_FLOOR);
    }

    // -------- Integration with MoveArrivalEvent --------

    [Fact]
    public void UnitWalk_CreditsEveryTileEntered()
    {
        // Walk a unit from (0,0) to (4,0). Tiles 1..4 should each get
        // exactly one traffic credit (tile 0 is the starting position,
        // no arrival event there).
        var (sim, world) = MakeWorld(width: 5);
        world.AddUnit(new Unit(1, new TileCoord(0, 0)));
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(4, 0)));
        sim.Run();

        for (var x = 1; x <= 4; x++)
        {
            var t = new TileCoord(x, 0);
            Assert.True(world.Roads.ContainsKey(t), $"tile ({x},0) should be credited");
            Assert.Equal(RoadConstants.BASE_GAIN, world.Roads[t].Condition);
        }
        Assert.False(world.Roads.ContainsKey(new TileCoord(0, 0)),
            "starting tile should not be credited (no arrival event)");
    }

    [Fact]
    public void FencedArrivalEvent_DoesNotCreditRoad()
    {
        // A stale MoveArrivalEvent that fences out via epoch mismatch must
        // NOT credit the tile. The pure-read wall depends on traversals
        // being the ONLY mutation, and fenced arrivals aren't real
        // traversals from the current task.
        var (sim, world) = MakeWorld(width: 5);
        world.AddUnit(new Unit(1, new TileCoord(0, 0)));

        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(4, 0)));
        sim.Run(until: 11);         // one tile traversed (Grassland cost 10)

        // Retask to the same destination — bumps epoch, the second arrival
        // (already scheduled at tick 20 by the original chain) will fence out.
        sim.SubmitIntent(sim.Now, new MoveIntent(1, new TileCoord(4, 0)));
        sim.Run();

        // Each tile from start to dest should have been credited exactly
        // once (only the new chain's arrivals count). Tile gain at base = 50;
        // a tile credited twice would have ~99 (50 + 49).
        for (var x = 1; x <= 4; x++)
        {
            var c = world.Roads[new TileCoord(x, 0)].Condition;
            Assert.True(c <= RoadConstants.BASE_GAIN + 1,
                $"tile ({x},0) credited too many times: condition={c}");
        }
    }
}
