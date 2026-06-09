using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// Phase 0 of M2: MoveIntent on a non-Idle unit clears their status,
// cleans up the structure they were attached to, and proceeds with the
// move. Stale per-unit events (HaulPickup, HaulDeposit) carry the
// AssignmentEpoch they were scheduled at and no-op on fire when retasked.
public class MoveOnBusyTests
{
    [Fact]
    public void TrySetActivity_BumpsEpoch_OnActualChange()
    {
        var u = new Unit(1, new TileCoord(0, 0));
        var e0 = u.AssignmentEpoch;
        u.TrySetActivity(Activity.Hauling);
        var e1 = u.AssignmentEpoch;
        u.TrySetActivity(Activity.Idle);
        var e2 = u.AssignmentEpoch;

        Assert.NotEqual(e0, e1);
        Assert.NotEqual(e1, e2);
    }

    [Fact]
    public void TrySetActivity_NoBump_OnNoChange()
    {
        var u = new Unit(1, new TileCoord(0, 0));
        var e0 = u.AssignmentEpoch;
        u.TrySetActivity(Activity.Idle); // already Idle
        Assert.Equal(e0, u.AssignmentEpoch);
    }

    // -------- Move-on-Working --------

    [Fact]
    public void MoveOnWorking_RemovesFromExtractor_AndProductionGoesDormant()
    {
        var grid = new TileGrid(8, 8, Biome.Grassland);
        var camp = new TileCoord(2, 0);
        grid.SetBiome(camp, Biome.Forest);
        var world = new GameWorld(grid);
        var ex = world.AddStructure(new Extractor(StructureKind.LumberCamp, camp));
        var worker = world.AddUnit(new Unit(1, camp) { Role = UnitRole.Lumberjack });
        var sim = new Simulation(world, seed: 1);

        sim.SubmitIntent(0, new AssignWorkersIntent(camp, new[] { 1 }));
        sim.Run(until: 0);
        Assert.Contains(1, ex.Workers);
        Assert.True(ex.TickArmed);

        // Move-on-busy: pull worker off to another tile.
        sim.SubmitIntent(sim.Now, new MoveIntent(1, new TileCoord(7, 0)));
        // Let the move complete + the next ProductionTick fire.
        sim.Run(until: ex.Spec.ProductionPeriodTicks * 2);

        Assert.DoesNotContain(1, ex.Workers);
        Assert.Equal(Activity.Idle, worker.Activity);
        // Production tick saw no workers, went dormant, no buffer accumulated.
        Assert.False(ex.TickArmed);
        Assert.Equal(0, ex.Buffer);
    }

    // -------- Move-on-Building --------

    [Fact]
    public void MoveOnBuilding_BelowRequirement_PausesBuild()
    {
        var grid = new TileGrid(8, 8, Biome.Grassland);
        var siteTile = new TileCoord(2, 2);
        var world = new GameWorld(grid);
        var site = world.AddStructure(new ConstructionSite(siteTile, StructureKind.Stockpile));
        var spec = StructureCatalog.Spec(StructureKind.Stockpile);
        foreach (var (r, n) in spec.BuildCost) site.Deposit(r, n);
        world.AddUnit(new Unit(1, siteTile) { Role = UnitRole.Builder });
        var sim = new Simulation(world, seed: 1);

        sim.SubmitIntent(0, new AssignBuildersIntent(siteTile, new[] { 1 }));
        sim.Run(until: 0);
        Assert.True(site.IsActive);

        // Move-on-busy: pull the only builder off. Build pauses.
        sim.SubmitIntent(sim.Now, new MoveIntent(1, new TileCoord(7, 7)));
        sim.Run();

        Assert.True(site.BuildPaused);
        Assert.False(site.IsActive);
        // Site survives; would-be BuildCompleteEvent fenced via ScheduledCompletion.
        Assert.IsType<ConstructionSite>(sim.World.Structures[siteTile]);
    }

    // -------- Move-on-Hauling --------

    [Fact]
    public void MoveOnHauling_KeepsCargo_AndStaleEventsFence()
    {
        var grid = new TileGrid(10, 1, Biome.Grassland);
        var castle = new TileCoord(0, 0);
        var stockpile = new TileCoord(9, 0);
        var world = new GameWorld(grid);
        var c = world.AddStructure(new Castle(castle));
        c.Deposit(Resource.Wood, 20);
        world.AddStructure(new Stockpile(stockpile));
        world.AddUnit(new Unit(1, castle) { Role = UnitRole.Hauler });
        var sim = new Simulation(world, seed: 1);

        // Start a haul. Hauler picks up Wood at castle, walks toward stockpile.
        sim.SubmitIntent(0, new HaulIntent(1, castle, stockpile, Resource.Wood));
        // Run until mid-trip (after pickup, mid-walk).
        sim.Run(until: 30); // enough to pick up + walk a few steps
        var hauler = sim.World.Units[1];
        Assert.Equal(Activity.Hauling, hauler.Activity);
        // Hauler cap is 25 (UnitCargoCatalog.HaulerCapacity); castle stock
        // is 20, so the pickup is stock-limited at 20.
        Assert.Equal(20, hauler.CargoAmount);

        // Move-on-busy: divert the hauler somewhere else mid-trip.
        var diversion = new TileCoord(3, 0);
        sim.SubmitIntent(sim.Now, new MoveIntent(1, diversion));
        sim.Run();

        // Hauler ended up at the diversion, still holding cargo (move-on-Hauling
        // preserves it), Activity cleared.
        Assert.Equal(diversion, hauler.Position);
        Assert.Equal(Activity.Idle, hauler.Activity);
        Assert.Equal(20, hauler.CargoAmount);
        Assert.Equal(Resource.Wood, hauler.CargoResource);

        // The fence intercepts at the move-chain level: stale
        // MoveArrivalEvents from the original haul's walk-to-stockpile leg
        // fire after the retask and no-op via epoch mismatch. The
        // HaulDepositEvent is never even reached because the chain that
        // would have scheduled it fenced first — exactly what we want.
        var fencedMoves = sim.ResolvedLog.OfType<MoveArrivalEvent>()
            .Where(e => e.UnitId == 1
                && e.Outcome.IsRejected
                && e.Outcome.Reason == "stale (epoch mismatch)")
            .ToList();
        Assert.NotEmpty(fencedMoves);
        // No deposit was ever scheduled because the chain fenced first.
        Assert.Empty(sim.ResolvedLog.OfType<HaulDepositEvent>());
    }

    // -------- The race window: Move-then-new-Haul --------

    [Fact]
    public void MoveThenNewHaul_OldEventsFence_NewHaulCompletes()
    {
        // This is the canonical race window: hauler in flight, player retasks
        // via Move, *then* issues a new Haul. Without epoch fencing, the old
        // pickup/deposit would damage the new haul's state.
        var grid = new TileGrid(10, 1, Biome.Grassland);
        var castle = new TileCoord(0, 0);
        var stockA = new TileCoord(9, 0);
        var stockB = new TileCoord(5, 0);
        var world = new GameWorld(grid);
        var c = world.AddStructure(new Castle(castle));
        c.Deposit(Resource.Wood, 50);
        c.Deposit(Resource.Stone, 50);
        world.AddStructure(new Stockpile(stockA));
        var stockpileB = world.AddStructure(new Stockpile(stockB));
        world.AddUnit(new Unit(1, castle) { Role = UnitRole.Hauler });
        var sim = new Simulation(world, seed: 1);

        // Haul #1: wood to stockA. Mid-walk diversion via MoveIntent. Then new
        // Haul #2: stone to stockB.
        sim.SubmitIntent(0, new HaulIntent(1, castle, stockA, Resource.Wood));
        sim.Run(until: 30); // pickup done, walking toward stockA
        sim.SubmitIntent(sim.Now, new MoveIntent(1, castle)); // walk back home
        sim.Run(); // complete the move
        var hauler = sim.World.Units[1];
        Assert.Equal(castle, hauler.Position);
        Assert.Equal(Activity.Idle, hauler.Activity);
        // Hauler is back at castle with old Wood cargo still on them.
        // Hauler cap = 25 (UnitCargoCatalog.HaulerCapacity), castle had 50
        // wood, so the pickup was cap-limited at 25.
        Assert.Equal(UnitCargoCatalog.HaulerCapacity, hauler.CargoAmount);
        Assert.Equal(Resource.Wood, hauler.CargoResource);

        // For Haul #2 the hauler must drop their cargo first. A new
        // HaulIntent doesn't reject (hauler IS Idle), but pickup checks
        // free capacity — full cargo means it would pick up 0 and reject.
        // Cleanest: self-haul (castle → castle) to drop the wood back in.
        sim.SubmitIntent(sim.Now, new HaulIntent(1, castle, castle, Resource.Wood));
        sim.Run();
        Assert.Equal(0, hauler.CargoAmount);

        sim.SubmitIntent(sim.Now, new HaulIntent(1, castle, stockB, Resource.Stone));
        sim.Run();

        // New haul completed: stockB has Stone (pickup cap-limited at 25).
        Assert.Equal(UnitCargoCatalog.HaulerCapacity, stockpileB.AmountOf(Resource.Stone));
        Assert.Equal(Activity.Idle, hauler.Activity);
        Assert.Equal(stockB, hauler.Position);

        // The original Haul #1's move chain fenced via MoveArrival epoch
        // mismatch (the chain never reached the deposit, so no deposit got
        // scheduled for it). Self-haul + new haul deposits both applied
        // normally, confirming no cross-task damage.
        Assert.Contains(sim.ResolvedLog.OfType<MoveArrivalEvent>(),
            e => e.UnitId == 1 && e.Outcome.IsRejected
                && e.Outcome.Reason == "stale (epoch mismatch)");
        // Two deposits applied successfully: self-haul (drop wood back at
        // castle) and new haul (deposit stone at stockB).
        var appliedDeposits = sim.ResolvedLog.OfType<HaulDepositEvent>()
            .Where(e => e.Outcome.IsApplied).ToList();
        Assert.Equal(2, appliedDeposits.Count);
    }

    // -------- Determinism + snapshot --------

    [Fact]
    public void TwinRun_MoveOnBusyScenario_HashesMatch()
    {
        Simulation Run()
        {
            var grid = new TileGrid(8, 8, Biome.Grassland);
            var camp = new TileCoord(2, 0);
            grid.SetBiome(camp, Biome.Forest);
            var world = new GameWorld(grid);
            world.AddStructure(new Extractor(StructureKind.LumberCamp, camp));
            world.AddUnit(new Unit(1, camp) { Role = UnitRole.Lumberjack });
            var sim = new Simulation(world, seed: 1);
            sim.SubmitIntent(0, new AssignWorkersIntent(camp, new[] { 1 }));
            sim.SubmitIntent(50, new MoveIntent(1, new TileCoord(7, 0)));
            sim.Run();
            return sim;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    [Fact]
    public void Snapshot_RoundTripsNonZeroEpoch()
    {
        var grid = new TileGrid(4, 4, Biome.Grassland);
        var world = new GameWorld(grid);
        var u = world.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Hauler });
        // Cycle activity a few times to bump epoch above zero.
        u.TrySetActivity(Activity.Hauling);
        u.TrySetActivity(Activity.Idle);
        u.TrySetActivity(Activity.Working, new TileCoord(0, 0));
        Assert.True(u.AssignmentEpoch > 0);

        var sim = new Simulation(world, seed: 1);
        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        Assert.Equal(u.AssignmentEpoch, restored.World.Units[1].AssignmentEpoch);
    }
}
