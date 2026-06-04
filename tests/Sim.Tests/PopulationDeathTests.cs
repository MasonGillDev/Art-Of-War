using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M8 Phase B: lifespan rolled once at unit creation, materialized to
// DeathTick, scheduled as a DeathByAgeEvent, M4-regenerable on snapshot
// restore.
public class PopulationDeathTests
{
    private static GenesisSpec MakeSpec(int unitCount = 1, int startingAge = 30, PopulationConfig? config = null)
    {
        var spawns = new List<UnitSpawn>();
        for (var i = 1; i <= unitCount; i++)
            spawns.Add(new UnitSpawn(i, new TileCoord(0, 0), UnitRole.Builder));
        return new GenesisSpec
        {
            Width = 10, Height = 10,
            Population = config ?? new PopulationConfig(),
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    StartingAgeYears = startingAge,
                    UnitSpawns = spawns,
                },
            },
        };
    }

    [Fact]
    public void SpecCtor_RollsLifespan_ForEveryGenesisUnit()
    {
        var sim = new Simulation(MakeSpec(unitCount: 5), seed: 0xA8E);
        foreach (var u in sim.World.Units.Values)
        {
            Assert.NotNull(u.DeathTick);
            Assert.NotNull(u.DeathSeq);
        }
    }

    [Fact]
    public void ScheduleLifespan_RollsWithinConfiguredBand()
    {
        var cfg = new PopulationConfig(
            TicksPerYear: 100,
            MinTrainAge: 15, MinFertileAge: 18, MaxFertileAge: 40,
            GestationTicks: 300, BirthFoodCost: 20,
            LifespanMinYears: 50, LifespanMaxYears: 80);
        var sim = new Simulation(MakeSpec(unitCount: 30, startingAge: 0, config: cfg), seed: 0xA8E);
        foreach (var u in sim.World.Units.Values)
        {
            var lifespanTicks = u.DeathTick!.Value - u.BornTick;
            var lifespanYears = lifespanTicks / cfg.TicksPerYear;
            Assert.InRange(lifespanYears, cfg.LifespanMinYears, cfg.LifespanMaxYears);
        }
    }

    [Fact]
    public void LifespansVary_NoSyncedCliff()
    {
        // 20 units born the same tick — at least 5 distinct DeathTicks
        // proves variability.
        var sim = new Simulation(MakeSpec(unitCount: 20), seed: 0xA8E);
        var distinctDeathTicks = sim.World.Units.Values.Select(u => u.DeathTick!.Value).Distinct().Count();
        Assert.True(distinctDeathTicks >= 5,
            $"expected at least 5 distinct death ticks; got {distinctDeathTicks}");
    }

    [Fact]
    public void UnitDies_AtExactDeathTick()
    {
        // Configure a tight lifespan band so the test runs quickly.
        var cfg = new PopulationConfig(
            TicksPerYear: 10,
            MinTrainAge: 15, MinFertileAge: 18, MaxFertileAge: 40,
            GestationTicks: 50, BirthFoodCost: 5,
            LifespanMinYears: 50, LifespanMaxYears: 50);
        var sim = new Simulation(MakeSpec(unitCount: 1, startingAge: 0, config: cfg), seed: 0xA8E);
        var u = sim.World.Units[1];
        var death = u.DeathTick!.Value;

        sim.Run(until: death - 1);
        Assert.True(sim.World.Units.ContainsKey(1), $"unit should still exist at {death - 1}");
        sim.Run(until: death);
        Assert.False(sim.World.Units.ContainsKey(1), $"unit should be gone at {death}");
    }

    [Fact]
    public void DeathByAge_PathReusesOnUnitDeath_DropsCargo()
    {
        // A unit carrying cargo dies of old age; cargo drops to its tile
        // (the M7 OnUnitDeath pipeline).
        var cfg = new PopulationConfig(
            TicksPerYear: 10,
            MinTrainAge: 15, MinFertileAge: 18, MaxFertileAge: 40,
            GestationTicks: 50, BirthFoodCost: 5,
            LifespanMinYears: 10, LifespanMaxYears: 10);
        var sim = new Simulation(MakeSpec(unitCount: 1, startingAge: 0, config: cfg), seed: 0xA8E);
        var u = sim.World.Units[1];
        u.CargoResource = Resource.Wood;
        u.CargoAmount = 3;
        var tile = u.Position;

        sim.Run(until: u.DeathTick!.Value);
        Assert.False(sim.World.Units.ContainsKey(1));
        Assert.True(sim.World.GroundResources.TryGetValue(tile, out var pile));
        Assert.Equal(3, pile[Resource.Wood]);
    }

    [Fact]
    public void Twin_LifespanScenario_HashesMatch()
    {
        Simulation Run() => new Simulation(MakeSpec(unitCount: 10), seed: 0xA8E);
        var a = Run();
        var b = Run();
        a.Run(until: 500);
        b.Run(until: 500);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    // M4 regen proof for aging — THE PHASE B CLOSURE GATE.
    [Fact]
    public void MidLife_SnapshotRoundTrip_DeathFiresAtSameTick()
    {
        var cfg = new PopulationConfig(
            TicksPerYear: 10,
            MinTrainAge: 15, MinFertileAge: 18, MaxFertileAge: 40,
            GestationTicks: 50, BirthFoodCost: 5,
            LifespanMinYears: 30, LifespanMaxYears: 30);
        var simA = new Simulation(MakeSpec(unitCount: 1, startingAge: 0, config: cfg), seed: 0xA8E);
        var deathTick = simA.World.Units[1].DeathTick!.Value;
        simA.Run(until: deathTick + 10);
        var hashA = Snapshot.Hash(simA);

        // Path B: snapshot mid-life, restore, run past death.
        var simB = new Simulation(MakeSpec(unitCount: 1, startingAge: 0, config: cfg), seed: 0xA8E);
        simB.Run(until: deathTick / 2);
        var bytes = Snapshot.Serialize(simB);
        var restored = Snapshot.Restore(bytes, seed: 0xA8E);
        Assert.Equal(deathTick, restored.World.Units[1].DeathTick);

        restored.Run(until: deathTick + 10);
        Assert.Equal(hashA, Snapshot.Hash(restored));
    }

    [Fact]
    public void LifespanFloor_GenesisUnitNearMaxAge_StillGetsOneTickToLive()
    {
        // A genesis unit configured at 80 years old with max lifespan 60
        // would roll DeathTick already in the past. The floor pushes it
        // to sim.Now + 1 so the event still schedules.
        var cfg = new PopulationConfig(
            TicksPerYear: 10,
            MinTrainAge: 15, MinFertileAge: 18, MaxFertileAge: 40,
            GestationTicks: 50, BirthFoodCost: 5,
            LifespanMinYears: 50, LifespanMaxYears: 60);
        var sim = new Simulation(MakeSpec(unitCount: 1, startingAge: 80, config: cfg), seed: 0xA8E);
        Assert.True(sim.World.Units[1].DeathTick > 0);
    }
}
