using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// These pin down A7's contract: a world built from the Phase-A state types
// (Castle, Stockpile, Extractor with workers + buffer, ConstructionSite that
// is paused mid-build, Units with role/activity/assignment/cargo) round-trips
// through Serialize -> Restore byte-for-byte equivalent.
//
// "Byte-for-byte equivalent" is operationalized as: same Snapshot.Hash.
//
// Scope note: these all assert on FROZEN states — the ConstructionSite is
// "paused mid-build" specifically because Pause clears the queued
// BuildCompleteEvent (the fencing-token pattern from Phase C). That makes
// it round-trippable. An ACTIVE mid-build / mid-haul / mid-walk state would
// NOT round-trip today — the queued events are dropped on restore. See
// docs/persistence-model.md, section "The in-flight correctness gap." This
// test suite covers what is fixable now; intent-tail replay (persistence
// milestone) closes the rest.
public class SnapshotRoundTripTests
{
    private static Simulation BuildRichWorld()
    {
        var grid = new TileGrid(8, 6, Biome.Grassland);
        grid.SetBiome(new TileCoord(2, 2), Biome.Forest);
        grid.SetBiome(new TileCoord(3, 2), Biome.Forest);
        grid.SetBiome(new TileCoord(5, 4), Biome.Mountain);
        grid.SetBiome(new TileCoord(6, 1), Biome.Hills);
        grid.SetBiome(new TileCoord(0, 5), Biome.Water);

        var world = new GameWorld(grid);

        // Castle with a non-empty mixed-resource holding.
        var castle = world.AddStructure(new Castle(new TileCoord(0, 0)));
        castle.Deposit(Resource.Wood, 50);
        castle.Deposit(Resource.Stone, 20);
        castle.Deposit(Resource.Food, 10);

        // Stockpile elsewhere.
        var stockpile = world.AddStructure(new Stockpile(new TileCoord(7, 0)));
        stockpile.Deposit(Resource.Ore, 5);

        // Extractor with workers assigned and partial buffer.
        var camp = world.AddStructure(new Extractor(StructureKind.LumberCamp, new TileCoord(2, 2)));
        camp.Workers.Add(11);
        camp.Workers.Add(7);
        camp.Workers.Add(20);
        camp.Buffer = 12;
        camp.LastProductionTick = 84;
        camp.TickArmed = true;

        // ConstructionSite, paused mid-build, partially delivered. ScheduledCompletion
        // and LastActiveAtTick are null because it's PAUSED — exactly the state
        // pause/resume produces.
        var site = world.AddStructure(new ConstructionSite(new TileCoord(4, 3), StructureKind.Farm));
        site.Deposit(Resource.Wood, 4);
        site.ProgressTicks = 17;
        site.BuildPaused = true;
        site.LastActiveAtTick = null;
        site.ScheduledCompletion = null;

        // Units in a mix of states. Build them in non-sorted order to make
        // sure canonical ordering is doing the work.
        var u3 = world.AddUnit(new Unit(3, new TileCoord(2, 2)) { Role = UnitRole.Lumberjack });
        u3.TrySetActivity(Activity.Working, new TileCoord(2, 2));

        var u1 = world.AddUnit(new Unit(1, new TileCoord(4, 3)) { Role = UnitRole.Builder });
        u1.TrySetActivity(Activity.Building, new TileCoord(4, 3));

        var u2 = world.AddUnit(new Unit(2, new TileCoord(5, 5)) { Role = UnitRole.Hauler, CargoCapacity = 8 });
        u2.TrySetActivity(Activity.Hauling);
        u2.CargoResource = Resource.Wood;
        u2.CargoAmount = 6;

        world.AddUnit(new Unit(4, new TileCoord(0, 0)) { Role = UnitRole.None });

        var sim = new Simulation(world, seed: 0xBADF00D);
        // Tick the rng + advance Now a little so non-zero clock fields participate.
        sim.Rng.NextUInt64();
        sim.Rng.NextUInt64();
        return sim;
    }

    [Fact]
    public void RichWorld_RoundTrips()
    {
        var original = BuildRichWorld();
        var bytes = Snapshot.Serialize(original);
        var restored = Snapshot.Restore(bytes, seed: 0xBADF00D);

        Assert.Equal(Snapshot.Hash(original), Snapshot.Hash(restored));
    }

    [Fact]
    public void SerializeIsStable_AcrossIdenticalRuns()
    {
        var a = BuildRichWorld();
        var b = BuildRichWorld();
        var ba = Snapshot.Serialize(a);
        var bb = Snapshot.Serialize(b);
        Assert.Equal(ba, bb);
    }

    [Fact]
    public void RestoredSim_PreservesClocks()
    {
        var original = BuildRichWorld();
        var bytes = Snapshot.Serialize(original);
        var restored = Snapshot.Restore(bytes, seed: 0xBADF00D);

        Assert.Equal(original.Now, restored.Now);
        Assert.Equal(original.Rng.State, restored.Rng.State);
    }

    [Fact]
    public void RestoredSim_CanScheduleAndRunForward()
    {
        // After restore, the sim must accept new events and not collide with
        // pre-snapshot Seq numbers. We schedule two events at the current tick
        // and assert they resolve in submission order.
        var original = BuildRichWorld();
        var bytes = Snapshot.Serialize(original);
        var restored = Snapshot.Restore(bytes, seed: 0xBADF00D);

        var resolved = new List<string>();
        restored.Schedule(restored.Now, new RecordingEvent("first", resolved));
        restored.Schedule(restored.Now, new RecordingEvent("second", resolved));
        restored.Run();

        Assert.Equal(new[] { "first", "second" }, resolved);
    }

    [Fact]
    public void MagicMismatch_Throws()
    {
        var bytes = new byte[64];
        Assert.Throws<InvalidDataException>(() => Snapshot.Restore(bytes, seed: 1));
    }

    private sealed class RecordingEvent : ScheduledEvent
    {
        private readonly string _tag;
        private readonly List<string> _sink;
        public RecordingEvent(string tag, List<string> sink) { _tag = tag; _sink = sink; }
        public override void Apply(Simulation sim) => _sink.Add(_tag);
    }
}
