using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Hauling;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.World;
using Sim.Server.Hauling;

namespace Sim.Tests;

// M36 Phase C (docs/hauling-queue-and-routes.md): named routes — a loop of
// stops with pickup / drop rules measured against CAPACITY, walked by crews
// that move together, never wait, and never join the queue's pool.
public class HaulRouteTests
{
    private const int Cap = UnitCargoCatalog.HaulerCapacity;

    private static Simulation MakeSim(out GameWorld world)
    {
        world = new GameWorld(new TileGrid(24, 12, Biome.Grassland));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        return new Simulation(world, seed: 0x7047E);
    }

    private static Stockpile Pile(GameWorld world, TileCoord at, Resource r = Resource.Wood, int amount = 0,
        int owner = 0)
    {
        var p = (Stockpile)world.AddStructure(new Stockpile(at) { OwnerId = owner });
        if (amount > 0) p.Deposit(r, amount);
        return p;
    }

    private static Unit Add(GameWorld world, int id, TileCoord at, UnitRole role = UnitRole.Hauler, int owner = 0) =>
        world.AddUnit(new Unit(id, at) { Role = role, OwnerId = owner });

    private static RouteStop Stop(TileCoord tile, params StopRule[] rules) =>
        new() { Tile = tile, Rules = rules.ToList() };

    private static StopRule Up(Resource r, int pct) => new(r, StopRuleOp.Pickup, pct);
    private static StopRule Down(Resource r, int pct) => new(r, StopRuleOp.Drop, pct);

    private static int SetRoute(Simulation sim, params RouteStop[] stops)
    {
        var id = sim.World.NextHaulRouteId;
        var o = new SetHaulRouteIntent(stops.ToList()) { PlayerId = 0 }.Resolve(sim);
        Assert.True(o.IsApplied, o.Reason);
        return id;
    }

    private static void Crew(Simulation sim, int route, int start, params int[] members)
    {
        var o = new AddRouteCrewIntent(route, members.ToList(), start) { PlayerId = 0 }.Resolve(sim);
        Assert.True(o.IsApplied, o.Reason);
    }

    private static HaulingDriver Driver() => new(new HaulingConfig { ThinkPeriodTicks = 1 });

    private static RouteCrew CrewOf(GameWorld world, int route, int crewId = 1) =>
        world.HaulRoutes[route].Crews.Single(c => c.CrewId == crewId);

    // Think + run, one tick at a time, until `done` or the budget runs out.
    private static void RunUntil(HaulingDriver driver, Simulation sim, Func<bool> done, long budget = 5000)
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

    private static void RunFor(HaulingDriver driver, Simulation sim, long until)
    {
        for (var t = sim.Now; t <= until; t++)
        {
            sim.Run(until: t);
            driver.Think(sim, t);
            sim.Run(until: t);
        }
    }

    // A crew has served `n` stops once the serve intents have landed.
    private static int Serves(Simulation sim) =>
        sim.ResolvedLog.OfType<IntentEvent>()
            .Count(e => e.Intent is ServeRouteStopIntent && e.Outcome.IsApplied);

    [Fact]
    public void Quota_IsPercentOfCapacity_RoundedDown_AtLeastOne()
    {
        Assert.Equal(6, new StopRule(Resource.Wood, StopRuleOp.Drop, 25).QuotaFor(25));
        Assert.Equal(25, new StopRule(Resource.Wood, StopRuleOp.Drop, 100).QuotaFor(25));
        Assert.Equal(1, new StopRule(Resource.Wood, StopRuleOp.Drop, 1).QuotaFor(25));
        Assert.Equal(0, new StopRule(Resource.Wood, StopRuleOp.Drop, 50).QuotaFor(0));
    }

    [Fact]
    public void FourDropsOfAQuarter_SplitEvenly_LeftoverRidesOn()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 400);
        var houses = new[] { new TileCoord(6, 2), new TileCoord(10, 2), new TileCoord(14, 2), new TileCoord(18, 2) }
            .Select(t => Pile(world, t)).ToArray();
        var route = SetRoute(sim,
            Stop(src.At, Up(Resource.Wood, 100)),
            Stop(houses[0].At, Down(Resource.Wood, 25)),
            Stop(houses[1].At, Down(Resource.Wood, 25)),
            Stop(houses[2].At, Down(Resource.Wood, 25)),
            Stop(houses[3].At, Down(Resource.Wood, 25)));
        var hauler = Add(world, 1, src.At);
        Crew(sim, route, 0, 1);
        var driver = Driver();

        RunUntil(driver, sim, () => Serves(sim) >= 5);   // one full lap

        var quarter = Cap * 25 / 100;
        Assert.All(houses, h => Assert.Equal(quarter, h.AmountOf(Resource.Wood)));
        Assert.Equal(Cap - 4 * quarter, hauler.Cargo.AmountOf(Resource.Wood));   // kept, not dumped

        // Next lap: the pickup tops up to capacity, counting the leftover.
        RunUntil(driver, sim, () => Serves(sim) >= 6);
        Assert.Equal(Cap, hauler.Cargo.AmountOf(Resource.Wood));
        Assert.Equal(400 - Cap - (4 * quarter), src.AmountOf(Resource.Wood));
    }

    [Fact]
    public void Drop_KeepsWhatTheStopCannotTake()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 100);
        var full = Pile(world, new TileCoord(6, 2));
        full.Deposit(Resource.Stone, full.FreeSpace() - 3);   // room for 3
        var route = SetRoute(sim, Stop(src.At, Up(Resource.Wood, 100)), Stop(full.At, Down(Resource.Wood, 100)));
        var hauler = Add(world, 1, src.At);
        Crew(sim, route, 0, 1);
        var driver = Driver();

        RunUntil(driver, sim, () => Serves(sim) >= 2);

        Assert.Equal(3, full.AmountOf(Resource.Wood));
        Assert.Equal(Cap - 3, hauler.Cargo.AmountOf(Resource.Wood));
        Assert.False(world.GroundResources.ContainsKey(full.At));
    }

    [Fact]
    public void Pickup_FillsUpTo_CountingWhatIsAlreadyAboard()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 100);
        var route = SetRoute(sim, Stop(src.At, Up(Resource.Wood, 60)));
        var hauler = Add(world, 1, src.At);
        hauler.Cargo.Add(Resource.Wood, 10);
        Crew(sim, route, 0, 1);
        var driver = Driver();

        RunUntil(driver, sim, () => Serves(sim) >= 1);

        Assert.Equal(Cap * 60 / 100, hauler.Cargo.AmountOf(Resource.Wood));   // 15: took 5, not 15
        Assert.Equal(100 - (Cap * 60 / 100 - 10), src.AmountOf(Resource.Wood));
    }

    [Fact]
    public void OneLoop_CarriesTwoResources_ThatWouldBeFourLegs()
    {
        // The sword-chain loop from the design talk: ore and wood in, iron
        // back out, one crew, no empty leg.
        var sim = MakeSim(out var world);
        var mine = Pile(world, new TileCoord(2, 2), Resource.Ore, 100);
        var yard = Pile(world, new TileCoord(6, 2), Resource.Wood, 100);
        var smelter = Pile(world, new TileCoord(10, 2), Resource.Iron, 100);
        var smithy = Pile(world, new TileCoord(14, 2));
        var route = SetRoute(sim,
            Stop(mine.At, Up(Resource.Ore, 40)),
            Stop(yard.At, Up(Resource.Wood, 60)),
            Stop(smelter.At, Down(Resource.Ore, 100), Down(Resource.Wood, 50), Up(Resource.Iron, 40)),
            Stop(smithy.At, Down(Resource.Iron, 100), Down(Resource.Wood, 100)));
        var hauler = Add(world, 1, mine.At);
        Crew(sim, route, 0, 1);
        var driver = Driver();

        RunUntil(driver, sim, () => Serves(sim) >= 4);

        var ore = Cap * 40 / 100;
        var wood = Cap * 60 / 100;
        var woodAtSmelter = Math.Min(wood, Cap * 50 / 100);
        Assert.Equal(ore, smelter.AmountOf(Resource.Ore));
        Assert.Equal(woodAtSmelter, smelter.AmountOf(Resource.Wood));
        Assert.Equal(Cap * 40 / 100, smithy.AmountOf(Resource.Iron));
        Assert.Equal(wood - woodAtSmelter, smithy.AmountOf(Resource.Wood));
        Assert.Equal(0, hauler.CargoAmount);
    }

    [Fact]
    public void UnservableStops_AreSkipped_TheCrewNeverWaits()
    {
        var sim = MakeSim(out var world);
        var empty = Pile(world, new TileCoord(2, 2));             // nothing to pick up
        var theirs = Pile(world, new TileCoord(6, 2), amount: 50, owner: 1);   // not ours to trade with
        var bare = new TileCoord(10, 2);                          // no structure at all
        var route = SetRoute(sim,
            Stop(empty.At, Up(Resource.Wood, 100)),
            Stop(theirs.At, Up(Resource.Wood, 100)),
            Stop(bare, Down(Resource.Wood, 100)));
        var hauler = Add(world, 1, empty.At);
        Crew(sim, route, 0, 1);
        var driver = Driver();

        RunUntil(driver, sim, () => Serves(sim) >= 4);   // round the loop and on

        Assert.Equal(1, CrewOf(world, route).CurrentStop);
        Assert.Equal(0, hauler.CargoAmount);
        Assert.Equal(50, theirs.AmountOf(Resource.Wood));
    }

    [Fact]
    public void Escorts_WalkWithTheCrew_AndCarryNothing()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 100);
        var dst = Pile(world, new TileCoord(12, 2));
        var route = SetRoute(sim, Stop(src.At, Up(Resource.Wood, 100)), Stop(dst.At, Down(Resource.Wood, 100)));
        var hauler = Add(world, 1, src.At);
        var guard = Add(world, 2, src.At, UnitRole.Soldier);
        Crew(sim, route, 0, 1, 2);
        var driver = Driver();

        RunUntil(driver, sim, () => Serves(sim) >= 2);

        Assert.Equal(Cap, dst.AmountOf(Resource.Wood));
        Assert.Equal(0, guard.CargoAmount);
        Assert.Equal(dst.At, guard.Position);   // it came along
        Assert.Equal(hauler.Position, guard.Position);
    }

    [Fact]
    public void Crew_WaitsAtTheStop_ForAStraggler()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 100);
        var route = SetRoute(sim, Stop(src.At, Up(Resource.Wood, 100)), Stop(new TileCoord(20, 2)));
        Add(world, 1, src.At);
        var far = Add(world, 2, new TileCoord(20, 10), UnitRole.Soldier);
        Crew(sim, route, 0, 1, 2);
        var driver = Driver();

        RunUntil(driver, sim, () => Serves(sim) >= 1);

        // The serve landed only once the straggler had arrived.
        Assert.Equal(src.At, far.Position);
    }

    [Fact]
    public void RouteCrew_IsNeverInTheQueuePool_OrDormant()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 500);
        var dst = Pile(world, new TileCoord(8, 2));
        var route = SetRoute(sim, Stop(new TileCoord(20, 10)));
        var hauler = Add(world, 1, src.At);
        Crew(sim, route, 0, 1);
        var jobId = sim.World.NextHaulJobId;
        Assert.True(new SetHaulJobIntent(src.At, dst.At, Resource.Wood, HaulJobKind.Standing, 100)
            { PlayerId = 0 }.Resolve(sim).IsApplied);

        Assert.False(ClaimLedger.IsDormant(world, hauler));   // breed/train/staff pulls skip it too

        var driver = Driver();
        driver.Think(sim, sim.Now);
        sim.Run(until: sim.Now);

        Assert.Null(hauler.HaulPlan);
        Assert.Equal(HaulJobState.WaitingForHauler, driver.Reports[jobId].State);
    }

    [Fact]
    public void StaggeredCrews_ServeDifferentStops()
    {
        var sim = MakeSim(out var world);
        var tiles = new[] { new TileCoord(2, 2), new TileCoord(8, 2), new TileCoord(14, 2), new TileCoord(20, 2) };
        foreach (var t in tiles) Pile(world, t, amount: 100);
        var route = SetRoute(sim, tiles.Select(t => Stop(t, Up(Resource.Wood, 20))).ToArray());
        Add(world, 1, tiles[0]);
        Add(world, 2, tiles[2]);
        Crew(sim, route, 0, 1);
        Crew(sim, route, 2, 2);
        var driver = Driver();

        RunUntil(driver, sim, () => Serves(sim) >= 2);

        Assert.Equal(1, CrewOf(world, route, 1).CurrentStop);
        Assert.Equal(3, CrewOf(world, route, 2).CurrentStop);
    }

    [Fact]
    public void RemovingACrew_ReleasesItsMembers_ClearingTheRouteReleasesEveryone()
    {
        var sim = MakeSim(out var world);
        var route = SetRoute(sim, Stop(new TileCoord(2, 2)), Stop(new TileCoord(6, 2)));
        var a = Add(world, 1, new TileCoord(2, 2));
        var b = Add(world, 2, new TileCoord(2, 2));
        Crew(sim, route, 0, 1);
        Crew(sim, route, 1, 2);

        Assert.True(new RemoveRouteCrewIntent(route, 1) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Null(a.RouteId);
        Assert.Equal(route, b.RouteId);

        Assert.True(new ClearHaulRouteIntent(route) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Null(b.RouteId);
        Assert.Empty(world.HaulRoutes);
    }

    [Fact]
    public void SetRoute_And_AddCrew_Validation()
    {
        var sim = MakeSim(out var world);
        IntentOutcome Set(params RouteStop[] stops) =>
            new SetHaulRouteIntent(stops.ToList()) { PlayerId = 0 }.Resolve(sim);
        var t = new TileCoord(2, 2);

        Assert.True(Set().IsRejected);
        Assert.True(Set(Stop(new TileCoord(99, 2))).IsRejected);
        Assert.True(Set(Stop(t, Up(Resource.None, 50))).IsRejected);
        Assert.True(Set(Stop(t, Up(Resource.Wood, 0))).IsRejected);
        Assert.True(Set(Stop(t, Up(Resource.Wood, 101))).IsRejected);
        Assert.True(Set(Stop(t, new StopRule(Resource.Wood, (StopRuleOp)9, 50))).IsRejected);
        Assert.True(Set(Enumerable.Repeat(Stop(t), HaulingConstants.MaxStopsPerRoute + 1).ToArray()).IsRejected);

        var route = SetRoute(sim, Stop(t), Stop(new TileCoord(6, 2)));
        var other = SetRoute(sim, Stop(t));
        Add(world, 1, t);
        Add(world, 2, t, owner: 1);
        Add(world, 3, t);
        world.Claims[3] = new Claim(3, OrderId: 5, ClaimPurpose.Crew);
        Add(world, 4, t);

        IntentOutcome Crew(int r, int start, params int[] ids) =>
            new AddRouteCrewIntent(r, ids.ToList(), start) { PlayerId = 0 }.Resolve(sim);

        Assert.True(Crew(route, 0).IsRejected);          // nobody
        Assert.True(Crew(route, 2, 1).IsRejected);       // no such stop
        Assert.True(Crew(route, 0, 2).IsRejected);       // someone else's
        Assert.True(Crew(route, 0, 3).IsRejected);       // claimed by an order
        Assert.True(Crew(route, 0, 99).IsRejected);      // doesn't exist
        Assert.True(Crew(route, 0, 1).IsApplied);
        Assert.True(Crew(other, 0, 1).IsRejected);       // already on a route
        Assert.True(new AddRouteCrewIntent(route, new() { 4 }) { PlayerId = 1 }.Resolve(sim).IsRejected);
    }

    [Fact]
    public void SetRoute_WithACrew_StaffsItInOneStep_OrNotAtAll()
    {
        var sim = MakeSim(out var world);
        var a = Add(world, 1, new TileCoord(2, 2));
        Add(world, 2, new TileCoord(2, 2), owner: 1);
        var stops = new List<RouteStop> { Stop(new TileCoord(2, 2)), Stop(new TileCoord(6, 2)) };

        // A crew that fails its checks takes the route down with it.
        var bad = new SetHaulRouteIntent(stops, new() { 1, 2 }) { PlayerId = 0 }.Resolve(sim);
        Assert.True(bad.IsRejected);
        Assert.Empty(world.HaulRoutes);
        Assert.Null(a.RouteId);

        var id = world.NextHaulRouteId;
        Assert.True(new SetHaulRouteIntent(stops, new() { 1 }) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Equal(id, a.RouteId);
        Assert.Equal(0, CrewOf(world, id).CurrentStop);
    }

    [Fact]
    public void Serve_IsFencedOnTheStopTheDriverSaw()
    {
        var sim = MakeSim(out var world);
        var route = SetRoute(sim, Stop(new TileCoord(2, 2)), Stop(new TileCoord(6, 2)));
        Add(world, 1, new TileCoord(2, 2));
        Crew(sim, route, 0, 1);

        Assert.True(new ServeRouteStopIntent(route, 1, expectedStop: 1) { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.True(new ServeRouteStopIntent(route, 1, expectedStop: 0) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.True(new ServeRouteStopIntent(route, 1, expectedStop: 0) { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.Equal(1, CrewOf(world, route).CurrentStop);
    }

    // ---- headline: determinism ------------------------------------------------

    private static (Simulation, HaulingDriver) Busy()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 500);
        var ore = Pile(world, new TileCoord(2, 9), Resource.Ore, 500);
        var a = Pile(world, new TileCoord(12, 2));
        var b = Pile(world, new TileCoord(20, 6));
        var route = SetRoute(sim,
            Stop(src.At, Up(Resource.Wood, 50)),
            Stop(ore.At, Up(Resource.Ore, 50)),
            Stop(a.At, Down(Resource.Wood, 50), Down(Resource.Ore, 25)),
            Stop(b.At, Down(Resource.Wood, 100), Down(Resource.Ore, 100)));
        Add(world, 1, src.At);
        Add(world, 2, src.At, UnitRole.Soldier);
        Add(world, 3, a.At);
        Crew(sim, route, 0, 1, 2);
        Crew(sim, route, 2, 3);
        // The queue runs beside the route, on its own haulers.
        Add(world, 4, src.At);
        Assert.True(new SetHaulJobIntent(src.At, b.At, Resource.Wood, HaulJobKind.Standing, 200)
            { PlayerId = 0 }.Resolve(sim).IsApplied);
        return (sim, Driver());
    }

    [Fact]
    public void TwinRun_SameHash()
    {
        var (a, da) = Busy();
        var (b, db) = Busy();
        RunFor(da, a, until: 1200);
        RunFor(db, b, until: 1200);

        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
        Assert.True(Serves(a) > 4);
    }

    [Fact]
    public void SnapshotMidRoute_RecoversToTheSameWorld()
    {
        var (live, dl) = Busy();
        RunFor(dl, live, until: 500);

        var restored = Snapshot.Restore(Snapshot.Serialize(live), seed: 0x7047E);
        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(restored));

        RunFor(Driver(), live, until: 1200);
        RunFor(Driver(), restored, until: 1200);
        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(restored));
    }
}
