using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Hauling;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.World;
using Sim.Server.Hauling;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M47 — a route crew is a group on a route (docs/m47-route-groups-spec.md): it runs
// the loop in formation; a muster lets it finish its leg first; dismiss puts it back
// on the route; a player's move suspends it; taking it off keeps the group; and the
// whole of it is deterministic across a mid-leg restore.
public class RouteGroupTests
{
    private static Simulation MakeSim(out GameWorld world)
    {
        world = new GameWorld(new TileGrid(32, 16, Biome.Grassland));
        world.Players[0] = new Player(0);
        return new Simulation(world, seed: 0x7047E);
    }

    private static Stockpile Pile(GameWorld world, TileCoord at, int wood = 0)
    {
        var p = (Stockpile)world.AddStructure(new Stockpile(at) { OwnerId = 0 });
        if (wood > 0) p.Deposit(Resource.Wood, wood);
        return p;
    }

    private static HaulingDriver Driver() => new(new HaulingConfig { ThinkPeriodTicks = 1 });

    private static IntentOutcome Do(Simulation sim, Intent intent)
    {
        sim.SubmitIntent(sim.Now, intent);
        sim.Run(until: sim.Now);
        return sim.ResolvedLog.OfType<IntentEvent>().Last(e => e.Intent == intent).Outcome;
    }

    private static void RunUntil(HaulingDriver driver, Simulation sim, Func<bool> done, long budget = 20_000)
    {
        var end = sim.Now + budget;
        for (var t = sim.Now; t <= end && !done(); t++)
        {
            sim.Run(until: t);
            driver.Think(sim, t);
            sim.Run(until: t);
        }
        Assert.True(done(), "condition not reached within the tick budget");
    }

    private static int Serves(Simulation sim) =>
        sim.ResolvedLog.OfType<IntentEvent>().Count(e => e.Intent is ServeRouteStopIntent && e.Outcome.IsApplied);

    // A two-stop wood loop crewed by a named group (four haulers and a soldier) that
    // sits in an army. Returns (army, crew group, route, source, destination).
    private static (int Army, int Crew, int Route, Stockpile Src, Stockpile Dst) Loop(Simulation sim, GameWorld world)
    {
        var src = Pile(world, new TileCoord(3, 4), wood: 1000);
        var dst = Pile(world, new TileCoord(24, 4));
        for (var id = 1; id <= 4; id++) world.AddUnit(new Unit(id, new TileCoord(2 + id, 8)) { Role = UnitRole.Hauler });
        world.AddUnit(new Unit(5, new TileCoord(2, 8)) { Role = UnitRole.Soldier });
        var route = world.NextHaulRouteId;
        Assert.True(new SetHaulRouteIntent(new()
        {
            new RouteStop { Tile = src.At, Rules = new() { new StopRule(Resource.Wood, StopRuleOp.Pickup, 100) } },
            new RouteStop { Tile = dst.At, Rules = new() { new StopRule(Resource.Wood, StopRuleOp.Drop, 100) } },
        }, name: "Timber") { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.False(Do(sim, new CreateGroupIntent("Army", Array.Empty<int>(), holdsGroups: true)).IsRejected);
        var army = world.NextGroupId - 1;
        Assert.False(Do(sim, new CreateGroupIntent("Carters", new[] { 1, 2, 3, 4, 5 }, parentId: army)).IsRejected);
        var crew = world.NextGroupId - 1;
        Assert.False(Do(sim, new AssignGroupToRouteIntent(crew, route)).IsRejected);
        return (army, crew, route, src, dst);
    }

    [Fact]
    public void ACrew_RunsTheLoop_AsOneBody()
    {
        var sim = MakeSim(out var world);
        var (_, crew, route, _, dst) = Loop(sim, world);
        var driver = Driver();
        Assert.Equal(crew, world.HaulRoutes[route].Crews.Single().GroupId);
        Assert.True(GroupRules.UnderCommand(world, world.Units[1]));
        Assert.False(ClaimLedger.IsDormant(world, world.Units[1]));
        Assert.Equal(route, RouteCrews.RouteOf(world, world.Units[1]));

        var spread = 0;
        RunUntil(driver, sim, () =>
        {
            if (world.Groups[crew].MarchPath is not null)
            {
                var xs = world.Groups[crew].Members.Select(id => world.Units[id].Position.X).ToList();
                spread = Math.Max(spread, xs.Max() - xs.Min());
            }
            return dst.AmountOf(Resource.Wood) > 0;
        });

        Assert.InRange(spread, 0, 3);   // five marching together
        // Four haulers each carried a full load; the soldier is escort.
        Assert.Equal(4 * UnitCargoCatalog.HaulerCapacity, dst.AmountOf(Resource.Wood));
    }

    [Fact]
    public void Mustered_MidLeg_TheCrewFinishesTheLeg_ThenAnswers_AndDismissResumes()
    {
        var sim = MakeSim(out var world);
        var (army, crew, route, _, dst) = Loop(sim, world);
        var driver = Driver();
        // Loaded at the source and on its way to the drop.
        RunUntil(driver, sim, () => world.HaulRoutes[route].Crews[0].CurrentStop == 1 && world.Groups[crew].State == GroupState.Moving);
        var servesBefore = Serves(sim);

        Assert.False(Do(sim, new MusterGroupIntent(army, new TileCoord(14, 12))).IsRejected);
        Assert.NotNull(world.Groups[crew].PendingMuster);
        Assert.False(world.Groups[crew].RouteSuspended);

        // It finishes the leg: the drop is served, then it answers.
        RunUntil(driver, sim, () => world.Groups[crew].RouteSuspended);
        Assert.Equal(servesBefore + 1, Serves(sim));
        Assert.Equal(4 * UnitCargoCatalog.HaulerCapacity, dst.AmountOf(Resource.Wood));
        RunUntil(driver, sim, () => world.Groups[crew].State == GroupState.Idle);
        Assert.All(world.Groups[crew].Members, id => Assert.True(
            Math.Max(Math.Abs(world.Units[id].Position.X - 14), Math.Abs(world.Units[id].Position.Y - 12)) <= 1));
        var called = Serves(sim);
        for (var i = 0; i < 300; i++) { driver.Think(sim, sim.Now); sim.Run(until: sim.Now + 1); }
        Assert.Equal(called, Serves(sim));   // called away: the loop waits

        // Dismissed, it goes back to the route at the next stop.
        Assert.False(Do(sim, new DismissGroupIntent(army)).IsRejected);
        Assert.False(world.Groups[crew].RouteSuspended);
        Assert.NotEqual(GroupState.Dismissed, world.Groups[crew].State);
        RunUntil(driver, sim, () => Serves(sim) > called);
    }

    [Fact]
    public void APlayersMove_SuspendsTheRoute_UntilDismissed()
    {
        var sim = MakeSim(out var world);
        var (_, crew, _, _, _) = Loop(sim, world);
        var driver = Driver();
        RunUntil(driver, sim, () => Serves(sim) >= 1);

        Assert.False(Do(sim, new MoveGroupIntent(crew, new TileCoord(10, 14))).IsRejected);
        Assert.True(world.Groups[crew].RouteSuspended);
        RunUntil(driver, sim, () => world.Groups[crew].State == GroupState.Idle);
        Assert.Equal(new TileCoord(10, 14), world.Groups[crew].Position);
        var serves = Serves(sim);

        Do(sim, new DismissGroupIntent(crew));
        RunUntil(driver, sim, () => Serves(sim) > serves);
    }

    [Fact]
    public void TakenOffTheRoute_TheGroupIsKept_AndItsMembersAreFree()
    {
        var sim = MakeSim(out var world);
        var (_, crew, route, _, _) = Loop(sim, world);

        Assert.False(Do(sim, new UnassignGroupFromRouteIntent(crew)).IsRejected);

        Assert.Empty(world.HaulRoutes[route].Crews);
        Assert.Equal(GroupState.Dismissed, world.Groups[crew].State);
        Assert.False(GroupRules.UnderCommand(world, world.Units[1]));
        Assert.Null(RouteCrews.RouteOf(world, world.Units[1]));
        Assert.True(Do(sim, new UnassignGroupFromRouteIntent(crew)).IsRejected);   // on no route now
    }

    [Fact]
    public void DeletingTheCrewsGroup_TakesItOffTheRoute()
    {
        var sim = MakeSim(out var world);
        var (_, crew, route, _, _) = Loop(sim, world);
        Assert.False(Do(sim, new DeleteGroupIntent(crew)).IsRejected);
        Assert.Empty(world.HaulRoutes[route].Crews);
        Assert.Null(world.Units[1].GroupId);
    }

    [Fact]
    public void AssignRefusals()
    {
        var sim = MakeSim(out var world);
        var (army, crew, route, _, _) = Loop(sim, world);
        Assert.True(Do(sim, new AssignGroupToRouteIntent(crew, route)).IsRejected);          // already on it
        Assert.True(Do(sim, new AssignGroupToRouteIntent(army, route)).IsRejected);          // holds groups
        Assert.True(Do(sim, new AssignGroupToRouteIntent(crew + 99, route)).IsRejected);     // no such group
        for (var id = 10; id <= 22; id++) world.AddUnit(new Unit(id, new TileCoord(id, 14)) { Role = UnitRole.Hauler });
        Do(sim, new CreateGroupIntent("Too many", Enumerable.Range(10, 13).ToArray()));
        Assert.True(Do(sim, new AssignGroupToRouteIntent(world.NextGroupId - 1, route)).IsRejected);
        Assert.True(Do(sim, new MergeGroupsIntent(world.NextGroupId - 1, crew)).IsRejected);   // a crew doesn't merge
    }

    [Fact]
    public void TheOldCrewIntent_MakesANamedGroup()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(3, 4), wood: 100);
        world.AddUnit(new Unit(1, src.At) { Role = UnitRole.Hauler });
        var route = world.NextHaulRouteId;
        new SetHaulRouteIntent(new() { new RouteStop { Tile = src.At }, new RouteStop { Tile = new TileCoord(9, 4) } }, name: "Timber")
            { PlayerId = 0 }.Resolve(sim);

        Assert.True(new AddRouteCrewIntent(route, new() { 1 }) { PlayerId = 0 }.Resolve(sim).IsApplied);

        var group = world.Groups[world.Units[1].GroupId!.Value];
        Assert.Equal("Timber crew 1", group.Name);
        Assert.Equal(group.Id, world.HaulRoutes[route].Crews.Single().GroupId);
    }

    [Fact]
    public void TwinRun_AndMidLegRestore_EndTheSame()
    {
        (Simulation, HaulingDriver) Build()
        {
            var sim = MakeSim(out var world);
            Loop(sim, world);
            return (sim, Driver());
        }
        void RunTo((Simulation sim, HaulingDriver d) x, long until)
        {
            for (var t = x.sim.Now; t <= until; t++) { x.sim.Run(until: t); x.d.Think(x.sim, t); x.sim.Run(until: t); }
        }

        var a = Build(); RunTo(a, 3000);
        var b = Build(); RunTo(b, 3000);
        Assert.Equal(Snapshot.Hash(a.Item1), Snapshot.Hash(b.Item1));
        Assert.True(Serves(a.Item1) >= 2);

        var live = Build(); RunTo(live, 700);
        Assert.NotNull(live.Item1.World.Groups.Values.Single(g => g.Kind == GroupKind.Units).MarchPath);
        var restored = Snapshot.Restore(Snapshot.Serialize(live.Item1), seed: 0x7047E);
        Assert.Equal(Snapshot.Hash(live.Item1), Snapshot.Hash(restored));
        RunTo((live.Item1, Driver()), 3000);
        RunTo((restored, Driver()), 3000);
        Assert.Equal(Snapshot.Hash(live.Item1), Snapshot.Hash(restored));
    }
}
