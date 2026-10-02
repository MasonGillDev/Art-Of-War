using Sim.Core.Automation;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Roads;
using Sim.Core.World;
using Sim.Server.Automation;

namespace Sim.Tests;

// The un-pin (docs/combat-pin-strands-hauls.md), on the battlefield grid (M43). A unit caught by
// a fight keeps its obligation and, when the board closes, its walk resumes from where it stands
// (Walk.Resume, CombatRules.ResumeInterrupted). Before the un-pin a unit caught mid-leg kept its
// obligation with no leg to finish it. Pins:
//   * a hauler stopped by a fight on the return leg walks again the beat the board closes and
//     the deposit lands;
//   * a hauler that reaches its stop with a fight ON it deposits without waiting;
//   * a goal walker (worker) stopped mid-route still reaches its post;
//   * a pursuer stopped by a bystander still catches its target;
//   * the driver names a stalled hand and releases its claim;
//   * determinism: twin run and snapshot round-trip across a stopped haul.
//
// The one-hit bystander does not fight (a builder withdraws): the fight ends when the test has
// it fall, which is the moment the un-pin is about.
public class CombatPinResumeTests
{
    private static readonly int RoadHop = System.Math.Max(RoadConstants.MIN_COST,
        Biomes.MoveCost(Biome.Grassland)
        - (int)((long)Biomes.MoveCost(Biome.Grassland) * RoadConstants.MAX_REDUCTION_PERCENT / 100L));
    private static readonly long RoundInterval = RoadHop * 3;
    private const int Row = 5;
    private const int Size = 12;
    private static readonly TileCoord Keep = new(9, Row);
    private static readonly TileCoord FarmAt = new(2, Row);
    private static readonly TileCoord Ambush = new(6, Row);

    private static (Simulation sim, GameWorld world) MakeWorld(int castleFood = 10)
    {
        var grid = new TileGrid(Size, Size, Biome.Grassland);
        var world = new GameWorld(grid, new DiplomacyConfig(), new CombatConfig(RoundIntervalTicks: RoundInterval));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        world.Players[2] = new Player(2);
        for (var x = 0; x < Size - 1; x++)
            for (var s = 0; s < Sim.Core.Battlefields.Subtile.Size; s++) world.Roads[SubtileLink.FromOwner(new Sim.Core.Battlefields.WorldSubtile(x * Sim.Core.Battlefields.Subtile.Size + s, Row * Sim.Core.Battlefields.Subtile.Size + 1), SubtileLink.Axis.East)] = new RoadState(RoadConstants.CONDITION_MAX, 0);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        world.Diplomacy.SetState(FactionPair.Of(0, 2), RelationshipState.Enemy);

        var castle = world.AddStructure(new Castle(Keep) { OwnerId = 0 });
        castle.Deposit(Resource.Food, castleFood);
        var farm = new Extractor(StructureKind.Farm, FarmAt) { OwnerId = 0, Buffer = 40, TickArmed = false };
        world.AddStructure(farm);

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;
        return (new Simulation(world, seed: 0xB1D), world);
    }

    private sealed class NoOpEvent : ScheduledEvent { public override void Apply(Simulation sim) { } }

    private static void AdvanceTo(Simulation sim, long tick)
    {
        if (tick <= sim.Now) return;
        sim.Schedule(tick, new NoOpEvent());
        sim.Run(until: tick);
    }

    // A one-hit bystander: dies in round 1, so the fight is one round long.
    private static Unit Blocker(GameWorld world, int id, TileCoord at, int owner) =>
        world.AddUnit(new Unit(id, at) { Role = UnitRole.Builder, OwnerId = owner, Health = 1 });

    private static long Horizon => RoadHop * 40 + RoundInterval * 8;

    // Step one tick at a time until `stop` says so (or the horizon).
    private static void RunUntil(Simulation sim, Func<bool> stop)
    {
        var end = sim.Now + Horizon;
        while (!stop() && sim.Now < end) AdvanceTo(sim, sim.Now + 1);
    }

    private static void Fall(Simulation sim, int id)
    {
        if (sim.World.Units.TryGetValue(id, out var u)) CombatRules.OnUnitDeath(sim, u);
    }

    [Fact]
    public void HaulResumesAfterTheFight()
    {
        var (sim, world) = MakeWorld();
        var hauler = world.AddUnit(new Unit(1, Keep) { Role = UnitRole.Hauler, OwnerId = 0 });
        sim.SubmitIntent(0, new HaulIntent(1, FarmAt, Keep, Resource.Food) { PlayerId = 0 });

        RunUntil(sim, () => hauler.CargoAmount > 0);          // picked up at the farm
        var carried = hauler.CargoAmount;
        Assert.True(carried > 0, "fixture: the hauler never picked up");
        Blocker(world, 2, Ambush, owner: 1);                  // waits on the return leg

        RunUntil(sim, () => world.Battlefields.ContainsKey(Ambush));
        Assert.True(world.Battlefields.ContainsKey(Ambush), "fixture: the hauler never met the ambush");
        Assert.False(hauler.IsWalking);                       // stopped by the fight: standing, still Hauling
        Assert.Equal(Activity.Hauling, hauler.Activity);
        Assert.NotNull(hauler.HaulPlan);

        Fall(sim, 2);                                         // the bystander falls
        RunUntil(sim, () => !world.Battlefields.ContainsKey(Ambush));
        Assert.False(world.Battlefields.ContainsKey(Ambush));
        Assert.True(hauler.IsWalking);                        // walking again the beat the board closed

        RunUntil(sim, () => hauler.HaulPlan is null);
        Assert.Equal(Activity.Idle, hauler.Activity);
        Assert.Equal(0, hauler.CargoAmount);
        Assert.Equal(10 + carried, ((Castle)world.Structures[Keep]).AmountOf(Resource.Food));
    }

    [Fact]
    public void HaulResumesWhenTheFightIsOnTheStop()
    {
        var (sim, world) = MakeWorld();
        var hauler = world.AddUnit(new Unit(1, Keep) { Role = UnitRole.Hauler, OwnerId = 0 });
        sim.SubmitIntent(0, new HaulIntent(1, FarmAt, Keep, Resource.Food) { PlayerId = 0 });

        RunUntil(sim, () => hauler.CargoAmount > 0);
        var carried = hauler.CargoAmount;
        Blocker(world, 2, Keep, owner: 1);                    // the fight is ON the castle tile

        RunUntil(sim, () => hauler.HaulPlan is null);
        Assert.Equal(Keep, hauler.Position);
        Assert.Equal(Activity.Idle, hauler.Activity);
        Assert.Equal(10 + carried, ((Castle)world.Structures[Keep]).AmountOf(Resource.Food));
        // The deposit landed as it reached the stop, under the fight; the fight then ends without
        // disturbing it.
        Fall(sim, 2);
        RunUntil(sim, () => !world.Battlefields.ContainsKey(Keep));
        Assert.Equal(Activity.Idle, hauler.Activity);
    }

    [Fact]
    public void GoalWalkerResumesAfterTheFight()
    {
        var (sim, world) = MakeWorld();
        var farmer = world.AddUnit(new Unit(1, Keep) { Role = UnitRole.Farmer, OwnerId = 0 });
        Blocker(world, 2, Ambush, owner: 1);
        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });

        RunUntil(sim, () => world.Battlefields.ContainsKey(Ambush));
        Assert.True(world.Battlefields.ContainsKey(Ambush), "fixture: the farmer never met the ambush");
        Assert.NotNull(farmer.Goal);                          // stopped, goal intact
        Assert.False(farmer.IsWalking);

        Fall(sim, 2);
        RunUntil(sim, () => farmer.Activity == Activity.Working);
        Assert.Equal(FarmAt, farmer.Position);
        Assert.Equal(FarmAt, farmer.Assignment);
        Assert.Null(farmer.Goal);
    }

    [Fact]
    public void PursuitResumesAfterTheFight()
    {
        // A chase needs the target inside the pursuer's own sight (radius 3),
        // so the quarry stands three tiles off with the bystander between.
        var (sim, world) = MakeWorld();
        var quarryAt = new TileCoord(Keep.X - 3, Row);
        var between = new TileCoord(Keep.X - 2, Row);
        var soldier = world.AddUnit(new Unit(1, Keep) { Role = UnitRole.Soldier, OwnerId = 0 });
        world.AddUnit(new Unit(3, quarryAt) { Role = UnitRole.Builder, OwnerId = 1 });
        Blocker(world, 2, between, owner: 2);                 // a third faction in the way
        sim.SubmitIntent(0, new EngageUnitIntent(1, 3, Keep, 20) { PlayerId = 0 });
        sim.Run(0);
        Assert.NotNull(soldier.Pursuit);                      // fixture: the chase was issued

        RunUntil(sim, () => world.Battlefields.ContainsKey(between));
        Assert.True(world.Battlefields.ContainsKey(between), "fixture: the soldier never met the bystander");
        Assert.NotNull(soldier.Pursuit);                      // stopped, chase suspended not ended
        Assert.False(soldier.IsWalking);

        Fall(sim, 2);
        RunUntil(sim, () => soldier.Position == quarryAt);
        Assert.Equal(quarryAt, soldier.Position);             // caught up after the bystander fell
        Assert.False(world.Units.ContainsKey(2));
    }

    [Fact]
    public void AStoppedHaul_TwinRunAndSnapshot_HashesMatch()
    {
        static Simulation Run()
        {
            var (sim, world) = MakeWorld();
            var hauler = world.AddUnit(new Unit(1, Keep) { Role = UnitRole.Hauler, OwnerId = 0 });
            world.AddUnit(new Unit(2, Ambush) { Role = UnitRole.Builder, OwnerId = 1, Health = 1 });
            sim.SubmitIntent(0, new HaulIntent(1, FarmAt, Keep, Resource.Food) { PlayerId = 0 });
            RunUntil(sim, () => world.Battlefields.ContainsKey(Ambush));
            Fall(sim, 2);
            RunUntil(sim, () => hauler.HaulPlan is null && sim.Now > RoadHop * 20);
            return sim;
        }
        var a = Run();
        Assert.Equal(Activity.Idle, a.World.Units[1].Activity);
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(Run()));
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(Snapshot.Restore(Snapshot.Serialize(a), seed: 0xB1D)));
    }

    // ---- Part B: the driver ------------------------------------------------

    [Fact]
    public void StalledHandIsReleasedAndReported()
    {
        // The stranded state built directly (a pre-fix snapshot): Hauling, a
        // plan, cargo, no path, no arrival — claimed by a Pull line.
        var (sim, world) = MakeWorld();
        var hauler = world.AddUnit(new Unit(1, Ambush) { Role = UnitRole.Hauler, OwnerId = 0 });
        world.NextUnitId = 2;
        var order = new Order
        {
            OwnerId = 0,
            SubjectKind = SubjectKind.Structure,
            SubjectTile = Keep,
            Program = ProgramKind.Maintain,
            Recipe = RecipeKind.Haul,
            Resource = Resource.Food,
            SourceTile = FarmAt,
            Target = 100,
            CrewMode = CrewMode.Pull,
            Selector = Selector.OfRole(UnitRole.Hauler, Keep, radius: 0),
            Trigger = Trigger.When(Predicate.StockBelow(Keep, Resource.Food, 100)),
        };
        sim.SubmitIntent(0, new SetOrderIntent(order) { PlayerId = 0 });
        sim.Run(0);
        var installed = Assert.Single(world.Orders.Values);
        sim.SubmitIntent(0, ClaimUnitIntent.Claim(1, installed.OrderId, ClaimPurpose.InFlight, 0));
        sim.Run(0);
        Assert.NotNull(ClaimLedger.ClaimOf(world, 1));

        hauler.TrySetActivity(Activity.Hauling);
        hauler.HaulPlan = new HaulPlan(FarmAt, Keep, Resource.Food, HaulPhase.ToDest);
        hauler.Cargo.Add(Resource.Food, 25);

        var journal = new OrderJournal();
        var driver = new SubstrateDriver(new AutomationConfig { ThinkPeriodTicks = 60 }, journal);
        driver.Think(sim, 0);

        var last = journal.Last(installed.OrderId);
        Assert.NotNull(last);
        Assert.Equal(JournalOutcome.Blocked, last!.Value.Outcome);
        Assert.Contains("unit 1 stalled carrying 25 Food", last.Value.Detail);

        sim.Run(0);                                           // the release lands
        Assert.Null(ClaimLedger.ClaimOf(world, 1));
        Assert.Equal(25, hauler.CargoAmount);                 // cargo kept
    }
}
