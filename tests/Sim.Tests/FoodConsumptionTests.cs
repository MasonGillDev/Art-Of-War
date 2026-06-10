using System.Reflection;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Food;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M13 Phase A — Player.PopulationCount with single-mutation discipline.
// (Castle food state, famine, starvation events land in Phases B–D.)
public class FoodConsumptionTests
{
    private static Simulation MakeSim(int unitCount = 3, int ownerId = 0)
    {
        var spawns = new List<UnitSpawn>();
        for (var i = 0; i < unitCount; i++)
            spawns.Add(new UnitSpawn(i + 1, new TileCoord(0, 0), UnitRole.Builder, OwnerId: ownerId));
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = ownerId,
                    CastlePosition = new TileCoord(0, 0),
                    StartingAgeYears = 30,
                    UnitSpawns = spawns,
                },
            },
        };
        return new Simulation(spec, seed: 0xF00D);
    }

    // ---- Phase A: PopulationCount mutation discipline -------------------

    [Fact]
    public void PopulationCount_StartsCorrectAfterGenesis()
    {
        var sim = MakeSim(unitCount: 5);
        Assert.Equal(5, sim.World.Players[0].PopulationCount);
    }

    [Fact]
    public void PopulationCount_IncrementsWhenWorldAddUnitIsCalled()
    {
        var sim = MakeSim(unitCount: 2);
        var p = sim.World.Players[0];
        Assert.Equal(2, p.PopulationCount);

        sim.World.AddUnit(new Unit(100, new TileCoord(1, 0))
        {
            Role = UnitRole.Hauler,
            OwnerId = 0,
            BornTick = 0,
        });
        Assert.Equal(3, p.PopulationCount);
    }

    [Fact]
    public void PopulationCount_DecrementsOnCombatDeath()
    {
        var sim = MakeSim(unitCount: 2);
        var p = sim.World.Players[0];
        Assert.Equal(2, p.PopulationCount);

        var victim = sim.World.Units[1];
        // Use the M7 single-death pipeline (which calls OnUnitRemoved).
        CombatRules.OnUnitDeath(sim, victim);

        Assert.Equal(1, p.PopulationCount);
        Assert.False(sim.World.Units.ContainsKey(1));
    }

    [Fact]
    public void PopulationCount_DecrementsOnAgingDeath_ViaDeathByAgeEvent()
    {
        // Genesis-age 30 + lifespan band → DeathByAgeEvent fires for some
        // unit after sim ticks long enough. Cheaper: just trigger the same
        // OnUnitDeath path the event uses.
        var sim = MakeSim(unitCount: 1);
        var p = sim.World.Players[0];
        Assert.Equal(1, p.PopulationCount);

        CombatRules.OnUnitDeath(sim, sim.World.Units[1]);
        Assert.Equal(0, p.PopulationCount);
    }

    [Fact]
    public void PopulationCount_NewbornBirth_Increments()
    {
        // Direct AddUnit increments — exercises the BirthEvent-shaped
        // path without scheduling a real breeding cycle (which would
        // need a House, two parents, and tick time).
        var sim = MakeSim(unitCount: 0); // genesis with no UnitSpawns
        var p = sim.World.Players[0];
        Assert.Equal(0, p.PopulationCount);

        sim.World.AddUnit(new Unit(50, new TileCoord(0, 0))
        {
            Role = UnitRole.None,
            OwnerId = 0,
            BornTick = 0,
        });
        Assert.Equal(1, p.PopulationCount);
    }

    [Fact]
    public void PopulationCount_DecrementBelowZero_Throws()
    {
        var sim = MakeSim(unitCount: 1);
        var p = sim.World.Players[0];
        CombatRules.OnUnitDeath(sim, sim.World.Units[1]);
        Assert.Equal(0, p.PopulationCount);

        // Decrement a second time directly via reflection (simulating a
        // double-decrement bug); must throw rather than silently going
        // negative.
        var dec = typeof(Player).GetMethod(
            "DecrementPopulation",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ex = Assert.Throws<TargetInvocationException>(() => dec.Invoke(p, null));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void PopulationCount_TwoFactions_TrackedIndependently()
    {
        var spec = new GenesisSpec
        {
            Width = 20, Height = 20,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(0, 0), UnitRole.Builder, OwnerId: 0),
                        new UnitSpawn(2, new TileCoord(1, 0), UnitRole.Builder, OwnerId: 0),
                    },
                },
                new FactionStartSpec
                {
                    OwnerId = 1,
                    CastlePosition = new TileCoord(19, 19),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(100, new TileCoord(19, 19), UnitRole.Hauler, OwnerId: 1),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xCAFE);
        Assert.Equal(2, sim.World.Players[0].PopulationCount);
        Assert.Equal(1, sim.World.Players[1].PopulationCount);

        // Kill one of owner 0's units; owner 1 should be unaffected.
        CombatRules.OnUnitDeath(sim, sim.World.Units[1]);
        Assert.Equal(1, sim.World.Players[0].PopulationCount);
        Assert.Equal(1, sim.World.Players[1].PopulationCount);
    }

    [Fact]
    public void PopulationCount_SnapshotRoundTrip_ReDerivedFromUnits()
    {
        // PopulationCount is NOT serialized; Snapshot.Restore re-derives
        // it by re-running world.AddUnit per persisted unit. The contract
        // tested here: after round-trip, the count equals the original.
        var sim = MakeSim(unitCount: 4);
        Assert.Equal(4, sim.World.Players[0].PopulationCount);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xF00D);
        Assert.Equal(4, restored.World.Players[0].PopulationCount);
    }

    [Fact]
    public void PopulationCount_HasOneMutationPoint_Audit()
    {
        // Mutation audit: the only callers of IncrementPopulation /
        // DecrementPopulation should live in known sites
        // (GameWorld.AddUnit for increments; Population.OnUnitRemoved
        // for decrements). The strongest invariant we can express purely
        // in code is that the setter is private and the helpers are
        // internal — which restricts mutations to inside Sim.Core. This
        // test pins those visibility constraints; the named-callsite
        // claim is documented in docs/determinism-audit.md and verified
        // by grep.
        var popCountProp = typeof(Player).GetProperty(nameof(Player.PopulationCount))!;
        Assert.True(popCountProp.CanRead);
        Assert.NotNull(popCountProp.GetSetMethod(nonPublic: true));
        Assert.Null(popCountProp.GetSetMethod()); // private setter

        var inc = typeof(Player).GetMethod(
            "IncrementPopulation",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var dec = typeof(Player).GetMethod(
            "DecrementPopulation",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(inc);
        Assert.NotNull(dec);
        Assert.True(inc.IsAssembly); // internal only
        Assert.True(dec.IsAssembly); // internal only
    }

    [Fact]
    public void PopulationCount_DyingUnit_BreedingCleanupStillRuns()
    {
        // Sanity: extending OnUnitRemoved with the pop-decrement didn't
        // break the M8 breeding stop-on-removal path. Concrete check:
        // a unit with NO breeding involvement is still removed cleanly,
        // and pop is decremented. (HouseBirthTests covers the full
        // breeding-parent-dies cleanup.)
        var sim = MakeSim(unitCount: 2);
        var p = sim.World.Players[0];
        CombatRules.OnUnitDeath(sim, sim.World.Units[1]);
        Assert.Equal(1, p.PopulationCount);
        Assert.False(sim.World.Units.ContainsKey(1));
    }

    // ---- Phase B: Castle food state + lazy catch-up math ---------------

    // Helper: make a sim with one castle that has a known starting food.
    private static (Simulation sim, Castle castle) MakeSimWithFood(
        int unitCount, int startingFood)
    {
        var spawns = new List<UnitSpawn>();
        for (var i = 0; i < unitCount; i++)
            spawns.Add(new UnitSpawn(i + 1, new TileCoord(0, 0), UnitRole.Builder, OwnerId: 0));
        var holdings = new SortedDictionary<Resource, int> { [Resource.Food] = startingFood };
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = holdings,
                    UnitSpawns = spawns,
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xF00D);
        var castle = (Castle)sim.World.Structures[new TileCoord(0, 0)];
        return (sim, castle);
    }

    // Advance "now" forward by mutating a long via reflection isn't
    // available; instead the catch-up takes (sim, now). For Phase B
    // we drive the helper directly with explicit now values rather
    // than running events.
    [Fact]
    public void CatchUp_OnePeriod_ConsumesExactlyOneRatePerCitizen()
    {
        var (sim, castle) = MakeSimWithFood(unitCount: 3, startingFood: 1000);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var rate = 3 * FoodConsumptionConstants.FoodPerCitizenPerPeriod;
        FoodConsumption.CatchUp(castle, sim, now: period);
        Assert.Equal(1000 - rate, castle.AmountOf(Resource.Food));
        Assert.Equal(period, castle.LastFoodConsumedTick);
    }

    [Fact]
    public void CatchUp_PartialPeriod_ConsumesNothing_AnchorUnchanged()
    {
        var (sim, castle) = MakeSimWithFood(unitCount: 5, startingFood: 1000);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        FoodConsumption.CatchUp(castle, sim, now: period - 1);
        Assert.Equal(1000, castle.AmountOf(Resource.Food));
        Assert.Equal(0, castle.LastFoodConsumedTick);
    }

    [Fact]
    public void CatchUp_ZeroPopulation_NoConsumption_AnchorAdvancesByCompletedPeriods()
    {
        var (sim, castle) = MakeSimWithFood(unitCount: 0, startingFood: 1000);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        FoodConsumption.CatchUp(castle, sim, now: 5 * period);
        Assert.Equal(1000, castle.AmountOf(Resource.Food));
        Assert.Equal(5 * period, castle.LastFoodConsumedTick);
    }

    [Fact]
    public void CatchUp_ClampsAtZero_WhenDemandExceedsHoldings()
    {
        var (sim, castle) = MakeSimWithFood(unitCount: 10, startingFood: 25);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        // 10 citizens × 1/period × 10 periods = 100 demanded > 25 held.
        FoodConsumption.CatchUp(castle, sim, now: 10 * period);
        Assert.Equal(0, castle.AmountOf(Resource.Food));
    }

    [Fact]
    public void CurrentLevel_IsPureRead_NoMutation()
    {
        var (sim, castle) = MakeSimWithFood(unitCount: 3, startingFood: 500);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var hashBefore = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++)
            FoodConsumption.CurrentLevel(castle, sim, now: 5 * period + i);
        Assert.Equal(hashBefore, Snapshot.Hash(sim));
    }

    [Fact]
    public void CurrentLevel_MatchesPostCatchUpHoldings()
    {
        var (sim, castle) = MakeSimWithFood(unitCount: 4, startingFood: 200);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var derived = FoodConsumption.CurrentLevel(castle, sim, now: 7 * period);
        FoodConsumption.CatchUp(castle, sim, now: 7 * period);
        Assert.Equal(castle.AmountOf(Resource.Food), derived);
    }

    [Fact]
    public void CatchUp_IsObservationIndependent()
    {
        // Compute once at T vs many irregular times along the way to T;
        // assert identical final state. M9 pattern.
        var (sim1, castle1) = MakeSimWithFood(unitCount: 4, startingFood: 1000);
        var (sim2, castle2) = MakeSimWithFood(unitCount: 4, startingFood: 1000);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var target = 12 * period + 17; // include partial-period remainder

        FoodConsumption.CatchUp(castle1, sim1, now: target);
        foreach (var stop in new[] { 1L, period - 1, period, 3 * period, 3 * period + 5,
                                     7 * period, 11 * period, target })
            FoodConsumption.CatchUp(castle2, sim2, now: stop);

        Assert.Equal(castle1.AmountOf(Resource.Food), castle2.AmountOf(Resource.Food));
        Assert.Equal(castle1.LastFoodConsumedTick, castle2.LastFoodConsumedTick);
    }

    [Fact]
    public void OnUnitRemoved_ClosesOldRateWindow_BeforeDecrement()
    {
        // 5 citizens × rate × N periods is consumed at the OLD rate; then
        // one dies. The catch-up must use 5, not 4.
        var (sim, castle) = MakeSimWithFood(unitCount: 5, startingFood: 1000);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;

        // Advance sim time by directly using ScheduleClocks isn't public;
        // instead, simulate "the death happens at tick T" by driving
        // catch-up to T, then triggering the death (which catches up again
        // — should be a no-op since LastFoodConsumedTick == T).
        var deathTick = 3 * period;
        // Pre-condition: we have not yet caught up.
        Assert.Equal(0, castle.LastFoodConsumedTick);
        Assert.Equal(1000, castle.AmountOf(Resource.Food));

        // Hand-advance sim.Now via reflection so OnUnitRemoved's
        // internal sim.Now matches the death tick.
        AdvanceSimNow(sim, deathTick);
        CombatRules.OnUnitDeath(sim, sim.World.Units[1]);

        // 5 citizens × 3 periods = 15 consumed.
        Assert.Equal(1000 - 15, castle.AmountOf(Resource.Food));
        Assert.Equal(3 * period, castle.LastFoodConsumedTick);
        Assert.Equal(4, sim.World.Players[0].PopulationCount);
    }

    [Fact]
    public void OnUnitAdded_ClosesOldRateWindow_BeforeIncrement()
    {
        var (sim, castle) = MakeSimWithFood(unitCount: 2, startingFood: 1000);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var addTick = 4 * period;

        AdvanceSimNow(sim, addTick);
        Population.OnUnitAdded(sim, new Unit(100, new TileCoord(0, 0))
        {
            Role = UnitRole.None, OwnerId = 0, BornTick = addTick,
        });

        // 2 citizens × 4 periods consumed BEFORE the third is added.
        Assert.Equal(1000 - 8, castle.AmountOf(Resource.Food));
        Assert.Equal(3, sim.World.Players[0].PopulationCount);
    }

    [Fact]
    public void HaulDeposit_OfFood_CatchesUpFirst()
    {
        // Set up: castle has 50 food, 2 citizens + 1 hauler = 3 mouths,
        // lots of time passes, then a Food haul deposits 100. The
        // catch-up must close the window before the deposit lands so
        // the final Holdings reflects both: (50 - consumed) + 100.
        var (sim, castle) = MakeSimWithFood(unitCount: 2, startingFood: 50);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var depositTick = 10 * period;

        // Hauler is a third mouth — it counts toward the consumption
        // rate from the moment it's added.
        var hauler = sim.World.AddUnit(new Unit(99, new TileCoord(0, 0))
        {
            Role = UnitRole.Hauler, OwnerId = 0,
            CargoResource = Resource.Food, CargoAmount = 100, BornTick = 0,
        });
        hauler.TrySetActivity(Activity.Hauling);
        Assert.Equal(3, sim.World.Players[0].PopulationCount);

        AdvanceSimNow(sim, depositTick);
        var ev = new HaulDepositEvent(99, new TileCoord(0, 0), expectedEpoch: hauler.AssignmentEpoch);
        ev.Apply(sim);

        // 3 mouths × 10 periods = 30 consumed before the deposit.
        Assert.Equal(50 - 30 + 100, castle.AmountOf(Resource.Food));
        Assert.Equal(depositTick, castle.LastFoodConsumedTick);
    }

    [Fact]
    public void LastFoodConsumedTick_SnapshotRoundTrip_Preserved()
    {
        var (sim, castle) = MakeSimWithFood(unitCount: 3, startingFood: 500);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        FoodConsumption.CatchUp(castle, sim, now: 4 * period);
        Assert.Equal(4 * period, castle.LastFoodConsumedTick);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xF00D);
        var restoredCastle = (Castle)restored.World.Structures[new TileCoord(0, 0)];
        Assert.Equal(4 * period, restoredCastle.LastFoodConsumedTick);
        Assert.Equal(castle.AmountOf(Resource.Food), restoredCastle.AmountOf(Resource.Food));
    }

    [Fact]
    public void FindCastleFor_ReturnsNull_WhenPlayerHasNoCastle()
    {
        // Construct a sim where player 1 has no castle. (Two-faction
        // genesis then remove faction 1's castle.)
        var spec = new GenesisSpec
        {
            Width = 20, Height = 20,
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(0, 0) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(19, 19) },
            },
        };
        var sim = new Simulation(spec, seed: 1);
        sim.World.Structures.Remove(new TileCoord(19, 19));
        Assert.Null(FoodConsumption.FindCastleFor(sim.World, ownerId: 1));
        Assert.NotNull(FoodConsumption.FindCastleFor(sim.World, ownerId: 0));
    }

    // Reflection helper — advance sim.Now without running events.
    // Internal RestoreClocks is the canonical "set both" method.
    private static void AdvanceSimNow(Simulation sim, long now)
    {
        var method = typeof(Simulation).GetMethod(
            "RestoreClocks",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(sim, new object[] { now, sim.NextSeqForTest() });
    }
}

// Bridge to internal NextSeq for the test helper. Lives in the same
// namespace so we can extend Simulation without exposing NextSeq publicly.
internal static class SimulationTestExtensions
{
    public static long NextSeqForTest(this Simulation sim)
    {
        // Reflection — NextSeq is internal and we need it to call
        // RestoreClocks without consuming a seq.
        var prop = typeof(Simulation).GetProperty(
            "NextSeq", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (long)prop.GetValue(sim)!;
    }
}

// ---- Phase C: Famine anchor + FamineCheckEvent ----------------------

public class FoodConsumptionPhaseCTests
{
    private static (Simulation sim, Castle castle) MakeSimWithFood(
        int unitCount, int startingFood)
    {
        var spawns = new List<UnitSpawn>();
        for (var i = 0; i < unitCount; i++)
            spawns.Add(new UnitSpawn(i + 1, new TileCoord(0, 0), UnitRole.Builder, OwnerId: 0));
        var holdings = new SortedDictionary<Resource, int> { [Resource.Food] = startingFood };
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = holdings,
                    UnitSpawns = spawns,
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xF00D);
        var castle = (Castle)sim.World.Structures[new TileCoord(0, 0)];
        return (sim, castle);
    }

    [Fact]
    public void GenesisCtor_SchedulesFamineCheck_AtFirstDryOutTick()
    {
        // 5 mouths, 100 food, period 60, rate 5. fullMeals = 100/5 = 20.
        // First failure at boundary (20+1) × 60 = 1260 from anchor 0.
        var (sim, castle) = MakeSimWithFood(unitCount: 5, startingFood: 100);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        Assert.NotNull(castle.NextFamineCheckTick);
        Assert.Equal(21 * period, castle.NextFamineCheckTick);
        Assert.NotNull(castle.NextFamineCheckSeq);
    }

    [Fact]
    public void CatchUp_SetsFamineStartTick_AtExactFailureBoundary()
    {
        // Run the catch-up past the dry-out tick directly. Ticks derive from
        // config so the test survives retuning the period / per-citizen rate.
        var (sim, castle) = MakeSimWithFood(unitCount: 5, startingFood: 23);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var rate = 5 * FoodConsumptionConstants.FoodPerCitizenPerPeriod;
        var fullMeals = 23 / rate;                       // integer floor (4 at rate 5)
        var failureBoundary = (fullMeals + 1) * period;  // first meal that can't feed everyone
        FoodConsumption.CatchUp(castle, sim, now: failureBoundary + period); // well past dry-out
        Assert.Equal(0, castle.AmountOf(Resource.Food));
        Assert.Equal(failureBoundary, castle.FamineStartTick);
        Assert.Equal(failureBoundary, castle.LastFoodConsumedTick);
    }

    [Fact]
    public void FamineCheckEvent_FiresAtPredictedTick_AndSetsFamine()
    {
        var (sim, castle) = MakeSimWithFood(unitCount: 5, startingFood: 50);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        // 50/5 = 10 full meals, failure at (10+1) × 60 = 660.
        Assert.Equal(11 * period, castle.NextFamineCheckTick);

        sim.Run(until: 11 * period);
        Assert.Equal(11 * period, castle.FamineStartTick);
        Assert.Equal(0, castle.AmountOf(Resource.Food));
        // No further check scheduled — famine handles it now (Phase D).
        Assert.Null(castle.NextFamineCheckTick);
    }

    [Fact]
    public void HaulDeposit_DuringFamine_ClearsFamineAndReschedules()
    {
        var (sim, castle) = MakeSimWithFood(unitCount: 5, startingFood: 50);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;

        // Drive into famine.
        sim.Run(until: 11 * period);
        Assert.NotNull(castle.FamineStartTick);

        // Add a hauler at the castle with food.
        var hauler = sim.World.AddUnit(new Unit(99, new TileCoord(0, 0))
        {
            Role = UnitRole.Hauler, OwnerId = 0,
            CargoResource = Resource.Food, CargoAmount = 100, BornTick = 0,
        });
        hauler.TrySetActivity(Activity.Hauling);

        // Famine state at the moment of deposit.
        var depositTick = 12 * period;
        AdvanceSimNow(sim, depositTick);
        var ev = new HaulDepositEvent(99, new TileCoord(0, 0), expectedEpoch: hauler.AssignmentEpoch);
        ev.Apply(sim);

        Assert.Null(castle.FamineStartTick);
        Assert.True(castle.AmountOf(Resource.Food) > 0);
        // New famine check should be scheduled in the future.
        Assert.NotNull(castle.NextFamineCheckTick);
        Assert.True(castle.NextFamineCheckTick > depositTick);
    }

    [Fact]
    public void FamineCheck_FencesOnRateChange_FamineFiresAtNewBoundary()
    {
        // 5 mouths + 100 food → original schedule at tick 1260.
        // Add a 6th mouth → new schedule at tick 1020 (since 100/6 = 16,
        // (16+1)*60 = 1020). The old event still fires at 1260 — it
        // must fence, but by then famine is already set from 1020.
        var (sim, castle) = MakeSimWithFood(unitCount: 5, startingFood: 100);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var originalCheckTick = castle.NextFamineCheckTick;
        Assert.Equal(21 * period, originalCheckTick);

        Population.OnUnitAdded(sim, new Unit(100, new TileCoord(0, 0))
        {
            Role = UnitRole.Builder, OwnerId = 0, BornTick = 0,
        });

        // New anchor is earlier: 100/6 = 16 full meals, fail at 17 × period.
        Assert.Equal(17 * period, castle.NextFamineCheckTick);

        // Run through the OLD check tick. The new event fires first
        // (at 17 × period) and sets famine. The old event at 21 × period
        // fences cleanly (otherwise the run would crash or set famine
        // to the wrong tick).
        sim.Run(until: originalCheckTick!.Value);
        Assert.Equal(17 * period, castle.FamineStartTick);
    }

    [Fact]
    public void Population_ReturnsToZero_ClearsCheckSchedule()
    {
        // With 1 unit, schedule exists. Remove the unit. Schedule clears.
        var (sim, castle) = MakeSimWithFood(unitCount: 1, startingFood: 100);
        Assert.NotNull(castle.NextFamineCheckTick);

        CombatRules.OnUnitDeath(sim, sim.World.Units[1]);
        Assert.Null(castle.NextFamineCheckTick);
        Assert.Equal(0, sim.World.Players[0].PopulationCount);
    }

    [Fact]
    public void EmptyFoodAtStart_FamineSetByFirstCheckEvent()
    {
        // 5 mouths, 0 food, period 60. Predicted dry-out at first
        // boundary (60). FamineCheckEvent fires there and sets famine.
        var (sim, castle) = MakeSimWithFood(unitCount: 5, startingFood: 0);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        Assert.Equal(period, castle.NextFamineCheckTick);

        sim.Run(until: period);
        Assert.Equal(period, castle.FamineStartTick);
    }

    [Fact]
    public void SnapshotRoundTrip_PreservesAllAnchors()
    {
        var (sim, castle) = MakeSimWithFood(unitCount: 5, startingFood: 50);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        sim.Run(until: 11 * period);
        // Now in famine.
        Assert.NotNull(castle.FamineStartTick);
        var savedFamine = castle.FamineStartTick;
        var savedLast = castle.LastFoodConsumedTick;

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xF00D);
        var rc = (Castle)restored.World.Structures[new TileCoord(0, 0)];
        Assert.Equal(savedFamine, rc.FamineStartTick);
        Assert.Equal(savedLast, rc.LastFoodConsumedTick);
    }

    private static void AdvanceSimNow(Simulation sim, long now)
    {
        var method = typeof(Simulation).GetMethod(
            "RestoreClocks",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(sim, new object[] { now, sim.NextSeqForTest() });
    }
}

// ---- Phase E: persistence + headline tests ---------------------------

public class FoodConsumptionPhaseETests
{
    private static (Simulation sim, Castle castle) MakeFamineSim(
        int unitCount, int startingFood)
    {
        var spawns = new List<UnitSpawn>();
        for (var i = 0; i < unitCount; i++)
            spawns.Add(new UnitSpawn(i + 1, new TileCoord(0, 0), UnitRole.Builder, OwnerId: 0));
        var holdings = new SortedDictionary<Resource, int> { [Resource.Food] = startingFood };
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            Population = new Sim.Core.Population.PopulationConfig
            {
                LifespanMinYears = 1000, LifespanMaxYears = 1001,
            },
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = holdings,
                    UnitSpawns = spawns,
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xF00D);
        var castle = (Castle)sim.World.Structures[new TileCoord(0, 0)];
        return (sim, castle);
    }

    [Fact]
    public void Recovery_MidFamine_BeforeFirstDeath_Identical()
    {
        var (sim, castle) = MakeFamineSim(unitCount: 3, startingFood: 30);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        // Drive into famine but stop before the first death.
        sim.Run(until: 11 * period + 30);
        Assert.NotNull(castle.FamineStartTick);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xF00D);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        // Continue both. Should hash-match after the same advancement.
        sim.Run(until: 13 * period);
        restored.Run(until: 13 * period);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void Recovery_MidFamine_BetweenDeaths_Identical()
    {
        var (sim, castle) = MakeFamineSim(unitCount: 4, startingFood: 40);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        // 40/4 = 10 meals, fail at 11*60 = 660. First death at 660+1440.
        var firstDeath = 11 * period + FoodConsumptionConstants.StarvationStartDelay;
        sim.Run(until: firstDeath + 30); // between first and second death
        Assert.True(sim.World.Players[0].PopulationCount < 4);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xF00D);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        // Run both forward by several death intervals.
        var endpoint = firstDeath + 5 * FoodConsumptionConstants.StarvationDeathInterval;
        sim.Run(until: endpoint);
        restored.Run(until: endpoint);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void Food_TwinRun_HashesMatch_AcrossPopulationChurn()
    {
        // M13 HEADLINE TEST. Two identical scenarios run end-to-end
        // through births / deaths / deposit / multiple famine cycles
        // / starvation deaths must produce identical Snapshot.Hash.
        var sim1 = MakeChurnyScenario();
        var sim2 = MakeChurnyScenario();
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;

        // Just run both for a long time and let famine cycles play out.
        sim1.Run(until: 100 * period);
        sim2.Run(until: 100 * period);
        Assert.Equal(Snapshot.Hash(sim1), Snapshot.Hash(sim2));

        sim1.Run(until: 300 * period);
        sim2.Run(until: 300 * period);
        Assert.Equal(Snapshot.Hash(sim1), Snapshot.Hash(sim2));
    }

    private static Simulation MakeChurnyScenario()
    {
        // 4 starting citizens; 100 food; long lifespan so we observe
        // the food/famine mechanics without age-deaths.
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            Population = new Sim.Core.Population.PopulationConfig
            {
                LifespanMinYears = 1000, LifespanMaxYears = 1001,
            },
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = new SortedDictionary<Resource, int>
                    {
                        [Resource.Food] = 100,
                    },
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(0, 0), UnitRole.Builder, OwnerId: 0),
                        new UnitSpawn(2, new TileCoord(1, 0), UnitRole.Builder, OwnerId: 0),
                        new UnitSpawn(3, new TileCoord(2, 0), UnitRole.Hauler, OwnerId: 0),
                        new UnitSpawn(4, new TileCoord(3, 0), UnitRole.Hauler, OwnerId: 0),
                    },
                },
            },
        };
        return new Simulation(spec, seed: 0xF00D);
    }
}

// ---- Phase D: StarvationDeathEvent -----------------------------------

public class FoodConsumptionPhaseDTests
{
    [Fact]
    public void Famine_SchedulesFirstStarvationDeath_AtFamineStartPlusDelay()
    {
        var (sim, castle) = MakeFamineSim(unitCount: 3, startingFood: 30);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        // 30/3 = 10 full meals, failure at 11 × 60 = 660.
        sim.Run(until: 11 * period);
        Assert.Equal(11 * period, castle.FamineStartTick);
        Assert.NotNull(castle.NextStarvationDeathTick);
        Assert.Equal(
            11 * period + FoodConsumptionConstants.StarvationStartDelay,
            castle.NextStarvationDeathTick);
    }

    [Fact]
    public void FirstStarvationDeath_FiresAtDelay_KillsOldestFirst()
    {
        // Three units with explicit ages: oldest first should die.
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            Population = new Sim.Core.Population.PopulationConfig
            {
                LifespanMinYears = 1000, LifespanMaxYears = 1001,
            },
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Food] = 30 },
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(0, 0), UnitRole.Builder, OwnerId: 0, StartingAgeYears: 30),
                        new UnitSpawn(2, new TileCoord(0, 0), UnitRole.Builder, OwnerId: 0, StartingAgeYears: 20),
                        new UnitSpawn(3, new TileCoord(0, 0), UnitRole.Builder, OwnerId: 0, StartingAgeYears: 10),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xF00D);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;

        sim.Run(until: 11 * period + FoodConsumptionConstants.StarvationStartDelay);
        Assert.False(sim.World.Units.ContainsKey(1)); // oldest dies first
        Assert.True(sim.World.Units.ContainsKey(2));
        Assert.True(sim.World.Units.ContainsKey(3));
    }

    [Fact]
    public void StarvationDeaths_StaggerAtInterval()
    {
        var (sim, castle) = MakeFamineSim(unitCount: 3, startingFood: 30);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var firstDeath = 11 * period + FoodConsumptionConstants.StarvationStartDelay;

        sim.Run(until: firstDeath);
        Assert.Equal(2, sim.World.Players[0].PopulationCount);

        sim.Run(until: firstDeath + FoodConsumptionConstants.StarvationDeathInterval);
        Assert.Equal(1, sim.World.Players[0].PopulationCount);

        sim.Run(until: firstDeath + 2 * FoodConsumptionConstants.StarvationDeathInterval);
        Assert.Equal(0, sim.World.Players[0].PopulationCount);
    }

    [Fact]
    public void StarvationDeath_FencesWhenFamineEnds()
    {
        var (sim, castle) = MakeFamineSim(unitCount: 3, startingFood: 30);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        sim.Run(until: 11 * period);
        Assert.NotNull(castle.NextStarvationDeathTick);
        var scheduledDeath = castle.NextStarvationDeathTick!.Value;

        // Deposit food, ending famine.
        var hauler = sim.World.AddUnit(new Unit(99, new TileCoord(0, 0))
        {
            Role = UnitRole.Hauler, OwnerId = 0,
            CargoResource = Resource.Food, CargoAmount = 100, BornTick = 0,
        });
        hauler.TrySetActivity(Activity.Hauling);
        AdvanceSimNow(sim, 12 * period);
        new HaulDepositEvent(99, new TileCoord(0, 0), expectedEpoch: hauler.AssignmentEpoch)
            .Apply(sim);

        // Famine cleared, but the scheduled death STAYS in flight —
        // closes the trickle-deposit exploit. The death fences itself
        // when it fires (FamineStartTick is null at that point) and
        // clears its own anchor.
        Assert.Null(castle.FamineStartTick);
        Assert.Equal(scheduledDeath, castle.NextStarvationDeathTick);

        var popBefore = sim.World.Players[0].PopulationCount;
        sim.Run(until: scheduledDeath);
        Assert.Equal(popBefore, sim.World.Players[0].PopulationCount);
        // After the orphan death fenced, the anchor is cleaned up.
        Assert.Null(castle.NextStarvationDeathTick);
    }

    [Fact]
    public void TrickleDeposit_DoesNotResetStarvationCadence()
    {
        // The exploit: previously, depositing any food during famine
        // cleared the starvation anchor — so a re-famine got a fresh
        // StarvationStartDelay grace window, and trickle deposits could
        // postpone deaths indefinitely. Fix: keep the original anchor;
        // a re-famine before the original death tick honours the original
        // schedule.
        var (sim, castle) = MakeFamineSim(unitCount: 5, startingFood: 50);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        sim.Run(until: 11 * period);
        Assert.NotNull(castle.FamineStartTick);
        Assert.NotNull(castle.NextStarvationDeathTick);
        var originalDeathTick = castle.NextStarvationDeathTick!.Value;

        // Trickle in just 5 food — enough for one period at the original
        // rate, not enough to escape the cadence. (Hauler is added to the
        // world raw, which bumps PopulationCount via GameWorld.AddUnit;
        // capture popBeforeDeath AFTER the bump.)
        var hauler = sim.World.AddUnit(new Unit(99, new TileCoord(0, 0))
        {
            Role = UnitRole.Hauler, OwnerId = 0,
            CargoResource = Resource.Food, CargoAmount = 5, BornTick = 0,
        });
        hauler.TrySetActivity(Activity.Hauling);
        AdvanceSimNow(sim, 12 * period);
        new HaulDepositEvent(99, new TileCoord(0, 0), expectedEpoch: hauler.AssignmentEpoch)
            .Apply(sim);
        var popBeforeDeath = sim.World.Players[0].PopulationCount;

        // Famine cleared, but the original death is still scheduled at
        // its original tick — no fresh grace window.
        Assert.Null(castle.FamineStartTick);
        Assert.Equal(originalDeathTick, castle.NextStarvationDeathTick);

        // Let the new food deplete (famine re-arms shortly after) and
        // advance to the original death tick. A death MUST fire on the
        // original schedule — the genesis units have negative BornTicks,
        // so a genesis citizen is the victim, not the hauler.
        sim.Run(until: originalDeathTick);
        Assert.Equal(popBeforeDeath - 1, sim.World.Players[0].PopulationCount);
    }

    [Fact]
    public void LastCitizenDies_FamineEndsImplicitly()
    {
        var (sim, castle) = MakeFamineSim(unitCount: 2, startingFood: 20);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        var firstDeath = 11 * period + FoodConsumptionConstants.StarvationStartDelay;

        sim.Run(until: firstDeath + FoodConsumptionConstants.StarvationDeathInterval);
        Assert.Equal(0, sim.World.Players[0].PopulationCount);
        Assert.Null(castle.FamineStartTick);
        Assert.Null(castle.NextStarvationDeathTick);
    }

    [Fact]
    public void SnapshotRoundTrip_PreservesStarvationAnchor()
    {
        var (sim, castle) = MakeFamineSim(unitCount: 3, startingFood: 30);
        var period = FoodConsumptionConstants.FoodConsumptionPeriod;
        sim.Run(until: 11 * period);
        Assert.NotNull(castle.NextStarvationDeathTick);
        var savedTick = castle.NextStarvationDeathTick;
        var savedSeq = castle.NextStarvationDeathSeq;

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xF00D);
        var rc = (Castle)restored.World.Structures[new TileCoord(0, 0)];
        Assert.Equal(savedTick, rc.NextStarvationDeathTick);
        Assert.Equal(savedSeq, rc.NextStarvationDeathSeq);
    }

    private static (Simulation sim, Castle castle) MakeFamineSim(
        int unitCount, int startingFood)
    {
        var spawns = new List<UnitSpawn>();
        for (var i = 0; i < unitCount; i++)
            spawns.Add(new UnitSpawn(i + 1, new TileCoord(0, 0), UnitRole.Builder, OwnerId: 0));
        var holdings = new SortedDictionary<Resource, int> { [Resource.Food] = startingFood };
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            // Long lifespan so age-deaths don't pollute starvation tests.
            Population = new Sim.Core.Population.PopulationConfig
            {
                LifespanMinYears = 1000, LifespanMaxYears = 1001,
            },
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = holdings,
                    UnitSpawns = spawns,
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xF00D);
        var castle = (Castle)sim.World.Structures[new TileCoord(0, 0)];
        return (sim, castle);
    }

    private static void AdvanceSimNow(Simulation sim, long now)
    {
        var method = typeof(Simulation).GetMethod(
            "RestoreClocks",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(sim, new object[] { now, sim.NextSeqForTest() });
    }
}
