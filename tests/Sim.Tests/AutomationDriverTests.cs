using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server.Automation;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M18 Phase D — the AutomationDriver + OrderRunner: step sequencing through
// real movement, the M16 pitfall regressions (mid-march units are not
// re-ordered; rejected steps can't wedge forever), the fog contract at the
// driver level, and cold-start resume. Tests drive Think() by hand between
// Run() calls — no clock thread (the BanditDriverTests harness shape).
public class AutomationDriverTests
{
    private static Simulation MakeWorld(out GameWorld world)
    {
        var grid = new TileGrid(64, 64, Biome.Grassland);
        world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        var castle = world.AddStructure(new Castle(new TileCoord(5, 5)) { OwnerId = 0 });
        castle.Deposit(Resource.Wood, 30);
        world.AddUnit(new Unit(3, new TileCoord(6, 5)) { Role = UnitRole.Hauler });
        world.NextUnitId = 4;
        return new Simulation(world, seed: 0xA77);
    }

    private static void RunWithDriver(Simulation sim, AutomationDriver driver, long until, long step)
    {
        for (var t = sim.Now; t <= until; t += step)
        {
            sim.Run(until: t);
            driver.Think(sim, t);
        }
        sim.Run(until: until);
    }

    private static void Install(Simulation sim, SetStandingOrderIntent intent)
    {
        sim.SubmitIntent(sim.Now, intent);
        sim.Run(sim.Now);
        var ev = Assert.IsType<IntentEvent>(sim.ResolvedLog[^1]);
        Assert.False(ev.Outcome.IsRejected, ev.Outcome.Reason);
    }

    private static int CountIntents<T>(Simulation sim) =>
        sim.IntentLog.Count(e => e.Intent is T);

    [Fact]
    public void Disabled_SubmitsNothing()
    {
        var sim = MakeWorld(out var world);
        Install(sim, new SetStandingOrderIntent(OrderKind.Route, LoopMode.Once,
            new[] { 3 },
            new List<OrderStep> { new() { Action = ActionSpec.MoveTo(3, new TileCoord(9, 5)) } }));

        var driver = new AutomationDriver(new AutomationConfig { Enabled = false, ThinkPeriodTicks = 10 });
        RunWithDriver(sim, driver, until: 2_000, step: 10);

        Assert.Equal(1, sim.IntentLog.Count); // just the Set
        Assert.Equal(new TileCoord(6, 5), world.Units[3].Position);
    }

    [Fact]
    public void OnceMoveOrder_Completes_ParksDisabled()
    {
        var sim = MakeWorld(out var world);
        Install(sim, new SetStandingOrderIntent(OrderKind.Route, LoopMode.Once,
            new[] { 3 },
            new List<OrderStep> { new() { Action = ActionSpec.MoveTo(3, new TileCoord(9, 5)) } }));

        var driver = new AutomationDriver(new AutomationConfig { ThinkPeriodTicks = 10 });
        RunWithDriver(sim, driver, until: 20_000, step: 10);

        Assert.Equal(new TileCoord(9, 5), world.Units[3].Position);
        var order = world.StandingOrders[1];
        Assert.False(order.Enabled);     // Once-mode completion
        Assert.Equal(0, order.CurrentStep);

        // THE M16 PITFALL REGRESSION: a marching unit reads Activity.Idle;
        // a driver that guards on Activity instead of the movement anchors
        // re-submits the move every think. Exactly ONE MoveIntent total.
        Assert.Equal(1, CountIntents<MoveIntent>(sim));
    }

    [Fact]
    public void LoopRoute_Wraps_AndKeepsRunning()
    {
        var sim = MakeWorld(out var world);
        Install(sim, new SetStandingOrderIntent(OrderKind.Route, LoopMode.Loop,
            new[] { 3 },
            new List<OrderStep>
            {
                new() { Action = ActionSpec.MoveTo(3, new TileCoord(9, 5)) },
                new() { Action = ActionSpec.MoveTo(3, new TileCoord(6, 5)) },
            }));

        var driver = new AutomationDriver(new AutomationConfig { ThinkPeriodTicks = 10 });
        RunWithDriver(sim, driver, until: 60_000, step: 10);

        var order = world.StandingOrders[1];
        Assert.True(order.Enabled);
        // At least one full lap and into the next: 3+ MoveIntents.
        Assert.True(CountIntents<MoveIntent>(sim) >= 3,
            $"route never lapped ({CountIntents<MoveIntent>(sim)} moves)");
    }

    [Fact]
    public void RejectedAction_BoundedRetry_AutoDisables_ConfigDerived()
    {
        var sim = MakeWorld(out var world);
        // Source tile (20,20) holds nothing — HaulIntent rejects at
        // resolution every time.
        Install(sim, new SetStandingOrderIntent(OrderKind.SupplyLine, LoopMode.Loop,
            new[] { 3 },
            new List<OrderStep>
            {
                new() { Action = ActionSpec.HaulTrip(3, new TileCoord(20, 20), new TileCoord(5, 5), Resource.Wood) },
            }));

        var cfg = new AutomationConfig { ThinkPeriodTicks = 10, MaxStepRetries = 3 };
        var driver = new AutomationDriver(cfg);
        RunWithDriver(sim, driver, until: 2_000, step: 10);

        var order = world.StandingOrders[1];
        Assert.False(order.Enabled); // gave up — the anti-wedge rule
        Assert.Equal(cfg.MaxStepRetries - 1, order.StepRetryCount);
        // Dispatch happened exactly MaxStepRetries times, then the disable.
        Assert.Equal(cfg.MaxStepRetries, CountIntents<HaulIntent>(sim));
        Assert.Equal(1, sim.IntentLog.Count(e =>
            e.Intent is AdvanceOrderCursorIntent { Op: CursorOp.Disable }));
    }

    [Fact]
    public void WaitingOnCondition_IsNotFailure_NeverBumpsRetry()
    {
        var sim = MakeWorld(out var world);
        // Castle holds 30 Wood; the gate wants 1000 — a legitimate wait.
        Install(sim, new SetStandingOrderIntent(OrderKind.SupplyLine, LoopMode.Loop,
            new[] { 3 },
            new List<OrderStep>
            {
                new()
                {
                    Conditions = new List<ConditionSpec>
                        { ConditionSpec.StoreAtLeast(new TileCoord(5, 5), Resource.Wood, 1000) },
                    Action = ActionSpec.MoveTo(3, new TileCoord(9, 5)),
                },
            }));

        var driver = new AutomationDriver(new AutomationConfig { ThinkPeriodTicks = 10, MaxStepRetries = 3 });
        RunWithDriver(sim, driver, until: 5_000, step: 10);

        var order = world.StandingOrders[1];
        Assert.True(order.Enabled);
        Assert.Equal(0, order.StepRetryCount);
        Assert.Equal(0, CountIntents<MoveIntent>(sim)); // never dispatched
    }

    [Fact]
    public void ManuallyBusiedUnit_StallsStep_DriverDoesNotFight()
    {
        var sim = MakeWorld(out var world);
        Install(sim, new SetStandingOrderIntent(OrderKind.Route, LoopMode.Once,
            new[] { 3 },
            new List<OrderStep> { new() { Action = ActionSpec.MoveTo(3, new TileCoord(9, 5)) } }));

        // The PLAYER sends the claimed unit somewhere else first.
        sim.SubmitIntent(0, new MoveIntent(3, new TileCoord(6, 20)));

        var driver = new AutomationDriver(new AutomationConfig { ThinkPeriodTicks = 10 });
        RunWithDriver(sim, driver, until: 60_000, step: 10);

        // Manual move happened (unit passed through (6,20)'s chain), THEN
        // the order ran. Final position = the order's destination; exactly
        // two MoveIntents total (manual + one automated, no fighting).
        Assert.Equal(new TileCoord(9, 5), world.Units[3].Position);
        Assert.Equal(2, CountIntents<MoveIntent>(sim));
        Assert.False(world.StandingOrders[1].Enabled); // Once completed
    }

    [Fact]
    public void FogContract_UnseenSource_NoDispatch_UntilScouted()
    {
        var sim = MakeWorld(out var world);
        // A stocked foreign stockpile far outside every vision source.
        var farPile = world.AddStructure(new Stockpile(new TileCoord(50, 50)) { OwnerId = 1 });
        farPile.Deposit(Resource.Wood, 50);

        Install(sim, new SetStandingOrderIntent(OrderKind.SupplyLine, LoopMode.Loop,
            new[] { 3 },
            new List<OrderStep>
            {
                new()
                {
                    Conditions = new List<ConditionSpec>
                        { ConditionSpec.StoreAtLeast(new TileCoord(50, 50), Resource.Wood, 1) },
                    Action = ActionSpec.HaulTrip(3, new TileCoord(50, 50), new TileCoord(5, 5), Resource.Wood),
                },
            }));

        var driver = new AutomationDriver(new AutomationConfig { ThinkPeriodTicks = 10 });
        RunWithDriver(sim, driver, until: 2_000, step: 10);

        // Unseen → the condition can never be met → nothing dispatched.
        Assert.Equal(0, CountIntents<HaulIntent>(sim));

        // Scout it: a player-0 unit appears next to the pile (stand-in for
        // a real scouting trip; vision is positional). The driver's think
        // gate sits at t=2000 from the first run (sim.Now never advanced —
        // no events fired), so walk the virtual clock past it.
        world.AddUnit(new Unit(7, new TileCoord(49, 50)) { Role = UnitRole.Scout });
        RunWithDriver(sim, driver, until: 2_500, step: 10);

        Assert.True(CountIntents<HaulIntent>(sim) >= 1, "scouted source still not dispatched");
    }

    [Fact]
    public void ColdStart_DispatchedCursorWithNoBrain_RecoversAndCompletes()
    {
        var sim = MakeWorld(out var world);
        Install(sim, new SetStandingOrderIntent(OrderKind.Route, LoopMode.Once,
            new[] { 3 },
            new List<OrderStep> { new() { Action = ActionSpec.MoveTo(3, new TileCoord(9, 5)) } }));

        // Simulate "the old process dispatched and died": the durable
        // cursor says dispatched, but no driver holds the intent reference.
        sim.SubmitIntent(0, new AdvanceOrderCursorIntent(1, CursorOp.MarkDispatched, 0));
        sim.Run(0);
        Assert.True(world.StandingOrders[1].ActionDispatched);

        var driver = new AutomationDriver(new AutomationConfig { ThinkPeriodTicks = 10, MaxStepRetries = 5 });
        RunWithDriver(sim, driver, until: 20_000, step: 10);

        // BumpRetry cleared the stale fence; the step re-evaluated,
        // re-dispatched, and ran to completion.
        Assert.Equal(new TileCoord(9, 5), world.Units[3].Position);
        Assert.False(world.StandingOrders[1].Enabled); // Once completed
    }

    [Fact]
    public void ClearedOrder_RunnerCensusedOut_NoStrayIntents()
    {
        var sim = MakeWorld(out var world);
        Install(sim, new SetStandingOrderIntent(OrderKind.Route, LoopMode.Loop,
            new[] { 3 },
            new List<OrderStep>
            {
                new()
                {
                    Conditions = new List<ConditionSpec> { ConditionSpec.ElapsedTicks(1_000_000) },
                    Action = ActionSpec.MoveTo(3, new TileCoord(9, 5)),
                },
            }));

        var driver = new AutomationDriver(new AutomationConfig { ThinkPeriodTicks = 10 });
        RunWithDriver(sim, driver, until: 500, step: 10);

        sim.SubmitIntent(sim.Now, new ClearStandingOrderIntent(1));
        RunWithDriver(sim, driver, until: sim.Now + 1_000, step: 10);

        Assert.Empty(world.StandingOrders);
        Assert.Equal(0, CountIntents<MoveIntent>(sim));
        Assert.Equal(0, CountIntents<AdvanceOrderCursorIntent>(sim));
    }
}
