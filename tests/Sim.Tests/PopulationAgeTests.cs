using System.Reflection;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M8 Phase A: age is a pure derivation from BornTick. No global age
// sweep; no mutable Age field; gates evaluate at thresholds; snapshot
// round-trips the new state.
public class PopulationAgeTests
{
    private static GameWorld MakeWorld(int startingAgeYears = 30, PopulationConfig? config = null)
    {
        return Genesis.Build(new GenesisSpec
        {
            Width = 10, Height = 10,
            Population = config ?? new PopulationConfig(),
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    StartingAgeYears = startingAgeYears,
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(0, 0), UnitRole.Builder),
                    },
                },
            },
        });
    }

    [Fact]
    public void Genesis_Default_StartingAge_IsBakedIntoBornTick()
    {
        var world = MakeWorld(startingAgeYears: 30);
        var cfg = world.PopulationConfig;
        // At sim.Now = 0, age = (0 - BornTick) / TicksPerYear should equal 30.
        // Use cfg.TicksPerYear rather than a literal so this test stays
        // correct as the default tuning evolves.
        Assert.Equal(-30 * cfg.TicksPerYear, world.Units[1].BornTick);
        Assert.Equal(30, Population.AgeYears(world.Units[1], now: 0, cfg));
    }

    [Fact]
    public void AgeYears_DerivesFromBornTick_AcrossTicks()
    {
        var world = MakeWorld(startingAgeYears: 20);
        var u = world.Units[1];
        var cfg = world.PopulationConfig;
        var bornTickBefore = u.BornTick;
        // Advance simulated time WITHOUT mutating any per-unit field.
        Assert.Equal(20, Population.AgeYears(u, now: 0, cfg));
        Assert.Equal(25, Population.AgeYears(u, now: 5 * cfg.TicksPerYear, cfg));
        Assert.Equal(30, Population.AgeYears(u, now: 10 * cfg.TicksPerYear, cfg));
        // BornTick is immutable — derived age changed but state did not.
        Assert.Equal(bornTickBefore, u.BornTick);
    }

    [Fact]
    public void CanTrain_GatesAtMinTrainAge_Inclusive()
    {
        var world = MakeWorld();
        var u = world.Units[1];
        var cfg = world.PopulationConfig;
        long T(int years) => u.BornTick + years * cfg.TicksPerYear;
        // Gate is inclusive at cfg.MinTrainAge — derive from config (like
        // CanBreed below) so this stays correct as the train age is retuned.
        Assert.False(Population.CanTrain(u, T(cfg.MinTrainAge - 1), cfg));
        Assert.True(Population.CanTrain(u, T(cfg.MinTrainAge), cfg));
    }

    [Fact]
    public void CanBreed_GatesAtMin_AndAtMax_Inclusive()
    {
        var world = MakeWorld();
        var u = world.Units[1];
        var cfg = world.PopulationConfig;
        long T(int years) => u.BornTick + years * cfg.TicksPerYear;
        // Bounds use cfg.MinFertileAge / MaxFertileAge so the test stays
        // correct as the default fertility window is retuned.
        Assert.False(Population.CanBreed(u, T(cfg.MinFertileAge - 1), cfg));
        Assert.True(Population.CanBreed(u, T(cfg.MinFertileAge), cfg));
        Assert.True(Population.CanBreed(u, T(cfg.MaxFertileAge), cfg));
        Assert.False(Population.CanBreed(u, T(cfg.MaxFertileAge + 1), cfg));
    }

    [Fact]
    public void Snapshot_RoundTrips_BornTick_AndConfig()
    {
        var world = MakeWorld(startingAgeYears: 25,
            config: new PopulationConfig(
                TicksPerYear: 50,
                MinTrainAge: 12,
                MinFertileAge: 16,
                MaxFertileAge: 35,
                GestationTicks: 200,
                BirthFoodCost: 15,
                LifespanMinYears: 40,
                LifespanMaxYears: 60));
        var sim = new Simulation(world, seed: 0xA8E);
        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xA8E);

        Assert.Equal(world.Units[1].BornTick, restored.World.Units[1].BornTick);
        Assert.Equal(50, restored.World.PopulationConfig.TicksPerYear);
        Assert.Equal(12, restored.World.PopulationConfig.MinTrainAge);
        Assert.Equal(60, restored.World.PopulationConfig.LifespanMaxYears);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void Unit_HasNoMutableAgeField()
    {
        // Sentinel: the M8 invariant is "age is derived, never stored."
        // A future change adding a public mutable Age property would silently
        // break this. Guard via reflection so the test fails loudly.
        var prop = typeof(Unit).GetProperty("Age", BindingFlags.Public | BindingFlags.Instance);
        Assert.Null(prop);
    }

    [Fact]
    public void Unit_BornTickOverride_PerUnit_Wins()
    {
        var world = Genesis.Build(new GenesisSpec
        {
            Width = 10, Height = 10,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    StartingAgeYears = 30,
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(0, 0), UnitRole.Builder),
                        new UnitSpawn(2, new TileCoord(0, 0), UnitRole.Builder, StartingAgeYears: 5),
                    },
                },
            },
        });
        // Unit 1 inherits faction (30); unit 2 overrides to 5.
        Assert.Equal(30, Population.AgeYears(world.Units[1], now: 0, world.PopulationConfig));
        Assert.Equal(5, Population.AgeYears(world.Units[2], now: 0, world.PopulationConfig));
    }
}
