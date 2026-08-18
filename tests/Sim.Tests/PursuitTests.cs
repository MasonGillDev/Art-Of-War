using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// M29 Phase A — the chase (docs/patrols.md).
//
// Pursuit is a SIM verb, not a driver behaviour: the driver thinks once per
// game-hour, and a chase re-evaluated hourly is not a chase. The anchor
// re-paths one hop at a time inside MoveArrivalEvent, so everything here is
// exercised WITHOUT any automation involved — which is also how the player's
// manual "run them down" works.
//
// The load-bearing facts pinned here:
//   * contact is same-tile co-location, so a catch must land ON the target;
//   * every exit path releases the anchor (the permanent-brick lesson);
//   * an equal-speed runner escapes, because pursuit is pure, not predictive.
public class PursuitTests
{
    private static readonly TileCoord Home = new(10, 10);

    // A world with a soldier and a bandit, at war by default (bandits are
    // hostile to everyone). Units carry a rolled DeathTick like genesis units
    // do — the fixture/production divergence that hid a total outage once
    // already (SubstrateLifespanTests).
    private static Simulation BuildWorld(TileCoord soldierAt, TileCoord banditAt)
    {
        var grid = new TileGrid(32, 32, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        var cfg = new Sim.Core.Population.PopulationConfig();
        world.AddUnit(new Unit(1, soldierAt)
        {
            Role = UnitRole.Soldier,
            OwnerId = 0,
            BornTick = -25 * cfg.TicksPerYear,
            DeathTick = 60L * cfg.TicksPerYear,
            DeathSeq = 1,
        });
        world.AddUnit(new Unit(2, banditAt)
        {
            Role = UnitRole.Bandit,
            OwnerId = Sim.Core.Bandits.BanditConstants.OwnerId,
        });

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 29);
    }

    private static IntentOutcome Submit(Simulation sim, long at, Intent intent)
    {
        sim.SubmitIntent(at, intent);
        sim.Run(at);
        return Assert.IsType<IntentEvent>(sim.ResolvedLog[^1]).Outcome;
    }

    private static EngageUnitIntent Engage(int leashRadius = 0, TileCoord? leashFrom = null) =>
        new(unitId: 1, targetUnitId: 2, leashFrom ?? Home, leashRadius) { PlayerId = 0 };

    [Fact]
    public void AChaseCatchesAStationaryTarget_AndContactStartsTheFight()
    {
        // THE PLAN'S KEYSTONE: combat triggers on same-tile co-location, so a
        // pursuit must terminate by stepping ONTO the target. If this ever
        // goes red, patrols engage and then stand around next to the enemy.
        var target = new TileCoord(13, 10);
        var sim = BuildWorld(Home, target);

        Assert.False(Submit(sim, 0, Engage()).IsRejected);

        // Checked WHILE the fight is on: rounds run hourly and a soldier kills
        // a bandit in about ten of them, so a late assertion would find the
        // combat state already cleared and prove nothing.
        sim.Run(200);
        Assert.Equal(target, sim.World.Units[1].Position);
        Assert.Null(sim.World.Units[1].Pursuit);                  // released on catch
        Assert.True(sim.World.CombatStates.ContainsKey(target),
            "arriving on the target's tile must have started a fight");

        // And it resolves on its own — the patrol layer never touches combat.
        sim.Run(5000);
        Assert.False(sim.World.Units.ContainsKey(2), "the bandit should have died in the fight");
        Assert.True(sim.World.Units[1].Health < 30, "the soldier should have taken damage winning it");
    }

    [Fact]
    public void TheChaseFollowsAMovingTarget_RatherThanWhereItUsedToBe()
    {
        // Pure pursuit re-paths every hop. Send the bandit walking away after
        // the chase begins; the soldier must end up where the bandit WENT.
        var sim = BuildWorld(Home, new TileCoord(12, 10));
        Assert.False(Submit(sim, 0, Engage()).IsRejected);

        // Bandits are owned by the NPC faction; move it as its owner.
        sim.SubmitIntent(sim.Now, new Sim.Core.Movement.MoveIntent(2, new TileCoord(12, 16))
            { PlayerId = Sim.Core.Bandits.BanditConstants.OwnerId });
        sim.Run(20_000);

        var soldier = sim.World.Units[1];
        Assert.Null(soldier.Pursuit);
        // Either it caught the bandit (same tile, fight on) or the bandit
        // stopped and was caught at its destination — both mean the soldier
        // tracked the MOVE rather than walking to the original tile.
        Assert.NotEqual(new TileCoord(12, 10), soldier.Position);
        Assert.True(sim.World.Units.ContainsKey(2) == false
                    || soldier.Position == sim.World.Units[2].Position,
            "the pursuer should have run the target down, not its starting tile");
    }

    [Fact]
    public void BreakingTheLeashEndsTheChase_AndFreesTheUnit()
    {
        // The leash measures from the ROUTE anchor to the TARGET, so a runner
        // that gets far enough from the patrol's stop is gone.
        var sim = BuildWorld(Home, new TileCoord(12, 10));
        Assert.False(Submit(sim, 0, Engage(leashRadius: 3, leashFrom: Home)).IsRejected);

        sim.SubmitIntent(sim.Now, new Sim.Core.Movement.MoveIntent(2, new TileCoord(25, 10))
            { PlayerId = Sim.Core.Bandits.BanditConstants.OwnerId });
        sim.Run(40_000);

        var soldier = sim.World.Units[1];
        Assert.Null(soldier.Pursuit);
        Assert.Equal(Activity.Idle, soldier.Activity);
        Assert.Null(soldier.PathRemaining);
        // Never dragged beyond the leash from the route it was guarding.
        Assert.True(PursuitRules.Chebyshev(soldier.Position, Home) <= 5,
            $"patrol was pulled to {soldier.Position.X},{soldier.Position.Y} — the leash should hold it near its stop");
    }

    [Fact]
    public void ADeadTargetEndsTheChase_Cleanly()
    {
        var sim = BuildWorld(Home, new TileCoord(13, 10));
        Assert.False(Submit(sim, 0, Engage()).IsRejected);
        sim.Run(100);

        sim.World.Units.Remove(2);          // target dies mid-chase
        sim.Run(20_000);

        var soldier = sim.World.Units[1];
        Assert.Null(soldier.Pursuit);
        Assert.Equal(Activity.Idle, soldier.Activity);
        Assert.Null(soldier.PathRemaining);
        Assert.Null(soldier.NextArrivalTick);
    }

    [Fact]
    public void AnEqualSpeedRunnerEscapes()
    {
        // The consequence of refusing to predict: a straight chase between
        // equals never closes. Catching things has to come from positioning,
        // which is what keeps the tactics with the player.
        var sim = BuildWorld(Home, new TileCoord(12, 10));
        Assert.False(Submit(sim, 0, Engage()).IsRejected);

        // One long, UNBROKEN run. The bandit must never stop — a target that
        // pauses is caught, which is the whole reason cornering works.
        sim.SubmitIntent(sim.Now, new Sim.Core.Movement.MoveIntent(2, new TileCoord(31, 10))
            { PlayerId = Sim.Core.Bandits.BanditConstants.OwnerId });

        var stepsWhileFleeing = 0;
        for (long t = sim.Now + 30; t <= 500; t += 30)
        {
            sim.Run(t);
            if (!sim.World.Units.TryGetValue(2, out var bandit)) break;
            if (bandit.PathRemaining is null) break;      // it arrived; no longer fleeing
            stepsWhileFleeing++;
            Assert.NotEqual(sim.World.Units[1].Position, bandit.Position);
        }

        Assert.True(stepsWhileFleeing > 3, "the test needs the bandit to actually be running");
        Assert.True(sim.World.Units.ContainsKey(2), "an equal-speed runner must be able to escape");
    }

    [Fact]
    public void EngageRejectsANeutral_SoAPatrolCannotStartAWar()
    {
        var sim = BuildWorld(Home, new TileCoord(12, 10));
        // A second PLAYER's unit, at peace with us.
        sim.World.Players[1] = new Player(1);
        sim.World.AddUnit(new Unit(3, new TileCoord(11, 10)) { Role = UnitRole.Scout, OwnerId = 1 });

        var outcome = Submit(sim, 0,
            new EngageUnitIntent(1, 3, Home, 0) { PlayerId = 0 });

        Assert.True(outcome.IsRejected);
        Assert.Contains("not at war", outcome.Reason);
        Assert.Null(sim.World.Units[1].Pursuit);
    }

    [Fact]
    public void EngageRejectsATargetOutOfSight()
    {
        // The fog contract, at the pursuer's own eyes (Soldier radius 3).
        var sim = BuildWorld(Home, new TileCoord(20, 10));
        var outcome = Submit(sim, 0, Engage());
        Assert.True(outcome.IsRejected);
        Assert.Contains("not within sight", outcome.Reason);
    }

    [Fact]
    public void AChaseSurvivesASnapshotRoundTrip()
    {
        // A chase in flight at save time must resume, not evaporate.
        var sim = BuildWorld(Home, new TileCoord(13, 10));
        Assert.False(Submit(sim, 0, Engage(leashRadius: 7, leashFrom: Home)).IsRejected);
        sim.Run(60);
        Assert.NotNull(sim.World.Units[1].Pursuit);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 29);

        var chase = restored.World.Units[1].Pursuit;
        Assert.NotNull(chase);
        Assert.Equal(2, chase!.TargetUnitId);
        Assert.Equal(Home, chase.LeashTile);
        Assert.Equal(7, chase.LeashRadius);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void AChaseReplaysFromTheIntentLogAlone()
    {
        // THE HEADLINE: one EngageUnitIntent in the log is the whole chase.
        // Everything after unfolds from the anchor, so a replay reproduces it
        // byte-for-byte — which is what lets the patrol driver be an ephemeral
        // advisor rather than a second source of truth.
        var sim = BuildWorld(Home, new TileCoord(13, 10));
        Assert.False(Submit(sim, 0, Engage(leashRadius: 9, leashFrom: Home)).IsRejected);
        sim.Run(6000);

        var replay = BuildWorld(Home, new TileCoord(13, 10));
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
        replay.Run(6000);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(replay));
    }
}
