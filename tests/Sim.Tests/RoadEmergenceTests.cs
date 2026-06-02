using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Roads;
using Sim.Core.World;

namespace Sim.Tests;

// Phase E of M2: the emergence threshold ("one citizen can't make a road")
// + same-tick contention + twin-run + snapshot round-trip on a roaded world.
public class RoadEmergenceTests
{
    // -------- The emergence threshold --------

    [Fact]
    public void SingleTraversal_LongSilence_Decays_NoLastingRoad()
    {
        // One unit walks across a tile, then nothing for a long time.
        // The tile gets a credit, but decay erases it before any second
        // traversal could reinforce. No counter — emergent from gain vs decay.
        var grid = new TileGrid(3, 1, Biome.Grassland);
        var world = new GameWorld(grid);
        var sim = new Simulation(world, seed: 1);
        world.AddUnit(new Unit(1, new TileCoord(0, 0)));
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(2, 0)));
        sim.Run();   // walk completes

        var midTile = new TileCoord(1, 0);
        Assert.True(world.Roads.ContainsKey(midTile));
        var conditionAfterWalk = world.Roads[midTile].Condition;

        // Now silence: advance the clock far past full decay.
        // Decay = 1 per 100 ticks; condition was BASE_GAIN = 50; 5000 ticks erases.
        Road.CatchUpDecay(world, midTile, now: 10_000);
        Assert.False(world.Roads.ContainsKey(midTile),
            $"single traversal of condition {conditionAfterWalk} should have decayed away by tick 10000");
    }

    [Fact]
    public void SustainedTraffic_BuildsLastingRoad()
    {
        // Repeated walks over the same route faster than decay can erase
        // → condition climbs. Concrete tempo: a walk every 200 ticks; per
        // walk the mid tile gets ~BASE_GAIN credit; decay over 200 ticks =
        // 2 condition units. Net gain per cycle ≈ 50 (then less, diminishing).
        var grid = new TileGrid(3, 1, Biome.Grassland);
        var world = new GameWorld(grid);
        var midTile = new TileCoord(1, 0);

        long now = 0;
        for (var i = 0; i < 20; i++)
        {
            // Each "traversal" is a manual credit at the chosen tempo. This
            // sidesteps movement-time variance and pins the gain-vs-decay math.
            Road.CreditTraffic(world, midTile, now);
            now += 200;
        }
        // After 20 credits with that tempo, condition should be well above
        // single-traversal level.
        Assert.True(world.Roads.ContainsKey(midTile));
        Assert.True(world.Roads[midTile].Condition > RoadConstants.BASE_GAIN,
            $"sustained traffic should exceed single-traversal level; got {world.Roads[midTile].Condition}");
    }

    // -------- Same-tick contention (gameplay-observable ordering) --------

    [Fact]
    public void TwoTraversalsOnSameTileSameTick_SecondSeesFirstsGain()
    {
        // Two units arrive on the same tile on the same sim tick. Each
        // calls CreditTraffic. The second's gain reads the post-first
        // condition (decay is a no-op since LastDecayTick == now), so the
        // second sees diminishing returns. Final state deterministic by
        // submission order.
        var grid = new TileGrid(3, 1, Biome.Grassland);
        var world = new GameWorld(grid);
        var sim = new Simulation(world, seed: 1);
        var tile = new TileCoord(1, 0);

        // Two units at (0,0) and (2,0) — both move to (1,0). At Grassland
        // cost 10, both arrivals fire at tick 10. Submission order decides
        // who's processed first (lower Seq).
        world.AddUnit(new Unit(1, new TileCoord(0, 0)));
        world.AddUnit(new Unit(2, new TileCoord(2, 0)));
        sim.SubmitIntent(0, new MoveIntent(1, tile));
        sim.SubmitIntent(0, new MoveIntent(2, tile));
        sim.Run();

        // The tile got TWO credits at the same tick. Final condition reflects
        // both (with the second's gain diminished). It must be > one credit
        // alone and < two flat credits.
        var c = world.Roads[tile].Condition;
        Assert.True(c > RoadConstants.BASE_GAIN, "second credit didn't apply");
        Assert.True(c < 2 * RoadConstants.BASE_GAIN, "diminishing returns broken");
    }

    [Fact]
    public void TwoTraversalsSameTick_ReproducibleAcrossRuns()
    {
        Simulation Run()
        {
            var grid = new TileGrid(3, 1, Biome.Grassland);
            var world = new GameWorld(grid);
            var sim = new Simulation(world, seed: 1);
            world.AddUnit(new Unit(1, new TileCoord(0, 0)));
            world.AddUnit(new Unit(2, new TileCoord(2, 0)));
            sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(1, 0)));
            sim.SubmitIntent(0, new MoveIntent(2, new TileCoord(1, 0)));
            sim.Run();
            return sim;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    // -------- Twin-run + snapshot round-trip on a roaded world --------

    [Fact]
    public void BuildTrafficAbandonScenario_HashesMatchAcrossRuns()
    {
        Simulation Run()
        {
            var grid = new TileGrid(6, 1, Biome.Grassland);
            var world = new GameWorld(grid);
            var sim = new Simulation(world, seed: 0xAB);
            world.AddUnit(new Unit(1, new TileCoord(0, 0)));

            // Walk back and forth a few times to build the road.
            sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(5, 0)));
            sim.Run();
            sim.SubmitIntent(sim.Now, new MoveIntent(1, new TileCoord(0, 0)));
            sim.Run();
            sim.SubmitIntent(sim.Now, new MoveIntent(1, new TileCoord(5, 0)));
            sim.Run();
            return sim;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
        Assert.True(a.World.Roads.Count > 0);
    }

    [Fact]
    public void Snapshot_RoundTripsPartiallyRoadedWorld()
    {
        var grid = new TileGrid(8, 1, Biome.Grassland);
        var world = new GameWorld(grid);
        var sim = new Simulation(world, seed: 0xCD);
        world.AddUnit(new Unit(1, new TileCoord(0, 0)));
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(7, 0)));
        sim.Run();
        // Some tiles now have road state from the walk.
        Assert.True(world.Roads.Count > 0);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xCD);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(world.Roads.Count, restored.World.Roads.Count);
    }
}
