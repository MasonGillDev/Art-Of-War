using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

public class DeterminismTests
{
    private static Simulation BuildBasicScenario()
    {
        var grid = new TileGrid(10, 10);  // defaults to Grassland (cost 10)
        for (var i = 1; i < 9; i++) grid.SetBiome(new TileCoord(i, i), Biome.Forest);
        var world = new GameWorld(grid);
        world.AddUnit(1, new TileCoord(0, 0));
        var sim = new Simulation(world, seed: 0xC0FFEE);
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(9, 9)));
        return sim;
    }

    [Fact]
    public void IdenticalScenarios_ProduceIdenticalSnapshots()
    {
        var a = BuildBasicScenario();
        var b = BuildBasicScenario();
        a.Run();
        b.Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
        Assert.Equal(a.ResolvedLog.Count, b.ResolvedLog.Count);
        Assert.Equal(a.Now, b.Now);
    }

    [Fact]
    public void ReplayingRecordedIntentLog_ReproducesFinalState()
    {
        var original = BuildBasicScenario();
        original.Run();
        var originalHash = Snapshot.Hash(original);
        var capturedIntents = original.IntentLog.ToList();

        // Fresh world, same seed, replay the recorded intents.
        var grid = new TileGrid(10, 10);  // defaults to Grassland (cost 10)
        for (var i = 1; i < 9; i++) grid.SetBiome(new TileCoord(i, i), Biome.Forest);
        var world = new GameWorld(grid);
        world.AddUnit(1, new TileCoord(0, 0));
        var replay = new Simulation(world, seed: 0xC0FFEE);
        foreach (var (at, intent) in capturedIntents) replay.SubmitIntent(at, intent);
        replay.Run();

        Assert.Equal(originalHash, Snapshot.Hash(replay));
    }

    [Fact]
    public void UnitReachesDestination_AtExpectedTick()
    {
        // Open 3x3 grid, Grassland cost 10 per step. (0,0) -> (2,2) = 4 steps = 40 ticks.
        var grid = new TileGrid(3, 3);
        var world = new GameWorld(grid);
        world.AddUnit(1, new TileCoord(0, 0));
        var sim = new Simulation(world, seed: 1);
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(2, 2)));
        sim.Run();
        Assert.Equal(new TileCoord(2, 2), sim.World.Units[1].Position);
        Assert.Equal(40, sim.Now);
    }

    // Counter-event proves the tiebreak: two events scheduled at the same tick
    // must resolve in submission order (Seq ascending), not in some
    // implementation-defined heap order.
    private sealed class RecordEvent : ScheduledEvent
    {
        private readonly string _tag;
        private readonly List<string> _sink;
        public RecordEvent(string tag, List<string> sink) { _tag = tag; _sink = sink; }
        public override void Apply(Simulation sim) => _sink.Add(_tag);
    }

    [Fact]
    public void SameTickEvents_ResolveInSubmissionOrder()
    {
        var world = new GameWorld(new TileGrid(1, 1));
        var sim = new Simulation(world, seed: 1);
        var resolved = new List<string>();
        sim.Schedule(100, new RecordEvent("A", resolved));
        sim.Schedule(100, new RecordEvent("B", resolved));
        sim.Schedule(100, new RecordEvent("C", resolved));
        sim.Schedule(50, new RecordEvent("Z-earlier", resolved));
        sim.Run();
        Assert.Equal(new[] { "Z-earlier", "A", "B", "C" }, resolved);
    }

    [Fact]
    public void Pathfinding_RoutesAroundExpensiveTerrain()
    {
        var grid = new TileGrid(5, 3);
        // Wall of expensive terrain at x=2 — cheap detour exists via y=0 or y=2.
        grid.SetBiome(new TileCoord(2, 1), Biome.Mountain);
        var path = Pathfinding.FindPath(grid, new TileCoord(0, 1), new TileCoord(4, 1));
        Assert.NotNull(path);
        Assert.DoesNotContain(new TileCoord(2, 1), path!);
    }

    [Fact]
    public void Rng_IsDeterministicForSameSeed()
    {
        var a = new Rng(42);
        var b = new Rng(42);
        for (var i = 0; i < 1000; i++) Assert.Equal(a.NextUInt64(), b.NextUInt64());
    }
}
