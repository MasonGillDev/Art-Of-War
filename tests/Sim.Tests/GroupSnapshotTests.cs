using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M5 Phase A — snapshot covers Group state and Unit.GroupId. Round-trip
// must be byte-for-byte equivalent (proven by Snapshot.Hash equality).
public class GroupSnapshotTests
{
    [Fact]
    public void RichWorld_WithGroups_RoundTrips()
    {
        var grid = new TileGrid(8, 8, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        // Unit 1 is solo (GroupId null).
        world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder });

        // Units 2 and 3 are members of an Idle group at (3, 3).
        var u2 = world.AddUnit(new Unit(2, new TileCoord(3, 3)) { Role = UnitRole.Hauler });
        var u3 = world.AddUnit(new Unit(3, new TileCoord(3, 3)) { Role = UnitRole.Scout });
        var idleGroup = new Group(id: 1) { OwnerId = 0 };
        idleGroup.Members.Add(2);
        idleGroup.Members.Add(3);
        idleGroup.Position = new TileCoord(3, 3);
        idleGroup.State = GroupState.Idle;
        world.Groups[1] = idleGroup;
        u2.GroupId = 1;
        u3.GroupId = 1;

        // Units 4 and 5 belong to a Forming group with rendezvous at (5, 5);
        // unit 4 is already there, unit 5 is walking. Pending count = 1.
        var u4 = world.AddUnit(new Unit(4, new TileCoord(5, 5)) { Role = UnitRole.Builder });
        var u5 = world.AddUnit(new Unit(5, new TileCoord(2, 5)) { Role = UnitRole.Builder });
        var formingGroup = new Group(id: 2) { OwnerId = 0 };
        formingGroup.Members.Add(4);
        formingGroup.Members.Add(5);
        formingGroup.Position = new TileCoord(5, 5);
        formingGroup.State = GroupState.Forming;
        formingGroup.RendezvousTile = new TileCoord(5, 5);
        formingGroup.PendingArrivals = 1;
        world.Groups[2] = formingGroup;
        u4.GroupId = 2;
        u5.GroupId = 2;
        // Movement anchors so the snapshot round-trip exercises that path
        // on the Unit side too.
        u5.PathRemaining = new List<TileCoord> { new(3, 5), new(4, 5), new(5, 5) };
        u5.PathFinalDest = new TileCoord(5, 5);
        u5.NextArrivalTick = 30;
        u5.NextArrivalSeq  = 11;

        var sim = new Simulation(world, seed: 0xCAFE);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xCAFE);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        // Spot-check restored content.
        Assert.Equal(2, restored.World.Groups.Count);
        Assert.Equal(GroupState.Idle,    restored.World.Groups[1].State);
        Assert.Equal(GroupState.Forming, restored.World.Groups[2].State);
        Assert.Equal(1, restored.World.Groups[2].PendingArrivals);
        Assert.Equal(new TileCoord(5, 5), restored.World.Groups[2].RendezvousTile);

        Assert.Null(restored.World.Units[1].GroupId);
        Assert.Equal(1, restored.World.Units[2].GroupId);
        Assert.Equal(1, restored.World.Units[3].GroupId);
        Assert.Equal(2, restored.World.Units[4].GroupId);
        Assert.Equal(2, restored.World.Units[5].GroupId);

        Assert.NotNull(restored.World.Units[5].PathRemaining);
        Assert.Equal(3, restored.World.Units[5].PathRemaining!.Count);
        Assert.Equal(30, restored.World.Units[5].NextArrivalTick);
    }

    [Fact]
    public void EmptyGroupRegistry_RoundTrips()
    {
        var grid = new TileGrid(4, 4, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Builder });
        var sim = new Simulation(world, seed: 1);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Empty(restored.World.Groups);
        Assert.Null(restored.World.Units[1].GroupId);
    }
}
