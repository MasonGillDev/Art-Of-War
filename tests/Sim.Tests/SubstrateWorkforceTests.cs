using Sim.Core;
using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.World;
using Sim.Server.Automation;

namespace Sim.Tests;

// Automation substrate, Phase E — the workforce recipes: Train ("keep N of
// this profession") and Staff ("keep this farm at N hands").
//
// Tested directly against small fixtures rather than through the keystone
// colony: whether a 160-day economy happens to produce a spare adult at the
// right moment is a BALANCE question, and using it to answer "does Train
// work?" conflates two things that fail for different reasons.
//
// The pair's headline property is that they COMPOSE THROUGH THE POOL and
// nowhere else (law 3): Train makes farmers, Staff puts farmers on farms,
// and neither recipe contains a line of code that knows the other exists.
public class SubstrateWorkforceTests
{
    private static readonly TileCoord Keep = new(10, 10);
    private static readonly TileCoord SchoolAt = new(12, 10);
    private static readonly TileCoord FarmAt = new(6, 10);

    private static Simulation BuildWorld(int adults = 4, UnitRole role = UnitRole.None, long ageYears = 25)
    {
        var grid = new TileGrid(20, 20, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        var castle = new Castle(Keep) { OwnerId = 0 };
        castle.Holdings[Resource.Food] = 8000;      // no famine noise
        world.AddStructure(castle);
        world.AddStructure(new School(SchoolAt) { OwnerId = 0 });
        world.AddStructure(new Extractor(StructureKind.Farm, FarmAt) { OwnerId = 0 });

        var cfg = new PopulationConfig();
        for (var i = 0; i < adults; i++)
            world.AddUnit(new Unit(i + 1, new TileCoord(10 + i, 12))
                { Role = role, OwnerId = 0, BornTick = -ageYears * cfg.TicksPerYear });
        world.NextUnitId = adults + 1;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 20; y++)
            for (var x = 0; x < 20; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 21);
    }

    private static IntentOutcome Submit(Simulation sim, long at, Intent intent)
    {
        sim.SubmitIntent(at, intent);
        sim.Run(at);
        return Assert.IsType<IntentEvent>(sim.ResolvedLog[^1]).Outcome;
    }

    private static Order TrainOrder(UnitRole role, int quota) => new()
    {
        SubjectKind = SubjectKind.RoleCount, SubjectRole = role,
        Program = ProgramKind.Maintain, Recipe = RecipeKind.Train,
        Target = quota, SourceTile = SchoolAt,
        CrewMode = CrewMode.Pull,
        Selector = Selector.OfRole(UnitRole.None, SchoolAt, radius: 16,
            minAgeYears: new PopulationConfig().MinTrainAge),
        Trigger = Trigger.When(Predicate.RoleCountBelow(role, quota)),
    };

    private static Order StaffOrder(int workers) => new()
    {
        SubjectKind = SubjectKind.Structure, SubjectTile = FarmAt,
        Program = ProgramKind.Maintain, Recipe = RecipeKind.Staff,
        Target = workers,
        CrewMode = CrewMode.Pull,
        Selector = Selector.OfRole(UnitRole.Farmer, FarmAt, radius: 16),
        Trigger = Trigger.When(Predicate.WorkersBelow(FarmAt, workers)),
    };

    private static SubstrateDriver Driver() =>
        new(new AutomationConfig { ThinkPeriodTicks = Time.Hour });

    private static void RunFor(Simulation sim, SubstrateDriver driver, long horizon)
    {
        for (long t = 0; t <= horizon; t += Time.Hour) { sim.Run(t); driver.Think(sim, t); }
    }

    // ---- Train -----------------------------------------------------------

    [Fact]
    public void Train_WalksAnUntrainedAdultToTheSchool_AndGivesThemATrade()
    {
        var sim = BuildWorld(adults: 3, role: UnitRole.None);
        Assert.False(Submit(sim, 0, new SetOrderIntent(TrainOrder(UnitRole.Farmer, 2))).IsRejected);
        var driver = Driver();

        RunFor(sim, driver, 10 * Time.Day);

        Assert.Equal(2, sim.World.Units.Values.Count(u => u.Role == UnitRole.Farmer));
        // Quota met → the order stops recruiting and hands everyone back.
        Assert.Empty(sim.World.Claims);
    }

    [Fact]
    public void Train_StopsExactlyAtTheQuota()
    {
        var sim = BuildWorld(adults: 6, role: UnitRole.None);
        Submit(sim, 0, new SetOrderIntent(TrainOrder(UnitRole.Hauler, 3)));
        var driver = Driver();

        RunFor(sim, driver, 20 * Time.Day);

        Assert.Equal(3, sim.World.Units.Values.Count(u => u.Role == UnitRole.Hauler));
        Assert.Equal(3, sim.World.Units.Values.Count(u => u.Role == UnitRole.None));
    }

    [Fact]
    public void Train_TwoSchoolsOnOneQuota_DoNotEachTrainTheSameHead()
    {
        // The in-flight-count guarantee. Two Train orders for the same role
        // must not both walk a trainee to school for the same missing slot:
        // a claim made earlier in the pass is visible to the later order,
        // which reads the quota as already covered and stands down.
        var sim = BuildWorld(adults: 4, role: UnitRole.None);
        var second = new TileCoord(8, 10);
        sim.World.AddStructure(new School(second) { OwnerId = 0 });

        Submit(sim, 0, new SetOrderIntent(TrainOrder(UnitRole.Farmer, 1)));
        var rival = TrainOrder(UnitRole.Farmer, 1);
        Submit(sim, 0, new SetOrderIntent(new Order
        {
            Priority = 1,
            SubjectKind = SubjectKind.RoleCount, SubjectRole = UnitRole.Farmer,
            Program = ProgramKind.Maintain, Recipe = RecipeKind.Train,
            Target = 1, SourceTile = second,
            CrewMode = CrewMode.Pull, Selector = rival.Selector, Trigger = rival.Trigger,
        }));
        var driver = Driver();

        driver.Think(sim, Time.Hour);
        sim.Run(Time.Hour);

        // Exactly one trainee committed, not two.
        Assert.Single(sim.World.Claims);
    }

    [Fact]
    public void SetOrder_RejectsTrainingARoleAtTheWrongBuilding()
    {
        // The catalog owns trainer routing (School vs Barracks); automation
        // must not invent a second table.
        var sim = BuildWorld();
        var soldierAtSchool = TrainOrder(UnitRole.Soldier, 2);

        var outcome = Submit(sim, 0, new SetOrderIntent(soldierAtSchool));

        Assert.True(outcome.IsRejected);
        Assert.Contains("Barracks", outcome.Reason);
    }

    // ---- Staff -----------------------------------------------------------

    [Fact]
    public void Staff_WalksFarmersToTheField_AndPutsThemToWork()
    {
        var sim = BuildWorld(adults: 4, role: UnitRole.Farmer);
        Assert.False(Submit(sim, 0, new SetOrderIntent(StaffOrder(2))).IsRejected);
        var driver = Driver();

        RunFor(sim, driver, 5 * Time.Day);

        var farm = (Extractor)sim.World.Structures[FarmAt];
        Assert.Equal(2, farm.Workers.Count);
        Assert.Empty(sim.World.Claims);          // hands released once working
        // And the field is actually producing.
        Assert.True(farm.Buffer > 0, "a staffed farm grew nothing");
    }

    [Fact]
    public void Staff_ReportsAShortage_RatherThanTrainingAReplacementItself()
    {
        // Staff does ONE dumb verb. When the trade is scarce it says so and
        // waits — it never quietly retrains somebody, because choosing to
        // convert a citizen's profession is the player's call (their Train
        // quota), not a hidden fallback inside a staffing rule.
        var sim = BuildWorld(adults: 3, role: UnitRole.None);   // nobody is a farmer
        Submit(sim, 0, new SetOrderIntent(StaffOrder(2)));
        var driver = Driver();

        RunFor(sim, driver, 3 * Time.Day);

        Assert.Empty(((Extractor)sim.World.Structures[FarmAt]).Workers);
        Assert.All(sim.World.Units.Values, u => Assert.Equal(UnitRole.None, u.Role));
        Assert.Contains(driver.Journal.For(1), e => e.Outcome == JournalOutcome.NoCrew);
        Assert.True(sim.World.Orders[1].Enabled, "a trade shortage disabled the order");
    }

    [Fact]
    public void Staff_RefillsTheFieldWhenAWorkerDies()
    {
        var sim = BuildWorld(adults: 4, role: UnitRole.Farmer);
        Submit(sim, 0, new SetOrderIntent(StaffOrder(2)));
        var driver = Driver();
        RunFor(sim, driver, 5 * Time.Day);
        var farm = (Extractor)sim.World.Structures[FarmAt];
        Assert.Equal(2, farm.Workers.Count);

        // A raider kills one of the field hands.
        var victim = farm.Workers.First();
        farm.Workers.Remove(victim);
        sim.World.Units.Remove(victim);

        RunFor(sim, driver, 10 * Time.Day);

        Assert.Equal(2, farm.Workers.Count);     // the thermostat healed it
    }

    // ---- the pair, composing --------------------------------------------

    [Fact]
    public void TrainAndStaff_ComposeThroughThePool_WithoutKnowingEachOther()
    {
        // THE PHASE E CLAIM: a kingdom of untrained adults ends up with a
        // working farm, because Train makes farmers and Staff hires them.
        // No order references another order anywhere; they meet only in the
        // shared pool of people (docs/automation-as-core-game.md law 3).
        var sim = BuildWorld(adults: 4, role: UnitRole.None);
        Submit(sim, 0, new SetOrderIntent(TrainOrder(UnitRole.Farmer, 2)));
        Submit(sim, 0, new SetOrderIntent(StaffOrder(2)));
        var driver = Driver();

        RunFor(sim, driver, 15 * Time.Day);

        var farm = (Extractor)sim.World.Structures[FarmAt];
        Assert.Equal(2, farm.Workers.Count);
        Assert.True(farm.Buffer > 0, "the composed pipeline grew no food");
    }

    [Fact]
    public void WorkforceOrders_RoundTripAndReplayDeterministically()
    {
        var sim = BuildWorld(adults: 4, role: UnitRole.None);
        Submit(sim, 0, new SetOrderIntent(TrainOrder(UnitRole.Farmer, 2)));
        Submit(sim, 0, new SetOrderIntent(StaffOrder(2)));
        var driver = Driver();
        var horizon = 12 * Time.Day;

        RunFor(sim, driver, horizon);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 21);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        var replay = BuildWorld(adults: 4, role: UnitRole.None);
        foreach (var batch in sim.ResolvedLog.OfType<IntentEvent>()
                     .OrderBy(e => e.Seq).GroupBy(e => e.At))
        {
            replay.Run(until: batch.Key);
            foreach (var ev in batch)
            {
                var (typeName, payload) = Sim.Persistence.IntentJson.Serialize(ev.Intent);
                replay.SubmitIntent(batch.Key,
                    Sim.Persistence.IntentJson.Deserialize(typeName, payload));
            }
        }
        replay.Run(horizon);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(replay));
    }
}
