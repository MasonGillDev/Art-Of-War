using Sim.Core;
using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Persistence;
using Sim.Core.World;
using Sim.Server.Automation;

namespace Sim.Tests;

// Automation substrate, Phase D — the Routine program: a named crew walking
// a circuit of stops. The trade-route / patrol primitive, and the successor
// to M18's Route template.
//
// Routine is the one program with a DURABLE CURSOR. Maintain re-derives its
// stage from the world every think, but "which stop is this caravan on"
// genuinely isn't in the world — a unit standing on a tile that appears
// twice in a circuit is either inbound or outbound and the map cannot say
// which. So the cursor is sim state, and these tests pin that it survives a
// restart and that a stale advance fences cleanly.
public class SubstrateRoutineTests
{
    private static readonly TileCoord Depot = new(3, 5);
    private static readonly TileCoord Market = new(9, 5);

    private static Simulation BuildWorld(int depotFood = 500)
    {
        var grid = new TileGrid(16, 12, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        var depot = new Stockpile(Depot) { OwnerId = 0 };
        depot.Holdings[Resource.Food] = depotFood;
        world.AddStructure(depot);
        world.AddStructure(new Stockpile(Market) { OwnerId = 0 });

        world.AddUnit(new Unit(1, Depot) { Role = UnitRole.Hauler, OwnerId = 0 });
        world.AddUnit(new Unit(2, Depot) { Role = UnitRole.Soldier, OwnerId = 0 });  // escort
        world.NextUnitId = 3;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 12; y++)
            for (var x = 0; x < 16; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 13);
    }

    private static IntentOutcome Submit(Simulation sim, long at, Intent intent)
    {
        sim.SubmitIntent(at, intent);
        sim.Run(at);
        return Assert.IsType<IntentEvent>(sim.ResolvedLog[^1]).Outcome;
    }

    // Depot: load food. Market: unload. Round and round.
    private static Order Circuit(params int[] crew)
    {
        var order = new Order
        {
            Program = ProgramKind.Routine,
            SubjectKind = SubjectKind.Structure, SubjectTile = Depot,
            CrewMode = CrewMode.Named,
            Steps =
            {
                new RoutineStep { Tile = Depot, Action = RoutineAction.Load, Resource = Resource.Food },
                new RoutineStep { Tile = Market, Action = RoutineAction.Unload },
            },
        };
        foreach (var c in crew) order.NamedCrew.Add(c);
        return order;
    }

    private static SubstrateDriver Driver() =>
        new(new AutomationConfig { ThinkPeriodTicks = Time.Hour });

    private static void RunFor(Simulation sim, SubstrateDriver driver, long horizon)
    {
        for (long t = 0; t <= horizon; t += Time.Hour) { sim.Run(t); driver.Think(sim, t); }
    }

    // ---- install ---------------------------------------------------------

    [Fact]
    public void SetOrder_InstallsACircuitAndClaimsItsCrew()
    {
        var sim = BuildWorld();

        Assert.False(Submit(sim, 0, new SetOrderIntent(Circuit(1, 2))).IsRejected);

        var order = Assert.Single(sim.World.Orders).Value;
        Assert.Equal(ProgramKind.Routine, order.Program);
        Assert.Equal(2, order.Steps.Count);
        Assert.Equal(0, order.CurrentStep);
        Assert.Equal(2, sim.World.Claims.Count);
    }

    [Fact]
    public void SetOrder_RejectsAPooledCircuit()
    {
        // A circuit's identity IS its crew — rotating a different body
        // through each leg would make the caravan (and its escort)
        // meaningless, so Routine is Named-only.
        var sim = BuildWorld();
        var pooled = new Order
        {
            Program = ProgramKind.Routine,
            SubjectKind = SubjectKind.Structure, SubjectTile = Depot,
            CrewMode = CrewMode.Pull,
            Selector = Selector.OfRole(UnitRole.Hauler, Depot, radius: 10),
            Steps = { new RoutineStep { Tile = Market } },
        };

        var outcome = Submit(sim, 0, new SetOrderIntent(pooled));

        Assert.True(outcome.IsRejected);
        Assert.Contains("Named crew", outcome.Reason);
    }

    [Fact]
    public void SetOrder_RejectsAnEmptyCircuitAndALoadWithoutAResource()
    {
        var sim = BuildWorld();

        var empty = new Order
        {
            Program = ProgramKind.Routine, SubjectKind = SubjectKind.Structure,
            SubjectTile = Depot, CrewMode = CrewMode.Named,
        };
        empty.NamedCrew.Add(1);
        Assert.Contains("at least one stop", Submit(sim, 0, new SetOrderIntent(empty)).Reason);

        var loadless = new Order
        {
            Program = ProgramKind.Routine, SubjectKind = SubjectKind.Structure,
            SubjectTile = Depot, CrewMode = CrewMode.Named,
            Steps = { new RoutineStep { Tile = Depot, Action = RoutineAction.Load } },
        };
        loadless.NamedCrew.Add(1);
        Assert.Contains("needs a resource", Submit(sim, 0, new SetOrderIntent(loadless)).Reason);
    }

    // ---- running the circuit --------------------------------------------

    [Fact]
    public void Routine_LoadsAtOneStopAndUnloadsAtTheNext()
    {
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(Circuit(1)));
        var driver = Driver();

        RunFor(sim, driver, 3 * Time.Day);

        // Freight actually moved from the depot to the market.
        var market = (StorageStructure)sim.World.Structures[Market];
        Assert.True(market.AmountOf(Resource.Food) > 0,
            "the circuit never delivered anything to the market");
    }

    [Fact]
    public void Routine_Loops_SoTheCursorKeepsComingBackAround()
    {
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(Circuit(1)));
        var driver = Driver();

        RunFor(sim, driver, 6 * Time.Day);

        var market = (StorageStructure)sim.World.Structures[Market];
        // More than one hauler-load (25) means it went round more than once.
        Assert.True(market.AmountOf(Resource.Food) > 25,
            $"circuit did not loop: market holds {market.AmountOf(Resource.Food)}");
    }

    [Fact]
    public void Routine_MovesTheWholeCrewTogether_EscortIncluded()
    {
        // A caravan is one thing. The circuit only advances once every
        // member has arrived — which is what lets an escort actually be
        // present when the caravan is ambushed.
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(Circuit(1, 2)));
        var driver = Driver();

        RunFor(sim, driver, 2 * Time.Day);

        var hauler = sim.World.Units[1];
        var escort = sim.World.Units[2];
        Assert.Equal(hauler.Position, escort.Position);
    }

    [Fact]
    public void Routine_HoldsAtAStop_UntilItsDepartureConditionIsMet()
    {
        // The train-schedule primitive: wait here until the load is worth
        // carrying. Holding is legitimate WAITING, never a stall, so it
        // must not burn the retry budget.
        var sim = BuildWorld(depotFood: 0);
        var order = Circuit(1);
        order.Steps[0].DepartWhen.Add(Predicate.StockAtLeast(Depot, Resource.Food, 100));
        Submit(sim, 0, new SetOrderIntent(order));
        var driver = Driver();

        RunFor(sim, driver, 2 * Time.Day);

        Assert.Equal(0, sim.World.Orders[1].CurrentStep);     // never departed
        Assert.Equal(0, sim.World.Orders[1].RetryCount);      // and never penalised
        Assert.True(sim.World.Orders[1].Enabled);
        Assert.Contains(driver.Journal.For(1), e => e.Outcome == JournalOutcome.Waiting);
    }

    [Fact]
    public void Routine_WithADeadCrew_GoesDarkAndSaysSo()
    {
        // Law 4 for circuits: a Named crew cannot be refilled by a pull, so
        // a caravan whose people are killed is genuinely finished.
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(Circuit(1)));
        sim.World.Units.Remove(1);
        var driver = new SubstrateDriver(
            new AutomationConfig { ThinkPeriodTicks = Time.Hour, RetryBudget = 3 });

        RunFor(sim, driver, 1 * Time.Day);

        Assert.False(sim.World.Orders[1].Enabled);
        Assert.Contains(driver.Journal.For(1), e => e.Outcome == JournalOutcome.NoCrew);
    }

    // ---- the cursor ------------------------------------------------------

    [Fact]
    public void AdvanceStep_WrapsAtTheEnd_AndFencesOnAStaleCursor()
    {
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(Circuit(1)));

        Assert.False(Submit(sim, 0,
            new OrderStatusIntent(1, OrderStatusOp.AdvanceStep, expectedStep: 0)).IsRejected);
        Assert.Equal(1, sim.World.Orders[1].CurrentStep);

        // Wrap.
        Assert.False(Submit(sim, 0,
            new OrderStatusIntent(1, OrderStatusOp.AdvanceStep, expectedStep: 1)).IsRejected);
        Assert.Equal(0, sim.World.Orders[1].CurrentStep);

        // Stale advance (the driver observed step 1, the cursor is at 0).
        var stale = Submit(sim, 0,
            new OrderStatusIntent(1, OrderStatusOp.AdvanceStep, expectedStep: 1));
        Assert.True(stale.IsRejected);
        Assert.Contains("cursor fence", stale.Reason);
        Assert.Equal(0, sim.World.Orders[1].CurrentStep);
    }

    [Fact]
    public void Circuit_RoundTripsTheSnapshot_CursorAndAll()
    {
        var sim = BuildWorld();
        var order = Circuit(1, 2);
        order.Steps[0].DepartWhen.Add(Predicate.StockAtLeast(Depot, Resource.Food, 60));
        Submit(sim, 0, new SetOrderIntent(order));
        Submit(sim, 0, new OrderStatusIntent(1, OrderStatusOp.AdvanceStep, expectedStep: 0));

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 13);

        var o = restored.World.Orders[1];
        Assert.Equal(ProgramKind.Routine, o.Program);
        Assert.Equal(2, o.Steps.Count);
        Assert.Equal(RoutineAction.Load, o.Steps[0].Action);
        Assert.Equal(Resource.Food, o.Steps[0].Resource);
        Assert.Equal(Market, o.Steps[1].Tile);
        // The caravan resumes mid-circuit rather than restarting.
        Assert.Equal(1, o.CurrentStep);
        var depart = Assert.Single(o.Steps[0].DepartWhen);
        Assert.Equal(PredicateKind.StockAtLeast, depart.Kind);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void Headline_RoutineReplaysFromTheIntentLog()
    {
        var sim = BuildWorld();
        Submit(sim, 0, new SetOrderIntent(Circuit(1, 2)));
        var driver = Driver();
        var horizon = 4 * Time.Day;

        RunFor(sim, driver, horizon);

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
        replay.Run(horizon);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(replay));
    }
}
