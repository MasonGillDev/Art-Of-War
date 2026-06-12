using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// M18 Phase B — AdvanceOrderCursorIntent: the single mutation path for the
// StandingOrder cursor block. Op semantics, the ExpectedStep fence, owner
// defense-in-depth, and the replay contract (cursor intents in the log
// reproduce cursor state without any driver).
public class AutomationCursorTests
{
    private static Simulation BuildWorld()
    {
        var grid = new TileGrid(10, 10, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        world.AddUnit(new Unit(3, new TileCoord(1, 1)) { Role = UnitRole.Hauler });
        world.NextUnitId = 4;
        return new Simulation(world, seed: 7);
    }

    private static IntentOutcome Submit(Simulation sim, long at, Intent intent)
    {
        sim.SubmitIntent(at, intent);
        sim.Run(at);
        var ev = Assert.IsType<IntentEvent>(sim.ResolvedLog[^1]);
        return ev.Outcome;
    }

    private static void InstallTwoStepLoop(Simulation sim, long at = 0)
    {
        var outcome = Submit(sim, at, new SetStandingOrderIntent(OrderKind.Route, LoopMode.Loop,
            claimedUnits: new[] { 3 },
            steps: new List<OrderStep>
            {
                new() { Action = ActionSpec.MoveTo(3, new TileCoord(6, 6)) },
                new() { Action = ActionSpec.MoveTo(3, new TileCoord(1, 1)) },
            }));
        Assert.False(outcome.IsRejected);
    }

    [Fact]
    public void MarkDispatched_SetsFence_DoubleDispatchRejects()
    {
        var sim = BuildWorld();
        InstallTwoStepLoop(sim);

        Assert.False(Submit(sim, 0, new AdvanceOrderCursorIntent(1, CursorOp.MarkDispatched, expectedStep: 0)).IsRejected);
        Assert.True(sim.World.StandingOrders[1].ActionDispatched);

        var dup = Submit(sim, 0, new AdvanceOrderCursorIntent(1, CursorOp.MarkDispatched, expectedStep: 0));
        Assert.True(dup.IsRejected);
        Assert.Contains("already dispatched", dup.Reason);
    }

    [Fact]
    public void AdvanceStep_ResetsCursorBlock_AnchorsStepEnteredTick()
    {
        var sim = BuildWorld();
        InstallTwoStepLoop(sim);
        Submit(sim, 0, new AdvanceOrderCursorIntent(1, CursorOp.MarkDispatched, expectedStep: 0));
        Submit(sim, 0, new AdvanceOrderCursorIntent(1, CursorOp.BumpRetry, expectedStep: 0));

        // Advance at a LATER tick so the re-anchored StepEnteredTick is provable.
        var outcome = Submit(sim, 50, new AdvanceOrderCursorIntent(1, CursorOp.AdvanceStep, expectedStep: 0));
        Assert.False(outcome.IsRejected);

        var order = sim.World.StandingOrders[1];
        Assert.Equal(1, order.CurrentStep);
        Assert.Equal(50, order.StepEnteredTick);
        Assert.Equal(0, order.StepRetryCount);
        Assert.False(order.ActionDispatched);
        Assert.True(order.Enabled);
    }

    [Fact]
    public void AdvanceStep_PastLastStep_LoopWraps_OnceDisables()
    {
        var loopSim = BuildWorld();
        InstallTwoStepLoop(loopSim);
        Submit(loopSim, 0, new AdvanceOrderCursorIntent(1, CursorOp.AdvanceStep, expectedStep: 0));
        Submit(loopSim, 0, new AdvanceOrderCursorIntent(1, CursorOp.AdvanceStep, expectedStep: 1));
        Assert.Equal(0, loopSim.World.StandingOrders[1].CurrentStep); // wrapped
        Assert.True(loopSim.World.StandingOrders[1].Enabled);

        var onceSim = BuildWorld();
        Assert.False(Submit(onceSim, 0, new SetStandingOrderIntent(OrderKind.Route, LoopMode.Once,
            claimedUnits: new[] { 3 },
            steps: new List<OrderStep> { new() { Action = ActionSpec.MoveTo(3, new TileCoord(6, 6)) } })).IsRejected);
        Submit(onceSim, 0, new AdvanceOrderCursorIntent(1, CursorOp.AdvanceStep, expectedStep: 0));
        var order = onceSim.World.StandingOrders[1];
        Assert.False(order.Enabled);  // Once-mode completion
        Assert.Equal(0, order.CurrentStep);
    }

    [Fact]
    public void BumpRetry_Increments_AndClearsDispatchFence()
    {
        var sim = BuildWorld();
        InstallTwoStepLoop(sim);
        Submit(sim, 0, new AdvanceOrderCursorIntent(1, CursorOp.MarkDispatched, expectedStep: 0));

        Submit(sim, 0, new AdvanceOrderCursorIntent(1, CursorOp.BumpRetry, expectedStep: 0));
        Submit(sim, 0, new AdvanceOrderCursorIntent(1, CursorOp.BumpRetry, expectedStep: 0));

        var order = sim.World.StandingOrders[1];
        Assert.Equal(2, order.StepRetryCount);
        Assert.False(order.ActionDispatched); // cleared → next think re-dispatches
    }

    [Fact]
    public void Disable_Disables_DoubleDisableRejects()
    {
        var sim = BuildWorld();
        InstallTwoStepLoop(sim);

        Assert.False(Submit(sim, 0, new AdvanceOrderCursorIntent(1, CursorOp.Disable, expectedStep: 0)).IsRejected);
        Assert.False(sim.World.StandingOrders[1].Enabled);

        Assert.True(Submit(sim, 0, new AdvanceOrderCursorIntent(1, CursorOp.Disable, expectedStep: 0)).IsRejected);
    }

    [Fact]
    public void Fence_StaleExpectedStep_Rejects_NothingMutates()
    {
        var sim = BuildWorld();
        InstallTwoStepLoop(sim);
        Submit(sim, 0, new AdvanceOrderCursorIntent(1, CursorOp.AdvanceStep, expectedStep: 0)); // now at step 1

        var stale = Submit(sim, 0, new AdvanceOrderCursorIntent(1, CursorOp.AdvanceStep, expectedStep: 0));
        Assert.True(stale.IsRejected);
        Assert.Contains("cursor fence", stale.Reason);
        Assert.Equal(1, sim.World.StandingOrders[1].CurrentStep);
    }

    [Fact]
    public void WrongOwner_Rejects()
    {
        var sim = BuildWorld();
        InstallTwoStepLoop(sim);

        var outcome = Submit(sim, 0,
            new AdvanceOrderCursorIntent(1, CursorOp.AdvanceStep, expectedStep: 0) { PlayerId = 1 });
        Assert.True(outcome.IsRejected);
        Assert.Contains("not owned", outcome.Reason);
    }

    [Fact]
    public void MissingOrder_Rejects()
    {
        var sim = BuildWorld();
        var outcome = Submit(sim, 0, new AdvanceOrderCursorIntent(99, CursorOp.AdvanceStep, expectedStep: 0));
        Assert.True(outcome.IsRejected);
    }

    // The Phase B replay contract: a log containing Set + cursor intents,
    // re-fed to a fresh sim (no driver anywhere), reproduces the exact
    // cursor state — same chronological interleave discipline as the M16
    // headline (Run to `at`, then submit).
    [Fact]
    public void Replay_WithCursorIntents_ReproducesCursorState()
    {
        var live = BuildWorld();
        InstallTwoStepLoop(live, at: 0);
        Submit(live, 10, new AdvanceOrderCursorIntent(1, CursorOp.MarkDispatched, expectedStep: 0));
        Submit(live, 25, new AdvanceOrderCursorIntent(1, CursorOp.AdvanceStep, expectedStep: 0));
        Submit(live, 40, new AdvanceOrderCursorIntent(1, CursorOp.BumpRetry, expectedStep: 1));

        var replay = BuildWorld();
        foreach (var (at, intent) in live.IntentLog)
        {
            replay.Run(until: at);
            replay.SubmitIntent(at, intent);
        }
        replay.Run(until: live.Now);

        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(replay));
        var order = replay.World.StandingOrders[1];
        Assert.Equal(1, order.CurrentStep);
        Assert.Equal(25, order.StepEnteredTick);
        Assert.Equal(1, order.StepRetryCount);
    }
}
