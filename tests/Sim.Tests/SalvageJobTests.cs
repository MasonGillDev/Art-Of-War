using Sim.Core;
using Sim.Core.Caches;
using Sim.Core.Engine;
using Sim.Core.Hauling;
using Sim.Core.Scouting;
using Sim.Core.World;
using Sim.Server.Hauling;

namespace Sim.Tests;

// M40 (docs/salvage.md): a salvage job carries EVERYTHING from a secret or a
// pile that is nobody's to one of the player's buildings. The source must be
// charted (a returned scout reported it) or in live sight. The driver keeps up
// to the job's crew of haulers on it and never peeks at the source; the job
// ends when a hauler arrives and finds nothing, which is also when its eyes
// strike the chart.
public class SalvageJobTests
{
    private static readonly TileCoord Seat = new(5, 5);
    private static readonly TileCoord CacheAt = new(5, 30);

    private static (Simulation sim, Cache cache) MakeSim(int haulers = 2)
    {
        var world = new GameWorld(new TileGrid(60, 60, Biome.Grassland));
        world.Players[0] = new Player(0);
        world.Players[CacheConstants.OwnerId] = new Player(CacheConstants.OwnerId);
        world.AddStructure(new Castle(Seat) { OwnerId = 0 });
        var cache = world.AddStructure(new Cache(CacheAt) { OwnerId = CacheConstants.OwnerId });
        cache.Deposit(Resource.CopperOre, 30);
        cache.Deposit(Resource.BronzeSword, 1);
        for (var i = 0; i < haulers; i++)
            world.AddUnit(new Unit(world.NextUnitId++, Seat) { OwnerId = 0, Role = UnitRole.Hauler });
        return (new Simulation(world, seed: 1), cache);
    }

    private static void Chart(Simulation sim, TileCoord tile) =>
        sim.World.Charts[0] = new SortedDictionary<TileCoord, ChartEntry>(TileOrder.Instance)
        {
            [tile] = new ChartEntry { Tile = tile, Hint = SecretHint.Glint, SeenTick = 0 },
        };

    private static bool Set(Simulation sim, TileCoord source, Resource resource = Resource.None, int crew = 2, HaulJobKind kind = HaulJobKind.Salvage)
    {
        sim.SubmitIntent(sim.Now, new SetHaulJobIntent(source, Seat, resource, kind, crew) { PlayerId = 0 });
        sim.Run(until: sim.Now);
        return sim.ResolvedLog[^1].Outcome.IsApplied;
    }

    private static void RunDriver(Simulation sim, long days)
    {
        var driver = new HaulingDriver(new HaulingConfig { ThinkPeriodTicks = Time.Hour });
        var until = sim.Now + days * Time.Day;
        for (var t = sim.Now; t <= until; t += Time.Hour)
        {
            sim.Run(until: t);
            driver.Think(sim, t);
        }
    }

    private static int Held(Simulation sim, Resource r) => ((Castle)sim.World.Structures[Seat]).AmountOf(r);

    // -------- who may salvage what --------

    [Fact]
    public void ACacheYouNeitherChartedNorSee_CannotBeSalvaged()
    {
        var (sim, _) = MakeSim();
        Assert.False(Set(sim, CacheAt));
        Assert.Contains("chart it, or see it", sim.ResolvedLog[^1].Outcome.Reason);
    }

    [Fact]
    public void ACacheYouCharted_OrCanSee_Can()
    {
        var (charted, _) = MakeSim();
        Chart(charted, CacheAt);
        Assert.True(Set(charted, CacheAt));

        var (seen, _) = MakeSim();
        seen.World.AddUnit(new Unit(seen.World.NextUnitId++, new TileCoord(CacheAt.X, CacheAt.Y - 1)) { OwnerId = 0 });
        Assert.True(Set(seen, CacheAt));
    }

    [Fact]
    public void SalvageTakesEverything_AndOnlyWhatIsNobodys()
    {
        var (sim, _) = MakeSim();
        Chart(sim, CacheAt);
        Assert.False(Set(sim, CacheAt, resource: Resource.CopperOre));                          // names no resource
        Assert.False(Set(sim, CacheAt, crew: 0));
        Assert.False(Set(sim, CacheAt, crew: HaulingConstants.MaxSalvageCrew + 1));
        var yours = new TileCoord(9, 9);
        sim.World.AddStructure(new Stockpile(yours) { OwnerId = 0 });
        Assert.False(Set(sim, yours));                                                    // a building is not salvage
    }

    // -------- the salvage, run by the queue --------

    [Fact]
    public void AChartedCache_IsCarriedHomeWhole_ThenTheJobEnds_AndTheChartIsStruck()
    {
        var (sim, _) = MakeSim();
        Chart(sim, CacheAt);
        Assert.True(Set(sim, CacheAt));

        RunDriver(sim, days: 12);

        Assert.Equal(30, Held(sim, Resource.CopperOre));
        Assert.Equal(1, Held(sim, Resource.BronzeSword));
        Assert.False(sim.World.Structures.ContainsKey(CacheAt));
        Assert.Empty(sim.World.HaulJobs);
        Assert.Equal(ChartState.Gone, sim.World.Charts[0][CacheAt].State);
    }

    [Fact]
    public void APileInPlainSight_IsCarriedHome()
    {
        var (sim, _) = MakeSim();
        var spill = new TileCoord(8, 20);
        sim.World.GroundResources[spill] = new SortedDictionary<Resource, int> { [Resource.Bronze] = 12, [Resource.Stone] = 7 };
        sim.World.AddUnit(new Unit(sim.World.NextUnitId++, new TileCoord(8, 19)) { OwnerId = 0 });   // watching it
        Assert.True(Set(sim, spill));

        RunDriver(sim, days: 8);

        Assert.Equal(12, Held(sim, Resource.Bronze));
        Assert.Equal(7, Held(sim, Resource.Stone));
        Assert.False(sim.World.GroundResources.ContainsKey(spill));
        Assert.Empty(sim.World.HaulJobs);
    }

    [Fact]
    public void ACacheSomeoneElseEmptied_EndsTheJob_WhenTheHaulersGetThere()
    {
        var (sim, cache) = MakeSim();
        Chart(sim, CacheAt);
        Assert.True(Set(sim, CacheAt));
        // A rival strips it behind the fog before anyone arrives.
        var thief = sim.World.AddUnit(new Unit(sim.World.NextUnitId++, CacheAt) { OwnerId = 7, Role = UnitRole.Hauler });
        while (sim.World.Structures.ContainsKey(CacheAt)) { thief.Cargo.Clear(); CacheLooting.TryLoot(sim, thief, cache.Holdings.Keys.First()); }
        Assert.Single(sim.World.HaulJobs);   // the player doesn't know yet

        RunDriver(sim, days: 8);

        Assert.Empty(sim.World.HaulJobs);
        Assert.Equal(0, Held(sim, Resource.CopperOre));
        Assert.Equal(ChartState.Gone, sim.World.Charts[0][CacheAt].State);
    }

    [Fact]
    public void TheQueue_KeepsTheCrewAtWork_NoMore()
    {
        var (sim, _) = MakeSim(haulers: 4);
        Chart(sim, CacheAt);
        Assert.True(Set(sim, CacheAt, crew: 2));
        var driver = new HaulingDriver(new HaulingConfig { ThinkPeriodTicks = Time.Hour });
        sim.Run(until: Time.Hour);
        driver.Think(sim, Time.Hour);
        sim.Run(until: Time.Hour);

        var onIt = sim.World.Units.Values.Count(u => u.HaulPlan is { JobId: > 0 });
        Assert.Equal(2, onIt);
    }
}
