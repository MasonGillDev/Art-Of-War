using Sim.Core;
using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Persistence;
using Sim.Core.World;
using Sim.Server.Automation;

namespace Sim.Tests;

// M29 Phase C — the patrol (docs/patrols.md).
//
// A patrol is a Routine circuit with EngageRadius/LeashRadius set; 0/0 is the
// pacifist circuit that already existed. The driver automates POSTURE only —
// spot, and close the distance — and hands the rest to the sim: the chase is
// PursuitRules, the fight is CombatRoundEvent. Nothing here resolves combat.
//
// These are the doc's acceptance criteria, one test each.
public class PatrolTests
{
    private static readonly TileCoord StopA = new(8, 10);
    private static readonly TileCoord StopB = new(16, 10);

    // Genesis-shaped units: adults carrying a rolled DeathTick, as the real
    // server's spec-aware ctor leaves them (SubstrateLifespanTests).
    private static Unit Soldier(int id, TileCoord at)
    {
        var cfg = new Sim.Core.Population.PopulationConfig();
        return new Unit(id, at)
        {
            Role = UnitRole.Soldier,
            OwnerId = 0,
            BornTick = -25 * cfg.TicksPerYear,
            DeathTick = 60L * cfg.TicksPerYear,
            DeathSeq = id,
        };
    }

    private static Simulation BuildWorld()
    {
        var grid = new TileGrid(32, 32, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.AddStructure(new Castle(new TileCoord(12, 10)) { OwnerId = 0 });
        world.AddUnit(Soldier(1, StopA));
        world.AddUnit(Soldier(2, StopA));
        world.NextUnitId = 3;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 29);
    }

    private static Order Patrol(int engage, int leash)
    {
        var o = new Order
        {
            OwnerId = 0,
            SubjectKind = SubjectKind.Structure,
            SubjectTile = new TileCoord(12, 10),
            Program = ProgramKind.Routine,
            CrewMode = CrewMode.Named,
            EngageRadius = engage,
            LeashRadius = leash,
            Steps =
            {
                new RoutineStep { Tile = StopA },
                new RoutineStep { Tile = StopB },
            },
        };
        o.NamedCrew.Add(1);
        o.NamedCrew.Add(2);
        return o;
    }

    private static Order Install(Simulation sim, Order definition)
    {
        sim.SubmitIntent(0, new SetOrderIntent(definition) { PlayerId = 0 });
        sim.Run(0);
        var ev = Assert.IsType<IntentEvent>(sim.ResolvedLog[^1]);
        Assert.False(ev.Outcome.IsRejected, ev.Outcome.Reason);
        return Assert.Single(sim.World.Orders.Values);
    }

    private static int AddBandit(Simulation sim, TileCoord at)
    {
        var id = sim.World.NextUnitId++;
        sim.World.AddUnit(new Unit(id, at)
            { Role = UnitRole.Bandit, OwnerId = Sim.Core.Bandits.BanditConstants.OwnerId });
        return id;
    }

    private static (OrderJournal journal, SubstrateDriver driver) Run(
        Simulation sim, long until, long period = 60)
    {
        var journal = new OrderJournal();
        var driver = new SubstrateDriver(new AutomationConfig { ThinkPeriodTicks = period }, journal);
        for (long t = 0; t <= until; t += period)
        {
            sim.Run(t);
            driver.Think(sim, t);
            sim.Run(t);
        }
        // Land exactly on `until`: the loop's last think is at the final
        // multiple of `period`, so without this the live run stops short of
        // the horizon a replay would run to — and the two diverge on nothing
        // but elapsed time.
        sim.Run(until);
        return (journal, driver);
    }

    [Fact]
    public void APatrolEngagesABanditInsideItsRadius()
    {
        var sim = BuildWorld();
        var order = Install(sim, Patrol(engage: 6, leash: 12));
        var bandit = AddBandit(sim, new TileCoord(11, 10));   // 3 from StopA

        var (journal, _) = Run(sim, 600);

        Assert.Contains(journal.For(order.OrderId),
            e => e.Outcome == JournalOutcome.Fired && e.Detail.Contains($"engaging unit {bandit}"));
        Assert.False(sim.World.Units.ContainsKey(bandit), "two soldiers should have run down one bandit");
    }

    [Fact]
    public void APatrolIgnoresABanditOutsideItsRadius()
    {
        // Party-LOCAL aggro: where you walk is what you protect. A bandit
        // sitting in plain sight across the kingdom is the player's problem,
        // not this patrol's.
        var sim = BuildWorld();
        var order = Install(sim, Patrol(engage: 3, leash: 12));
        var bandit = AddBandit(sim, new TileCoord(30, 30));

        var (journal, _) = Run(sim, 300);

        Assert.DoesNotContain(journal.For(order.OrderId), e => e.Detail.Contains("engaging"));
        Assert.True(sim.World.Units.ContainsKey(bandit));
        Assert.Null(sim.World.Units[1].Pursuit);
    }

    [Fact]
    public void APatrolIgnoresANeutralRival_SoItCannotStartAWar()
    {
        var sim = BuildWorld();
        var order = Install(sim, Patrol(engage: 8, leash: 12));
        sim.World.Players[1] = new Player(1);
        sim.World.AddUnit(new Unit(50, new TileCoord(10, 10)) { Role = UnitRole.Scout, OwnerId = 1 });

        var (journal, _) = Run(sim, 300);

        Assert.DoesNotContain(journal.For(order.OrderId), e => e.Detail.Contains("engaging"));
        Assert.True(sim.World.Units.ContainsKey(50), "a neutral must be walked past, not attacked");
    }

    [Fact]
    public void APatrolRefusesATargetAlreadyOutsideItsLeash()
    {
        // Engaging something the leash would release on step one would submit
        // a doomed intent every think. The leash is part of the DECISION.
        var sim = BuildWorld();
        var order = Install(sim, Patrol(engage: 10, leash: 2));
        var bandit = AddBandit(sim, new TileCoord(8, 4));   // 6 from StopA, far outside leash 2

        var (journal, _) = Run(sim, 300);

        Assert.DoesNotContain(journal.For(order.OrderId), e => e.Detail.Contains("engaging"));
        Assert.True(sim.World.Units.ContainsKey(bandit));
    }

    [Fact]
    public void AfterTheFightThePatrolResumesItsCircuit_AtTheSameCursor()
    {
        var sim = BuildWorld();
        var order = Install(sim, Patrol(engage: 6, leash: 12));
        AddBandit(sim, new TileCoord(11, 10));

        var (journal, _) = Run(sim, 4000);

        // The chase never touched the cursor, so the circuit picks up where it
        // was and the crew is walking again.
        Assert.True(order.Enabled);
        Assert.Null(sim.World.Units[1].Pursuit);
        Assert.Contains(journal.For(order.OrderId),
            e => e.Detail.Contains("bound for stop") || e.Detail.Contains("departing stop"));
    }

    [Fact]
    public void APatrolWalksOnPastTheLoot()
    {
        // Law 3: piles compose with the salvage/hauler machinery THROUGH THE
        // WORLD. Looting inside the order would make a patrol a second
        // logistics system.
        var sim = BuildWorld();
        Install(sim, Patrol(engage: 6, leash: 12));
        var bandit = AddBandit(sim, new TileCoord(11, 10));
        sim.World.Units[bandit].Cargo.Add(Resource.Food, 20);

        Run(sim, 4000);

        Assert.False(sim.World.Units.ContainsKey(bandit));
        Assert.Equal(0, sim.World.Units[1].CargoAmount);
        Assert.Equal(0, sim.World.Units[2].CargoAmount);
    }

    [Fact]
    public void APacifistCircuitNeverEngages()
    {
        // 0/0 must behave exactly as the pre-M29 circuit did.
        var sim = BuildWorld();
        var order = Install(sim, Patrol(engage: 0, leash: 0));
        var bandit = AddBandit(sim, new TileCoord(9, 10));   // right beside them

        var (journal, _) = Run(sim, 300);

        Assert.DoesNotContain(journal.For(order.OrderId), e => e.Detail.Contains("engaging"));
        Assert.True(sim.World.Units.ContainsKey(bandit));
    }

    [Fact]
    public void PostureIsRejectedOnAMaintainOrder()
    {
        var sim = BuildWorld();
        var bad = new Order
        {
            OwnerId = 0,
            SubjectKind = SubjectKind.Structure,
            SubjectTile = new TileCoord(12, 10),
            Program = ProgramKind.Maintain,
            Recipe = RecipeKind.Haul,
            Resource = Resource.Food,
            SourceTile = StopA,
            CrewMode = CrewMode.Named,
            EngageRadius = 5,
        };
        bad.NamedCrew.Add(1);

        sim.SubmitIntent(0, new SetOrderIntent(bad) { PlayerId = 0 });
        sim.Run(0);
        var ev = Assert.IsType<IntentEvent>(sim.ResolvedLog[^1]);
        Assert.True(ev.Outcome.IsRejected);
        Assert.Contains("Routine patrol", ev.Outcome.Reason);
    }

    [Fact]
    public void APatrolThatFought_ReplaysWithoutTheDriver()
    {
        // THE HEADLINE. The driver is an ephemeral advisor: everything it
        // decided reached the world as ordinary intents, so replaying the log
        // with NO driver reproduces the patrol, the chase and the fight
        // byte-for-byte.
        var sim = BuildWorld();
        Install(sim, Patrol(engage: 6, leash: 12));
        AddBandit(sim, new TileCoord(11, 10));
        Run(sim, 4000);

        var replay = BuildWorld();
        AddBandit(replay, new TileCoord(11, 10));
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
        replay.Run(4000);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(replay));
    }

    [Fact]
    public void PostureSurvivesASnapshotRoundTrip()
    {
        var sim = BuildWorld();
        Install(sim, Patrol(engage: 7, leash: 13));

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 29);
        var order = Assert.Single(restored.World.Orders.Values);

        Assert.Equal(7, order.EngageRadius);
        Assert.Equal(13, order.LeashRadius);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }
}
