using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M46 Phase E — merge and split (docs/m46-groups-spec.md, "Intents"): units take the
// receiving group's orders; split-out members of a group under command go back to
// their saved tasks; a group of groups merges its children; nothing marching reshapes.
public class GroupReshapeTests
{
    private static (Simulation sim, GameWorld world) MakeWorld(int w = 32, int h = 20)
    {
        var world = new GameWorld(new TileGrid(w, h, Biome.Grassland));
        world.Players[0] = new Player(0);
        return (new Simulation(world, seed: 1), world);
    }

    private static IntentOutcome Do(Simulation sim, Intent intent)
    {
        sim.SubmitIntent(sim.Now, intent);
        sim.Run(until: sim.Now);
        return sim.ResolvedLog.OfType<IntentEvent>().Last(e => e.Intent == intent).Outcome;
    }

    private static int Create(Simulation sim, string name, int[] units, int? parent = null, bool ofGroups = false)
    {
        var o = Do(sim, new CreateGroupIntent(name, units, parent, ofGroups));
        Assert.False(o.IsRejected, o.Reason);
        return sim.World.NextGroupId - 1;
    }

    private static void Soldiers(GameWorld world, int from, int to)
    {
        for (var id = from; id <= to; id++)
            world.AddUnit(new Unit(id, new TileCoord(2 + id % 5, 3 + id % 9)) { Role = UnitRole.Soldier });
    }

    private static void RunUntil(Simulation sim, Func<bool> done, long budget = 100_000)
    {
        var end = sim.Now + budget;
        for (var t = sim.Now + 1; t <= end && !done(); t += 5) sim.Run(until: t);
        Assert.True(done(), "timed out");
    }

    [Fact]
    public void Merge_DismissedIntoMustered_CallsTheNewcomers()
    {
        var (sim, world) = MakeWorld();
        Soldiers(world, 1, 8);
        var guard = Create(sim, "Guard", new[] { 1, 2, 3, 4 });
        var reserve = Create(sim, "Reserve", new[] { 5, 6, 7, 8 });
        Do(sim, new MusterGroupIntent(guard, new TileCoord(20, 10)));
        RunUntil(sim, () => world.Groups[guard].State == GroupState.Idle);

        Assert.False(Do(sim, new MergeGroupsIntent(reserve, guard)).IsRejected);

        Assert.False(world.Groups.ContainsKey(reserve));
        Assert.Equal(8, world.Groups[guard].Members.Count);
        Assert.Equal(guard, world.Units[5].GroupId);
        RunUntil(sim, () => world.Groups[guard].State == GroupState.Idle);
        Assert.All(Enumerable.Range(1, 8), id => Assert.Equal(new TileCoord(20, 10), world.Units[id].Position));
    }

    [Fact]
    public void Merge_MusteredIntoDismissed_SendsThemBackToWork()
    {
        var (sim, world) = MakeWorld();
        var farmAt = new TileCoord(6, 6);
        var farm = (Extractor)world.AddStructure(new Extractor(StructureKind.Farm, farmAt) { OwnerId = 0 });
        world.AddUnit(new Unit(1, farmAt) { Role = UnitRole.Farmer });
        Soldiers(world, 2, 3);
        Do(sim, new AssignWorkersIntent(farmAt, new[] { 1 }));
        var militia = Create(sim, "Militia", new[] { 1 });
        var home = Create(sim, "Home Guard", new[] { 2, 3 });
        Do(sim, new MusterGroupIntent(militia, new TileCoord(20, 6)));
        RunUntil(sim, () => world.Groups[militia].State == GroupState.Idle);

        Assert.False(Do(sim, new MergeGroupsIntent(militia, home)).IsRejected);

        Assert.Equal(home, world.Units[1].GroupId);
        Assert.False(GroupRules.UnderCommand(world, world.Units[1]));
        RunUntil(sim, () => farm.Workers.Contains(1));
        Assert.Empty(farm.HeldBy);
    }

    [Fact]
    public void Merge_GroupsOfGroups_MovesTheChildren_WithinTheDepthLimit()
    {
        var (sim, world) = MakeWorld();
        var north = Create(sim, "North", Array.Empty<int>(), ofGroups: true);
        var south = Create(sim, "South", Array.Empty<int>(), ofGroups: true);
        var a = Create(sim, "A", Array.Empty<int>(), parent: south, ofGroups: true);
        var b = Create(sim, "B", Array.Empty<int>(), parent: south, ofGroups: true);

        Assert.False(Do(sim, new MergeGroupsIntent(south, north)).IsRejected);

        Assert.False(world.Groups.ContainsKey(south));
        Assert.Equal(new[] { a, b }, world.Groups[north].Children.ToArray());
        Assert.Equal(north, world.Groups[a].ParentId);

        // A child two levels tall can't go under a group at level two.
        var deep = Create(sim, "Deep", Array.Empty<int>(), ofGroups: true);
        Create(sim, "Deep-1", Array.Empty<int>(), parent: deep, ofGroups: true);
        var holder = Create(sim, "Holder", Array.Empty<int>(), ofGroups: true);
        var deepHolder = Create(sim, "DeepHolder", Array.Empty<int>(), ofGroups: true);
        Do(sim, new SetGroupParentIntent(deep, deepHolder));
        Do(sim, new SetGroupParentIntent(holder, north));
        Assert.True(Do(sim, new MergeGroupsIntent(deepHolder, holder)).IsRejected);
    }

    [Fact]
    public void Merge_RefusesMixedKinds_AndAMarchingGroup()
    {
        var (sim, world) = MakeWorld();
        Soldiers(world, 1, 4);
        var units = Create(sim, "Units", new[] { 1, 2 });
        var groups = Create(sim, "Groups", Array.Empty<int>(), ofGroups: true);
        Assert.True(Do(sim, new MergeGroupsIntent(units, groups)).IsRejected);

        var other = Create(sim, "Other", new[] { 3, 4 });
        Do(sim, new MusterGroupIntent(units, new TileCoord(5, 5)));
        RunUntil(sim, () => world.Groups[units].State == GroupState.Idle);
        Do(sim, new MoveGroupIntent(units, new TileCoord(25, 5)));
        Assert.True(Do(sim, new MergeGroupsIntent(other, units)).IsRejected);
        Assert.Equal(2, world.Groups[units].Members.Count);
    }

    [Fact]
    public void Split_IntoANewGroup_SameParent_Dismissed_BackToWork()
    {
        var (sim, world) = MakeWorld();
        var farmAt = new TileCoord(6, 6);
        var farm = (Extractor)world.AddStructure(new Extractor(StructureKind.Farm, farmAt) { OwnerId = 0 });
        world.AddUnit(new Unit(1, farmAt) { Role = UnitRole.Farmer });
        Soldiers(world, 2, 4);
        Do(sim, new AssignWorkersIntent(farmAt, new[] { 1 }));
        var army = Create(sim, "Army", Array.Empty<int>(), ofGroups: true);
        var company = Create(sim, "Company", new[] { 1, 2, 3, 4 }, parent: army);
        Do(sim, new MusterGroupIntent(army, new TileCoord(20, 10)));
        RunUntil(sim, () => world.Groups[company].State == GroupState.Idle);

        Assert.False(Do(sim, new SplitGroupIntent(company, new[] { 1, 2 }, "Farmers")).IsRejected);

        var farmers = world.NextGroupId - 1;
        Assert.Equal("Farmers", world.Groups[farmers].Name);
        Assert.Equal(army, world.Groups[farmers].ParentId);
        Assert.Equal(GroupState.Dismissed, world.Groups[farmers].State);
        Assert.Equal(new[] { 3, 4 }, world.Groups[company].Members.ToArray());
        Assert.Equal(farmers, world.Units[1].GroupId);
        RunUntil(sim, () => farm.Workers.Contains(1));
        Assert.Equal(GroupState.Idle, world.Groups[company].State);
    }

    [Fact]
    public void Split_Solo_LeavesTheGroup()
    {
        var (sim, world) = MakeWorld();
        Soldiers(world, 1, 3);
        var company = Create(sim, "Company", new[] { 1, 2, 3 });
        Assert.False(Do(sim, new SplitGroupIntent(company, new[] { 3 })).IsRejected);
        Assert.Null(world.Units[3].GroupId);
        Assert.Equal(new[] { 1, 2 }, world.Groups[company].Members.ToArray());
        Assert.True(Do(sim, new SplitGroupIntent(company, new[] { 3 })).IsRejected);   // not a member now
    }

    [Fact]
    public void Split_WhileMustering_StopsWaitingForThem()
    {
        var (sim, world) = MakeWorld();
        Soldiers(world, 1, 4);
        var company = Create(sim, "Company", new[] { 1, 2, 3, 4 });
        Do(sim, new MusterGroupIntent(company, new TileCoord(28, 18)));
        Assert.Equal(GroupState.Forming, world.Groups[company].State);
        var walker = world.Groups[company].Awaiting.First();

        Assert.False(Do(sim, new SplitGroupIntent(company, new[] { walker })).IsRejected);

        Assert.DoesNotContain(walker, world.Groups[company].Awaiting);
        Assert.False(world.Units[walker].IsWalking);
        RunUntil(sim, () => world.Groups[company].State == GroupState.Idle);
    }

    [Fact]
    public void Reshaping_IsDeterministic()
    {
        Simulation Build()
        {
            var (sim, world) = MakeWorld();
            Soldiers(world, 1, 12);
            sim.SubmitIntent(0, new CreateGroupIntent("A", new[] { 1, 2, 3, 4, 5, 6 }));
            sim.SubmitIntent(0, new CreateGroupIntent("B", new[] { 7, 8, 9, 10, 11, 12 }));
            sim.SubmitIntent(1, new MusterGroupIntent(1, new TileCoord(20, 10)));
            sim.SubmitIntent(50, new MergeGroupsIntent(2, 1));
            sim.SubmitIntent(80, new SplitGroupIntent(1, new[] { 1, 7 }, "Pair"));
            sim.Run(until: 6000);
            return sim;
        }
        Assert.Equal(Snapshot.Hash(Build()), Snapshot.Hash(Build()));
    }
}
