using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M5 Phase B — FormGroupIntent + Forming → Idle transition.
public class GroupFormationTests
{
    private static (Simulation sim, GameWorld world) MakeWorld(int w = 12, int h = 12)
    {
        var grid = new TileGrid(w, h, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        var sim = new Simulation(world, seed: 1);
        return (sim, world);
    }

    [Fact]
    public void FormWith_AllMembersAtRendezvous_GoesStraightToIdle()
    {
        var (sim, world) = MakeWorld();
        var tile = new TileCoord(5, 5);
        world.AddUnit(new Unit(1, tile) { Role = UnitRole.Builder });
        world.AddUnit(new Unit(2, tile) { Role = UnitRole.Builder });

        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2 }, tile));
        sim.Run();

        Assert.True(world.Groups.TryGetValue(1, out var group));
        Assert.Equal(GroupState.Idle, group!.State);
        Assert.Equal(0, group.PendingArrivals);
        Assert.Null(group.RendezvousTile);
        Assert.Equal(1, world.Units[1].GroupId);
        Assert.Equal(1, world.Units[2].GroupId);
    }

    [Fact]
    public void FormWith_ScatteredMembers_TransitionsToIdle_WhenAllArrive()
    {
        var (sim, world) = MakeWorld();
        var rendezvous = new TileCoord(5, 5);
        world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder });
        world.AddUnit(new Unit(2, new TileCoord(9, 9)) { Role = UnitRole.Builder });
        world.AddUnit(new Unit(3, rendezvous)         { Role = UnitRole.Builder });

        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2, 3 }, rendezvous));
        // After resolution: group exists at Forming; pending = 2 (units 1+2);
        // unit 3 is already at rendezvous and contributes nothing.
        sim.Run(until: 0);
        Assert.Equal(GroupState.Forming, world.Groups[1].State);
        Assert.Equal(2, world.Groups[1].PendingArrivals);

        sim.Run();
        Assert.Equal(GroupState.Idle, world.Groups[1].State);
        Assert.Equal(0, world.Groups[1].PendingArrivals);
        Assert.Null(world.Groups[1].RendezvousTile);
        Assert.Equal(rendezvous, world.Units[1].Position);
        Assert.Equal(rendezvous, world.Units[2].Position);
    }

    [Fact]
    public void FormWith_EmptyMemberList_Rejected()
    {
        var (sim, _) = MakeWorld();
        sim.SubmitIntent(0, new FormGroupIntent(Array.Empty<int>(), new TileCoord(5, 5)));
        sim.Run();
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void FormWith_OutOfBoundsRendezvous_Rejected()
    {
        var (sim, world) = MakeWorld();
        world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder });
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1 }, new TileCoord(100, 100)));
        sim.Run();
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Empty(world.Groups);
    }

    [Fact]
    public void FormWith_NonexistentMember_Rejected_NoMembershipChanges()
    {
        var (sim, world) = MakeWorld();
        world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder });
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 999 }, new TileCoord(5, 5)));
        sim.Run();
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Empty(world.Groups);
        // The real unit's GroupId must remain null — no partial formation.
        Assert.Null(world.Units[1].GroupId);
    }

    [Fact]
    public void FormWith_NonIdleMember_Rejected()
    {
        var (sim, world) = MakeWorld();
        var u1 = world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Hauler, CargoCapacity = 5 });
        u1.TrySetActivity(Activity.Hauling);
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1 }, new TileCoord(5, 5)));
        sim.Run();
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Empty(world.Groups);
    }

    [Fact]
    public void FormWith_AlreadyGroupedMember_Rejected()
    {
        var (sim, world) = MakeWorld();
        world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder });
        world.AddUnit(new Unit(2, new TileCoord(5, 5)) { Role = UnitRole.Builder });

        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1 }, new TileCoord(5, 5)));
        sim.Run();
        Assert.True(sim.ResolvedLog[^1].Outcome.IsApplied);
        // Now unit 1 is grouped. A second Form attempt that includes it must reject.
        sim.SubmitIntent(sim.Now, new FormGroupIntent(new[] { 1, 2 }, new TileCoord(5, 5)));
        sim.Run();
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        // Group 2 wasn't created; unit 2 stays solo.
        Assert.False(world.Groups.ContainsKey(2));
        Assert.Null(world.Units[2].GroupId);
    }

    [Fact]
    public void FormWith_WrongOwner_Rejected()
    {
        var (sim, world) = MakeWorld();
        world.Players[1] = new Player(1);
        world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder, OwnerId = 1 });
        // Issued as player 0 but unit owned by player 1.
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1 }, new TileCoord(5, 5)) { PlayerId = 0 });
        sim.Run();
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Empty(world.Groups);
    }

    [Fact]
    public void Form_ThenSnapshotRoundTrips_DuringForming()
    {
        var (sim, world) = MakeWorld();
        var rendezvous = new TileCoord(5, 5);
        world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder });
        world.AddUnit(new Unit(2, new TileCoord(9, 9)) { Role = UnitRole.Builder });
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2 }, rendezvous));
        sim.Run(until: 0);

        Assert.Equal(GroupState.Forming, world.Groups[1].State);

        // Snapshot at Forming state; restore; complete the run; reach Idle.
        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);
        restored.Run();

        // Continue the original to compare.
        sim.Run();

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(GroupState.Idle, restored.World.Groups[1].State);
    }

    [Fact]
    public void TwinRun_Form_HashesMatch()
    {
        Simulation Build()
        {
            var (sim, world) = MakeWorld();
            world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder });
            world.AddUnit(new Unit(2, new TileCoord(9, 9)) { Role = UnitRole.Builder });
            sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2 }, new TileCoord(5, 5)));
            sim.Run();
            return sim;
        }
        var a = Build();
        var b = Build();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }
}
