using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.World;
using Sim.Server;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M46 Phase F — groups on the wire (docs/m46-groups-spec.md, "What the player sees"):
// the viewer's own groups with their tree, orders and muster progress; each own unit's
// saved task; an own extractor's held slots. Nobody else's order of battle, and the
// projection writes nothing.
public class GroupWireTests
{
    private static (Simulation sim, ViewProjector projector) MakeWorld()
    {
        var opts = new ServerOptions { MapWidth = 128, MapHeight = 128, MapSeed = 7, AiPlayers = 1 };
        var build = WorldFactory.Build(opts);
        return (new Simulation(build.Spec, seed: 0xA117), new ViewProjector(build));
    }

    [Fact]
    public void OwnGroups_AreOnTheWire_WithTheirTreeAndProgress_OthersAreNot()
    {
        var (sim, projector) = MakeWorld();
        var world = sim.World;
        var mine = world.Units.Values.Where(u => u.OwnerId == 0 && u.Role != UnitRole.Boat).Take(3).Select(u => u.Id).ToArray();
        var theirs = world.Units.Values.Where(u => u.OwnerId == 1 && u.Role != UnitRole.Boat).Take(2).Select(u => u.Id).ToArray();
        Assert.NotEmpty(theirs);
        sim.SubmitIntent(sim.Now, new CreateGroupIntent("Army", Array.Empty<int>(), holdsGroups: true) { PlayerId = 0 });
        sim.SubmitIntent(sim.Now, new CreateGroupIntent("Guard", mine, parentId: 1) { PlayerId = 0 });
        sim.SubmitIntent(sim.Now, new CreateGroupIntent("Their lot", theirs) { PlayerId = 1 });
        sim.Run(until: sim.Now);
        var anchor = world.Units[mine[0]].Position;
        sim.SubmitIntent(sim.Now, new MusterGroupIntent(1, anchor) { PlayerId = 0 });
        sim.Run(until: sim.Now);

        var v = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: true);

        Assert.Equal(new[] { 1, 2 }, v.Groups.Select(g => g.Id).OrderBy(i => i).ToArray());
        var army = v.Groups.Single(g => g.Id == 1);
        var guard = v.Groups.Single(g => g.Id == 2);
        Assert.Equal("Army", army.Name);
        Assert.Equal((int)GroupKind.Groups, army.Kind);
        Assert.Equal(new[] { 2 }, army.Children);
        Assert.Equal(1, guard.ParentId);
        Assert.Equal(mine.OrderBy(i => i).ToArray(), guard.Members);
        Assert.Equal((int)world.Groups[2].State, guard.State);
        Assert.Equal(mine.Length, army.Here + army.OnTheWay + army.NoRoom + army.Finishing.Length);
        Assert.Equal(anchor.X, guard.X);
    }

    [Fact]
    public void ASavedTask_AndAHeldSlot_AreOnTheWire()
    {
        var (sim, projector) = MakeWorld();
        var world = sim.World;
        var worker = world.Units.Values.First(u => u.OwnerId == 0 && u.Role != UnitRole.Boat);
        var farmAt = worker.Position;
        world.AddStructure(new Extractor(StructureKind.Farm, farmAt) { OwnerId = 0 });
        // Gather the hand-built structure's state the way a real build would have.
        sim.SubmitIntent(sim.Now, new Sim.Core.Logistics.AssignWorkersIntent(farmAt, new[] { worker.Id }) { PlayerId = 0 });
        sim.SubmitIntent(sim.Now, new CreateGroupIntent("Militia", new[] { worker.Id }) { PlayerId = 0 });
        sim.Run(until: sim.Now);
        Assert.Equal(Activity.Working, worker.Activity);
        var muster = new TileCoord(farmAt.X + 3, farmAt.Y);
        sim.SubmitIntent(sim.Now, new MusterGroupIntent(world.NextGroupId - 1, muster) { PlayerId = 0 });
        sim.Run(until: sim.Now);

        var v = projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false);

        var u = v.Units.Single(x => x.Id == worker.Id);
        Assert.Equal((int)GoalKind.AssignWorker, u.SavedTaskKind);
        Assert.Equal(farmAt.X, u.SavedTaskX);
        Assert.Equal(1, v.Structures.Single(s => s.X == farmAt.X && s.Y == farmAt.Y).HeldSlots);
    }

    [Fact]
    public void TheProjection_IsAPureRead()
    {
        var (sim, projector) = MakeWorld();
        var mine = sim.World.Units.Values.Where(u => u.OwnerId == 0 && u.Role != UnitRole.Boat).Take(3).Select(u => u.Id).ToArray();
        sim.SubmitIntent(sim.Now, new CreateGroupIntent("Guard", mine) { PlayerId = 0 });
        sim.Run(until: sim.Now);
        sim.SubmitIntent(sim.Now, new MusterGroupIntent(1, sim.World.Units[mine[0]].Position) { PlayerId = 0 });
        sim.Run(until: sim.Now + 5);

        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 20; i++) projector.ProjectV2(sim, sim.Now, playerId: 0, reveal: false);
        Assert.Equal(before, Snapshot.Hash(sim));
    }
}
