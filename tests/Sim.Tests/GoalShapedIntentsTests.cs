using Sim.Core;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.Sieges;
using Sim.Core.World;

namespace Sim.Tests;

// GOAL-SHAPED INTENTS — the sim executes, the player decides.
//
// The defect these tests pin: "assign this person to that farm" used to
// require the person to already be standing on the farm, so the player's one
// decision became a move, a WAIT, and a second intent. The wait is the damage
// — an appointment the player has to keep — and a stack of appointments is
// what the first hour used to be (docs/goal-shaped-intents.md).
//
// Every test below is really the same assertion from a different angle: after
// firing one intent, the player never has to come back.
public class GoalShapedIntentsTests
{
    private static readonly TileCoord Keep = new(4, 4);
    private static readonly TileCoord FarmAt = new(12, 4);
    private static readonly TileCoord SiteAt = new(12, 8);

    private static Simulation BuildWorld(out Extractor farm)
    {
        var grid = new TileGrid(24, 24, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        var castle = world.AddStructure(new Castle(Keep) { OwnerId = 0 });
        castle.Deposit(Resource.Food, 100_000);

        farm = new Extractor(StructureKind.Farm, FarmAt) { OwnerId = 0 };
        world.AddStructure(farm);

        var cfg = new PopulationConfig();
        // Unit 1 stands at the KEEP, not at the farm — the whole point.
        world.AddUnit(new Unit(1, Keep)
        {
            Role = UnitRole.Farmer, OwnerId = 0,
            BornTick = -25 * cfg.TicksPerYear,
        });
        world.NextUnitId = 2;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 24; y++)
            for (var x = 0; x < 24; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 11);
    }

    // ---- the headline ------------------------------------------------------

    [Fact]
    public void AWorkerAssignedFromAcrossTheMap_WalksThereAndTakesThePost()
    {
        var sim = BuildWorld(out var farm);
        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);

        // One intent, and the sim owns everything after it: the unit is under
        // way and the assignment is pending, not forgotten.
        Assert.NotNull(sim.World.Units[1].Goal);
        Assert.Equal(GoalKind.AssignWorker, sim.World.Units[1].Goal!.Kind);
        Assert.Empty(farm.Workers);

        sim.Run(10 * Time.Day);

        Assert.Equal(FarmAt, sim.World.Units[1].Position);
        Assert.Contains(1, farm.Workers);
        Assert.Equal(Activity.Working, sim.World.Units[1].Activity);
        Assert.Null(sim.World.Units[1].Goal);   // anchor cleared on completion
    }

    [Fact]
    public void AWorkerAlreadyStandingThere_StillAssignsImmediately()
    {
        // The pre-M30 path must not regress: no walk, no anchor, no waiting.
        var sim = BuildWorld(out var farm);
        sim.World.Units[1].Position = FarmAt;

        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);

        Assert.Contains(1, farm.Workers);
        Assert.Equal(Activity.Working, sim.World.Units[1].Activity);
        Assert.Null(sim.World.Units[1].Goal);
    }

    [Fact]
    public void ABuilderAssignedFromAcrossTheMap_WalksThereAndBuilds()
    {
        var sim = BuildWorld(out _);
        var world = sim.World;
        var site = new ConstructionSite(SiteAt, StructureKind.House) { OwnerId = 0 };
        world.AddStructure(site);
        var cfg = new PopulationConfig();
        world.AddUnit(new Unit(2, Keep)
        {
            Role = UnitRole.Builder, OwnerId = 0,
            BornTick = -25 * cfg.TicksPerYear,
        });

        sim.SubmitIntent(0, new AssignBuildersIntent(SiteAt, new[] { 2 }) { PlayerId = 0 });
        sim.Run(0);
        Assert.NotNull(world.Units[2].Goal);

        sim.Run(10 * Time.Day);

        Assert.Equal(SiteAt, world.Units[2].Position);
        Assert.Equal(Activity.Building, world.Units[2].Activity);
        Assert.Null(world.Units[2].Goal);
    }

    // ---- the conjunction, from both directions ------------------------------

    [Fact]
    public void MaterialsFirstAndBuilderFirst_ConvergeOnTheSameStartedBuild()
    {
        // The condition-conjunction rule: whichever half lands last starts the
        // build. Materials-last was already sim-driven (CargoTransfer);
        // builder-last is what M30 adds. Both orders must agree.
        static Simulation Scenario(bool materialsFirst, out ConstructionSite site)
        {
            var grid = new TileGrid(24, 24, Biome.Grassland);
            var world = new GameWorld(grid);
            world.Players[0] = new Player(0);
            world.AddStructure(new Castle(Keep) { OwnerId = 0 }).Deposit(Resource.Food, 100_000);
            site = new ConstructionSite(SiteAt, StructureKind.House) { OwnerId = 0 };
            world.AddStructure(site);
            var cfg = new PopulationConfig();
            world.AddUnit(new Unit(2, materialsFirst ? SiteAt : Keep)
            {
                Role = UnitRole.Builder, OwnerId = 0,
                BornTick = -25 * cfg.TicksPerYear,
            });
            var explored = new HashSet<TileCoord>();
            for (var y = 0; y < 24; y++)
                for (var x = 0; x < 24; x++) explored.Add(new TileCoord(x, y));
            world.Explored[0] = explored;
            return new Simulation(world, seed: 11);
        }

        // (a) materials already delivered, builder walks in last.
        var simA = Scenario(materialsFirst: false, out var siteA);
        foreach (var (r, n) in siteA.Required)
            CargoTransfer.DepositInto(simA, siteA, r, n);
        simA.SubmitIntent(0, new AssignBuildersIntent(SiteAt, new[] { 2 }) { PlayerId = 0 });
        simA.Run(10 * Time.Day);
        Assert.True(siteA.IsActive || !simA.World.Structures.ContainsKey(SiteAt));

        // (b) builder already there, materials land last.
        var simB = Scenario(materialsFirst: true, out var siteB);
        simB.SubmitIntent(0, new AssignBuildersIntent(SiteAt, new[] { 2 }) { PlayerId = 0 });
        simB.Run(0);
        foreach (var (r, n) in siteB.Required)
            CargoTransfer.DepositInto(simB, siteB, r, n);
        Assert.True(siteB.IsActive || !simB.World.Structures.ContainsKey(SiteAt));
    }

    // ---- dissolution: every way a goal can die -----------------------------

    [Fact]
    public void ANewOrderCountermandsTheGoal_AndLeavesNoOrphanAnchor()
    {
        var sim = BuildWorld(out var farm);
        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);
        Assert.NotNull(sim.World.Units[1].Goal);

        // The player changes their mind mid-walk.
        var elsewhere = new TileCoord(4, 12);
        sim.SubmitIntent(sim.Now, new MoveIntent(1, elsewhere) { PlayerId = 0 });
        sim.Run(sim.Now);
        Assert.Null(sim.World.Units[1].Goal);

        sim.Run(10 * Time.Day);

        // Arrived where the PLAYER said, and did not silently take the job.
        Assert.Equal(elsewhere, sim.World.Units[1].Position);
        Assert.Empty(farm.Workers);
    }

    [Fact]
    public void TheTargetRazedMidWalk_DissolvesTheGoalAndAnnouncesIt()
    {
        var sim = BuildWorld(out var farm);
        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);

        SiegeDamage.RazeStructure(sim, farm);
        sim.Run(sim.Now);

        Assert.Null(sim.World.Units[1].Goal);
        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.Contains(sim.ResolvedLog, e => e is GoalDissolvedEvent);
    }

    [Fact]
    public void ADeadWalker_LeavesNoGoalBehind()
    {
        var sim = BuildWorld(out _);
        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);
        Assert.NotNull(sim.World.Units[1].Goal);

        var unit = sim.World.Units[1];
        Population.OnUnitRemoved(sim, unit);
        sim.World.Units.Remove(1);
        sim.Run(sim.Now);

        Assert.Contains(sim.ResolvedLog, e => e is GoalDissolvedEvent);
        Assert.DoesNotContain(sim.World.Units.Values, u => u.Goal is not null);
    }

    [Fact]
    public void MoreHopefulsThanSlots_AreNotDispatchedToDissolveOnArrival()
    {
        // Availability is a precondition, not a rejection — but a cap-N
        // workplace must not attract N+1 walkers who all discover the truth
        // on arrival. In-flight goals count against the cap at dispatch.
        var sim = BuildWorld(out var farm);
        var cfg = new PopulationConfig();
        var ids = new List<int> { 1 };
        for (var i = 0; i < farm.Spec.WorkerCap + 2; i++)
        {
            var id = 10 + i;
            sim.World.AddUnit(new Unit(id, Keep)
            {
                Role = UnitRole.Farmer, OwnerId = 0,
                BornTick = -25 * cfg.TicksPerYear,
            });
            ids.Add(id);
        }

        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, ids) { PlayerId = 0 });
        sim.Run(0);

        var walking = sim.World.Units.Values.Count(u => u.Goal is not null);
        Assert.Equal(farm.Spec.WorkerCap, walking);
    }

    // ---- breeding: the precondition stress-test -----------------------------
    //
    // Five appointments in one intent, pre-M30: both bodies here, both Idle,
    // both fertile, food already delivered. Four of those are mechanical.

    private static readonly TileCoord HouseAt = new(8, 8);

    // A real calendar (default PopulationConfig) — the breeding fixtures in
    // HouseBreedingTests run a compressed one, which cannot express a walk.
    private static Simulation BuildBreedingWorld(out House house, int foodInHouse)
    {
        var grid = new TileGrid(24, 24, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.AddStructure(new Castle(Keep) { OwnerId = 0 }).Deposit(Resource.Food, 100_000);

        house = new House(HouseAt) { OwnerId = 0 };
        world.AddStructure(house);
        if (foodInHouse > 0) house.Deposit(Resource.Food, foodInHouse);

        var cfg = new PopulationConfig();
        // Deliberately far apart: one at the keep, one across the map.
        world.AddUnit(new Unit(1, Keep) { OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });
        world.AddUnit(new Unit(2, new TileCoord(20, 20)) { OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });
        world.NextUnitId = 3;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 24; y++)
            for (var x = 0; x < 24; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 7);
    }

    [Fact]
    public void TwoParentsFromOppositeCorners_MeetAtTheHouseAndConceive()
    {
        var cfg = new PopulationConfig();
        var sim = BuildBreedingWorld(out var house, foodInHouse: cfg.BirthFoodCost);

        sim.SubmitIntent(0, new BeginBreedingIntent(HouseAt, 1, 2) { PlayerId = 0 });
        sim.Run(0);
        Assert.NotNull(house.PendingBreed);

        // Long enough for both walks, shorter than gestation, so the
        // conception itself is observable rather than already resolved.
        sim.Run(cfg.GestationTicks - 1);

        Assert.NotNull(house.Occupation);
        Assert.Null(house.PendingBreed);
        Assert.Equal(Activity.Working, sim.World.Units[1].Activity);
        Assert.Equal(Activity.Working, sim.World.Units[2].Activity);

        // And the goal runs all the way to its point: a child.
        sim.Run(sim.Now + cfg.GestationTicks);
        Assert.Equal(3, sim.World.Units.Count);
    }

    [Fact]
    public void TheFirstToArrive_WaitsForTheOther()
    {
        var cfg = new PopulationConfig();
        var sim = BuildBreedingWorld(out var house, foodInHouse: cfg.BirthFoodCost);
        // Unit 1 is already standing in the house; unit 2 is across the map.
        sim.World.Units[1].Position = HouseAt;

        sim.SubmitIntent(0, new BeginBreedingIntent(HouseAt, 1, 2) { PlayerId = 0 });
        sim.Run(0);

        // Parked, not working, not haulable — visible opportunity cost.
        Assert.Equal(Activity.Waiting, sim.World.Units[1].Activity);
        Assert.Null(house.Occupation);

        sim.Run(cfg.GestationTicks - 1);
        Assert.NotNull(house.Occupation);
    }


    [Fact]
    public void AWaitingParentCannotBeQuietlyRetasked()
    {
        // Activity.Waiting is a NON-Idle state precisely so that every
        // existing Idle-gated intent refuses it without being touched.
        var sim = BuildBreedingWorld(out var house, foodInHouse: 0);
        sim.World.Units[1].Position = HouseAt;
        sim.World.Units[2].Position = HouseAt;
        sim.World.AddStructure(new Extractor(StructureKind.Farm, FarmAt) { OwnerId = 0 });

        sim.SubmitIntent(0, new BeginBreedingIntent(HouseAt, 1, 2) { PlayerId = 0 });
        sim.Run(0);
        Assert.Equal(Activity.Waiting, sim.World.Units[1].Activity);

        // A farm assignment must not steal a waiting parent.
        sim.SubmitIntent(sim.Now, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(sim.Now);

        Assert.Equal(Activity.Waiting, sim.World.Units[1].Activity);
        Assert.NotNull(house.PendingBreed);
    }

    [Fact]
    public void NoFoodYet_ThePairWaits_AndTheDeliveryItselfConceives()
    {
        var cfg = new PopulationConfig();
        var sim = BuildBreedingWorld(out var house, foodInHouse: cfg.BirthFoodCost - 1);
        sim.World.Units[1].Position = HouseAt;
        sim.World.Units[2].Position = HouseAt;

        sim.SubmitIntent(0, new BeginBreedingIntent(HouseAt, 1, 2) { PlayerId = 0 });
        sim.Run(0);
        Assert.Null(house.Occupation);

        CargoTransfer.DepositInto(sim, house, Resource.Food, 1);

        Assert.NotNull(house.Occupation);
    }

    [Fact]
    public void APartnerWhoDiesWaiting_BreaksTheMatchInsteadOfSubstituting()
    {
        var sim = BuildBreedingWorld(out var house, foodInHouse: 0);
        sim.World.Units[1].Position = HouseAt;
        sim.World.Units[2].Position = HouseAt;
        // A third, perfectly eligible adult standing right there — the sim
        // must NOT quietly promote them. Auto-replacement is the automation
        // tier's feature (docs/goal-shaped-intents.md).
        var cfg = new PopulationConfig();
        sim.World.AddUnit(new Unit(3, HouseAt) { OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });

        sim.SubmitIntent(0, new BeginBreedingIntent(HouseAt, 1, 2) { PlayerId = 0 });
        sim.Run(0);

        var victim = sim.World.Units[2];
        Population.OnUnitRemoved(sim, victim);
        sim.World.Units.Remove(2);
        sim.Run(sim.Now);

        Assert.Null(house.PendingBreed);
        Assert.Null(house.Occupation);
        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.Null(sim.World.Units[1].Goal);
        Assert.Null(sim.World.Units[3].Goal);          // never conscripted
        Assert.Contains(sim.ResolvedLog, e => e is GoalDissolvedEvent);
    }

    [Fact]
    public void AgingOutWhileWaiting_DissolvesRatherThanStallingForever()
    {
        // The one case no existing event can wake: food that never arrives.
        // Without the expiry deadline the pair would stand there for life.
        var cfg = new PopulationConfig();
        var sim = BuildBreedingWorld(out var house, foodInHouse: 0);
        // Both right at the fertility ceiling, standing in the house. BornTick
        // is init-only, so the pair is replaced rather than mutated.
        var born = -(long)cfg.MaxFertileAge * cfg.TicksPerYear;
        sim.World.Units.Remove(1);
        sim.World.Units.Remove(2);
        sim.World.AddUnit(new Unit(1, HouseAt) { OwnerId = 0, BornTick = born });
        sim.World.AddUnit(new Unit(2, HouseAt) { OwnerId = 0, BornTick = born });

        sim.SubmitIntent(0, new BeginBreedingIntent(HouseAt, 1, 2) { PlayerId = 0 });
        sim.Run(0);
        Assert.NotNull(house.PendingBreed);

        // Past the deadline, config-derived — never a hard-coded tick.
        sim.Run(2L * cfg.TicksPerYear);

        Assert.Null(house.PendingBreed);
        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.Equal(Activity.Idle, sim.World.Units[2].Activity);
        Assert.Contains(sim.ResolvedLog, e => e is GoalDissolvedEvent);
    }

    [Fact]
    public void OneHouse_CannotBeReservedTwice()
    {
        var cfg = new PopulationConfig();
        var sim = BuildBreedingWorld(out var house, foodInHouse: cfg.BirthFoodCost);
        sim.World.AddUnit(new Unit(3, Keep) { OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });
        sim.World.AddUnit(new Unit(4, Keep) { OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });

        sim.SubmitIntent(0, new BeginBreedingIntent(HouseAt, 1, 2) { PlayerId = 0 });
        sim.Run(0);
        sim.SubmitIntent(sim.Now, new BeginBreedingIntent(HouseAt, 3, 4) { PlayerId = 0 });
        sim.Run(sim.Now);

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Null(sim.World.Units[3].Goal);
        Assert.Null(sim.World.Units[4].Goal);
    }

    // ---- training ----------------------------------------------------------
    //
    // "Train him as a builder" used to mean: walk him to the school, wait,
    // come back, fire the intent. The middle two steps were the appointment.

    private static readonly TileCoord SchoolAt = new(4, 14);

    [Fact]
    public void ACitizenTrainedFromAcrossTheMap_WalksToTheSchoolAndChangesRole()
    {
        var sim = BuildWorld(out _);
        sim.World.AddStructure(new School(SchoolAt) { OwnerId = 0 });

        sim.SubmitIntent(0, new TrainUnitIntent(1, UnitRole.Builder, SchoolAt) { PlayerId = 0 });
        sim.Run(0);

        Assert.False(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.NotNull(sim.World.Units[1].Goal);
        Assert.Equal(GoalKind.Train, sim.World.Units[1].Goal!.Kind);
        Assert.Equal(UnitRole.Farmer, sim.World.Units[1].Role);   // not yet

        sim.Run(10 * Time.Day);

        Assert.Equal(SchoolAt, sim.World.Units[1].Position);
        Assert.Equal(UnitRole.Builder, sim.World.Units[1].Role);
        Assert.Null(sim.World.Units[1].Goal);
    }

    [Fact]
    public void WithoutATrainerTile_TheOldStandingOnItContractIsUnchanged()
    {
        // Back-compatibility is the point of the optional parameter: seven
        // existing call sites keep working without being touched.
        var sim = BuildWorld(out _);
        sim.World.AddStructure(new School(SchoolAt) { OwnerId = 0 });
        sim.World.Units[1].Position = SchoolAt;

        sim.SubmitIntent(0, new TrainUnitIntent(1, UnitRole.Builder) { PlayerId = 0 });
        sim.Run(0);

        Assert.Equal(UnitRole.Builder, sim.World.Units[1].Role);
        Assert.Null(sim.World.Units[1].Goal);   // no walk, no anchor
    }

    [Fact]
    public void TrainingElsewhereWithoutATile_StillRejects()
    {
        // The unit is nowhere near a school and named no tile: that is a
        // malformed request, not a goal, and it must not silently become one.
        var sim = BuildWorld(out _);
        sim.World.AddStructure(new School(SchoolAt) { OwnerId = 0 });

        sim.SubmitIntent(0, new TrainUnitIntent(1, UnitRole.Builder) { PlayerId = 0 });
        sim.Run(0);

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Null(sim.World.Units[1].Goal);
    }

    [Fact]
    public void TheSchoolRazedMidWalk_DissolvesTheTrainingGoal()
    {
        var sim = BuildWorld(out _);
        var school = sim.World.AddStructure(new School(SchoolAt) { OwnerId = 0 });

        sim.SubmitIntent(0, new TrainUnitIntent(1, UnitRole.Builder, SchoolAt) { PlayerId = 0 });
        sim.Run(0);
        Assert.NotNull(sim.World.Units[1].Goal);

        SiegeDamage.RazeStructure(sim, school);
        sim.Run(sim.Now);

        Assert.Null(sim.World.Units[1].Goal);
        Assert.Equal(UnitRole.Farmer, sim.World.Units[1].Role);   // unchanged
        Assert.Contains(sim.ResolvedLog, e => e is GoalDissolvedEvent);
    }

    [Fact]
    public void ATrainingGoalCarriesItsRoleThroughASnapshot()
    {
        // The role to train into lives in GoalPlan.Arg; losing it across a
        // restore would resume the walk and then train the wrong thing.
        var sim = BuildWorld(out _);
        sim.World.AddStructure(new School(SchoolAt) { OwnerId = 0 });
        sim.SubmitIntent(0, new TrainUnitIntent(1, UnitRole.Scout, SchoolAt) { PlayerId = 0 });
        sim.Run(0);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 11);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal((int)UnitRole.Scout, restored.World.Units[1].Goal!.Arg);

        restored.Run(10 * Time.Day);
        Assert.Equal(UnitRole.Scout, restored.World.Units[1].Role);
    }

    // ---- the composite ------------------------------------------------------

    [Fact]
    public void OneGesture_PlacesTheSite_SendsTheBuilder_AndMansItOnCompletion()
    {
        // "A farm exists here, built by him, worked by her." One intent; every
        // participant named up front; nothing decided afterwards.
        var sim = BuildWorld(out _);
        var world = sim.World;
        var cfg = new PopulationConfig();
        var farmTile = new TileCoord(6, 10);

        world.AddUnit(new Unit(2, Keep) { Role = UnitRole.Builder, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });
        world.AddUnit(new Unit(3, Keep) { Role = UnitRole.Farmer,  OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });

        sim.SubmitIntent(0, new BuildIntent(farmTile, StructureKind.Farm,
            builderId: 2, workerToManId: 3) { PlayerId = 0 });
        sim.Run(0);

        // The site is up and the builder is walking. The WORKER is untouched —
        // they keep their life until there is something to work.
        var site = Assert.IsType<ConstructionSite>(world.Structures[farmTile]);
        Assert.Equal(3, site.WorkerToManId);
        Assert.NotNull(world.Units[2].Goal);
        Assert.Null(world.Units[3].Goal);
        Assert.Equal(Keep, world.Units[3].Position);

        // Materials are NOT part of the composite (no demand-driven haulage
        // exists), so they arrive the way they always did.
        sim.Run(5 * Time.Day);
        foreach (var (r, n) in site.Required)
            CargoTransfer.DepositInto(sim, site, r, n);

        sim.Run(sim.Now + 30 * Time.Day);

        // Built, and manned by the unit named a month earlier.
        var farm = Assert.IsType<Extractor>(world.Structures[farmTile]);
        Assert.Contains(3, farm.Workers);
        Assert.Equal(farmTile, world.Units[3].Position);
    }

    [Fact]
    public void ABoundWorkerWhoDies_LeavesTheStructureUnmanned_NotReplaced()
    {
        var sim = BuildWorld(out _);
        var world = sim.World;
        var cfg = new PopulationConfig();
        var farmTile = new TileCoord(6, 10);

        world.AddUnit(new Unit(2, farmTile) { Role = UnitRole.Builder, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });
        world.AddUnit(new Unit(3, Keep)     { Role = UnitRole.Farmer,  OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });
        // An equally good farmer standing right at the site. Not a candidate.
        world.AddUnit(new Unit(4, farmTile) { Role = UnitRole.Farmer,  OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });

        sim.SubmitIntent(0, new BuildIntent(farmTile, StructureKind.Farm,
            builderId: 2, workerToManId: 3) { PlayerId = 0 });
        sim.Run(0);

        var site = (ConstructionSite)world.Structures[farmTile];
        foreach (var (r, n) in site.Required)
            CargoTransfer.DepositInto(sim, site, r, n);

        // The bound worker dies before the build finishes.
        Population.OnUnitRemoved(sim, world.Units[3]);
        world.Units.Remove(3);

        sim.Run(30 * Time.Day);

        var farm = Assert.IsType<Extractor>(world.Structures[farmTile]);
        Assert.Empty(farm.Workers);                       // unmanned, and says so
        Assert.Null(world.Units[4].Goal);                 // never conscripted
        Assert.Contains(sim.ResolvedLog, e =>
            e is GoalDissolvedEvent g && g.Reason == "bound worker is gone");
    }

    [Fact]
    public void AnIllegalPlacement_RejectsWholesale_AndBindsNobody()
    {
        var sim = BuildWorld(out _);
        var cfg = new PopulationConfig();
        sim.World.AddUnit(new Unit(2, Keep) { Role = UnitRole.Builder, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear });

        // Keep already holds the castle — placement must fail, and failing
        // placement must not leave a builder walking toward nothing.
        sim.SubmitIntent(0, new BuildIntent(Keep, StructureKind.Farm, builderId: 2) { PlayerId = 0 });
        sim.Run(0);

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.Null(sim.World.Units[2].Goal);
    }

    // ---- persistence: a goal must survive a restart -------------------------

    [Fact]
    public void AGoalInFlight_SurvivesASnapshotRoundTrip()
    {
        var sim = BuildWorld(out _);
        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 11);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        var goal = restored.World.Units[1].Goal;
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.AssignWorker, goal!.Kind);
        Assert.Equal(FarmAt, goal.TargetTile);

        // And it still COMPLETES on the restored side — the walk resumes.
        restored.Run(10 * Time.Day);
        var farm = (Extractor)restored.World.Structures[FarmAt];
        Assert.Contains(1, farm.Workers);
    }

    [Fact]
    public void APairWaitingOnFood_SurvivesASnapshotRoundTrip()
    {
        var cfg = new PopulationConfig();
        var sim = BuildBreedingWorld(out var house, foodInHouse: cfg.BirthFoodCost - 1);
        sim.World.Units[1].Position = HouseAt;
        sim.World.Units[2].Position = HouseAt;
        sim.SubmitIntent(0, new BeginBreedingIntent(HouseAt, 1, 2) { PlayerId = 0 });
        sim.Run(0);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 7);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));

        var rHouse = (House)restored.World.Structures[HouseAt];
        Assert.NotNull(rHouse.PendingBreed);
        Assert.Equal(Activity.Waiting, restored.World.Units[1].Activity);

        // The wake-up still works on the far side of the restart.
        CargoTransfer.DepositInto(restored, rHouse, Resource.Food, 1);
        Assert.NotNull(rHouse.Occupation);
    }

    [Fact]
    public void ARestoredWait_StillExpires_RatherThanStallingForever()
    {
        // The expiry event lives only in the queue, so a restore that failed
        // to rebuild it would silently convert "dissolves" into "waits for
        // life" — invisible until someone watched a pair for a whole game.
        var cfg = new PopulationConfig();
        var sim = BuildBreedingWorld(out _, foodInHouse: 0);
        var born = -(long)cfg.MaxFertileAge * cfg.TicksPerYear;
        sim.World.Units.Remove(1);
        sim.World.Units.Remove(2);
        sim.World.AddUnit(new Unit(1, HouseAt) { OwnerId = 0, BornTick = born });
        sim.World.AddUnit(new Unit(2, HouseAt) { OwnerId = 0, BornTick = born });
        sim.SubmitIntent(0, new BeginBreedingIntent(HouseAt, 1, 2) { PlayerId = 0 });
        sim.Run(0);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 7);
        restored.Run(2L * cfg.TicksPerYear);

        var rHouse = (House)restored.World.Structures[HouseAt];
        Assert.Null(rHouse.PendingBreed);
        Assert.Equal(Activity.Idle, restored.World.Units[1].Activity);
    }

    // ---- determinism -------------------------------------------------------

    [Fact]
    public void TwinRun_OfAWalkedAssignment_IsIdentical()
    {
        static (long now, int pos, bool working) Run()
        {
            var sim = BuildWorld(out var farm);
            sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
            sim.Run(10 * Time.Day);
            var u = sim.World.Units[1];
            return (sim.Now, u.Position.X * 1000 + u.Position.Y, farm.Workers.Contains(1));
        }

        Assert.Equal(Run(), Run());
    }
}
