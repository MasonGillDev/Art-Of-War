using Sim.Core.Engine;
using Sim.Core.Hauling;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.World;
using Sim.Server.Hauling;

namespace Sim.Tests;

// M45 (docs/m45-status.md): routes the player can read and change. Every named
// crew member carries; a running route's stops change in place without losing
// its crews; routes have names; each crew reports its last serve; a queued
// job's amount changes without losing its place in line.
public class HaulRouteEditTests
{
    private const int Cap = UnitCargoCatalog.HaulerCapacity;

    private static Simulation MakeSim(out GameWorld world)
    {
        world = new GameWorld(new TileGrid(24, 12, Biome.Grassland));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        return new Simulation(world, seed: 0x45E01);
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

    // A crew: a group of exactly these units, put on the route. Returns the group's id.
    private static int Crew(Simulation sim, int route, int start, params int[] members) =>
        TestGroups.Crew(sim, route, start, members);

    private static HaulingDriver Driver() => new(new HaulingConfig { ThinkPeriodTicks = 1 });

    private static RouteCrew CrewOf(GameWorld world, int route, int crewId = 1) =>
        world.HaulRoutes[route].Crews.Single(c => c.CrewId == crewId);

    private static int Serves(Simulation sim) =>
        sim.ResolvedLog.OfType<IntentEvent>()
            .Count(e => e.Intent is ServeRouteStopIntent && e.Outcome.IsApplied);

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

    // ---- who carries ---------------------------------------------------------

    [Fact]
    public void EveryNamedMember_Carries_AtItsOwnCapacity_ButSoldiersEscort()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 400);
        var route = SetRoute(sim, Stop(src.At, Up(Resource.Wood, 100)), Stop(new TileCoord(8, 2)));
        var hauler = Add(world, 1, src.At);
        var citizen = Add(world, 2, src.At, UnitRole.None);
        var farmer = Add(world, 3, src.At, UnitRole.Farmer);
        var guard = Add(world, 4, src.At, UnitRole.Soldier);
        Crew(sim, route, 0, 1, 2, 3, 4);

        RunUntil(Driver(), sim, () => Serves(sim) >= 1);

        Assert.Equal(hauler.CargoCapacity, hauler.Cargo.AmountOf(Resource.Wood));
        Assert.Equal(citizen.CargoCapacity, citizen.Cargo.AmountOf(Resource.Wood));
        Assert.Equal(farmer.CargoCapacity, farmer.Cargo.AmountOf(Resource.Wood));
        Assert.Equal(0, guard.CargoAmount);
        Assert.True(citizen.CargoCapacity > 0 && citizen.CargoCapacity < hauler.CargoCapacity);
    }

    // ---- editing a running route -----------------------------------------------

    [Fact]
    public void Remap_KeepsTheStopByTile_ElseTheNextSurvivor_ElseTheStart()
    {
        TileCoord A = new(1, 1), B = new(2, 1), C = new(3, 1), D = new(4, 1);
        List<RouteStop> L(params TileCoord[] t) => t.Select(x => new RouteStop { Tile = x }).ToList();

        // Same stop, new index.
        Assert.Equal(2, RouteStops.Remap(L(A, B, C), L(D, A, B, C), 1));
        // Heading to a removed stop: on to the next survivor in loop order.
        Assert.Equal(1, RouteStops.Remap(L(A, B, C), L(A, C), 1));
        // ... wrapping round the loop.
        Assert.Equal(0, RouteStops.Remap(L(A, B, C), L(A, B), 2));
        // A tile visited twice keeps its occurrence.
        Assert.Equal(3, RouteStops.Remap(L(A, B, A, C), L(D, A, B, A, C), 2));
        // Nothing survives: the new first stop.
        Assert.Equal(0, RouteStops.Remap(L(A, B), L(C, D), 1));
    }

    [Fact]
    public void Update_ReplacesTheStops_KeepsCrewsCargoAndTheRouteNumber()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 400);
        var a = Pile(world, new TileCoord(8, 2));
        var b = Pile(world, new TileCoord(14, 2));
        var route = SetRoute(sim, Stop(src.At, Up(Resource.Wood, 100)), Stop(a.At, Down(Resource.Wood, 50)));
        var hauler = Add(world, 1, src.At);
        Crew(sim, route, 0, 1);
        var driver = Driver();
        RunUntil(driver, sim, () => Serves(sim) >= 1);   // loaded at the source, heading to stop 1 (a)
        Assert.Equal(1, CrewOf(world, route).CurrentStop);
        var aboard = hauler.CargoAmount;

        var o = new UpdateHaulRouteIntent(route, new()
        {
            Stop(src.At, Up(Resource.Wood, 100)),
            Stop(b.At, Down(Resource.Wood, 25)),
            Stop(a.At, Down(Resource.Wood, 25)),
        }) { PlayerId = 0 }.Resolve(sim);

        Assert.True(o.IsApplied, o.Reason);
        var r = world.HaulRoutes[route];
        Assert.Equal(3, r.Stops.Count);
        Assert.Equal(1, r.Revision);
        Assert.Equal(1, r.Crews.Count);
        Assert.Equal(2, CrewOf(world, route).CurrentStop);   // still heading to a, now stop 2
        Assert.Equal(route, RouteCrews.RouteOf(world, hauler));
        Assert.Equal(aboard, hauler.CargoAmount);

        RunUntil(driver, sim, () => a.AmountOf(Resource.Wood) > 0);
        Assert.Equal(Cap * 25 / 100, a.AmountOf(Resource.Wood));   // the NEW drop size
    }

    [Fact]
    public void AServeSubmittedBeforeTheEdit_NoOps_OnTheRevisionFence()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 400);
        var route = SetRoute(sim, Stop(src.At, Up(Resource.Wood, 100)), Stop(new TileCoord(8, 2)));
        Add(world, 1, src.At);
        Crew(sim, route, 0, 1);

        var stale = new ServeRouteStopIntent(route, 1, 0, expectedRevision: 0) { PlayerId = 0 };
        Assert.True(new UpdateHaulRouteIntent(route, new()
        {
            Stop(src.At, Up(Resource.Wood, 50)), Stop(new TileCoord(8, 2)),
        }) { PlayerId = 0 }.Resolve(sim).IsApplied);

        var o = stale.Resolve(sim);
        Assert.True(o.IsRejected);
        Assert.Contains("revision fence", o.Reason);
        Assert.Equal(0, CrewOf(world, route).CurrentStop);
        Assert.Equal(400, src.AmountOf(Resource.Wood));
    }

    [Fact]
    public void Update_FailsClean_OnBadStopsOrSomeoneElsesRoute()
    {
        var sim = MakeSim(out var world);
        var route = SetRoute(sim, Stop(new TileCoord(2, 2)), Stop(new TileCoord(8, 2)));
        var before = Snapshot.Hash(sim);

        Assert.True(new UpdateHaulRouteIntent(route, new()) { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.True(new UpdateHaulRouteIntent(route, new() { Stop(new TileCoord(99, 2)) })
            { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.True(new UpdateHaulRouteIntent(route, new() { Stop(new TileCoord(2, 2), Up(Resource.Wood, 101)) })
            { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.True(new UpdateHaulRouteIntent(route, new() { Stop(new TileCoord(2, 2)) })
            { PlayerId = 1 }.Resolve(sim).IsRejected);
        Assert.True(new UpdateHaulRouteIntent(route + 5, new() { Stop(new TileCoord(2, 2)) })
            { PlayerId = 0 }.Resolve(sim).IsRejected);

        Assert.Equal(before, Snapshot.Hash(sim));
    }

    // ---- names -----------------------------------------------------------------

    [Fact]
    public void Routes_TakeAName_AtSetOrLater_TrimmedAndCapped()
    {
        var sim = MakeSim(out var world);
        var id = world.NextHaulRouteId;
        Assert.True(new SetHaulRouteIntent(new() { Stop(new TileCoord(2, 2)) }, name: "  North farms ")
            { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Equal("North farms", world.HaulRoutes[id].Name);

        Assert.True(new RenameHaulRouteIntent(id, "Wood loop") { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Equal("Wood loop", world.HaulRoutes[id].Name);
        Assert.True(new RenameHaulRouteIntent(id, new string('x', HaulingConstants.MaxRouteNameLength + 1))
            { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.True(new RenameHaulRouteIntent(id, "a\nb") { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.True(new RenameHaulRouteIntent(id, "Theirs") { PlayerId = 1 }.Resolve(sim).IsRejected);
        Assert.Equal("Wood loop", world.HaulRoutes[id].Name);

        Assert.True(new RenameHaulRouteIntent(id, "") { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Equal("", world.HaulRoutes[id].Name);
        Assert.True(new SetHaulRouteIntent(new() { Stop(new TileCoord(2, 2)) },
            name: new string('y', HaulingConstants.MaxRouteNameLength + 1)) { PlayerId = 0 }.Resolve(sim).IsRejected);
    }

    // ---- the last serve -------------------------------------------------------

    [Fact]
    public void LastServe_ReportsWhatMoved_AndWhatGotInTheWay()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 10);           // less than a load
        var theirs = Pile(world, new TileCoord(8, 2), owner: 1);
        var dst = Pile(world, new TileCoord(14, 2));
        var route = SetRoute(sim,
            Stop(src.At, Up(Resource.Wood, 100)),
            Stop(theirs.At, Down(Resource.Wood, 100)),
            Stop(dst.At, Down(Resource.Wood, 40)));
        Add(world, 1, src.At);
        Crew(sim, route, 0, 1);
        var driver = Driver();

        RunUntil(driver, sim, () => Serves(sim) >= 1);
        var first = CrewOf(world, route).LastServe!.Value;
        Assert.Equal((0, 10, 0), (first.Stop, first.Loaded, first.Unloaded));
        Assert.True(first.Notes.HasFlag(ServeNote.SourceEmpty));

        RunUntil(driver, sim, () => Serves(sim) >= 2);
        var second = CrewOf(world, route).LastServe!.Value;
        Assert.Equal((1, 0, 0, ServeNote.NotYours), (second.Stop, second.Loaded, second.Unloaded, second.Notes));

        RunUntil(driver, sim, () => Serves(sim) >= 3);
        var third = CrewOf(world, route).LastServe!.Value;
        Assert.Equal((2, Cap * 40 / 100, ServeNote.None), (third.Stop, third.Unloaded, third.Notes));
        Assert.Equal(sim.Now, third.Tick);
    }

    [Fact]
    public void LastServe_AllEscort_SaysNoCarriers()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 100);
        var route = SetRoute(sim, Stop(src.At, Up(Resource.Wood, 100)), Stop(new TileCoord(8, 2)));
        Add(world, 1, src.At, UnitRole.Soldier);
        Crew(sim, route, 0, 1);

        RunUntil(Driver(), sim, () => Serves(sim) >= 1);
        Assert.Equal(ServeNote.NoCarriers, CrewOf(world, route).LastServe!.Value.Notes);
        Assert.Equal(100, src.AmountOf(Resource.Wood));
    }

    // ---- queue jobs ------------------------------------------------------------

    private static int Job(Simulation sim, TileCoord src, TileCoord dst, HaulJobKind kind, int target)
    {
        var id = sim.World.NextHaulJobId;
        var o = new SetHaulJobIntent(src, dst, Resource.Wood, kind, target) { PlayerId = 0 }.Resolve(sim);
        Assert.True(o.IsApplied, o.Reason);
        return id;
    }

    [Fact]
    public void UpdateJob_KeepsItsPlaceInLine_AndWhatItDelivered()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 400);
        var dst = Pile(world, new TileCoord(8, 2));
        var first = Job(sim, src.At, dst.At, HaulJobKind.Once, 40);
        var second = Job(sim, src.At, dst.At, HaulJobKind.Standing, 60);
        world.HaulJobs[first].Delivered = 12;
        var stamp = world.HaulJobs[first].QueueStamp;

        Assert.True(new UpdateHaulJobIntent(first, HaulJobKind.Once, 50) { PlayerId = 0 }.Resolve(sim).IsApplied);
        var j = world.HaulJobs[first];
        Assert.Equal((HaulJobKind.Once, 50, 12, stamp), (j.Kind, j.Target, j.Delivered, j.QueueStamp));
        Assert.True(world.HaulJobs[first].QueueStamp < world.HaulJobs[second].QueueStamp);

        Assert.True(new UpdateHaulJobIntent(second, HaulJobKind.Once, 30) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Equal(0, world.HaulJobs[second].Delivered);   // switching to Once starts the count

        Assert.True(new UpdateHaulJobIntent(first, HaulJobKind.Once, 10) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.False(world.HaulJobs.ContainsKey(first));     // already met: done
    }

    [Fact]
    public void UpdateJob_FailsClean()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 400);
        var dst = Pile(world, new TileCoord(8, 2));
        var id = Job(sim, src.At, dst.At, HaulJobKind.Standing, 60);
        var before = Snapshot.Hash(sim);

        Assert.True(new UpdateHaulJobIntent(id, HaulJobKind.Standing, 0) { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.True(new UpdateHaulJobIntent(id, HaulJobKind.Salvage, 2) { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.True(new UpdateHaulJobIntent(id, (HaulJobKind)9, 5) { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.True(new UpdateHaulJobIntent(id, HaulJobKind.Once, 5) { PlayerId = 1 }.Resolve(sim).IsRejected);
        Assert.True(new UpdateHaulJobIntent(id + 9, HaulJobKind.Once, 5) { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.Equal(before, Snapshot.Hash(sim));
    }

    // ---- determinism -----------------------------------------------------------

    private static (Simulation sim, HaulingDriver driver, int route) Busy()
    {
        var sim = MakeSim(out var world);
        var src = Pile(world, new TileCoord(2, 2), amount: 2000);
        var a = Pile(world, new TileCoord(5, 2));
        var b = Pile(world, new TileCoord(7, 4));
        var route = SetRoute(sim,
            Stop(src.At, Up(Resource.Wood, 100)), Stop(a.At, Down(Resource.Wood, 50)), Stop(b.At, Down(Resource.Wood, 50)));
        Add(world, 1, src.At); Add(world, 2, src.At, UnitRole.None); Add(world, 3, src.At, UnitRole.Soldier);
        Crew(sim, route, 0, 1, 2, 3);
        Assert.True(new RenameHaulRouteIntent(route, "Wood loop") { PlayerId = 0 }.Resolve(sim).IsApplied);
        return (sim, Driver(), route);
    }

    private static void EditMidway(Simulation sim, int route)
    {
        sim.SubmitIntent(sim.Now, new UpdateHaulRouteIntent(route, new()
        {
            Stop(new TileCoord(2, 2), Up(Resource.Wood, 80)),
            Stop(new TileCoord(7, 4), Down(Resource.Wood, 40)),
            Stop(new TileCoord(5, 2), Down(Resource.Wood, 40)),
        }) { PlayerId = 0 });
    }

    [Fact]
    public void TwinRun_WithAnEditMidway_SameHash()
    {
        var (a, da, ra) = Busy();
        var (b, db, rb) = Busy();
        RunFor(da, a, until: 600); EditMidway(a, ra); RunFor(da, a, until: 2400);
        RunFor(db, b, until: 600); EditMidway(b, rb); RunFor(db, b, until: 2400);

        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
        Assert.True(Serves(a) > 4);
    }

    [Fact]
    public void Snapshot_CarriesNameRevisionAndLastServe_AndRecovers()
    {
        var (live, dl, route) = Busy();
        RunFor(dl, live, until: 600); EditMidway(live, route); live.Run(until: live.Now);
        var r = live.World.HaulRoutes[route];
        Assert.Null(r.Crews[0].LastServe);   // the edit cleared it
        RunUntil(dl, live, () => r.Crews[0].LastServe is not null);
        Assert.Equal(("Wood loop", 1), (r.Name, r.Revision));
        Assert.NotNull(r.Crews[0].LastServe);

        var restored = Snapshot.Restore(Snapshot.Serialize(live), seed: 0x45E01);
        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(restored));
        var rr = restored.World.HaulRoutes[route];
        Assert.Equal((r.Name, r.Revision, r.Crews[0].LastServe), (rr.Name, rr.Revision, rr.Crews[0].LastServe));

        var end = live.Now + 1200;
        RunFor(Driver(), live, until: end);
        RunFor(Driver(), restored, until: end);
        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(restored));
    }
}
