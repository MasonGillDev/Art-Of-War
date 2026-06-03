using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Tests;

// M7 Phase D: clean death.
//   * Dying units are removed from world.Units.
//   * Grouped units are removed from their group's Members.
//   * A group hitting zero Members is attrition-disbanded.
//   * Pending in-flight events (MoveArrival / HaulPickup / HaulDeposit)
//     fence cleanly via world.Units.TryGetValue when the unit is gone —
//     no crash, no silent state corruption.
public class CombatDeathTests
{
    private static Simulation MakeContestedScenario(TileCoord tile)
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
        var sim = MakeContestedScenario(tile);
        // 2 vs 1 — the lone B unit dies fast.
        sim.World.AddUnit(new Unit(100, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        sim.World.AddUnit(new Unit(101, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        sim.World.AddUnit(new Unit(200, tile) { Role = UnitRole.Builder, OwnerId = 1 });
        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);

        sim.Run(until: 200);
        Assert.False(sim.World.Units.ContainsKey(200));
    }

    [Fact]
    public void GroupedUnitDying_RemovedFromGroup()
    {
        var tile = new TileCoord(10, 10);
        var sim = MakeContestedScenario(tile);
        // Two A units on the tile in a group, one B unit on the tile.
        // We pre-attach a group to A's units (id 1).
        var u1 = sim.World.AddUnit(new Unit(100, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        var u2 = sim.World.AddUnit(new Unit(101, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        sim.World.AddUnit(new Unit(200, tile) { Role = UnitRole.Builder, OwnerId = 1 });
        var group = new Group(1) { OwnerId = 0 };
        group.Members.Add(u1.Id);
        group.Members.Add(u2.Id);
        group.Position = tile;
        group.State = GroupState.Idle;
        sim.World.Groups[group.Id] = group;
        u1.GroupId = group.Id;
        u2.GroupId = group.Id;

        // Bring A's units down to 1 HP each so they die in round 1
        // (B's power = 1 → 1 dmg → kills lowest-HP first).
        u1.Health = 1;
        u2.Health = 1;
        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        sim.Run(until: 30);

        // One A unit died → group has one member remaining (or zero,
        // depending on damage order).
        Assert.True(group.Members.Count < 2, "at least one grouped unit should have died");
    }

    [Fact]
    public void GroupAtZeroMembers_RemovedFromWorldGroups()
    {
        var tile = new TileCoord(10, 10);
        var sim = MakeContestedScenario(tile);
        var u1 = sim.World.AddUnit(new Unit(100, tile) { Role = UnitRole.Builder, OwnerId = 0 });
        // B has overwhelming force — guarantees A's lone group dies.
        sim.World.AddUnit(new Unit(200, tile) { Role = UnitRole.Builder, OwnerId = 1 });
        sim.World.AddUnit(new Unit(201, tile) { Role = UnitRole.Builder, OwnerId = 1 });
        sim.World.AddUnit(new Unit(202, tile) { Role = UnitRole.Builder, OwnerId = 1 });
        var group = new Group(1) { OwnerId = 0 };
        group.Members.Add(u1.Id);
        group.Position = tile;
        group.State = GroupState.Idle;
        sim.World.Groups[group.Id] = group;
        u1.GroupId = group.Id;

        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        sim.Run(until: 200);

        Assert.False(sim.World.Groups.ContainsKey(1),
            "group should attrition-disband when its last member dies");
    }

    [Fact]
    public void UnitDyingMidMove_PendingArrivalNoOps_NotCrash()
    {
        // Set up: faction 0 unit walks toward faction 1's territory; faction 1
        // ambushes it at a tile en route. The walking unit dies mid-path; the
        // remaining MoveArrivalEvents for that unit will fire on an absent
        // unit and must fence cleanly (no crash).
        var tile = new TileCoord(8, 10);
        var sim = MakeContestedScenario(tile);

        var walker = sim.World.AddUnit(new Unit(100, new TileCoord(2, 10))
            { Role = UnitRole.Builder, OwnerId = 0 });
        walker.Health = 1; // dies on first damage tick.

        // Ambusher — overwhelming force on the path.
        for (var i = 0; i < 5; i++)
            sim.World.AddUnit(new Unit(200 + i, tile) { Role = UnitRole.Builder, OwnerId = 1 });

        sim.SubmitIntent(0, new MoveIntent(walker.Id, new TileCoord(15, 10)));
        // Run long enough for arrival, combat round, walker death,
        // and any subsequent pending MoveArrivalEvent to fire harmlessly.
        sim.Run(until: 500);

        // Walker dead; no exception thrown.
        Assert.False(sim.World.Units.ContainsKey(100));
    }

    [Fact]
    public void UnitDyingMidHaul_PendingPickupNoOps_NotCrash()
    {
        var tile = new TileCoord(8, 10);
        var sim = MakeContestedScenario(tile);

        // Source on the LEFT of the contested tile, destination on the RIGHT,
        // so the hauler walks THROUGH the ambush.
        var src = new TileCoord(2, 10);
        var dst = new TileCoord(15, 10);
        var stockpile = sim.World.AddStructure(new Stockpile(src) { OwnerId = 0 });
        stockpile.Deposit(Resource.Wood, 50);
        var dstStockpile = sim.World.AddStructure(new Stockpile(dst) { OwnerId = 0 });
        var hauler = sim.World.AddUnit(new Unit(100, src)
            { Role = UnitRole.Hauler, CargoCapacity = 5, OwnerId = 0 });
        hauler.Health = 1;

        // Ambusher on the path — overwhelming.
        for (var i = 0; i < 5; i++)
            sim.World.AddUnit(new Unit(200 + i, tile) { Role = UnitRole.Builder, OwnerId = 1 });

        sim.SubmitIntent(0, new HaulIntent(hauler.Id, src, dst, Resource.Wood));
        sim.Run(until: 500);

        // Hauler died; no crash on the pending haul events that fence cleanly.
        Assert.False(sim.World.Units.ContainsKey(100));
    }
}
