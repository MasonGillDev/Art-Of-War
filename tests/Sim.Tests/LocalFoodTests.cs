using Sim.Core.Engine;
using Sim.Core.Food;
using Sim.Core.Logistics;
using Sim.Core.Population;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M19 Phase 2 — the sink split (docs/m19-per-house-food-spec.md):
// citizens eat from their HOME's larder; a dry house runs the full
// famine-debt machinery against its own residents even when the castle
// is stocked (the harsh-doctrine lock). All expectations derive from
// FoodConsumptionConstants per the standing convention.
public class LocalFoodTests
{
    private const int Period = FoodConsumptionConstants.FoodConsumptionPeriod;

    private static readonly PopulationConfig Cfg = new(
        TicksPerYear: 1_000_000,   // ages frozen — no fertility/death noise
        MinTrainAge: 15,
        MinFertileAge: 18,
        MaxFertileAge: 40,
        GestationTicks: 50,
        BirthFoodCost: 5,
        LifespanMinYears: 500,
        LifespanMaxYears: 500);

    // Castle (0,0) stocked deep; units spawned ON the future house tile.
    // Ages staggered so "oldest resident first" is a real ordering.
    private static Simulation MakeWorld(int units, int castleFood = 500)
    {
        var spawns = new List<UnitSpawn>();
        for (var i = 0; i < units; i++)
            spawns.Add(new UnitSpawn(1 + i, new TileCoord(5, 5),
                StartingAgeYears: 40 - i));   // unit 1 is the oldest
        var spec = new GenesisSpec
        {
            Width = 12, Height = 12,
            Population = Cfg,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = new SortedDictionary<Resource, int>
                        { [Resource.Food] = castleFood },
                    StartingAgeYears = 30,
                    UnitSpawns = spawns.ToArray(),
                },
            },
        };
        return new Simulation(spec, seed: 0x10CA1);
    }

    private static House AddHouse(Simulation sim, int food, params int[] residentIds)
    {
        var house = (House)sim.World.AddStructure(new House(new TileCoord(5, 5)) { OwnerId = 0 });
        house.LastFoodConsumedTick = sim.Now;
        if (food > 0) house.Deposit(Resource.Food, food);
        foreach (var id in residentIds)
            Population.SetHome(sim, sim.World.Units[id], house.At);
        return house;
    }

    [Fact]
    public void Homes_SplitTheConsumption()
    {
        // 3 citizens: 2 housed, 1 at the castle. After N periods the
        // house has paid 2×N meals and the castle 1×N — the sinks split
        // exactly by residency.
        var sim = MakeWorld(units: 3, castleFood: 500);
        var house = AddHouse(sim, food: 60, 1, 2);
        var castle = sim.World.Structures.Values.OfType<Castle>().Single();

        const int periods = 5;
        var at = (long)periods * Period;
        sim.Run(until: at);

        // Pure read AT the horizon (sim.Now parks at the last event;
        // CurrentLevel takes the asking tick explicitly — the same
        // convention the balance lab uses).
        Assert.Equal(60 - 2 * periods, FoodConsumption.CurrentLevel(house, sim, at));
        Assert.Equal(500 - 1 * periods, FoodConsumption.CurrentLevel(castle, sim, at));
    }

    [Fact]
    public void LocalFamine_StarvesTheDistrict_NotTheRealm()
    {
        // The harsh-doctrine headline: the house runs dry and its OLDEST
        // resident dies after the grace window — while the castle, full
        // the whole time, never enters famine and its residents live.
        var sim = MakeWorld(units: 3, castleFood: 500);
        var house = AddHouse(sim, food: 4, 1, 2);   // 2 residents ⇒ dry in 2 periods
        var castle = sim.World.Structures.Values.OfType<Castle>().Single();

        // Past dry-out + grace + one death interval.
        var deadline = 3 * Period
            + FoodConsumptionConstants.StarvationStartDelay
            + FoodConsumptionConstants.StarvationDeathInterval;
        sim.Run(until: deadline);

        Assert.True(house.FamineStartTick.HasValue, "house famine never flagged");
        Assert.True(house.FoodDebt > 0, "house debt never accrued");
        Assert.False(sim.World.Units.ContainsKey(1),
            "the house's OLDEST resident should have starved first");
        Assert.True(sim.World.Units.ContainsKey(3), "the castle-homed citizen died");
        Assert.Null(castle.FamineStartTick);
        Assert.Equal(0, castle.FoodDebt);
        Assert.True(FoodConsumption.CurrentLevel(castle, sim, sim.Now) > 0,
            "the realm's larder should be untouched by the district famine");
    }

    [Fact]
    public void Deposit_PaysTheHomesDebtFirst()
    {
        var sim = MakeWorld(units: 3, castleFood: 500);
        var house = AddHouse(sim, food: 4, 1, 2);

        // Run into famine (debt accrues at rate 2/period past the stock).
        sim.Run(until: 6 * Period);
        FoodConsumption.CatchUp(house, sim, sim.Now);
        var debt = house.FoodDebt;
        Assert.True(debt > 0, "fixture: house must be in debt");

        // A deposit smaller than the debt pays it down and restocks NOTHING.
        var partial = debt - 1;
        CargoTransfer.DepositInto(sim, house, Resource.Food, partial);
        Assert.Equal(1, house.FoodDebt);
        Assert.Equal(0, house.AmountOf(Resource.Food));
        Assert.True(house.FamineStartTick.HasValue, "partial payment must not end the famine");

        // Paying the rest ends the famine and clears the death anchor.
        CargoTransfer.DepositInto(sim, house, Resource.Food, 11);
        Assert.Equal(0, house.FoodDebt);
        Assert.Equal(10, house.AmountOf(Resource.Food));
        Assert.Null(house.FamineStartTick);
        Assert.Null(house.NextStarvationDeathTick);
    }

    [Fact]
    public void Recovery_MidLocalFamine_Identical()
    {
        // The M4 contract for the per-home machinery: snapshot in the
        // middle of a house famine (debt accrued, death scheduled),
        // restore, run both to the same horizon — byte-identical hashes.
        Simulation Build()
        {
            var s = MakeWorld(units: 3, castleFood: 500);
            AddHouse(s, food: 4, 1, 2);
            return s;
        }
        var horizon = 3 * Period
            + FoodConsumptionConstants.StarvationStartDelay
            + 2 * FoodConsumptionConstants.StarvationDeathInterval;

        var live = Build();
        live.Run(until: 4 * Period + FoodConsumptionConstants.StarvationStartDelay / 2);
        var restored = Snapshot.Restore(Snapshot.Serialize(live), seed: 0x10CA1);

        live.Run(until: horizon);
        restored.Run(until: horizon);
        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(restored));
    }
}
