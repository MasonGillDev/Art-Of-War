using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Persistence;
using Sim.Core.World;
using Sim.Server.Automation;

namespace Sim.Tests;

// Automation substrate, Phase B (docs/automation-substrate.md) — the
// universal Order record, the Maintain thermostat, the SupplyLine (Haul)
// recipe, and the driver pass.
//
// The headline contract is at the bottom: run with the driver live, replay
// the intent log with NO driver, snapshot hashes match. That is what makes
// an out-of-sim brain legal (the M16 lesson, re-proven for the substrate).
public class SubstrateOrderTests
{
    private static readonly TileCoord Farm = new(2, 2);
    private static readonly TileCoord Keep = new(5, 5);

    // A castle (dest) + a stockpile of food (source) + one hauler.
    private static Simulation BuildWorld(int food = 200)
    {
        var grid = new TileGrid(12, 12, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        // AddStructure (not Structures[..] =) so Health auto-inits from the
        // catalog — a hand-placed structure left at Health 0 restores at its
        // BaseHealth and silently breaks snapshot-hash equality.
        world.AddStructure(new Castle(Keep) { OwnerId = 0 });

        var source = new Stockpile(Farm) { OwnerId = 0 };
        source.Holdings[Resource.Food] = food;
        world.AddStructure(source);

        world.AddUnit(new Unit(1, Keep) { Role = UnitRole.Hauler, OwnerId = 0 });
        world.AddUnit(new Unit(2, Keep) { Role = UnitRole.Hauler, OwnerId = 0 });
        world.NextUnitId = 3;

        // Vision: the owner must SEE both tiles or the fog contract
        // (correctly) refuses to evaluate the trigger.
        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 12; y++)
            for (var x = 0; x < 12; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 5);
    }

    private static IntentOutcome Submit(Simulation sim, long at, Intent intent)
    {
        sim.SubmitIntent(at, intent);
        sim.Run(at);
        var ev = Assert.IsType<IntentEvent>(sim.ResolvedLog[^1]);
        return ev.Outcome;
    }

    // "Keep the castle stocked to 300 food from the farm, on hauler 1."
    private static Order SupplyLine(
        int keepAbove = 300, int sourceReserve = 1, CrewMode crew = CrewMode.Named, int priority = 0)
    {
        var order = new Order
        {
            OwnerId = 0,
            Priority = priority,
            SubjectKind = SubjectKind.Structure,
            SubjectTile = Keep,
            Program = ProgramKind.Maintain,
            Recipe = RecipeKind.Haul,
            Target = keepAbove,
            SourceTile = Farm,
            Resource = Resource.Food,
            CrewMode = crew,
            Selector = Selector.OfRole(UnitRole.Hauler, Keep, radius: 10),
            Trigger = Trigger.When(
                Predicate.StockBelow(Keep, Resource.Food, keepAbove),
                Predicate.StockAtLeast(Farm, Resource.Food, sourceReserve)),
        };
        if (crew == CrewMode.Named) order.NamedCrew.Add(1);
        return order;
    }

    private static SubstrateDriver Driver(long thinkPeriod = 60, int retryBudget = 24) =>
        new(new AutomationConfig { ThinkPeriodTicks = thinkPeriod, RetryBudget = retryBudget });

    // ---- install / clear -------------------------------------------------

    [Fact]
    public void SetOrder_InstallsAndClaimsItsNamedCrew()
    {
        var sim = BuildWorld();

        Assert.False(Submit(sim, 0, new SetOrderIntent(SupplyLine())).IsRejected);

        var order = Assert.Single(sim.World.Orders).Value;
        Assert.Equal(RecipeKind.Haul, order.Recipe);
        Assert.Equal(new List<int> { 1 }, order.NamedCrew);
        // Named crew is STANDING membership — claimed at install, so no
        // other order can grab those units.
        Assert.Equal(order.OrderId, ClaimLedger.ClaimOf(sim.World, 1)!.Value.OrderId);
        Assert.Equal(ClaimPurpose.Crew, ClaimLedger.ClaimOf(sim.World, 1)!.Value.Purpose);
        Assert.False(ClaimLedger.IsDormant(sim.World, sim.World.Units[1]));
    }

    [Fact]
    public void SetOrder_RejectsCrewAlreadyClaimedByAnotherOrder()
    {
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(SupplyLine()));

        var second = Submit(sim, 0, new SetOrderIntent(SupplyLine()));

        Assert.True(second.IsRejected);
        Assert.Contains("already claimed", second.Reason);
        Assert.Single(sim.World.Orders);
    }

    [Fact]
    public void SetOrder_RejectsUnownedSubjectAndBadRecipeWiring()
    {
        var sim = BuildWorld();
        sim.World.Structures[Keep] = new Castle(Keep) { OwnerId = 1, Health = 1000 }; // someone else's

        Assert.True(Submit(sim, 0, new SetOrderIntent(SupplyLine())).IsRejected);

        sim.World.Structures[Keep] = new Castle(Keep) { OwnerId = 0, Health = 1000 };
        // Source == dest is incoherent wiring, caught at authoring time.
        var bad = new Order
        {
            SubjectKind = SubjectKind.Structure, SubjectTile = Keep,
            Recipe = RecipeKind.Haul, Resource = Resource.Food, SourceTile = Keep,
            CrewMode = CrewMode.Named,
        };
        bad.NamedCrew.Add(1);
        var outcome = Submit(sim, 0, new SetOrderIntent(bad));
        Assert.True(outcome.IsRejected);
        Assert.Contains("same tile", outcome.Reason);
    }

    [Fact]
    public void ClearOrder_ReleasesEveryClaimItHeld()
    {
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(SupplyLine()));
        var orderId = sim.World.Orders.Keys.First();

        Assert.False(Submit(sim, 0, new ClearOrderIntent(orderId)).IsRejected);

        Assert.Empty(sim.World.Orders);
        // The units must go back to the pool — a leaked claim would strand
        // them: alive, idle, and invisible to every other order forever.
        Assert.False(ClaimLedger.IsClaimed(sim.World, 1));
        Assert.True(ClaimLedger.IsDormant(sim.World, sim.World.Units[1]));
    }

    // ---- the thermostat --------------------------------------------------

    [Fact]
    public void Maintain_FiresAHaul_WhenTheSubjectIsBelowTarget()
    {
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(SupplyLine(keepAbove: 300)));
        var driver = Driver();

        driver.Think(sim, 100);
        sim.Run(100);

        // The hauler is on its way with a haul plan.
        var hauler = sim.World.Units[1];
        Assert.NotNull(hauler.HaulPlan);
        Assert.Equal(Farm, hauler.HaulPlan!.SourceTile);
        Assert.Equal(Keep, hauler.HaulPlan.DestTile);
        Assert.Equal(JournalOutcome.Fired, driver.Journal.For(1)[^1].Outcome);
    }

    [Fact]
    public void Maintain_Waits_WhenTheSubjectIsAlreadyStocked()
    {
        var sim = BuildWorld();
        ((Castle)sim.World.Structures[Keep]).Holdings[Resource.Food] = 500;
        Submit(sim, 0, new SetOrderIntent(SupplyLine(keepAbove: 300)));
        var driver = Driver();

        driver.Think(sim, 100);
        sim.Run(100);

        Assert.Null(sim.World.Units[1].HaulPlan);
        // Waiting is the healthy resting state — it must NEVER burn retry
        // budget, or every idle supply line would disable itself.
        Assert.Equal(JournalOutcome.Waiting, driver.Journal.For(1)[^1].Outcome);
        Assert.Equal(0, sim.World.Orders[1].RetryCount);
    }

    [Fact]
    public void Maintain_Waits_WhenTheSourceIsBelowItsReserve()
    {
        var sim = BuildWorld(food: 0);   // source empty
        Submit(sim, 0, new SetOrderIntent(SupplyLine(keepAbove: 300, sourceReserve: 1)));
        var driver = Driver();

        driver.Think(sim, 100);
        sim.Run(100);

        Assert.Null(sim.World.Units[1].HaulPlan);
        Assert.Equal(JournalOutcome.Waiting, driver.Journal.For(1)[^1].Outcome);
    }

    [Fact]
    public void Maintain_DoesNotReorderACrewMemberAlreadyMidTrip()
    {
        // The M16 pitfall, substrate edition: a marching unit reads Idle.
        // Guarding on Activity instead of anchors would re-dispatch the
        // same hauler every single think.
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(SupplyLine()));
        var driver = Driver(thinkPeriod: 1);

        driver.Think(sim, 100);
        sim.Run(100);
        var planAfterFirst = sim.World.Units[1].HaulPlan;
        var firedFirst = driver.Journal.For(1).Count(e => e.Outcome == JournalOutcome.Fired);

        driver.Think(sim, 101);
        sim.Run(101);

        Assert.Same(planAfterFirst, sim.World.Units[1].HaulPlan);
        Assert.Equal(firedFirst, driver.Journal.For(1).Count(e => e.Outcome == JournalOutcome.Fired));
    }

    [Fact]
    public void DeadCrew_ErodesTheOrder_ThenAutoDisablesWithNews()
    {
        // Law 4 (docs/automation-as-core-game.md): automation is embodied in
        // mortal crews — kill the crew and the order goes dark. The player
        // must be TOLD, not left with a silently churning line.
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(SupplyLine()));
        sim.World.Units.Remove(1);                 // the crew is killed
        var driver = Driver(thinkPeriod: 1, retryBudget: 3);

        for (var t = 100; t < 110; t++) { sim.Run(t); driver.Think(sim, t); }

        var order = sim.World.Orders[1];
        Assert.False(order.Enabled);
        Assert.Contains(driver.Journal.For(1), e => e.Outcome == JournalOutcome.NoCrew);
        Assert.Contains(driver.Journal.For(1), e => e.Outcome == JournalOutcome.Suspended);
    }

    // ---- arbitration -----------------------------------------------------

    [Fact]
    public void PulledCrews_ClaimOnCommit_SoTwoOrdersNeverGrabTheSameUnit()
    {
        // THE arbitration test. Two pull-crew orders, one free hauler,
        // same think. Claim-on-commit means the first (by priority) takes
        // them and the second sees an empty pool — no collision, no race.
        var sim = BuildWorld();
        var second = new TileCoord(8, 8);
        sim.World.AddStructure(new Stockpile(second) { OwnerId = 0 });

        // Only ONE dormant hauler: unit 2 (unit 1 gets claimed below).
        sim.World.Units.Remove(1);

        Submit(sim, 0, new SetOrderIntent(SupplyLine(crew: CrewMode.Pull, priority: 0)));
        var rival = SupplyLine(crew: CrewMode.Pull, priority: 1);
        var rivalOrder = new Order
        {
            Priority = 1,
            SubjectKind = SubjectKind.Structure, SubjectTile = second,
            Program = ProgramKind.Maintain, Recipe = RecipeKind.Haul,
            Target = 300, SourceTile = Farm, Resource = Resource.Food,
            CrewMode = CrewMode.Pull,
            Selector = Selector.OfRole(UnitRole.Hauler, second, radius: 10),
            Trigger = Trigger.When(Predicate.StockBelow(second, Resource.Food, 300)),
        };
        Submit(sim, 0, new SetOrderIntent(rivalOrder));

        var driver = Driver();
        driver.Think(sim, 100);
        sim.Run(100);

        // Exactly one order got the hauler; the other reported an empty pool.
        var claim = ClaimLedger.ClaimOf(sim.World, 2);
        Assert.NotNull(claim);
        Assert.Equal(1, claim!.Value.OrderId);            // priority 0 won
        Assert.Equal(ClaimPurpose.InFlight, claim.Value.Purpose);
        Assert.Equal(JournalOutcome.NoCrew, driver.Journal.For(2)[^1].Outcome);
    }

    [Fact]
    public void Selector_IsAWhereClauseInCanonicalOrder_NotARanking()
    {
        // Nearest-first is a TIEBREAK that makes the sequence total and
        // deterministic — never a "best fit" score.
        var sim = BuildWorld();
        sim.World.Units.Remove(1);
        sim.World.Units.Remove(2);
        sim.World.AddUnit(new Unit(10, new TileCoord(9, 9)) { Role = UnitRole.Hauler, OwnerId = 0 });
        sim.World.AddUnit(new Unit(11, new TileCoord(6, 5)) { Role = UnitRole.Hauler, OwnerId = 0 });
        sim.World.AddUnit(new Unit(12, new TileCoord(5, 5)) { Role = UnitRole.Farmer, OwnerId = 0 });

        var order = SupplyLine(crew: CrewMode.Pull);
        var resolved = SelectorResolver.Resolve(
            sim.World, order, Selector.OfRole(UnitRole.Hauler, Keep, radius: 10), now: 0);

        // Farmer filtered out by role; haulers ordered by distance from Keep.
        Assert.Equal(new[] { 11, 10 }, resolved.Select(u => u.Id).ToArray());
    }

    [Fact]
    public void Selector_SkipsProtectedUnits_OnlyWhenConscripting()
    {
        var sim = BuildWorld();
        sim.World.Units[1].Protected = true;
        var order = SupplyLine(crew: CrewMode.Pull);

        // Ordinary dormant pull: Protected is irrelevant — an idle unit is
        // available. The flag guards against being TAKEN FROM WORK.
        var dormantPull = SelectorResolver.Resolve(
            sim.World, order, Selector.OfRole(UnitRole.Hauler, Keep, radius: 10), now: 0);
        Assert.Contains(dormantPull, u => u.Id == 1);

        // Conscripting pull (RequireDormant off): Protected is sacred.
        var conscript = new Selector(
            UnitRole.Hauler, AnyRole: false, MinAgeYears: 0, MaxAgeYears: 0,
            RequireDormant: false, Anchor: Keep, Radius: 10);
        var conscripted = SelectorResolver.Resolve(sim.World, order, conscript, now: 0);
        Assert.DoesNotContain(conscripted, u => u.Id == 1);
    }

    [Fact]
    public void Fog_AnUnseenSourceNeverEvaluatesTrue()
    {
        // THE FOG CONTRACT: automation may never react to state its owner
        // cannot see. Here the source is a full stockpile in the dark on the
        // far side of the map — the order must sit still, because "unknown"
        // is never "true".
        var sim = BuildWorld();
        var dark = new TileCoord(11, 11);
        var hidden = new Stockpile(dark) { OwnerId = 1 };   // someone else's, unseen
        hidden.Holdings[Resource.Food] = 500;
        sim.World.AddStructure(hidden);

        var order = SupplyLine();
        var fromTheDark = new Order
        {
            SubjectKind = SubjectKind.Structure, SubjectTile = Keep,
            Program = ProgramKind.Maintain, Recipe = RecipeKind.Haul,
            Target = 300, SourceTile = dark, Resource = Resource.Food,
            CrewMode = CrewMode.Named,
            Trigger = Trigger.When(
                Predicate.StockBelow(Keep, Resource.Food, 300),
                Predicate.StockAtLeast(dark, Resource.Food, 1)),
        };
        fromTheDark.NamedCrew.Add(1);
        Assert.False(Submit(sim, 0, new SetOrderIntent(fromTheDark)).IsRejected);

        // Sanity: the tile really is outside the owner's live vision.
        Assert.DoesNotContain(dark, Sim.Core.Vision.View.VisibleTiles(sim.World, 0));

        var driver = Driver();
        driver.Think(sim, 100);
        sim.Run(100);

        Assert.Null(sim.World.Units[1].HaulPlan);
        Assert.Equal(JournalOutcome.Waiting, driver.Journal.For(1)[^1].Outcome);
    }

    // ---- durability + the headline --------------------------------------

    [Fact]
    public void Orders_RoundTripTheSnapshot_ByteIdentical()
    {
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(SupplyLine()));
        Submit(sim, 0, new OrderStatusIntent(1, OrderStatusOp.BumpRetry));

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 5);

        var o = restored.World.Orders[1];
        Assert.Equal(RecipeKind.Haul, o.Recipe);
        Assert.Equal(Resource.Food, o.Resource);
        Assert.Equal(Farm, o.SourceTile);
        Assert.Equal(Keep, o.SubjectTile);
        Assert.Equal(300, o.Target);
        Assert.Equal(new List<int> { 1 }, o.NamedCrew);
        Assert.Equal(1, o.RetryCount);
        // The DNF trigger survives shape-for-shape.
        var clause = Assert.Single(o.Trigger.Any);
        Assert.Equal(2, clause.All.Count);
        Assert.Equal(PredicateKind.StockBelow, clause.All[0].Kind);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void SetOrderIntent_SurvivesDurableJsonRoundTrip()
    {
        var original = new SetOrderIntent(SupplyLine());

        var (typeName, json) = Sim.Persistence.IntentJson.Serialize(original);
        var back = Assert.IsType<SetOrderIntent>(
            Sim.Persistence.IntentJson.Deserialize(typeName, json));

        Assert.Equal("SetOrderIntent", typeName);
        Assert.Equal(RecipeKind.Haul, back.Definition.Recipe);
        Assert.Equal(Keep, back.Definition.SubjectTile);
        Assert.Equal(Farm, back.Definition.SourceTile);
        Assert.Equal(new List<int> { 1 }, back.Definition.NamedCrew);
        var clause = Assert.Single(back.Definition.Trigger.Any);
        Assert.Equal(2, clause.All.Count);
    }

    [Fact]
    public void Headline_ReplayFromIntentLog_HashesMatch()
    {
        // THE contract that makes an out-of-sim brain legal: the intent log
        // records the driver's OUTPUTS, so replaying it with no driver at
        // all reproduces the world exactly. Inherited from M16's
        // BanditDriverTests and re-proven for the substrate.
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(SupplyLine(crew: CrewMode.Pull)));
        var driver = Driver(thinkPeriod: 30);

        // RUN THEN THINK — the GameHost order (the driver thinks right
        // after Run). Thinking first front-loads the driver's intents ahead
        // of the same tick's scheduled events, which assigns different Seq
        // numbers to in-flight anchors than a chronological replay does, and
        // those anchors are hashed. Same trap the M18 disciplines name.
        for (long t = 0; t <= 600; t += 10)
        {
            sim.Run(t);
            driver.Think(sim, t);
        }
        sim.Run(600);

        // Advance the clock to the tick FIRST, then submit that tick's
        // intents in Seq order — submitting before running front-loads them
        // and reorders same-tick execution. Rejections replay too: they
        // mutate nothing but still consume a Seq, and Seq is hashed.
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
        replay.Run(600);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(replay));
    }

    [Fact]
    public void Journal_IsPresentationOnly_AndNeverAffectsTheWorld()
    {
        // The journal is an OBSERVATION of the driver, not an input to it:
        // a server that drops it produces a byte-identical world.
        var withJournal = BuildWorld();
        Submit(withJournal, 0, new SetOrderIntent(SupplyLine()));
        var d1 = new SubstrateDriver(
            new AutomationConfig { ThinkPeriodTicks = 30 }, new OrderJournal(capacity: 8192));

        var tiny = BuildWorld();
        Submit(tiny, 0, new SetOrderIntent(SupplyLine()));
        var d2 = new SubstrateDriver(
            new AutomationConfig { ThinkPeriodTicks = 30 }, new OrderJournal(capacity: 1));

        for (long t = 0; t <= 300; t += 10)
        {
            d1.Think(withJournal, t); withJournal.Run(t);
            d2.Think(tiny, t);        tiny.Run(t);
        }

        Assert.Equal(Snapshot.Hash(withJournal), Snapshot.Hash(tiny));
        Assert.Equal(1, d2.Journal.Capacity);   // ring really did bound it
    }
}
