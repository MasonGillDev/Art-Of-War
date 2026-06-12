using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// M18 Phase A — the durable order model: SetStandingOrderIntent /
// ClearStandingOrderIntent validation (resolution-time, fail-clean), claim
// exclusivity, the order cap (config-derived, never hard-coded), and the
// snapshot round-trip over every StandingOrder field including the cursor
// block. See docs/m18-automation-engine-spec.md.
public class AutomationOrderTests
{
    // World with two players and a few idle units: 3, 5 owned by player 0;
    // 9 owned by player 1.
    private static Simulation BuildWorld()
    {
        var grid = new TileGrid(10, 10, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        world.AddUnit(new Unit(3, new TileCoord(1, 1)) { Role = UnitRole.Hauler });
        world.AddUnit(new Unit(5, new TileCoord(2, 2)) { Role = UnitRole.Hauler });
        world.AddUnit(new Unit(9, new TileCoord(8, 8)) { Role = UnitRole.Hauler, OwnerId = 1 });
        world.NextUnitId = 10;
        return new Simulation(world, seed: 42);
    }

    private static IntentOutcome Submit(Simulation sim, Intent intent)
    {
        sim.SubmitIntent(sim.Now, intent);
        sim.Run(sim.Now);
        var ev = Assert.IsType<IntentEvent>(sim.ResolvedLog[^1]);
        return ev.Outcome;
    }

    // A minimal always-valid order shape: no claims, one Craft step (the
    // only structure-only atom — Set validates bounds + resource, not
    // craftability; that's the emitted intent's job).
    private static SetStandingOrderIntent MinimalOrder(int playerId = 0) =>
        new(OrderKind.StandingProduction, LoopMode.Loop,
            claimedUnits: Array.Empty<int>(),
            steps: new List<OrderStep>
            {
                new()
                {
                    Conditions = new List<ConditionSpec> { ConditionSpec.StoreAtLeast(new TileCoord(4, 4), Resource.Wood, 5) },
                    Action = ActionSpec.Craft(new TileCoord(4, 4), Resource.Wood),
                },
            })
        { PlayerId = playerId };

    private static SetStandingOrderIntent RouteOrder(int unitId, int playerId = 0) =>
        new(OrderKind.Route, LoopMode.Loop,
            claimedUnits: new[] { unitId },
            steps: new List<OrderStep>
            {
                new() { Action = ActionSpec.MoveTo(unitId, new TileCoord(6, 6)) },
                new()
                {
                    Conditions = new List<ConditionSpec> { ConditionSpec.CargoFull(unitId) },
                    Action = ActionSpec.MoveTo(unitId, new TileCoord(1, 1)),
                },
            })
        { PlayerId = playerId };

    // ---- Set: apply ------------------------------------------------------

    [Fact]
    public void Set_Applies_AssignsId_NormalizesClaims()
    {
        var sim = BuildWorld();
        var intent = new SetStandingOrderIntent(OrderKind.SupplyLine, LoopMode.Loop,
            claimedUnits: new[] { 5, 3 }, // deliberately unsorted
            steps: new List<OrderStep>
            {
                new() { Action = ActionSpec.HaulTrip(3, new TileCoord(2, 2), new TileCoord(0, 0), Resource.Wood) },
            });

        var outcome = Submit(sim, intent);

        Assert.False(outcome.IsRejected);
        var order = Assert.Single(sim.World.StandingOrders).Value;
        Assert.Equal(1, order.OrderId);
        Assert.Equal(2, sim.World.NextOrderId);
        Assert.Equal(new List<int> { 3, 5 }, order.ClaimedUnits); // ascending
        Assert.True(order.Enabled);
        Assert.Equal(0, order.CurrentStep);
        Assert.Equal(sim.Now, order.StepEnteredTick);
        Assert.False(order.ActionDispatched);
        Assert.Equal(OrderKind.SupplyLine, order.Kind);
    }

    [Fact]
    public void Set_CapEnforced_ConfigDerived()
    {
        var sim = BuildWorld();
        for (var i = 0; i < AutomationConstants.MaxOrdersPerPlayer; i++)
            Assert.False(Submit(sim, MinimalOrder()).IsRejected);

        var over = Submit(sim, MinimalOrder());
        Assert.True(over.IsRejected);
        Assert.Contains("order cap", over.Reason);

        // The cap is per-player: player 1 is unaffected.
        Assert.False(Submit(sim, MinimalOrder(playerId: 1)).IsRejected);
    }

    // ---- Set: rejections (fail-clean: nothing mutates) -------------------

    [Fact]
    public void Set_EmptySteps_Rejects()
    {
        var sim = BuildWorld();
        var outcome = Submit(sim, new SetStandingOrderIntent(
            OrderKind.Route, LoopMode.Once, Array.Empty<int>(), new List<OrderStep>()));
        Assert.True(outcome.IsRejected);
        Assert.Empty(sim.World.StandingOrders);
        Assert.Equal(1, sim.World.NextOrderId); // fail-clean: id not consumed
    }

    [Fact]
    public void Set_TooManySteps_Rejects_ConfigDerived()
    {
        var sim = BuildWorld();
        var steps = new List<OrderStep>();
        for (var i = 0; i < AutomationConstants.MaxStepsPerOrder + 1; i++)
            steps.Add(new OrderStep { Action = ActionSpec.Craft(new TileCoord(4, 4), Resource.Wood) });
        var outcome = Submit(sim, new SetStandingOrderIntent(
            OrderKind.Route, LoopMode.Once, Array.Empty<int>(), steps));
        Assert.True(outcome.IsRejected);
        Assert.Contains("steps", outcome.Reason);
    }

    [Fact]
    public void Set_DuplicateClaim_Rejects()
    {
        var sim = BuildWorld();
        var outcome = Submit(sim, new SetStandingOrderIntent(OrderKind.Route, LoopMode.Loop,
            new[] { 3, 3 },
            new List<OrderStep> { new() { Action = ActionSpec.MoveTo(3, new TileCoord(6, 6)) } }));
        Assert.True(outcome.IsRejected);
        Assert.Contains("claimed twice", outcome.Reason);
    }

    [Fact]
    public void Set_MissingClaimedUnit_Rejects()
    {
        var sim = BuildWorld();
        var outcome = Submit(sim, RouteOrder(unitId: 77));
        Assert.True(outcome.IsRejected);
        Assert.Contains("does not exist", outcome.Reason);
    }

    [Fact]
    public void Set_ClaimNotOwned_Rejects()
    {
        var sim = BuildWorld();
        var outcome = Submit(sim, RouteOrder(unitId: 9)); // unit 9 is player 1's
        Assert.True(outcome.IsRejected);
        Assert.Contains("not owned", outcome.Reason);
    }

    [Fact]
    public void Set_ClaimExclusivity_AcrossOrders()
    {
        var sim = BuildWorld();
        Assert.False(Submit(sim, RouteOrder(unitId: 3)).IsRejected);

        var second = Submit(sim, RouteOrder(unitId: 3));
        Assert.True(second.IsRejected);
        Assert.Contains("already claimed by order 1", second.Reason);

        // A different unit is fine.
        Assert.False(Submit(sim, RouteOrder(unitId: 5)).IsRejected);
    }

    [Fact]
    public void Set_ActionOnUnclaimedUnit_Rejects()
    {
        var sim = BuildWorld();
        var outcome = Submit(sim, new SetStandingOrderIntent(OrderKind.Route, LoopMode.Loop,
            claimedUnits: new[] { 3 },
            steps: new List<OrderStep> { new() { Action = ActionSpec.MoveTo(5, new TileCoord(6, 6)) } }));
        Assert.True(outcome.IsRejected);
        Assert.Contains("unclaimed unit 5", outcome.Reason);
    }

    [Fact]
    public void Set_ConditionOnUnclaimedUnit_Rejects()
    {
        var sim = BuildWorld();
        var outcome = Submit(sim, new SetStandingOrderIntent(OrderKind.Route, LoopMode.Loop,
            claimedUnits: new[] { 3 },
            steps: new List<OrderStep>
            {
                new()
                {
                    Conditions = new List<ConditionSpec> { ConditionSpec.CargoFull(5) },
                    Action = ActionSpec.MoveTo(3, new TileCoord(6, 6)),
                },
            }));
        Assert.True(outcome.IsRejected);
        Assert.Contains("unclaimed unit 5", outcome.Reason);
    }

    [Fact]
    public void Set_OutOfBoundsTile_Rejects()
    {
        var sim = BuildWorld();
        var outcome = Submit(sim, new SetStandingOrderIntent(OrderKind.Route, LoopMode.Loop,
            claimedUnits: new[] { 3 },
            steps: new List<OrderStep> { new() { Action = ActionSpec.MoveTo(3, new TileCoord(50, 50)) } }));
        Assert.True(outcome.IsRejected);
        Assert.Contains("out of bounds", outcome.Reason);
    }

    [Fact]
    public void Set_BadThresholds_Reject()
    {
        var sim = BuildWorld();

        var zeroElapsed = Submit(sim, new SetStandingOrderIntent(OrderKind.Route, LoopMode.Loop,
            claimedUnits: new[] { 3 },
            steps: new List<OrderStep>
            {
                new()
                {
                    Conditions = new List<ConditionSpec> { ConditionSpec.ElapsedTicks(0) },
                    Action = ActionSpec.MoveTo(3, new TileCoord(6, 6)),
                },
            }));
        Assert.True(zeroElapsed.IsRejected);

        var noResource = Submit(sim, new SetStandingOrderIntent(OrderKind.SupplyLine, LoopMode.Loop,
            claimedUnits: new[] { 3 },
            steps: new List<OrderStep>
            {
                new() { Action = ActionSpec.HaulTrip(3, new TileCoord(2, 2), new TileCoord(0, 0), Resource.None) },
            }));
        Assert.True(noResource.IsRejected);
        Assert.Contains("no resource", noResource.Reason);
    }

    // ---- Clear -----------------------------------------------------------

    [Fact]
    public void Clear_RemovesOrder_AndReleasesClaims()
    {
        var sim = BuildWorld();
        Assert.False(Submit(sim, RouteOrder(unitId: 3)).IsRejected);

        var clear = Submit(sim, new ClearStandingOrderIntent(orderId: 1));
        Assert.False(clear.IsRejected);
        Assert.Empty(sim.World.StandingOrders);

        // Claim released — the unit can be claimed by a fresh order.
        Assert.False(Submit(sim, RouteOrder(unitId: 3)).IsRejected);
    }

    [Fact]
    public void Clear_WrongOwner_Rejects()
    {
        var sim = BuildWorld();
        Assert.False(Submit(sim, RouteOrder(unitId: 3)).IsRejected);

        var clear = Submit(sim, new ClearStandingOrderIntent(orderId: 1) { PlayerId = 1 });
        Assert.True(clear.IsRejected);
        Assert.Contains("not owned", clear.Reason);
        Assert.Single(sim.World.StandingOrders);
    }

    [Fact]
    public void Clear_MissingOrder_Rejects()
    {
        var sim = BuildWorld();
        var clear = Submit(sim, new ClearStandingOrderIntent(orderId: 99));
        Assert.True(clear.IsRejected);
    }

    // ---- snapshot round-trip ---------------------------------------------

    [Fact]
    public void SnapshotRoundTrip_StandingOrders_AllFields()
    {
        var sim = BuildWorld();

        // One order exercising every condition kind and a spread of action
        // kinds, installed through the intent (the only mutation path).
        var rich = new SetStandingOrderIntent(OrderKind.Route, LoopMode.Loop,
            claimedUnits: new[] { 5, 3 },
            steps: new List<OrderStep>
            {
                new()
                {
                    Conditions = new List<ConditionSpec>
                    {
                        ConditionSpec.Always(),
                        ConditionSpec.StoreAtLeast(new TileCoord(4, 4), Resource.Wood, 20),
                        ConditionSpec.StoreBelow(new TileCoord(0, 0), Resource.Food, 100),
                    },
                    Action = ActionSpec.HaulTrip(3, new TileCoord(4, 4), new TileCoord(0, 0), Resource.Wood),
                },
                new()
                {
                    Conditions = new List<ConditionSpec>
                    {
                        ConditionSpec.CargoFull(3),
                        ConditionSpec.CargoEmpty(5),
                        ConditionSpec.UnitAtTile(5, new TileCoord(2, 2)),
                        ConditionSpec.ElapsedTicks(500),
                    },
                    Action = ActionSpec.Train(5, UnitRole.Soldier),
                },
            });
        Assert.False(Submit(sim, rich).IsRejected);
        Assert.False(Submit(sim, MinimalOrder()).IsRejected);

        // Drive the cursor block off its defaults so the round-trip proves
        // every cursor field (these are the fields AdvanceOrderCursorIntent
        // mutates; direct mutation here stands in for Phase B).
        var live = sim.World.StandingOrders[1];
        live.CurrentStep = 1;
        live.StepEnteredTick = 123;
        live.StepRetryCount = 3;
        live.ActionDispatched = true;
        sim.World.StandingOrders[2].Enabled = false;

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 42);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        var r = restored.World.StandingOrders[1];
        Assert.Equal(live.OwnerId, r.OwnerId);
        Assert.Equal(live.Kind, r.Kind);
        Assert.Equal(live.Loop, r.Loop);
        Assert.Equal(live.ClaimedUnits, r.ClaimedUnits);
        Assert.Equal(live.Steps.Count, r.Steps.Count);
        for (var i = 0; i < live.Steps.Count; i++)
        {
            Assert.Equal(live.Steps[i].Conditions, r.Steps[i].Conditions);
            Assert.Equal(live.Steps[i].Action, r.Steps[i].Action);
        }
        Assert.Equal(live.CurrentStep, r.CurrentStep);
        Assert.Equal(live.StepEnteredTick, r.StepEnteredTick);
        Assert.Equal(live.StepRetryCount, r.StepRetryCount);
        Assert.Equal(live.ActionDispatched, r.ActionDispatched);
        Assert.False(restored.World.StandingOrders[2].Enabled);
        Assert.Equal(sim.World.NextOrderId, restored.World.NextOrderId);
    }
}
