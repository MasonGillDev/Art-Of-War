using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Tests;

// M7 Phase D: clean death. (M43: the pooled rounds that used to deal the blows are gone: the
// board's resolver kills now, and every death converges on CombatRules.OnUnitDeath, which is
// what these pin, so they call it directly.)
//   * Dying units are removed from world.Units.
//   * Grouped units are removed from their group's Members.
//   * A group hitting zero Members is attrition-disbanded.
//   * A walk or a haul in flight fences cleanly when its unit is gone: the queued step
//     and the pending pickup/deposit find no unit and do nothing (no crash, no silent
//     state corruption).
public class CombatDeathTests
{
    private static Simulation MakeScenario()
    {
        var spec = new GenesisSpec
        {
            Width = 20, Height = 20,
            Combat = new CombatConfig(RoundIntervalTicks: 10),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(0, 0) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(19, 19) },
            },
        };
        var world = Genesis.Build(spec);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        return new Simulation(world, seed: 0xDEAD);
    }

    [Fact]
    public void DyingUnit_RemovedFromWorldUnits()
    {
        var tile = new TileCoord(10, 10);
        var sim = MakeScenario();
        sim.World.AddUnit(new Unit(100, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        var victim = sim.World.AddUnit(new Unit(200, tile) { Role = UnitRole.Builder, OwnerId = 1 });

        CombatRules.OnUnitDeath(sim, victim);

        Assert.False(sim.World.Units.ContainsKey(200));
        Assert.True(sim.World.Units.ContainsKey(100));
    }

    [Fact]
    public void GroupedUnitDying_RemovedFromGroup()
    {
        var tile = new TileCoord(10, 10);
        var sim = MakeScenario();
        var u1 = sim.World.AddUnit(new Unit(100, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        var u2 = sim.World.AddUnit(new Unit(101, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        var group = new Group(1) { OwnerId = 0 };
        group.Members.Add(u1.Id);
        group.Members.Add(u2.Id);
        group.Position = tile;
        group.State = GroupState.Idle;
        sim.World.Groups[group.Id] = group;
        u1.GroupId = group.Id;
        u2.GroupId = group.Id;

        CombatRules.OnUnitDeath(sim, u1);

        Assert.Equal(new[] { 101 }, group.Members.ToArray());
        Assert.True(sim.World.Groups.ContainsKey(1));
    }

    [Fact]
    public void GroupAtZeroMembers_RemovedFromWorldGroups()
    {
        var tile = new TileCoord(10, 10);
        var sim = MakeScenario();
        var u1 = sim.World.AddUnit(new Unit(100, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        var group = new Group(1) { OwnerId = 0 };
        group.Members.Add(u1.Id);
        group.Position = tile;
        group.State = GroupState.Idle;
        sim.World.Groups[group.Id] = group;
        u1.GroupId = group.Id;

        CombatRules.OnUnitDeath(sim, u1);

        Assert.False(sim.World.Groups.ContainsKey(1),
            "group should attrition-disband when its last member dies");
    }

    [Fact]
    public void UnitDyingMidWalk_ItsQueuedStepNoOps_NotCrash()
    {
        var sim = MakeScenario();
        var walker = sim.World.AddUnit(new Unit(100, new TileCoord(2, 10)) { Role = UnitRole.Builder, OwnerId = 0 });
        sim.SubmitIntent(0, new MoveIntent(walker.Id, new TileCoord(15, 10)));
        sim.Run(until: 60);
        Assert.True(walker.IsWalking);

        CombatRules.OnUnitDeath(sim, walker);
        sim.Run(until: 500);   // the step that was queued fires on an absent unit and must do nothing

        Assert.False(sim.World.Units.ContainsKey(100));
    }

    [Fact]
    public void UnitDyingMidHaul_PendingHaulEventsNoOp_NotCrash()
    {
        var sim = MakeScenario();
        var src = new TileCoord(2, 10);
        var dst = new TileCoord(15, 10);
        var stockpile = sim.World.AddStructure(new Stockpile(src) { OwnerId = 0 });
        stockpile.Deposit(Resource.Wood, 50);
        sim.World.AddStructure(new Stockpile(dst) { OwnerId = 0 });
        var hauler = sim.World.AddUnit(new Unit(100, src) { Role = UnitRole.Hauler, OwnerId = 0 });

        sim.SubmitIntent(0, new HaulIntent(hauler.Id, src, dst, Resource.Wood));
        sim.Run(until: 150);
        Assert.True(hauler.IsWalking || hauler.CargoAmount > 0);

        CombatRules.OnUnitDeath(sim, hauler);
        sim.Run(until: 800);

        Assert.False(sim.World.Units.ContainsKey(100));
    }
}
