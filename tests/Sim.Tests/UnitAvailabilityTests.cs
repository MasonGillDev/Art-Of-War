using Sim.Core.Automation;
using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Hauling;
using Sim.Core.Intents;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server.Hauling;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// UnitAvailability — the one answer to "is this unit busy?" (2026-10-02). Before it, six
// systems each worked it out for themselves and only the bandit driver knew a unit in a
// fight is not free. A unit on a battle's tile is busy until it dies or leaves; the
// automation, the hauling driver, a route stop's serve and a group's muster all read
// the same rule now.
public class UnitAvailabilityTests
{
    private static (Simulation sim, GameWorld world) MakeWorld()
    {
        var world = new GameWorld(new TileGrid(24, 12, Biome.Grassland));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        return (new Simulation(world, seed: 7), world);
    }

    [Fact]
    public void AnIdleUnit_IsFree_AndEachKindOfBusyIsNamed()
    {
        var (sim, world) = MakeWorld();
        var u = world.AddUnit(new Unit(1, new TileCoord(3, 3)) { Role = UnitRole.Builder });
        Assert.Equal(BusyReason.None, UnitAvailability.Busy(world, u));
        Assert.True(ClaimLedger.IsDormant(world, u));

        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(15, 3)));
        sim.Run(until: 0);
        Assert.Equal(BusyReason.Walking, UnitAvailability.Busy(world, u));
        Assert.False(ClaimLedger.IsDormant(world, u));
    }

    [Fact]
    public void OnABattlesTile_ABodyIsFighting_OnTheBoardOrWaitingAtItsEdge()
    {
        var (sim, world) = MakeWorld();
        var tile = new TileCoord(10, 5);
        var mine = world.AddUnit(new Unit(1, tile) { Role = UnitRole.Soldier });
        world.AddUnit(new Unit(2, tile) { Role = UnitRole.Soldier, OwnerId = 1 });
        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        Assert.True(world.Battlefields.ContainsKey(tile));

        Assert.Equal(BusyReason.Fighting, UnitAvailability.Busy(world, mine));
        Assert.False(ClaimLedger.IsDormant(world, mine));
        Assert.Equal("fighting", GroupMuster.Busy(world, mine));

        // Off the board but standing on the battle's tile (waiting for room) is fighting too.
        mine.Board = null;
        Assert.Equal(BusyReason.Fighting, UnitAvailability.Busy(world, mine));
    }

    [Fact]
    public void IsAPureRead()
    {
        var (sim, world) = MakeWorld();
        for (var id = 1; id <= 5; id++) world.AddUnit(new Unit(id, new TileCoord(id, 2)) { Role = UnitRole.Hauler });
        sim.SubmitIntent(0, new MoveIntent(2, new TileCoord(20, 2)));
        sim.Run(until: 5);
        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++)
            foreach (var u in world.Units.Values) UnitAvailability.Busy(world, u);
        Assert.Equal(before, Snapshot.Hash(sim));
    }

    // The bug that started it: a fight at a route's stop. The crew used to be served with a
    // member mid-battle and marched on without it. Now a member in a fight is busy: the
    // crew holds at the stop — no serve, no leaving — until the fight is over.
    [Fact]
    public void AFightAtAStop_HoldsTheCrew_NoServeNoLeaving()
    {
        var (sim, world) = MakeWorld();
        var srcAt = new TileCoord(4, 5);
        var src = (Stockpile)world.AddStructure(new Stockpile(srcAt) { OwnerId = 0 });
        src.Deposit(Resource.Wood, 500);
        world.AddStructure(new Stockpile(new TileCoord(18, 5)) { OwnerId = 0 });
        world.AddUnit(new Unit(1, srcAt) { Role = UnitRole.Hauler });
        world.AddUnit(new Unit(2, srcAt) { Role = UnitRole.Soldier });
        var route = world.NextHaulRouteId;
        Assert.True(new SetHaulRouteIntent(new()
        {
            new RouteStop { Tile = srcAt, Rules = new() { new StopRule(Resource.Wood, StopRuleOp.Pickup, 100) } },
            new RouteStop { Tile = new TileCoord(18, 5), Rules = new() { new StopRule(Resource.Wood, StopRuleOp.Drop, 100) } },
        }, crew: new() { 1, 2 }) { PlayerId = 0 }.Resolve(sim).IsApplied);
        var crewGroup = world.HaulRoutes[route].Crews.Single().GroupId;
        sim.Run(until: 300);   // the crew forms up at its first stop
        Assert.Equal(GroupState.Idle, world.Groups[crewGroup].State);

        // An enemy walks onto the stop before the serve: a fight there that won't end on its
        // own (both sides Hold).
        world.AddUnit(new Unit(9, srcAt) { Role = UnitRole.Soldier, OwnerId = 1 });
        CombatTrigger.MaybeBeginCombatOnTile(sim, srcAt);
        Assert.True(world.Battlefields.ContainsKey(srcAt));

        var driver = new HaulingDriver(new HaulingConfig { ThinkPeriodTicks = 1 });
        var end = sim.Now + 600;   // fixed once: sim.Now moves as the loop runs
        for (var t = sim.Now; t < end; t++)
        {
            sim.Run(until: t);
            driver.Think(sim, t);
            sim.Run(until: t);
        }

        Assert.DoesNotContain(sim.ResolvedLog.OfType<IntentEvent>(), e => e.Intent is ServeRouteStopIntent && e.Outcome.IsApplied);
        Assert.NotEqual(GroupState.Moving, world.Groups[crewGroup].State);
        Assert.Equal(0, world.HaulRoutes[route].Crews.Single().CurrentStop);
        Assert.Contains(driver.CrewReports, r => r.State == RouteCrewState.MemberBusy);
    }
}
