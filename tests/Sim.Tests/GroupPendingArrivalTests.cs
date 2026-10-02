using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.World;

namespace Sim.Tests;

// M46 Phase A — a Forming or Moving group counts the members still walking for its
// order (PendingArrivals) and goes Idle when the count reaches zero. Every way a
// member's walk can end must take it off the count: arriving, dying on the way, and
// a walk that can't go on. Before M46 a death never did, so the group stayed
// Forming / Moving for ever (docs/m46-groups-spec.md, "The gap").
public class GroupPendingArrivalTests
{
    private static (Simulation sim, GameWorld world) MakeWorld(int w = 20, int h = 12)
    {
        var world = new GameWorld(new TileGrid(w, h, Biome.Grassland));
        world.Players[0] = new Player(0);
        return (new Simulation(world, seed: 1), world);
    }

    [Fact]
    public void MovingGroup_MemberDiesOnTheWay_GroupStillArrives()
    {
        var (sim, world) = MakeWorld();
        var start = new TileCoord(2, 2);
        world.AddUnit(new Unit(1, start) { Role = UnitRole.Builder });
        world.AddUnit(new Unit(2, start) { Role = UnitRole.Builder });
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2 }, start));
        sim.Run(until: 0);
        var gid = world.Groups.Keys.Last();

        var dest = new TileCoord(15, 2);
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(gid, dest));
        sim.Run(until: sim.Now + 30);
        Assert.Equal(GroupState.Moving, world.Groups[gid].State);
        Assert.True(world.Units[2].IsWalking);

        CombatRules.OnUnitDeath(sim, world.Units[2]);
        sim.Run();

        var group = world.Groups[gid];
        Assert.Equal(GroupState.Idle, group.State);
        Assert.Equal(dest, group.Position);
        Assert.Equal(0, group.PendingArrivals);
    }

    [Fact]
    public void FormingGroup_WalkingMemberDies_GroupFinishesForming()
    {
        var (sim, world) = MakeWorld();
        var rendezvous = new TileCoord(2, 2);
        world.AddUnit(new Unit(1, rendezvous) { Role = UnitRole.Builder });
        world.AddUnit(new Unit(2, new TileCoord(14, 8)) { Role = UnitRole.Builder });
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2 }, rendezvous));
        sim.Run(until: 30);
        var gid = world.Groups.Keys.Last();
        Assert.Equal(GroupState.Forming, world.Groups[gid].State);
        Assert.True(world.Units[2].IsWalking);

        CombatRules.OnUnitDeath(sim, world.Units[2]);
        sim.Run();

        var group = world.Groups[gid];
        Assert.Equal(GroupState.Idle, group.State);
        Assert.Null(group.RendezvousTile);
        Assert.Equal(0, group.PendingArrivals);
    }

    [Fact]
    public void FormingGroup_MemberWalkHalted_GroupFinishesForming()
    {
        var (sim, world) = MakeWorld();
        var rendezvous = new TileCoord(2, 2);
        world.AddUnit(new Unit(1, rendezvous) { Role = UnitRole.Builder });
        world.AddUnit(new Unit(2, new TileCoord(14, 8)) { Role = UnitRole.Builder });
        sim.SubmitIntent(0, new FormGroupIntent(new[] { 1, 2 }, rendezvous));
        sim.Run(until: 30);
        var gid = world.Groups.Keys.Last();
        Assert.True(world.Units[2].IsWalking);

        // A walk that can't go on (a wall the planner couldn't see) ends here.
        Sim.Core.Movement.Walk.Halt(sim, world.Units[2]);
        sim.Run();

        var group = world.Groups[gid];
        Assert.Equal(GroupState.Idle, group.State);
        Assert.Equal(0, group.PendingArrivals);
    }
}
