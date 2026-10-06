using Sim.Core.Engine;
using Sim.Core.Hauling;
using Sim.Core.Persistence;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Hauling;

namespace Sim.Tests;

// M36 Phase D (server half): the haul queue, routes and mixed cargo reach the
// viewer's view, owner-only, with the driver's verdicts — and projecting
// never touches the world.
public class HaulWireTests
{
    private static (Simulation sim, ViewProjector projector, HaulingDriver driver, Castle castle) Match()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 1 });
        var sim = new Simulation(build.Spec, seed: 0x3A1E);
        var driver = new HaulingDriver(new HaulingConfig { ThinkPeriodTicks = 1 });
        var projector = new ViewProjector(build) { HaulSource = driver };
        var castle = sim.World.Structures.Values.OfType<Castle>().First(c => c.OwnerId == 0);
        return (sim, projector, driver, castle);
    }

    [Fact]
    public void QueueRoutesAndCargo_ReachTheOwner_Only()
    {
        var (sim, projector, driver, castle) = Match();
        var world = sim.World;
        var pileAt = new TileCoord(castle.At.X + 2, castle.At.Y);
        world.Structures.Remove(pileAt);
        world.AddStructure(new Stockpile(pileAt) { OwnerId = 0 });

        Assert.True(new SetHaulJobIntent(castle.At, pileAt, Resource.Wood, HaulJobKind.Once, 30)
            { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.True(new SetHaulRouteIntent(new()
        {
            new() { Tile = castle.At, Rules = new() { new(Resource.Food, StopRuleOp.Pickup, 50) } },
            new() { Tile = pileAt },
        }) { PlayerId = 0 }.Resolve(sim).IsApplied);
        var carrier = world.Units.Values.First(u => u.OwnerId == 0);
        carrier.Cargo.Add(Resource.CopperOre, 2);
        carrier.Cargo.Add(Resource.Wood, 1);

        driver.Think(sim, sim.Now);
        var mine = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        var theirs = projector.Project(sim, sim.Now, playerId: 1, reveal: true);

        var job = Assert.Single(mine.HaulQueue.Jobs);
        Assert.Equal((castle.At.X, castle.At.Y, pileAt.X, pileAt.Y, (int)Resource.Wood, 2, 30),
            (job.SourceX, job.SourceY, job.DestX, job.DestY, job.Resource, job.Kind, job.Target));
        Assert.NotEqual(0, job.State);
        var route = Assert.Single(mine.HaulRoutes);
        Assert.Equal(2, route.Stops.Length);
        Assert.Equal(50, Assert.Single(route.Stops[0].Rules).Percent);

        var row = mine.Units.Single(u => u.Id == carrier.Id);
        Assert.Equal(3, row.CargoAmount);
        Assert.Equal((int)Resource.CopperOre, row.CargoResource);
        Assert.Equal(new[] { (int)Resource.Wood, (int)Resource.CopperOre },
            row.Cargo.Select(c => c.Resource).ToArray());   // enum order

        Assert.Empty(theirs.HaulQueue.Jobs);
        Assert.Empty(theirs.HaulRoutes);
        Assert.Empty(theirs.Units.Single(u => u.Id == carrier.Id).Cargo);
    }

    // M45 — capacity and route id on own units, the route's name and each
    // crew's last serve; none of it on anyone else's view.
    [Fact]
    public void RouteNameCapacityAndLastServe_ReachTheOwner_Only()
    {
        var (sim, projector, driver, castle) = Match();
        var world = sim.World;
        var pileAt = new TileCoord(castle.At.X + 2, castle.At.Y);
        world.Structures.Remove(pileAt);
        world.AddStructure(new Stockpile(pileAt) { OwnerId = 0 });
        var carrier = world.Units.Values.First(u => u.OwnerId == 0 && u.GroupId is null && !u.IsEmbarked);
        var routeId = world.NextHaulRouteId;
        Assert.True(new SetHaulRouteIntent(new()
        {
            new() { Tile = carrier.Position },
            new() { Tile = pileAt },
        }, name: "Wood loop") { PlayerId = 0 }.Resolve(sim).IsApplied);
        TestGroups.Crew(sim, routeId, 0, new[] { carrier.Id });
        world.HaulRoutes[routeId].Crews[0].LastServe =
            new ServeReport(1, 42, 3, 2, ServeNote.SourceEmpty | ServeNote.DropRefused);

        var mine = projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        var theirs = projector.Project(sim, sim.Now, playerId: 1, reveal: true);

        var route = Assert.Single(mine.HaulRoutes);
        Assert.Equal("Wood loop", route.Name);
        var crew = Assert.Single(route.Crews);
        Assert.Equal((1, 42L, 3, 2, 3), (crew.LastStop, crew.LastTick, crew.LastLoaded, crew.LastUnloaded, crew.LastNotes));
        var row = mine.Units.Single(u => u.Id == carrier.Id);
        Assert.Equal((carrier.CargoCapacity, routeId), (row.CargoCapacity, row.HaulRouteId));

        var other = theirs.Units.Single(u => u.Id == carrier.Id);
        Assert.Equal((0, -1), (other.CargoCapacity, other.HaulRouteId));
    }

    [Fact]
    public void Projecting_IsAPureRead()
    {
        var (sim, projector, driver, castle) = Match();
        var pileAt = new TileCoord(castle.At.X + 2, castle.At.Y);
        sim.World.Structures.Remove(pileAt);
        sim.World.AddStructure(new Stockpile(pileAt) { OwnerId = 0 });
        Assert.True(new SetHaulJobIntent(castle.At, pileAt, Resource.Food, HaulJobKind.Standing, 50)
            { PlayerId = 0 }.Resolve(sim).IsApplied);
        driver.Think(sim, sim.Now);
        sim.Run(until: sim.Now);

        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++) projector.Project(sim, sim.Now, playerId: 0, reveal: false);
        Assert.Equal(before, Snapshot.Hash(sim));
    }
}
