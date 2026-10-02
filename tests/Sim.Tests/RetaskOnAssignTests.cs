using Sim.Core;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Population;
using Sim.Core.World;

namespace Sim.Tests;

// RETASK ON ASSIGN (2026-10-01). Found in play: a builder working one site was
// right-clicked onto a second site with an open spot and did not move, because
// the assign intents skipped every non-Idle unit while MoveIntent released
// them. The player's gesture is the same in both cases — "that body belongs
// there now" — so both now go through Sim.Core.Intents.Retask.
//
// The companion rule: when a workplace is already full, the extra hands still
// MARCH there (a plain walk, no goal). A body that stays put reads as a refusal.
public class RetaskOnAssignTests
{
    private static readonly TileCoord Keep = new(4, 4);
    private static readonly TileCoord SiteA = new(10, 4);
    private static readonly TileCoord SiteB = new(10, 10);
    private static readonly TileCoord FarmAt = new(4, 12);

    private static Simulation BuildWorld()
    {
        var grid = new TileGrid(24, 24, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.AddStructure(new Castle(Keep) { OwnerId = 0 }).Deposit(Resource.Food, 100_000);

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 24; y++)
            for (var x = 0; x < 24; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;
        return new Simulation(world, seed: 5);
    }

    private static Unit AddAdult(Simulation sim, int id, TileCoord at, UnitRole role)
    {
        var cfg = new PopulationConfig();
        var u = new Unit(id, at) { Role = role, OwnerId = 0, BornTick = -25 * cfg.TicksPerYear };
        sim.World.AddUnit(u);
        return u;
    }

    private static ConstructionSite AddProvisionedSite(Simulation sim, TileCoord at, StructureKind kind)
    {
        var site = new ConstructionSite(at, kind) { OwnerId = 0 };
        sim.World.AddStructure(site);
        foreach (var (r, n) in StructureCatalog.Spec(kind).BuildCost) site.Deposit(r, n);
        return site;
    }

    [Fact]
    public void ABuilderBusyOnOneSite_SentToAnother_LeavesTheFirstAndWalksToTheSecond()
    {
        var sim = BuildWorld();
        var a = AddProvisionedSite(sim, SiteA, StructureKind.House);
        AddProvisionedSite(sim, SiteB, StructureKind.House);
        AddAdult(sim, 1, SiteA, UnitRole.Builder);

        sim.SubmitIntent(0, new AssignBuildersIntent(SiteA, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);
        Assert.Equal(Activity.Building, sim.World.Units[1].Activity);
        Assert.True(a.IsActive);

        sim.SubmitIntent(sim.Now, new AssignBuildersIntent(SiteB, new[] { 1 }) { PlayerId = 0 });
        sim.Run(sim.Now);

        Assert.True(sim.ResolvedLog[^1].Outcome.IsApplied);
        Assert.True(a.BuildPaused);                      // the first site lost its only builder
        Assert.Equal(GoalKind.AssignBuilder, sim.World.Units[1].Goal?.Kind);
        Assert.Equal(SiteB, sim.World.Units[1].Goal?.TargetTile);

        sim.Run(sim.Now + 10 * Time.Day);
        Assert.Equal(SiteB, sim.World.Units[1].Position);
        // Ten days is long enough to finish a house, so either reading is the proof.
        Assert.True(sim.World.Structures[SiteB] is House
                    || sim.World.Units[1].Activity == Activity.Building);
    }

    [Fact]
    public void AWorkerOnTheFarm_SentToBuild_LeavesThePayroll()
    {
        var sim = BuildWorld();
        var farm = new Extractor(StructureKind.Farm, FarmAt) { OwnerId = 0 };
        sim.World.AddStructure(farm);
        AddProvisionedSite(sim, SiteA, StructureKind.House);
        AddAdult(sim, 1, FarmAt, UnitRole.Builder);

        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);
        Assert.Contains(1, farm.Workers);

        sim.SubmitIntent(sim.Now, new AssignBuildersIntent(SiteA, new[] { 1 }) { PlayerId = 0 });
        sim.Run(sim.Now);

        Assert.True(sim.ResolvedLog[^1].Outcome.IsApplied);
        Assert.DoesNotContain(1, farm.Workers);
        Assert.NotNull(sim.World.Units[1].Goal);
    }

    [Fact]
    public void AnErrandInFlight_IsCountermandedByTheNewAssignment_NotDoubled()
    {
        var sim = BuildWorld();
        AddProvisionedSite(sim, SiteA, StructureKind.House);
        AddProvisionedSite(sim, SiteB, StructureKind.House);
        AddAdult(sim, 1, Keep, UnitRole.Builder);

        sim.SubmitIntent(0, new AssignBuildersIntent(SiteA, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);
        Assert.Equal(SiteA, sim.World.Units[1].Goal?.TargetTile);

        sim.SubmitIntent(sim.Now, new AssignBuildersIntent(SiteB, new[] { 1 }) { PlayerId = 0 });
        sim.Run(sim.Now);

        Assert.Equal(SiteB, sim.World.Units[1].Goal?.TargetTile);
        Assert.Contains(sim.ResolvedLog, e => e is GoalDissolvedEvent d && d.TargetTile == SiteA);

        sim.Run(sim.Now + 10 * Time.Day);
        Assert.Equal(SiteB, sim.World.Units[1].Position);
        Assert.Null(sim.World.Units[1].Goal);
    }

    [Fact]
    public void ReissuingTheSameErrand_DoesNotRestartTheWalk()
    {
        var sim = BuildWorld();
        AddProvisionedSite(sim, SiteA, StructureKind.House);
        AddAdult(sim, 1, Keep, UnitRole.Builder);

        sim.SubmitIntent(0, new AssignBuildersIntent(SiteA, new[] { 1 }) { PlayerId = 0 });
        sim.Run(0);
        var epoch = sim.World.Units[1].AssignmentEpoch;

        sim.SubmitIntent(sim.Now, new AssignBuildersIntent(SiteA, new[] { 1 }) { PlayerId = 0 });
        sim.Run(sim.Now);

        Assert.Equal(epoch, sim.World.Units[1].AssignmentEpoch);
        Assert.DoesNotContain(sim.ResolvedLog, e => e is GoalDissolvedEvent);
    }

    [Fact]
    public void ABreedingParent_IsStillRefused()
    {
        // The one commitment a retask never overrides. Pinned here so the shared
        // refusal list cannot quietly lose it.
        var sim = BuildWorld();
        var house = new House(new TileCoord(6, 6)) { OwnerId = 0 };
        sim.World.AddStructure(house);
        house.Deposit(Resource.Food, 10_000);
        AddProvisionedSite(sim, SiteA, StructureKind.House);
        AddAdult(sim, 1, house.At, UnitRole.Builder);
        AddAdult(sim, 2, house.At, UnitRole.Farmer);

        sim.SubmitIntent(0, new BeginBreedingIntent(house.At, 1, 2) { PlayerId = 0 });
        sim.Run(0);
        Assert.NotNull(Population.GetActiveBreedingFor(sim.World, 1));

        sim.SubmitIntent(sim.Now, new AssignBuildersIntent(SiteA, new[] { 1 }) { PlayerId = 0 });
        sim.Run(sim.Now);

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
        Assert.NotNull(Population.GetActiveBreedingFor(sim.World, 1));
    }

    [Fact]
    public void HandsPastTheCap_StillMarchThere_WithoutAGoal()
    {
        var sim = BuildWorld();
        var farm = new Extractor(StructureKind.Farm, FarmAt) { OwnerId = 0 };
        sim.World.AddStructure(farm);
        var ids = new List<int>();
        for (var i = 0; i < farm.Spec.WorkerCap + 2; i++)
        {
            AddAdult(sim, 10 + i, Keep, UnitRole.Farmer);
            ids.Add(10 + i);
        }

        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, ids) { PlayerId = 0 });
        sim.Run(0);

        var units = ids.Select(id => sim.World.Units[id]).ToList();
        Assert.Equal(farm.Spec.WorkerCap, units.Count(u => u.Goal is not null));
        Assert.All(units, u => Assert.True(u.IsWalking));   // everyone goes

        sim.Run(10 * Time.Day);
        Assert.All(units, u => Assert.Equal(FarmAt, u.Position));
        Assert.Equal(farm.Spec.WorkerCap, farm.Workers.Count);
        Assert.Equal(2, units.Count(u => u.Activity == Activity.Idle));
    }

    [Fact]
    public void AFullWorkplace_StillMovesEveryoneSent()
    {
        var sim = BuildWorld();
        var farm = new Extractor(StructureKind.Farm, FarmAt) { OwnerId = 0 };
        sim.World.AddStructure(farm);
        var crew = new List<int>();
        for (var i = 0; i < farm.Spec.WorkerCap; i++) { AddAdult(sim, 10 + i, FarmAt, UnitRole.Farmer); crew.Add(10 + i); }
        sim.SubmitIntent(0, new AssignWorkersIntent(FarmAt, crew) { PlayerId = 0 });
        sim.Run(0);
        Assert.Equal(farm.Spec.WorkerCap, farm.Workers.Count);

        AddAdult(sim, 1, Keep, UnitRole.Farmer);
        sim.SubmitIntent(sim.Now, new AssignWorkersIntent(FarmAt, new[] { 1 }) { PlayerId = 0 });
        sim.Run(sim.Now);

        Assert.True(sim.ResolvedLog[^1].Outcome.IsApplied);   // used to reject outright
        Assert.True(sim.World.Units[1].IsWalking);
        Assert.Null(sim.World.Units[1].Goal);
    }
}
