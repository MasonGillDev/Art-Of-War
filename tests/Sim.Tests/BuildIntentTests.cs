using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

public class BuildIntentTests
{
    // Tiny helpers — every test starts from a known state so the asserts read
    // as "given X, when Y, then Z" without setup noise.
    private static Simulation MakeSim(int width = 6, int height = 6, Biome defaultBiome = Biome.Grassland)
    {
        var grid = new TileGrid(width, height, defaultBiome);
        var world = new GameWorld(grid);
        return new Simulation(world, seed: 1);
    }

    private static Unit AddBuilder(Simulation sim, int id, TileCoord pos)
    {
        var u = new Unit(id, pos) { Role = UnitRole.Builder };
        sim.World.AddUnit(u);
        return u;
    }

    // The sim only advances Now when an event fires. To "wait" to a tick
    // without doing anything else, schedule a no-op there.
    private sealed class NoOpEvent : ScheduledEvent
    {
        public override void Apply(Simulation sim) { }
    }

    private static void AdvanceTo(Simulation sim, long tick)
    {
        if (tick <= sim.Now) return;
        sim.Schedule(tick, new NoOpEvent());
        sim.Run(until: tick);
    }

    // -------- PlaceSiteIntent --------

    [Fact]
    public void PlaceSite_HappyPath_CreatesSite()
    {
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(2, 2), Biome.Forest);

        sim.SubmitIntent(0, new PlaceSiteIntent(new TileCoord(2, 2), StructureKind.LumberCamp));
        sim.Run();

        Assert.True(sim.World.Structures.ContainsKey(new TileCoord(2, 2)));
        Assert.IsType<ConstructionSite>(sim.World.Structures[new TileCoord(2, 2)]);
        Assert.True(sim.ResolvedLog[^1].Outcome.IsApplied);
    }

    [Fact]
    public void PlaceSite_WrongBiome_Rejected()
    {
        var sim = MakeSim(); // all Grassland
        sim.SubmitIntent(0, new PlaceSiteIntent(new TileCoord(2, 2), StructureKind.LumberCamp));
        sim.Run();

        Assert.False(sim.World.Structures.ContainsKey(new TileCoord(2, 2)));
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void PlaceSite_OccupiedTile_Rejected()
    {
        var sim = MakeSim();
        sim.World.AddStructure(new Castle(new TileCoord(2, 2)));
        sim.SubmitIntent(0, new PlaceSiteIntent(new TileCoord(2, 2), StructureKind.Stockpile));
        sim.Run();

        Assert.IsType<Castle>(sim.World.Structures[new TileCoord(2, 2)]);
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Theory]
    [InlineData(StructureKind.Castle)]
    [InlineData(StructureKind.ConstructionSite)]
    // Tower was reserved in earlier milestones; M3 graduated it to buildable.
    public void PlaceSite_NonBuildableKind_Rejected(StructureKind kind)
    {
        var sim = MakeSim();
        sim.SubmitIntent(0, new PlaceSiteIntent(new TileCoord(2, 2), kind));
        sim.Run();

        Assert.False(sim.World.Structures.ContainsKey(new TileCoord(2, 2)));
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void PlaceSite_OutOfBounds_Rejected()
    {
        var sim = MakeSim(width: 4, height: 4);
        sim.SubmitIntent(0, new PlaceSiteIntent(new TileCoord(10, 10), StructureKind.Stockpile));
        sim.Run();

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    // -------- AssignBuildersIntent --------

    [Fact]
    public void AssignBuilders_WithMaterials_StartsBuild()
    {
        var sim = MakeSim();
        var siteTile = new TileCoord(2, 2);
        sim.World.Grid.SetBiome(siteTile, Biome.Forest);
        var site = sim.World.AddStructure(new ConstructionSite(siteTile, StructureKind.LumberCamp));
        // Pre-deposit the build cost. Phase E will haul this.
        var spec = StructureCatalog.Spec(StructureKind.LumberCamp);
        foreach (var (r, n) in spec.BuildCost) site.Deposit(r, n);
        AddBuilder(sim, 1, siteTile);

        sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 1 }));
        sim.Run();

        // The build started, ran to completion, and the LumberCamp now exists.
        Assert.False(sim.World.Structures.ContainsKey(siteTile) && sim.World.Structures[siteTile] is ConstructionSite);
        Assert.IsType<Extractor>(sim.World.Structures[siteTile]);
        Assert.Equal(StructureKind.LumberCamp, sim.World.Structures[siteTile].Kind);
        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.Equal(spec.BuildDurationTicks, sim.Now);
    }

    [Fact]
    public void AssignBuilders_NonBuilderRole_Skipped()
    {
        var sim = MakeSim();
        var siteTile = new TileCoord(2, 2);
        sim.World.Grid.SetBiome(siteTile, Biome.Forest);
        sim.World.AddStructure(new ConstructionSite(siteTile, StructureKind.LumberCamp));
        sim.World.AddUnit(new Unit(1, siteTile) { Role = UnitRole.Hauler });

        sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 1 }));
        sim.Run();

        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void AssignBuilders_NotOnSiteTile_Skipped()
    {
        var sim = MakeSim();
        var siteTile = new TileCoord(2, 2);
        sim.World.Grid.SetBiome(siteTile, Biome.Forest);
        sim.World.AddStructure(new ConstructionSite(siteTile, StructureKind.LumberCamp));
        AddBuilder(sim, 1, new TileCoord(0, 0));

        sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 1 }));
        sim.Run();

        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void AssignBuilders_MissingSite_Rejected()
    {
        var sim = MakeSim();
        AddBuilder(sim, 1, new TileCoord(2, 2));
        sim.SubmitIntent(0, new AssignBuildersIntent(new TileCoord(2, 2), new[] { 1 }));
        sim.Run();

        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void AssignBuilders_NoMaterials_AssignsButDoesNotStart()
    {
        var sim = MakeSim();
        var siteTile = new TileCoord(2, 2);
        sim.World.Grid.SetBiome(siteTile, Biome.Forest);
        var site = sim.World.AddStructure(new ConstructionSite(siteTile, StructureKind.LumberCamp));
        AddBuilder(sim, 1, siteTile);

        sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 1 }));
        sim.Run();

        // Builder assigned, but conditions not met → no start.
        Assert.Equal(Activity.Building, sim.World.Units[1].Activity);
        Assert.False(site.IsActive);
        Assert.Null(site.ScheduledCompletion);
        // Site still pending.
        Assert.True(sim.World.Structures.ContainsKey(siteTile));
        Assert.IsType<ConstructionSite>(sim.World.Structures[siteTile]);
    }

    [Fact]
    public void AssignBuilders_PartialSuccess_AppliesValidOnes()
    {
        var sim = MakeSim();
        var siteTile = new TileCoord(2, 2);
        sim.World.Grid.SetBiome(siteTile, Biome.Mountain); // Quarry needs 2 builders
        var site = sim.World.AddStructure(new ConstructionSite(siteTile, StructureKind.Quarry));
        var spec = StructureCatalog.Spec(StructureKind.Quarry);
        foreach (var (r, n) in spec.BuildCost) site.Deposit(r, n);
        AddBuilder(sim, 1, siteTile);
        AddBuilder(sim, 2, siteTile);
        sim.World.AddUnit(new Unit(3, siteTile) { Role = UnitRole.Hauler }); // invalid role

        sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 1, 3, 2 }));
        sim.Run();

        // 1 and 2 assigned; 3 skipped; build completed (2 builders required, 2 present).
        Assert.IsType<Extractor>(sim.World.Structures[siteTile]);
        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.Equal(Activity.Idle, sim.World.Units[3].Activity);
    }

    // -------- BuildCompleteEvent / fencing --------

    [Fact]
    public void BuildComplete_OnPausedSite_NoOps()
    {
        // Drive the lifecycle manually so we can verify the fencing token
        // behaviour without needing an intent that triggers Pause.
        var sim = MakeSim();
        var siteTile = new TileCoord(2, 2);
        sim.World.Grid.SetBiome(siteTile, Biome.Forest);
        var site = sim.World.AddStructure(new ConstructionSite(siteTile, StructureKind.LumberCamp));
        var spec = StructureCatalog.Spec(StructureKind.LumberCamp);
        foreach (var (r, n) in spec.BuildCost) site.Deposit(r, n);
        AddBuilder(sim, 1, siteTile);
        sim.World.Units[1].TrySetActivity(Activity.Building, siteTile);

        site.StartOrResume(sim);
        var scheduledAt = site.ScheduledCompletion!.Value;
        var halfway = scheduledAt / 2;

        // Advance to halfway, then pause.
        AdvanceTo(sim, halfway);
        site.Pause(sim.Now);

        // Run to where BuildComplete was originally scheduled (and beyond).
        sim.Run();

        // Site is still there as a paused ConstructionSite.
        Assert.IsType<ConstructionSite>(sim.World.Structures[siteTile]);
        Assert.True(site.BuildPaused);
        Assert.Equal(halfway, site.ProgressTicks);

        // The stale BuildCompleteEvent fired and got fenced out.
        var fenced = sim.ResolvedLog.OfType<BuildCompleteEvent>().Single();
        Assert.Equal(scheduledAt, fenced.At);
        Assert.True(fenced.Outcome.IsRejected);
    }

    [Fact]
    public void Pause_Then_Resume_CompletesAtAdjustedTick()
    {
        var sim = MakeSim();
        var siteTile = new TileCoord(2, 2);
        sim.World.Grid.SetBiome(siteTile, Biome.Forest);
        var site = sim.World.AddStructure(new ConstructionSite(siteTile, StructureKind.LumberCamp));
        var spec = StructureCatalog.Spec(StructureKind.LumberCamp);
        foreach (var (r, n) in spec.BuildCost) site.Deposit(r, n);
        AddBuilder(sim, 1, siteTile);
        sim.World.Units[1].TrySetActivity(Activity.Building, siteTile);

        site.StartOrResume(sim);
        var firstCompletion = site.ScheduledCompletion!.Value;
        var halfway = firstCompletion / 2;

        // Pause halfway through.
        AdvanceTo(sim, halfway);
        site.Pause(sim.Now);
        var progressAtPause = site.ProgressTicks;
        Assert.Equal(halfway, progressAtPause);

        // Let the stale BuildComplete fire as a no-op; sim.Now advances to firstCompletion.
        sim.Run();
        Assert.Equal(firstCompletion, sim.Now);

        // Wait a bit longer to mimic real time spent paused, then resume.
        var resumeAt = firstCompletion + 5;
        AdvanceTo(sim, resumeAt);
        site.StartOrResume(sim);
        var secondCompletion = site.ScheduledCompletion!.Value;
        var expectedSecond = resumeAt + (spec.BuildDurationTicks - progressAtPause);
        Assert.Equal(expectedSecond, secondCompletion);

        sim.Run();
        Assert.IsType<Extractor>(sim.World.Structures[siteTile]);
        Assert.Equal(expectedSecond, sim.Now);
    }

    // -------- determinism --------

    [Fact]
    public void TwinRun_OnFullBuildScenario_ProducesIdenticalHash()
    {
        Simulation Build()
        {
            var sim = MakeSim();
            var siteTile = new TileCoord(2, 2);
            sim.World.Grid.SetBiome(siteTile, Biome.Forest);
            var site = sim.World.AddStructure(new ConstructionSite(siteTile, StructureKind.LumberCamp));
            var spec = StructureCatalog.Spec(StructureKind.LumberCamp);
            foreach (var (r, n) in spec.BuildCost) site.Deposit(r, n);
            AddBuilder(sim, 1, siteTile);
            sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 1 }));
            return sim;
        }

        var a = Build();
        var b = Build();
        a.Run();
        b.Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    [Fact]
    public void BuiltStructure_RoundTripsThroughSnapshot()
    {
        var sim = MakeSim();
        var siteTile = new TileCoord(2, 2);
        sim.World.Grid.SetBiome(siteTile, Biome.Forest);
        var site = sim.World.AddStructure(new ConstructionSite(siteTile, StructureKind.LumberCamp));
        var spec = StructureCatalog.Spec(StructureKind.LumberCamp);
        foreach (var (r, n) in spec.BuildCost) site.Deposit(r, n);
        AddBuilder(sim, 1, siteTile);
        sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 1 }));
        sim.Run();

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }
}
