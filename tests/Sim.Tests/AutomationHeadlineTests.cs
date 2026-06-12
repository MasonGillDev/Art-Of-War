using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.World;
using Sim.Server.Automation;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M18 Phase E — the order TEMPLATES (the data shapes the client UI will
// emit; the engine knows only steps) and the milestone HEADLINE: a live run
// with the AutomationDriver, replayed from the intent log with NO driver,
// lands on the identical hash. Out-of-sim evaluation, in-sim truth.
public static class AutomationTemplates
{
    // SupplyLine: keep `dest` stocked with `resource` while it reads below
    // `keepBelow`, hauling from `source` with one claimed hauler. One
    // condition + one HaulTrip, looped — 80% of the Factorio feeling.
    public static SetStandingOrderIntent SupplyLine(
        int haulerId, TileCoord source, TileCoord dest, Resource resource, long keepBelow) =>
        new(OrderKind.SupplyLine, LoopMode.Loop,
            claimedUnits: new[] { haulerId },
            steps: new List<OrderStep>
            {
                new()
                {
                    Conditions = new List<ConditionSpec>
                        { ConditionSpec.StoreBelow(dest, resource, keepBelow) },
                    Action = ActionSpec.HaulTrip(haulerId, source, dest, resource),
                },
            });

    // Route: a train-schedule loop. Each stop is "wait until <condition>,
    // then move there" — the wait gates DEPARTURE toward that stop, so
    // (stopA, Always) → (stopB, CargoFull(unit)) reads "go to A; once
    // cargo is full, go to B".
    public static SetStandingOrderIntent Route(
        int unitId, params (TileCoord Stop, ConditionSpec? Wait)[] stops) =>
        new(OrderKind.Route, LoopMode.Loop,
            claimedUnits: new[] { unitId },
            steps: stops.Select(s =>
            {
                var step = new OrderStep { Action = ActionSpec.MoveTo(unitId, s.Stop) };
                if (s.Wait is { } w) step.Conditions.Add(w);
                return step;
            }).ToList());

    // StandingProduction: "keep doing X while the gate holds" — here the
    // Train flavor (the Craft flavor is the same shape with
    // ActionSpec.Craft). Once-mode for one-shot training; Loop for
    // keep-training-while.
    public static SetStandingOrderIntent TrainWhile(
        int unitId, UnitRole role, ConditionSpec gate, LoopMode loop = LoopMode.Once) =>
        new(OrderKind.StandingProduction, loop,
            claimedUnits: new[] { unitId },
            steps: new List<OrderStep>
            {
                new() { Conditions = new List<ConditionSpec> { gate }, Action = ActionSpec.Train(unitId, role) },
            });
}

public class AutomationHeadlineTests
{
    // Deterministic hand-built scenario, rebuildable identically for the
    // replay leg. Exercises all three templates at once:
    //   * supply line draining a pre-stocked camp into the castle,
    //   * a two-stop route lapping continuously,
    //   * a one-shot Train order gated on castle stock.
    private static Simulation MakeScenario(out GameWorld world)
    {
        var grid = new TileGrid(64, 64, Biome.Grassland);
        grid.SetBiome(new TileCoord(10, 5), Biome.Forest);
        world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        var castle = world.AddStructure(new Castle(new TileCoord(5, 5)) { OwnerId = 0 });
        castle.Deposit(Resource.Food, 50);

        var camp = world.AddStructure(new Extractor(StructureKind.LumberCamp, new TileCoord(10, 5)) { OwnerId = 0 });
        camp.Buffer = 20;
        camp.TickArmed = false; // no workers, no production — fixed stock

        world.AddStructure(new Barracks(new TileCoord(8, 8)) { OwnerId = 0 });

        world.AddUnit(new Unit(3, new TileCoord(6, 5)) { Role = UnitRole.Hauler });   // supply hauler
        world.AddUnit(new Unit(4, new TileCoord(6, 8)) { Role = UnitRole.None });      // route walker
        world.AddUnit(new Unit(5, new TileCoord(8, 8)) { Role = UnitRole.None });      // trainee on the Barracks
        world.NextUnitId = 6;
        return new Simulation(world, seed: 0x5EED);
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

    [Fact]
    public void Headline_ReplayFromIntentLog_HashesMatch()
    {
        // ---- live leg: driver evaluating, all templates installed ----
        var live = MakeScenario(out var liveWorld);
        live.SubmitIntent(0, AutomationTemplates.SupplyLine(
            3, source: new TileCoord(10, 5), dest: new TileCoord(5, 5), Resource.Wood, keepBelow: 100));
        live.SubmitIntent(0, AutomationTemplates.Route(4,
            (new TileCoord(12, 8), null),
            (new TileCoord(6, 8), null)));
        live.SubmitIntent(0, AutomationTemplates.TrainWhile(5, UnitRole.Soldier,
            ConditionSpec.StoreAtLeast(new TileCoord(5, 5), Resource.Food, 10)));

        var driver = new AutomationDriver(new AutomationConfig { ThinkPeriodTicks = 30, MaxStepRetries = 4 });
        RunWithDriver(live, driver, until: 40_000, step: 30);

        // The automation did real work (this is a headline about a live
        // system, not an empty log): goods moved, the trainee trained.
        Assert.True(liveWorld.Structures[new TileCoord(5, 5)] is Castle c && c.AmountOf(Resource.Wood) > 0,
            "supply line moved no wood");
        Assert.Equal(UnitRole.Soldier, liveWorld.Units[5].Role);
        Assert.True(live.IntentLog.Count(e => e.Intent is AdvanceOrderCursorIntent) > 0,
            "no cursor intents were logged");

        // ---- replay leg: identical world, the captured log, NO driver ----
        var replay = MakeScenario(out _);
        foreach (var (at, intent) in live.IntentLog)
        {
            // Chronological interleave (the M16 replay discipline): run TO
            // the submission tick, then submit, so same-tick Seq ordering
            // matches the live run.
            replay.Run(until: at);
            replay.SubmitIntent(at, intent);
        }
        replay.Run(until: live.Now);

        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(replay));
    }

    [Fact]
    public void SupplyLine_StopsWhenTargetMet_ResumesWhenDrained()
    {
        var sim = MakeScenario(out var world);
        // Deep stock at the source so the line can serve TWO rounds — the
        // scenario default (20) fits in one hauler load and would leave the
        // resumed line with an empty source.
        ((Extractor)world.Structures[new TileCoord(10, 5)]).Buffer = 100;
        // keepBelow 10: the stop condition is re-checked per loop lap, so
        // the line parks once the castle reads >= 10 wood.
        sim.SubmitIntent(0, AutomationTemplates.SupplyLine(
            3, source: new TileCoord(10, 5), dest: new TileCoord(5, 5), Resource.Wood, keepBelow: 10));

        var driver = new AutomationDriver(new AutomationConfig { ThinkPeriodTicks = 30, MaxStepRetries = 4 });
        RunWithDriver(sim, driver, until: 40_000, step: 30);

        var castle = (Castle)world.Structures[new TileCoord(5, 5)];
        Assert.True(castle.AmountOf(Resource.Wood) >= 10, "line never met its target");
        Assert.True(sim.World.StandingOrders[1].Enabled, "a satisfied line must stay armed, not disable");

        // Drain below the threshold; the same standing order resumes. The
        // driver's think gate sits at ~40_000 from leg 1 (sim.Now lags it —
        // events stopped when the line parked), so the second horizon must
        // leave room past the gate for a full haul round-trip at march pace.
        var before = castle.AmountOf(Resource.Wood);
        castle.Withdraw(Resource.Wood, before);
        RunWithDriver(sim, driver, until: 100_000, step: 30);
        Assert.True(castle.AmountOf(Resource.Wood) > 0, "drained line never resumed");
    }
}
