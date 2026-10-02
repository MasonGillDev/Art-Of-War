using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M46 Phase D — muster and dismiss (docs/m46-groups-spec.md, "Muster", "Dismiss"):
// everyone called, a block that spills, nested groups called as one, a worker's slot
// held and taken back, a laden hauler delivering first, the player's choice winning
// over a held slot, and the whole round trip deterministic across a restore.
public class GroupMusterTests
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

    // Soldiers scattered over the map's west side, ids from..to.
    private static void Soldiers(GameWorld world, int from, int to)
    {
        for (var id = from; id <= to; id++)
            world.AddUnit(new Unit(id, new TileCoord(1 + id % 6, 2 + (id * 7) % 15)) { Role = UnitRole.Soldier });
    }

    private static void RunUntil(Simulation sim, Func<bool> done, long budget = 100_000)
    {
        var end = sim.Now + budget;
        for (var t = sim.Now + 1; t <= end && !done(); t += 5) sim.Run(until: t);
        Assert.True(done(), "timed out");
    }

    [Fact]
    public void MusteringAnArmy_CallsEveryCompany_IntoOneBlockThatSpills()
    {
        var (sim, world) = MakeWorld();
        Soldiers(world, 1, 24);
        var army = Create(sim, "Army", Array.Empty<int>(), ofGroups: true);
        var first = Create(sim, "First", Enumerable.Range(1, 12).ToArray(), parent: army);
        var second = Create(sim, "Second", Enumerable.Range(13, 12).ToArray(), parent: army);
        var anchor = new TileCoord(20, 10);

        Assert.False(Do(sim, new MusterGroupIntent(army, anchor)).IsRejected);
        Assert.Equal(GroupState.Forming, world.Groups[first].State);
        Assert.True(GroupRules.UnderCommand(world, world.Units[1]));
        RunUntil(sim, () => world.Groups[first].State == GroupState.Idle && world.Groups[second].State == GroupState.Idle);

        var spots = Enumerable.Range(1, 24).Select(id => (world.Units[id].Position, world.Units[id].Subtile)).ToList();
        Assert.Equal(24, spots.Distinct().Count());
        Assert.Equal(16, spots.Count(s => s.Position == anchor));
        Assert.All(spots, s => Assert.True(Math.Max(Math.Abs(s.Position.X - anchor.X), Math.Abs(s.Position.Y - anchor.Y)) <= 1));
        var progress = GroupMuster.Progress(world, world.Groups[army]);
        Assert.Equal(24, progress.Here);
        Assert.Equal(0, progress.OnTheWay);
    }

    [Fact]
    public void AWorker_LeavesItsPost_KeepsTheSlot_AndGoesBackOnDismiss()
    {
        var (sim, world) = MakeWorld();
        var farmAt = new TileCoord(6, 6);
        var farm = (Extractor)world.AddStructure(new Extractor(StructureKind.Farm, farmAt) { OwnerId = 0 });
        var farmer = world.AddUnit(new Unit(1, farmAt) { Role = UnitRole.Farmer });
        Assert.False(Do(sim, new AssignWorkersIntent(farmAt, new[] { 1 })).IsRejected);
        Assert.Contains(1, farm.Workers);
        var company = Create(sim, "Militia", new[] { 1 });

        Do(sim, new MusterGroupIntent(company, new TileCoord(20, 6)));

        Assert.DoesNotContain(1, farm.Workers);
        Assert.Contains(1, farm.HeldBy);
        Assert.Equal(GoalKind.AssignWorker, farmer.SavedTask!.Kind);
        RunUntil(sim, () => world.Groups[company].State == GroupState.Idle);
        Assert.Equal(new TileCoord(20, 6), farmer.Position);
        Assert.Contains(1, farm.HeldBy);

        Assert.False(Do(sim, new DismissGroupIntent(company)).IsRejected);
        RunUntil(sim, () => farm.Workers.Contains(1));
        Assert.Empty(farm.HeldBy);
        Assert.Null(farmer.SavedTask);
        Assert.Equal(Activity.Working, farmer.Activity);
        Assert.False(GroupRules.UnderCommand(world, farmer));
    }

    [Fact]
    public void ALadenHauler_DeliversFirst_ThenAnswers()
    {
        var (sim, world) = MakeWorld();
        var src = (Stockpile)world.AddStructure(new Stockpile(new TileCoord(3, 3)) { OwnerId = 0 });
        src.Deposit(Resource.Wood, 20);
        var dst = (Stockpile)world.AddStructure(new Stockpile(new TileCoord(14, 3)) { OwnerId = 0 });
        var hauler = world.AddUnit(new Unit(1, new TileCoord(3, 3)) { Role = UnitRole.Hauler });
        var company = Create(sim, "Carters", new[] { 1 });
        Assert.False(Do(sim, new HaulIntent(1, src.At, dst.At, Resource.Wood)).IsRejected);
        RunUntil(sim, () => hauler.HaulPlan is { Phase: HaulPhase.ToDest } && !hauler.Cargo.IsEmpty);
        var carrying = hauler.CargoAmount;

        Do(sim, new MusterGroupIntent(company, new TileCoord(3, 12)));
        Assert.Contains(1, world.Groups[company].Awaiting);
        Assert.Contains(GroupMuster.Progress(world, world.Groups[company]).Finishing, f => f.Why == "delivering");

        RunUntil(sim, () => world.Groups[company].State == GroupState.Idle);
        Assert.Equal(carrying, dst.AmountOf(Resource.Wood));
        Assert.True(hauler.Cargo.IsEmpty);
        Assert.Equal(new TileCoord(3, 12), hauler.Position);
    }

    [Fact]
    public void GivingAHeldSlotAway_CancelsTheAbsentWorkersTask()
    {
        var (sim, world) = MakeWorld();
        var farmAt = new TileCoord(6, 6);
        var farm = (Extractor)world.AddStructure(new Extractor(StructureKind.Farm, farmAt) { OwnerId = 0 });
        for (var id = 1; id <= 3; id++) world.AddUnit(new Unit(id, farmAt) { Role = UnitRole.Farmer });
        world.AddUnit(new Unit(4, farmAt) { Role = UnitRole.Farmer });
        Do(sim, new AssignWorkersIntent(farmAt, new[] { 1, 2, 3 }));
        Assert.Equal(farm.Spec.WorkerCap, farm.Workers.Count);
        var company = Create(sim, "Militia", new[] { 3 });
        Do(sim, new MusterGroupIntent(company, new TileCoord(20, 6)));
        Assert.Contains(3, farm.HeldBy);

        // The player puts someone else in the empty place.
        Assert.False(Do(sim, new AssignWorkersIntent(farmAt, new[] { 4 })).IsRejected);

        Assert.Contains(4, farm.Workers);
        Assert.Empty(farm.HeldBy);
        Assert.Null(world.Units[3].SavedTask);
        RunUntil(sim, () => world.Groups[company].State == GroupState.Idle);
        Do(sim, new DismissGroupIntent(company));
        sim.Run(until: sim.Now + 2000);
        Assert.DoesNotContain(3, farm.Workers);
        Assert.Equal(Activity.Idle, world.Units[3].Activity);
    }

    [Fact]
    public void Delete_DismissesFirst_SoTheWorkerGoesBack()
    {
        var (sim, world) = MakeWorld();
        var farmAt = new TileCoord(6, 6);
        var farm = (Extractor)world.AddStructure(new Extractor(StructureKind.Farm, farmAt) { OwnerId = 0 });
        world.AddUnit(new Unit(1, farmAt) { Role = UnitRole.Farmer });
        Do(sim, new AssignWorkersIntent(farmAt, new[] { 1 }));
        var company = Create(sim, "Militia", new[] { 1 });
        Do(sim, new MusterGroupIntent(company, new TileCoord(20, 6)));
        RunUntil(sim, () => world.Groups[company].State == GroupState.Idle);

        Do(sim, new DeleteGroupIntent(company));

        Assert.False(world.Groups.ContainsKey(company));
        RunUntil(sim, () => farm.Workers.Contains(1));
        Assert.Null(world.Units[1].GroupId);
    }

    [Fact]
    public void ANewcomer_IsCalledToAGroupUnderCommand()
    {
        var (sim, world) = MakeWorld();
        Soldiers(world, 1, 5);
        var company = Create(sim, "Guard", new[] { 1, 2, 3, 4 });
        Do(sim, new MusterGroupIntent(company, new TileCoord(20, 10)));
        RunUntil(sim, () => world.Groups[company].State == GroupState.Idle);

        Assert.False(Do(sim, new AddToGroupIntent(company, new[] { 5 })).IsRejected);
        Assert.Equal(GroupState.Forming, world.Groups[company].State);
        RunUntil(sim, () => world.Groups[company].State == GroupState.Idle);
        Assert.Equal(new TileCoord(20, 10), world.Units[5].Position);
    }

    [Fact]
    public void AMemberDying_OnTheWay_DoesNotHoldTheMusterOpen()
    {
        var (sim, world) = MakeWorld();
        Soldiers(world, 1, 4);
        var company = Create(sim, "Guard", new[] { 1, 2, 3, 4 });
        Do(sim, new MusterGroupIntent(company, new TileCoord(25, 10)));
        sim.Run(until: sim.Now + 30);
        var walker = world.Groups[company].Awaiting.First();

        Sim.Core.Combat.CombatRules.OnUnitDeath(sim, world.Units[walker]);

        RunUntil(sim, () => world.Groups[company].State == GroupState.Idle);
        Assert.DoesNotContain(walker, world.Groups[company].Members);
    }

    [Fact]
    public void TheLatestOrderWins_ACompanyCalledElsewhere()
    {
        var (sim, world) = MakeWorld();
        Soldiers(world, 1, 8);
        var army = Create(sim, "Army", Array.Empty<int>(), ofGroups: true);
        var first = Create(sim, "First", new[] { 1, 2, 3, 4 }, parent: army);
        var second = Create(sim, "Second", new[] { 5, 6, 7, 8 }, parent: army);
        Do(sim, new MusterGroupIntent(army, new TileCoord(20, 10)));
        Do(sim, new MusterGroupIntent(second, new TileCoord(10, 16)));

        RunUntil(sim, () => world.Groups[first].State == GroupState.Idle && world.Groups[second].State == GroupState.Idle);
        Assert.All(new[] { 1, 2, 3, 4 }, id => Assert.Equal(new TileCoord(20, 10), world.Units[id].Position));
        Assert.All(new[] { 5, 6, 7, 8 }, id => Assert.Equal(new TileCoord(10, 16), world.Units[id].Position));
    }

    [Fact]
    public void MusterRefusesGroundNobodyCanStandOn()
    {
        var (sim, world) = MakeWorld();
        world.Grid.SetBiome(new TileCoord(20, 10), Biome.Water);
        Soldiers(world, 1, 2);
        var company = Create(sim, "Guard", new[] { 1, 2 });
        Assert.True(Do(sim, new MusterGroupIntent(company, new TileCoord(20, 10))).IsRejected);
        Assert.Equal(GroupState.Dismissed, world.Groups[company].State);
    }

    private static Simulation Scenario()
    {
        var (sim, world) = MakeWorld();
        var farmAt = new TileCoord(6, 6);
        world.AddStructure(new Extractor(StructureKind.Farm, farmAt) { OwnerId = 0 });
        world.AddUnit(new Unit(1, farmAt) { Role = UnitRole.Farmer });
        Soldiers(world, 2, 20);
        sim.SubmitIntent(0, new AssignWorkersIntent(farmAt, new[] { 1 }));
        sim.SubmitIntent(1, new CreateGroupIntent("Army", Array.Empty<int>(), holdsGroups: true));
        sim.SubmitIntent(1, new CreateGroupIntent("First", Enumerable.Range(1, 10).ToArray(), parentId: 1));
        sim.SubmitIntent(1, new CreateGroupIntent("Second", Enumerable.Range(11, 10).ToArray(), parentId: 1));
        sim.SubmitIntent(5, new MusterGroupIntent(1, new TileCoord(22, 12)));
        return sim;
    }

    // The rest of the round trip from wherever `sim` is: mustered until 3000, then sent
    // home. (A snapshot holds the world, not intents still waiting in the queue, so the
    // dismiss is submitted on each side of a restore alike.)
    private static void Finish(Simulation sim)
    {
        sim.Run(until: 3000);
        sim.SubmitIntent(sim.Now, new DismissGroupIntent(1));
        sim.Run(until: 8000);
    }

    [Fact]
    public void TheRoundTrip_IsDeterministic_AcrossAMidMusterRestore()
    {
        var a = Scenario(); Finish(a);
        var b = Scenario(); Finish(b);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
        Assert.Contains(1, ((Extractor)a.World.Structures[new TileCoord(6, 6)]).Workers);
        Assert.All(a.World.Groups.Values, g => Assert.Equal(GroupState.Dismissed, g.State));

        var mid = Scenario();
        mid.Run(until: 60);
        Assert.Contains(mid.World.Groups.Values, g => g.State == GroupState.Forming);
        var restored = Snapshot.Restore(Snapshot.Serialize(mid), seed: 1);
        Assert.Equal(Snapshot.Hash(mid), Snapshot.Hash(restored));
        Finish(mid);
        Finish(restored);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(mid));
        Assert.Equal(Snapshot.Hash(mid), Snapshot.Hash(restored));
    }
}
