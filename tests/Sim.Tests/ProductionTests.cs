using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

public class ProductionTests
{
    private static (Simulation sim, Extractor extractor, TileCoord tile) MakeStaffableExtractor(
        StructureKind kind = StructureKind.LumberCamp,
        Biome biome = Biome.Forest)
    {
        var grid = new TileGrid(6, 6, Biome.Grassland);
        var tile = new TileCoord(2, 2);
        // M15: a claiming kind needs ClaimCount in-biome tiles within
        // ClaimRange (lazy auto-claim fills them at first arm) — paint the
        // whole claim box, derived from the spec.
        var spec = StructureCatalog.Spec(kind);
        var r = Math.Max(spec.ClaimRange, 0);
        for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
            {
                var t = new TileCoord(tile.X + dx, tile.Y + dy);
                if (t.X >= 0 && t.Y >= 0 && t.X < 6 && t.Y < 6) grid.SetBiome(t, biome);
            }
        var world = new GameWorld(grid);
        var extractor = world.AddStructure(new Extractor(kind, tile));
        var sim = new Simulation(world, seed: 1);
        return (sim, extractor, tile);
    }

    private static Unit AddIdleUnit(Simulation sim, int id, TileCoord pos, UnitRole role)
    {
        var u = new Unit(id, pos) { Role = role };
        sim.World.AddUnit(u);
        return u;
    }

    // -------- AssignWorkersIntent --------

    [Fact]
    public void AssignWorkers_HappyPath_ArmsProduction()
    {
        var (sim, ex, tile) = MakeStaffableExtractor();
        AddIdleUnit(sim, 1, tile, UnitRole.Lumberjack);

        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1 }));
        sim.Run(until: 0);

        Assert.Contains(1, ex.Workers);
        Assert.Equal(Activity.Working, sim.World.Units[1].Activity);
        Assert.Equal(tile, sim.World.Units[1].Assignment);
        Assert.True(ex.TickArmed);
        Assert.True(sim.ResolvedLog[^1].Outcome.IsApplied);
    }

    [Fact]
    public void AssignWorkers_MissingStructure_Rejected()
    {
        var (sim, _, _) = MakeStaffableExtractor();
        AddIdleUnit(sim, 1, new TileCoord(0, 0), UnitRole.Lumberjack);

        sim.SubmitIntent(0, new AssignWorkersIntent(new TileCoord(5, 5), new[] { 1 }));
        sim.Run();

        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void AssignWorkers_NonExtractorTarget_Rejected()
    {
        var grid = new TileGrid(6, 6);
        var tile = new TileCoord(2, 2);
        var world = new GameWorld(grid);
        world.AddStructure(new Castle(tile));
        var sim = new Simulation(world, seed: 1);
        AddIdleUnit(sim, 1, tile, UnitRole.Lumberjack);

        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1 }));
        sim.Run();

        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void AssignWorkers_NotOnTile_Skipped()
    {
        var (sim, ex, tile) = MakeStaffableExtractor();
        AddIdleUnit(sim, 1, new TileCoord(0, 0), UnitRole.Lumberjack);

        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1 }));
        sim.Run();

        Assert.Empty(ex.Workers);
        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void AssignWorkers_NotIdle_Skipped()
    {
        var (sim, ex, tile) = MakeStaffableExtractor();
        AddIdleUnit(sim, 1, tile, UnitRole.Lumberjack);
        sim.World.Units[1].TrySetActivity(Activity.Hauling); // pre-busy

        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1 }));
        sim.Run();

        Assert.Empty(ex.Workers);
        Assert.True(sim.ResolvedLog[^1].Outcome.IsRejected);
    }

    [Fact]
    public void AssignWorkers_CapEnforced_ExtraSkipped()
    {
        var (sim, ex, tile) = MakeStaffableExtractor();
        // LumberCamp WorkerCap == 3 per catalog
        for (var i = 1; i <= 5; i++) AddIdleUnit(sim, i, tile, UnitRole.Lumberjack);

        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1, 2, 3, 4, 5 }));
        sim.Run(until: 0);

        Assert.Equal(3, ex.Workers.Count);
        Assert.Equal(Activity.Idle, sim.World.Units[4].Activity);
        Assert.Equal(Activity.Idle, sim.World.Units[5].Activity);
    }

    // -------- UnassignWorkersIntent --------

    [Fact]
    public void UnassignWorkers_HappyPath_FreesUnits()
    {
        var (sim, ex, tile) = MakeStaffableExtractor();
        AddIdleUnit(sim, 1, tile, UnitRole.Lumberjack);
        AddIdleUnit(sim, 2, tile, UnitRole.Lumberjack);
        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1, 2 }));
        sim.Run(until: 0);

        sim.SubmitIntent(0, new UnassignWorkersIntent(tile, new[] { 1 }));
        sim.Run(until: 0);

        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.Equal(Activity.Working, sim.World.Units[2].Activity);
        Assert.DoesNotContain(1, ex.Workers);
        Assert.Contains(2, ex.Workers);
    }

    [Fact]
    public void UnassignAll_NextTickGoesDormant()
    {
        var (sim, ex, tile) = MakeStaffableExtractor();
        AddIdleUnit(sim, 1, tile, UnitRole.Lumberjack);
        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1 }));
        sim.SubmitIntent(0, new UnassignWorkersIntent(tile, new[] { 1 }));
        sim.Run();

        // The pre-armed tick fires, sees no workers, goes dormant, and is rejected.
        Assert.False(ex.TickArmed);
        Assert.Equal(0, ex.Buffer);
        Assert.Contains(sim.ResolvedLog.OfType<ProductionTickEvent>(),
            e => e.Outcome.IsRejected && e.Outcome.Reason == "no workers");
    }

    // -------- production math --------

    [Fact]
    public void OneWorker_BufferFillsAtBaseRate()
    {
        var (sim, ex, tile) = MakeStaffableExtractor();
        AddIdleUnit(sim, 1, tile, UnitRole.None); // not the preferred role
        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1 }));

        var spec = ex.Spec;
        // Run for 3 production periods
        sim.Run(until: spec.ProductionPeriodTicks * 3);

        Assert.Equal(spec.BaseRatePerWorker * 3, ex.Buffer);
        Assert.True(ex.TickArmed);
    }

    [Fact]
    public void MatchingRole_DoublesPerWorkerRate()
    {
        var (simA, exA, tileA) = MakeStaffableExtractor();
        AddIdleUnit(simA, 1, tileA, UnitRole.Lumberjack); // matches LumberCamp
        simA.SubmitIntent(0, new AssignWorkersIntent(tileA, new[] { 1 }));

        var (simB, exB, tileB) = MakeStaffableExtractor();
        AddIdleUnit(simB, 1, tileB, UnitRole.Hauler); // does not match
        simB.SubmitIntent(0, new AssignWorkersIntent(tileB, new[] { 1 }));

        var spec = exA.Spec;
        simA.Run(until: spec.ProductionPeriodTicks * 2);
        simB.Run(until: spec.ProductionPeriodTicks * 2);

        // Lumberjack: 2 * base * 2 periods. Hauler: 1 * base * 2 periods.
        Assert.Equal(spec.BaseRatePerWorker * 2 * 2, exA.Buffer);
        Assert.Equal(spec.BaseRatePerWorker * 1 * 2, exB.Buffer);
        Assert.Equal(exA.Buffer / 2, exB.Buffer);
    }

    [Fact]
    public void ThreeWorkers_TripleOutput()
    {
        var (sim, ex, tile) = MakeStaffableExtractor();
        for (var i = 1; i <= 3; i++) AddIdleUnit(sim, i, tile, UnitRole.None);
        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1, 2, 3 }));

        var spec = ex.Spec;
        sim.Run(until: spec.ProductionPeriodTicks * 2);

        Assert.Equal(spec.BaseRatePerWorker * 3 * 2, ex.Buffer);
    }

    // -------- back-pressure --------

    [Fact]
    public void BufferFills_NextTickGoesDormant()
    {
        var (sim, ex, tile) = MakeStaffableExtractor();
        AddIdleUnit(sim, 1, tile, UnitRole.Lumberjack);
        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1 }));

        var spec = ex.Spec;
        // BufferCap=30, Lumberjack rate=2/period, ProductionPeriodTicks=10.
        // 15 periods (tick 150) fills the buffer exactly to 30. That same tick
        // sees BufferFull on its re-arm check and stops scheduling.
        sim.Run(until: spec.ProductionPeriodTicks * 20);

        Assert.Equal(spec.BufferCap, ex.Buffer);
        Assert.False(ex.TickArmed);
        // The dormant transition happens *inside* the tick that fills the buffer,
        // not as a later rejected tick — that tick still produced successfully.
        // Verify production stopped: every applied tick is at or before the fill tick.
        var lastApplied = sim.ResolvedLog.OfType<ProductionTickEvent>()
            .Where(e => e.Outcome.IsApplied)
            .Max(e => e.At);
        Assert.Equal(spec.ProductionPeriodTicks * 15, lastApplied);
    }

    [Fact]
    public void BufferFullBeforeTickFires_FencingRejects()
    {
        // The "buffer full" reject path actually fires when the buffer gets
        // filled externally between ticks (e.g. test directly bumps it, or
        // a future production-affecting intent). Without that path, the
        // tick's own re-arm check handles it inside the same event.
        var (sim, ex, tile) = MakeStaffableExtractor();
        AddIdleUnit(sim, 1, tile, UnitRole.Lumberjack);
        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1 }));
        // Run one tick to get buffer = 2.
        sim.Run(until: ex.Spec.ProductionPeriodTicks);
        Assert.True(ex.TickArmed);
        Assert.Equal(2, ex.Buffer);

        // Cheat: fill the buffer externally. Next scheduled tick at 20 will fire,
        // see BufferFull, and reject.
        ex.Buffer = ex.Spec.BufferCap;
        sim.Run(until: ex.Spec.ProductionPeriodTicks * 2);

        Assert.False(ex.TickArmed);
        Assert.Contains(sim.ResolvedLog.OfType<ProductionTickEvent>(),
            e => e.Outcome.IsRejected && e.Outcome.Reason == "buffer full");
    }

    [Fact]
    public void ArmIfDormant_FromDormantBufferFull_NoArm()
    {
        var (sim, ex, tile) = MakeStaffableExtractor();
        AddIdleUnit(sim, 1, tile, UnitRole.Lumberjack);
        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1 }));
        sim.Run(until: ex.Spec.ProductionPeriodTicks * 16);
        Assert.False(ex.TickArmed);

        // Phase E will call this after a haul pickup. Without freeing buffer,
        // ArmIfDormant should be a no-op.
        ex.ArmIfDormant(sim);
        Assert.False(ex.TickArmed);
    }

    [Fact]
    public void ArmIfDormant_AfterTestDrain_ResumesProduction()
    {
        var (sim, ex, tile) = MakeStaffableExtractor();
        AddIdleUnit(sim, 1, tile, UnitRole.Lumberjack);
        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1 }));
        sim.Run(until: ex.Spec.ProductionPeriodTicks * 20);
        Assert.False(ex.TickArmed);
        Assert.Equal(ex.Spec.BufferCap, ex.Buffer);

        // Simulate a Phase-E haul pickup that frees 6 units of buffer space.
        // Lumberjack rate = 2/period, so 3 periods refill exactly.
        ex.Buffer -= 6;
        ex.ArmIfDormant(sim);
        Assert.True(ex.TickArmed);

        sim.Run(until: sim.Now + ex.Spec.ProductionPeriodTicks * 3);
        Assert.Equal(ex.Spec.BufferCap, ex.Buffer);
        Assert.False(ex.TickArmed);
    }

    // -------- determinism + snapshot --------

    [Fact]
    public void TwinRun_FullProductionScenario_HashMatches()
    {
        Simulation Build()
        {
            var (sim, _, tile) = MakeStaffableExtractor();
            AddIdleUnit(sim, 1, tile, UnitRole.Lumberjack);
            AddIdleUnit(sim, 2, tile, UnitRole.None);
            sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1, 2 }));
            return sim;
        }

        var a = Build();
        var b = Build();
        a.Run(until: 100);
        b.Run(until: 100);

        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    [Fact]
    public void ProductionInProgress_RoundTripsThroughSnapshot()
    {
        var (sim, ex, tile) = MakeStaffableExtractor();
        AddIdleUnit(sim, 1, tile, UnitRole.Lumberjack);
        sim.SubmitIntent(0, new AssignWorkersIntent(tile, new[] { 1 }));
        sim.Run(until: 35); // mid-production

        Assert.True(ex.Buffer > 0 && ex.Buffer < ex.Spec.BufferCap);
        Assert.True(ex.TickArmed);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }
}
