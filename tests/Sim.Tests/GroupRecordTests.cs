using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Groups;
using Sim.Core.Intents;
using Sim.Core.Movement;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M46 Phase A — the group as a lasting record (docs/m46-groups-spec.md): created
// Dismissed with its members free, named, nested at most three deep, kept when
// empty, ids never reused, and membership alone blocking nothing.
public class GroupRecordTests
{
    private static (Simulation sim, GameWorld world) MakeWorld(int w = 16, int h = 12)
    {
        var world = new GameWorld(new TileGrid(w, h, Biome.Grassland));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        return (new Simulation(world, seed: 1), world);
    }

    private static Unit Add(GameWorld world, int id, int x = 3, int y = 3, int owner = 0,
        UnitRole role = UnitRole.Builder) =>
        world.AddUnit(new Unit(id, new TileCoord(x, y)) { Role = role, OwnerId = owner });

    // Submit at now, run it, return the outcome.
    private static IntentOutcome Do(Simulation sim, Intent intent)
    {
        sim.SubmitIntent(sim.Now, intent);
        sim.Run(until: sim.Now);
        return sim.ResolvedLog[^1].Outcome;
    }

    private static int Create(Simulation sim, string name, int[] units, int? parent = null, bool ofGroups = false)
    {
        var outcome = Do(sim, new CreateGroupIntent(name, units, parent, ofGroups));
        Assert.False(outcome.IsRejected, outcome.Reason);
        return sim.World.NextGroupId - 1;
    }

    // ---- creation --------------------------------------------------------------

    [Fact]
    public void Create_GroupStartsDismissed_MembersStayWhereTheyAre()
    {
        var (sim, world) = MakeWorld();
        Add(world, 1, 2, 2); Add(world, 2, 9, 7);

        var gid = Create(sim, "  Bridge Guard  ", new[] { 1, 2 });

        var group = world.Groups[gid];
        Assert.Equal("Bridge Guard", group.Name);
        Assert.Equal(GroupState.Dismissed, group.State);
        Assert.Equal(GroupKind.Units, group.Kind);
        Assert.Equal(new[] { 1, 2 }, group.Members.ToArray());
        Assert.Equal(gid, world.Units[1].GroupId);
        Assert.False(world.Units[1].IsWalking);
        Assert.Equal(new TileCoord(9, 7), world.Units[2].Position);
        Assert.False(GroupRules.UnderCommand(world, world.Units[1]));
    }

    [Fact]
    public void Create_IsAllOrNothing()
    {
        var (sim, world) = MakeWorld();
        Add(world, 1); Add(world, 2, owner: 1);

        Assert.True(Do(sim, new CreateGroupIntent("x", new[] { 1, 2 })).IsRejected);   // 2 isn't ours
        Assert.True(Do(sim, new CreateGroupIntent("x", new[] { 1, 99 })).IsRejected);  // 99 doesn't exist
        Assert.True(Do(sim, new CreateGroupIntent("x", new[] { 1, 1 })).IsRejected);   // named twice
        Assert.True(Do(sim, new CreateGroupIntent(new string('a', 33), new[] { 1 })).IsRejected);
        Assert.True(Do(sim, new CreateGroupIntent("x", new[] { 1 }, holdsGroups: true)).IsRejected);

        Assert.Empty(world.Groups);
        Assert.Null(world.Units[1].GroupId);
        Assert.Equal(1, world.NextGroupId);
    }

    [Fact]
    public void Create_RefusesAUnitAlreadyInAGroup()
    {
        var (sim, world) = MakeWorld();
        Add(world, 1);
        Create(sim, "first", new[] { 1 });

        Assert.True(Do(sim, new CreateGroupIntent("second", new[] { 1 })).IsRejected);
        Assert.Single(world.Groups);
    }

    [Fact]
    public void Join_RefusesBoats()
    {
        var boat = new Unit(9, new TileCoord(0, 0)) { Role = UnitRole.Boat, Traversal = Traversal.Water };
        Assert.Contains("not a foot unit", GroupRules.JoinRefusal(boat, 9, 0));
    }

    [Fact]
    public void Create_EmptyGroupOfGroups_IsAllowed()
    {
        var (sim, world) = MakeWorld();
        var army = Create(sim, "Army", Array.Empty<int>(), ofGroups: true);
        Assert.Equal(GroupKind.Groups, world.Groups[army].Kind);
        Assert.Empty(world.Groups[army].Members);
    }

    // ---- ids -------------------------------------------------------------------

    [Fact]
    public void Ids_AreNeverReused_EvenAfterTheNewestIsDeleted()
    {
        var (sim, world) = MakeWorld();
        var a = Create(sim, "a", Array.Empty<int>(), ofGroups: true);
        var b = Create(sim, "b", Array.Empty<int>(), ofGroups: true);
        Assert.False(Do(sim, new DeleteGroupIntent(b)).IsRejected);

        var c = Create(sim, "c", Array.Empty<int>(), ofGroups: true);

        Assert.Equal((1, 2, 3), (a, b, c));
        Assert.False(world.Groups.ContainsKey(b));
    }

    [Fact]
    public void FormGroup_UsesTheSameCounter()
    {
        var (sim, world) = MakeWorld();
        Add(world, 1);
        Create(sim, "a", Array.Empty<int>(), ofGroups: true);
        Do(sim, new DeleteGroupIntent(1));

        sim.SubmitIntent(sim.Now, new CreateAndMuster(new[] { 1 }, new TileCoord(3, 3)));
        sim.Run(until: sim.Now);

        Assert.Equal(2, world.Units[1].GroupId);
    }

    // ---- command ---------------------------------------------------------------

    [Fact]
    public void DismissedMember_TakesSoloWork()
    {
        var (sim, world) = MakeWorld();
        var soldier = Add(world, 1, role: UnitRole.Soldier);
        Create(sim, "company", new[] { 1 });

        Assert.Null(Retask.Refusal(sim, soldier));
        Assert.True(ClaimLedger.IsDormant(world, soldier));
        Assert.Null(EquipRules.Blocker(world, soldier, Resource.BronzeSword));

        Assert.False(Do(sim, new MoveIntent(1, new TileCoord(10, 3))).IsRejected);
        Assert.True(soldier.IsWalking);
    }

    [Fact]
    public void CommandedMember_IsRefusedSoloWork()
    {
        var (sim, world) = MakeWorld();
        var soldier = Add(world, 1, role: UnitRole.Soldier);
        sim.SubmitIntent(0, new CreateAndMuster(new[] { 1 }, soldier.Position));
        sim.Run(until: 0);

        Assert.True(GroupRules.UnderCommand(world, soldier));
        Assert.NotNull(Retask.Refusal(sim, soldier));
        Assert.False(ClaimLedger.IsDormant(world, soldier));
        Assert.NotNull(EquipRules.Blocker(world, soldier, Resource.BronzeSword));
        Assert.True(Do(sim, new MoveIntent(1, new TileCoord(10, 3))).IsRejected);
    }

    [Fact]
    public void MoveGroup_RefusesADismissedGroup_AndAGroupOfGroups()
    {
        var (sim, world) = MakeWorld();
        Add(world, 1);
        var company = Create(sim, "company", new[] { 1 });
        var army = Create(sim, "army", Array.Empty<int>(), ofGroups: true);

        Assert.True(Do(sim, new MoveGroupIntent(company, new TileCoord(10, 3))).IsRejected);
        Assert.True(Do(sim, new MoveGroupIntent(army, new TileCoord(10, 3))).IsRejected);
        Assert.False(world.Units[1].IsWalking);
    }

    // ---- the tree --------------------------------------------------------------

    [Fact]
    public void Nest_KeepsBothSidesOfTheLink()
    {
        var (sim, world) = MakeWorld();
        Add(world, 1);
        var army = Create(sim, "army", Array.Empty<int>(), ofGroups: true);
        var company = Create(sim, "company", new[] { 1 }, parent: army);

        Assert.Equal(army, world.Groups[company].ParentId);
        Assert.Equal(new[] { company }, world.Groups[army].Children.ToArray());

        Assert.False(Do(sim, new SetGroupParentIntent(company, null)).IsRejected);
        Assert.Null(world.Groups[company].ParentId);
        Assert.Empty(world.Groups[army].Children);
    }

    [Fact]
    public void Nest_RefusesAParentThatHoldsUnits()
    {
        var (sim, world) = MakeWorld();
        Add(world, 1); Add(world, 2);
        var a = Create(sim, "a", new[] { 1 });
        var b = Create(sim, "b", new[] { 2 });

        Assert.True(Do(sim, new SetGroupParentIntent(b, a)).IsRejected);
        Assert.True(Do(sim, new CreateGroupIntent("c", Array.Empty<int>(), parentId: a, holdsGroups: true)).IsRejected);
        Assert.Null(world.Groups[b].ParentId);
    }

    [Fact]
    public void Nest_RefusesACycle()
    {
        var (sim, world) = MakeWorld();
        var army = Create(sim, "army", Array.Empty<int>(), ofGroups: true);
        var regiment = Create(sim, "regiment", Array.Empty<int>(), parent: army, ofGroups: true);

        Assert.True(Do(sim, new SetGroupParentIntent(army, regiment)).IsRejected);
        Assert.True(Do(sim, new SetGroupParentIntent(army, army)).IsRejected);
        Assert.Null(world.Groups[army].ParentId);
    }

    [Fact]
    public void Nest_AtMostThreeDeep()
    {
        var (sim, world) = MakeWorld();
        var army = Create(sim, "army", Array.Empty<int>(), ofGroups: true);
        var regiment = Create(sim, "regiment", Array.Empty<int>(), parent: army, ofGroups: true);
        Create(sim, "company", Array.Empty<int>(), parent: regiment, ofGroups: true);   // level 3: fine

        var tooDeep = new CreateGroupIntent("squad", Array.Empty<int>(), parentId: world.NextGroupId - 1, holdsGroups: true);
        Assert.True(Do(sim, tooDeep).IsRejected);

        // A two-level subtree can't go under a level-2 group either.
        var other = Create(sim, "other", Array.Empty<int>(), ofGroups: true);
        Create(sim, "other-child", Array.Empty<int>(), parent: other, ofGroups: true);
        Assert.True(Do(sim, new SetGroupParentIntent(other, regiment)).IsRejected);
        Assert.False(Do(sim, new SetGroupParentIntent(other, army)).IsRejected);   // 1 + 2 = 3
    }

    [Fact]
    public void Delete_FreesMembers_LiftsChildren_LeavesParent()
    {
        var (sim, world) = MakeWorld();
        Add(world, 1);
        var army = Create(sim, "army", Array.Empty<int>(), ofGroups: true);
        var regiment = Create(sim, "regiment", Array.Empty<int>(), parent: army, ofGroups: true);
        var company = Create(sim, "company", new[] { 1 }, parent: regiment);

        Assert.False(Do(sim, new DeleteGroupIntent(regiment)).IsRejected);

        Assert.False(world.Groups.ContainsKey(regiment));
        Assert.Empty(world.Groups[army].Children);
        Assert.Null(world.Groups[company].ParentId);
        Assert.Equal(company, world.Units[1].GroupId);

        Assert.False(Do(sim, new DeleteGroupIntent(company)).IsRejected);
        Assert.Null(world.Units[1].GroupId);
    }

    // ---- membership and names --------------------------------------------------

    [Fact]
    public void AddTo_JoinsADismissedLeaf_Only()
    {
        var (sim, world) = MakeWorld();
        Add(world, 1); Add(world, 2); Add(world, 3);
        var company = Create(sim, "company", new[] { 1 });
        var army = Create(sim, "army", Array.Empty<int>(), ofGroups: true);

        Assert.False(Do(sim, new AddToGroupIntent(company, new[] { 2 })).IsRejected);
        Assert.Equal(new[] { 1, 2 }, world.Groups[company].Members.ToArray());
        Assert.Equal(company, world.Units[2].GroupId);

        Assert.True(Do(sim, new AddToGroupIntent(army, new[] { 3 })).IsRejected);
        Assert.True(Do(sim, new AddToGroupIntent(company, new[] { 2 })).IsRejected);   // already in
        Assert.Null(world.Units[3].GroupId);
    }

    [Fact]
    public void Rename_CleansTheName_AndOnlyTheOwnerMay()
    {
        var (sim, world) = MakeWorld();
        var gid = Create(sim, "old", Array.Empty<int>(), ofGroups: true);

        Assert.False(Do(sim, new RenameGroupIntent(gid, "  New Name ")).IsRejected);
        Assert.Equal("New Name", world.Groups[gid].Name);

        Assert.True(Do(sim, new RenameGroupIntent(gid, "Stolen") { PlayerId = 1 }).IsRejected);
        Assert.True(Do(sim, new RenameGroupIntent(gid, "bad\nname")).IsRejected);
        Assert.Equal("New Name", world.Groups[gid].Name);
    }

    // ---- determinism -----------------------------------------------------------

    private static Simulation Scenario()
    {
        var (sim, world) = MakeWorld();
        for (var id = 1; id <= 4; id++) Add(world, id, 2 + id, 3);
        sim.SubmitIntent(0, new CreateGroupIntent("Army", Array.Empty<int>(), holdsGroups: true));
        sim.SubmitIntent(0, new CreateGroupIntent("Bridge Guard", new[] { 1, 2 }, parentId: 1));
        sim.SubmitIntent(0, new CreateGroupIntent("Gone", Array.Empty<int>(), holdsGroups: true));
        sim.SubmitIntent(1, new DeleteGroupIntent(3));
        sim.SubmitIntent(2, new CreateAndMuster(new[] { 3, 4 }, new TileCoord(10, 8)));
        sim.SubmitIntent(3, new RenameGroupIntent(2, "Night Watch"));
        sim.Run(until: 40);
        return sim;
    }

    [Fact]
    public void TwinRun_SameHash()
    {
        Assert.Equal(Snapshot.Hash(Scenario()), Snapshot.Hash(Scenario()));
    }

    [Fact]
    public void Snapshot_RoundTripsTheRecord()
    {
        var sim = Scenario();
        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        var world = restored.World;
        Assert.Equal(5, world.NextGroupId);
        var guard = world.Groups[2];
        Assert.Equal("Night Watch", guard.Name);
        Assert.Equal(GroupKind.Units, guard.Kind);
        Assert.Equal(1, guard.ParentId);
        Assert.Equal(GroupState.Dismissed, guard.State);
        Assert.Equal(new[] { 2 }, world.Groups[1].Children.ToArray());
        Assert.Equal(GroupKind.Groups, world.Groups[1].Kind);

        // The restored world keeps counting where the original left off.
        restored.SubmitIntent(restored.Now, new CreateGroupIntent("next", Array.Empty<int>(), holdsGroups: true));
        restored.Run(until: restored.Now);
        Assert.True(world.Groups.ContainsKey(5));
    }
}
