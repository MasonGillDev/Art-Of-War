using Sim.Core;
using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.World;
using Sim.Server.Automation;

namespace Sim.Tests;

// Automation substrate, Phase C (docs/automation-substrate.md) — the Breed
// recipe (the first STAGED, multi-unit recipe) and the claim lifecycle it
// forced us to get right.
//
// The Phase B bug these pin: a Pull order claimed a unit per firing and
// never released it, so a supply line drained the labour pool one body at a
// time and then reported NoCrew forever. The fix is borrow-and-return —
// hands come back when the order is satisfied, not after every errand.
public class SubstrateBreedTests
{
    private static readonly TileCoord HouseAt = new(4, 4);
    private static readonly TileCoord Keep = new(8, 8);
    private static readonly TileCoord Field = new(2, 2);

    private static Simulation BuildWorld(int houseFood = 100, int adults = 4, long ageYears = 25)
    {
        var grid = new TileGrid(16, 16, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        // The castle feeds everyone not homed in a house — an empty one
        // starves the fixture out from under the test in a few game-days.
        var castle = new Castle(Keep) { OwnerId = 0 };
        castle.Holdings[Resource.Food] = 4000;
        world.AddStructure(castle);
        var house = new House(HouseAt) { OwnerId = 0 };
        house.Holdings[Resource.Food] = houseFood;
        world.AddStructure(house);

        var field = new Stockpile(Field) { OwnerId = 0 };
        field.Holdings[Resource.Food] = 2000;
        world.AddStructure(field);

        // Adults born early enough to be mid-fertility-window at tick 0.
        var cfg = new PopulationConfig();
        var bornTick = -ageYears * cfg.TicksPerYear;
        for (var i = 0; i < adults; i++)
            world.AddUnit(new Unit(i + 1, new TileCoord(5 + i, 4))
                { Role = UnitRole.Farmer, OwnerId = 0, BornTick = bornTick });
        world.NextUnitId = adults + 1;

        return new Simulation(world, seed: 3);
    }

    private static IntentOutcome Submit(Simulation sim, long at, Intent intent)
    {
        sim.SubmitIntent(at, intent);
        sim.Run(at);
        var ev = Assert.IsType<IntentEvent>(sim.ResolvedLog[^1]);
        return ev.Outcome;
    }

    private static Order BreedOrder(long populationTarget = 0, int houseFoodFloor = 40)
    {
        var cfg = new PopulationConfig();
        var predicates = new List<Predicate>
        {
            // The house's OWN larder is the affordability signal — the
            // composition that replaces a global food computation.
            Predicate.StockAtLeast(HouseAt, Resource.Food, houseFoodFloor),
        };
        if (populationTarget > 0) predicates.Add(Predicate.PopulationBelow(populationTarget));

        var clause = new TriggerClause();
        clause.All.AddRange(predicates);
        var trigger = new Trigger();
        trigger.Any.Add(clause);

        return new Order
        {
            SubjectKind = SubjectKind.Structure,
            SubjectTile = HouseAt,
            Program = ProgramKind.Maintain,
            Recipe = RecipeKind.Breed,
            CrewMode = CrewMode.Pull,
            Selector = Selector.InAgeWindow(
                HouseAt, radius: 12, cfg.MinFertileAge, cfg.MaxFertileAge),
            Trigger = trigger,
        };
    }

    private static SubstrateDriver Driver(long thinkPeriod = 60, int retryBudget = 24) =>
        new(new AutomationConfig { ThinkPeriodTicks = thinkPeriod, RetryBudget = retryBudget });

    private static void RunFor(Simulation sim, SubstrateDriver driver, long from, long to, long step)
    {
        for (var t = from; t <= to; t += step) { sim.Run(t); driver.Think(sim, t); }
    }

    // ---- the Phase B regression ------------------------------------------

    [Fact]
    public void PullOrder_ReusesItsBorrowedHand_AndNeverDrainsThePool()
    {
        // THE Phase B bug: every firing claimed a fresh unit and released
        // nothing. Over many thinks the ledger grew until the pool was dry.
        var sim = BuildWorld();
        var line = new Order
        {
            SubjectKind = SubjectKind.Structure, SubjectTile = Keep,
            Program = ProgramKind.Maintain, Recipe = RecipeKind.Haul,
            Target = 400, SourceTile = Field, Resource = Resource.Food,
            CrewMode = CrewMode.Pull,
            Selector = Selector.OfRole(UnitRole.Farmer, Keep, radius: 12),
            Trigger = Trigger.When(Predicate.StockBelow(Keep, Resource.Food, 400)),
        };
        Assert.False(Submit(sim, 0, new SetOrderIntent(line)).IsRejected);
        var driver = Driver(thinkPeriod: 30);

        RunFor(sim, driver, 0, 6000, 30);

        // One line borrows at most one hand at a time — no leak, no drain.
        Assert.True(sim.World.Claims.Count <= 1,
            $"claims leaked: {sim.World.Claims.Count} held by one supply line");
    }

    [Fact]
    public void PullOrder_ReturnsItsHand_WhenTheLineIsSatisfied()
    {
        var sim = BuildWorld();
        ((Castle)sim.World.Structures[Keep]).Holdings[Resource.Food] = 1000; // already full
        var line = new Order
        {
            SubjectKind = SubjectKind.Structure, SubjectTile = Keep,
            Program = ProgramKind.Maintain, Recipe = RecipeKind.Haul,
            Target = 400, SourceTile = Field, Resource = Resource.Food,
            CrewMode = CrewMode.Pull,
            Selector = Selector.OfRole(UnitRole.Farmer, Keep, radius: 12),
            Trigger = Trigger.When(Predicate.StockBelow(Keep, Resource.Food, 400)),
        };
        Submit(sim, 0, new SetOrderIntent(line));
        var driver = Driver(thinkPeriod: 30);

        RunFor(sim, driver, 0, 300, 30);

        // Satisfied line holds nobody — the pool gets its people back.
        Assert.Empty(sim.World.Claims);
        Assert.All(sim.World.Units.Values, u => Assert.True(ClaimLedger.IsDormant(sim.World, u)));
    }

    // ---- the Breed recipe ------------------------------------------------

    [Fact]
    public void SetOrder_RejectsBreedOnANonHouse_AndOnANamedCrew()
    {
        var sim = BuildWorld();

        var onCastle = BreedOrder();
        var wrongSubject = new Order
        {
            SubjectKind = SubjectKind.Structure, SubjectTile = Keep,
            Program = ProgramKind.Maintain, Recipe = RecipeKind.Breed,
            CrewMode = CrewMode.Pull, Selector = onCastle.Selector, Trigger = onCastle.Trigger,
        };
        var outcome = Submit(sim, 0, new SetOrderIntent(wrongSubject));
        Assert.True(outcome.IsRejected);
        Assert.Contains("House", outcome.Reason);

        // A standing "breeding pair" would lock two adults out of the
        // workforce for life — Breed is Pull-only by construction.
        var named = new Order
        {
            SubjectKind = SubjectKind.Structure, SubjectTile = HouseAt,
            Program = ProgramKind.Maintain, Recipe = RecipeKind.Breed,
            CrewMode = CrewMode.Named, Trigger = onCastle.Trigger,
        };
        named.NamedCrew.Add(1);
        var namedOutcome = Submit(sim, 0, new SetOrderIntent(named));
        Assert.True(namedOutcome.IsRejected);
        Assert.Contains("Pull crew", namedOutcome.Reason);
    }

    [Fact]
    public void Breed_WalksAPairToTheHouse_ThenBeginsBreeding()
    {
        var sim = BuildWorld();
        Assert.False(Submit(sim, 0, new SetOrderIntent(BreedOrder())).IsRejected);
        var driver = Driver(thinkPeriod: 30);

        // First think: recruit + march.
        driver.Think(sim, 30);
        sim.Run(30);
        Assert.Equal(2, sim.World.Claims.Count);
        Assert.All(sim.World.Claims.Values,
            c => Assert.Equal(ClaimPurpose.InFlight, c.Purpose));

        // Let them walk, then begin.
        RunFor(sim, driver, 60, 4000, 30);

        var house = (House)sim.World.Structures[HouseAt];
        Assert.NotNull(house.Occupation);
        // Claims are handed back the moment breeding starts — the house's
        // Occupation owns the parents through gestation.
        Assert.Empty(sim.World.Claims);
    }

    [Fact]
    public void Breed_ProducesABirth_AndThePopulationGrows()
    {
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(BreedOrder()));
        var before = sim.World.Units.Count;
        var driver = Driver(thinkPeriod: 30);

        var cfg = new PopulationConfig();
        RunFor(sim, driver, 0, 4000 + cfg.GestationTicks, 30);

        Assert.True(sim.World.Units.Count > before,
            $"expected a birth: {before} → {sim.World.Units.Count}");
    }

    [Fact]
    public void Breed_StandsDown_WhenTheHouseLarderIsEmpty()
    {
        // THE SELF-THROTTLE: no global food computation anywhere — the
        // house's own larder is the signal, and an unstocked house simply
        // does not breed.
        var sim = BuildWorld(houseFood: 0);
        Submit(sim, 0, new SetOrderIntent(BreedOrder(houseFoodFloor: 40)));
        var driver = Driver(thinkPeriod: 30);

        RunFor(sim, driver, 0, 2000, 30);

        Assert.Null(((House)sim.World.Structures[HouseAt]).Occupation);
        Assert.Empty(sim.World.Claims);       // nobody left parked at the door
        Assert.Equal(JournalOutcome.Waiting, driver.Journal.For(1)[^1].Outcome);
    }

    [Fact]
    public void Breed_ReportsAShortageWithoutKillingItself()
    {
        // Everyone is too old — the pool has bodies but none eligible.
        //
        // CONTENTION IS NOT BREAKAGE: the order reports the shortage and
        // stays ALIVE. Auto-disabling here would mean a temporary labour
        // shortage permanently killed a standing order while the player was
        // asleep, and they would wake to a kingdom that had quietly stopped
        // — the silent death the async doctrine forbids. Found by the
        // 160-day keystone lab, where three of five orders had disabled
        // themselves over exactly this.
        var sim = BuildWorld(adults: 4, ageYears: 60);
        Submit(sim, 0, new SetOrderIntent(BreedOrder()));
        var driver = Driver(thinkPeriod: 30, retryBudget: 3);

        RunFor(sim, driver, 0, 600, 30);

        Assert.Null(((House)sim.World.Structures[HouseAt]).Occupation);
        Assert.Contains(driver.Journal.For(1), e => e.Outcome == JournalOutcome.NoCrew);
        Assert.True(sim.World.Orders[1].Enabled, "a labour shortage disabled the order");
        Assert.Equal(0, sim.World.Orders[1].RetryCount);
    }

    [Fact]
    public void Breed_ReleasesAPartnerWhoDiesMidWalk_AndRecruitsAnother()
    {
        var sim = BuildWorld(adults: 4);
        Submit(sim, 0, new SetOrderIntent(BreedOrder()));
        var driver = Driver(thinkPeriod: 30);

        driver.Think(sim, 30);
        sim.Run(30);
        var claimed = ClaimLedger.UnitsOf(sim.World, 1);
        Assert.Equal(2, claimed.Count);
        var before = sim.World.Units.Count;

        // One of the pair is killed on the road.
        sim.World.Units.Remove(claimed[0]);
        var cfg = new PopulationConfig();
        RunFor(sim, driver, 60, 4000 + cfg.GestationTicks, 30);

        // The dead unit's claim must be reapable — a claim that can only be
        // released by a living body would strand the ledger forever.
        Assert.False(ClaimLedger.IsClaimed(sim.World, claimed[0]));
        // ...and the order recovered: a replacement was recruited and bred.
        Assert.True(sim.World.Units.Count > before - 1,
            $"expected a birth after the replacement: {before - 1} → {sim.World.Units.Count}");
    }

    [Fact]
    public void Breed_CommittedPairFollowsThrough_EvenIfTheTargetIsReachedMidWalk()
    {
        // User decision (2026-08-06): committed work follows through. The
        // alternative — releasing them mid-walk — makes pairs oscillate
        // (walk out, turn around, walk back) as the population wobbles.
        var sim = BuildWorld(adults: 4);
        Submit(sim, 0, new SetOrderIntent(BreedOrder(populationTarget: 5)));
        var driver = Driver(thinkPeriod: 30);

        driver.Think(sim, 30);
        sim.Run(30);
        Assert.Equal(2, sim.World.Claims.Count);          // pair committed

        // Population jumps past the target while they are still walking.
        for (var i = 0; i < 4; i++)
            sim.World.AddUnit(new Unit(100 + i, Keep) { Role = UnitRole.Farmer, OwnerId = 0 });
        var before = sim.World.Units.Count;
        Assert.True(before >= 5);

        var cfg = new PopulationConfig();
        RunFor(sim, driver, 60, 4000 + cfg.GestationTicks, 30);

        // They finished the job rather than turning around at the door —
        // a birth landed even though the trigger went false mid-walk.
        Assert.True(sim.World.Units.Count > before,
            $"committed pair should have followed through: {before} → {sim.World.Units.Count}");
    }

    // ---- durability ------------------------------------------------------

    [Fact]
    public void BreedOrder_RoundTripsTheSnapshot_IncludingTheAgeWindow()
    {
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(BreedOrder(populationTarget: 40)));
        var cfg = new PopulationConfig();

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 3);

        var o = restored.World.Orders[1];
        Assert.Equal(RecipeKind.Breed, o.Recipe);
        Assert.Equal(cfg.MinFertileAge, o.Selector.MinAgeYears);
        Assert.Equal(cfg.MaxFertileAge, o.Selector.MaxAgeYears);   // v24
        Assert.True(o.Selector.AnyRole);
        var clause = Assert.Single(o.Trigger.Any);
        Assert.Contains(clause.All, p => p.Kind == PredicateKind.PopulationBelow);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void Headline_BreedReplayFromIntentLog_HashesMatch()
    {
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(BreedOrder()));
        var driver = Driver(thinkPeriod: 30);
        var cfg = new PopulationConfig();
        var end = 4000 + cfg.GestationTicks;

        RunFor(sim, driver, 0, end, 30);

        // THE REPLAY DISCIPLINE (docs/automation-layers.md, inherited):
        // advance the clock to the tick FIRST, then submit that tick's
        // intents in Seq order. Submitting before running front-loads the
        // intents and reorders same-tick execution against the scheduled
        // events (here, BirthEvent) — which diverges. Rejections are
        // replayed too: they mutate nothing but still consume a Seq, and
        // the Seq counter is hashed.
        var replay = BuildWorld();
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
        replay.Run(end);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(replay));
    }
}
