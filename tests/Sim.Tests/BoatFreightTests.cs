using Sim.Core.Boats;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// M28 — boat freight (docs/boats.md update). The Dock is the quay
// warehouse (a StorageStructure) and HaulIntent goes amphibious: a
// Water-traversal hauler serves docks, standing on each dock's SLIP while
// the cargo moves against the dock. Pins:
//   1. Land haulers serve the dock with ordinary HaulIntents (both
//      directions) — the quay bridges the two carrier domains.
//   2. The boat haul: pickup at the source dock from its slip, sail,
//      deposit at the dest dock — 100 cargo per trip (BoatCapacity).
//   3. Boat freight REQUIRES dock endpoints (the sole land/water cargo
//      interface — the embark/disembark rule applied to goods).
//   4. No water route → fail CLEAN and laden; UnloadCargoIntent empties a
//      slip-parked boat into its quay (the recovery path).
//   5. Determinism: twin-run, mid-sail recovery, quay-holdings round-trip.
public class BoatFreightTests
{
    // A strait: land west (x 0..3), water column (x 4..7), land east
    // (x 8..11). Dock A on the west shore, dock B on the east shore.
    private static (Simulation sim, Dock dockA, Dock dockB, Unit boat) MakeStrait()
    {
        var water = new Dictionary<TileCoord, Biome>();
        for (var x = 4; x <= 7; x++)
            for (var y = 0; y < 4; y++)
                water[new TileCoord(x, y)] = Biome.Water;
        var spec = new GenesisSpec
        {
            Width = 12, Height = 4,
            DefaultBiome = Biome.Grassland,
            Biomes = water,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(1, 1), UnitRole.Hauler, OwnerId: 0),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xF8E1);
        var dockA = sim.World.AddStructure(new Dock(
            new TileCoord(3, 1), new TileCoord(4, 1)) { OwnerId = 0 });
        var dockB = sim.World.AddStructure(new Dock(
            new TileCoord(8, 2), new TileCoord(7, 2)) { OwnerId = 0 });
        var boat = sim.World.AddUnit(new Unit(50, dockA.Slip)
        {
            Role = UnitRole.Boat, OwnerId = 0, Traversal = Traversal.Water,
            PassengerCap = BoatConstants.DefaultPassengerCap, BornTick = 0,
        });
        return (sim, dockA, dockB, boat);
    }

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

    private static Castle CastleOf(Simulation sim) =>
        sim.World.Structures.Values.OfType<Castle>().Single();

    // ====================================================================
    // The quay warehouse — land hauls serve the dock
    // ====================================================================

    [Fact]
    public void Dock_IsAQuayWarehouse_LandHaulsServeIt_BothWays()
    {
        var (sim, dockA, _, _) = MakeStrait();
        var castle = CastleOf(sim);
        castle.Deposit(Resource.Wood, 100);
        var hauler = sim.World.Units[1];

        // Castle → dock: the ordinary land haul, no new machinery.
        Assert.True(new HaulIntent(hauler.Id, castle.At, dockA.At, Resource.Wood)
        { PlayerId = 0 }.Resolve(sim).IsApplied);
        AdvanceTo(sim, 1000);
        Assert.Equal(UnitCargoCatalog.HaulerCapacity, dockA.AmountOf(Resource.Wood));

        // Dock → castle: the quay is a first-class haul source too.
        var castleWood = castle.AmountOf(Resource.Wood);
        Assert.True(new HaulIntent(hauler.Id, dockA.At, castle.At, Resource.Wood)
        { PlayerId = 0 }.Resolve(sim).IsApplied);
        AdvanceTo(sim, 2000);
        Assert.Equal(0, dockA.AmountOf(Resource.Wood));
        Assert.Equal(castleWood + UnitCargoCatalog.HaulerCapacity,
            castle.AmountOf(Resource.Wood));
    }

    // ====================================================================
    // The boat haul — dock to dock across the water
    // ====================================================================

    [Fact]
    public void BoatHaul_DockToDock_MovesCargoAcrossTheWater()
    {
        var (sim, dockA, dockB, boat) = MakeStrait();
        dockA.Deposit(Resource.Wood, 150);

        Assert.True(new HaulIntent(boat.Id, dockA.At, dockB.At, Resource.Wood)
        { PlayerId = 0 }.Resolve(sim).IsApplied);
        AdvanceTo(sim, 500);

        // One trip = the boat's 100 capacity: 4× a land hauler's.
        Assert.Equal(UnitCargoCatalog.BoatCapacity, dockB.AmountOf(Resource.Wood));
        Assert.Equal(150 - UnitCargoCatalog.BoatCapacity, dockA.AmountOf(Resource.Wood));
        // The boat finished at the DEST SLIP (never the dock's land tile),
        // empty and Idle — ready for the next charter.
        Assert.Equal(dockB.Slip, boat.Position);
        Assert.Equal(0, boat.CargoAmount);
        Assert.Equal(Activity.Idle, boat.Activity);
        Assert.Null(boat.HaulPlan);
    }

    [Fact]
    public void BoatHaul_RequiresDockEndpoints()
    {
        var (sim, dockA, _, boat) = MakeStrait();
        var castle = CastleOf(sim);
        castle.Deposit(Resource.Wood, 100);
        dockA.Deposit(Resource.Wood, 100);

        // A boat cannot serve a castle (no slip to stand on) — either end.
        Assert.True(new HaulIntent(boat.Id, castle.At, dockA.At, Resource.Wood)
        { PlayerId = 0 }.Resolve(sim).IsRejected);
        Assert.True(new HaulIntent(boat.Id, dockA.At, castle.At, Resource.Wood)
        { PlayerId = 0 }.Resolve(sim).IsRejected);
        // Foot haulers are unaffected by the dock rule (pre-M28 behavior).
        Assert.True(new HaulIntent(sim.World.Units[1].Id, castle.At, dockA.At, Resource.Wood)
        { PlayerId = 0 }.Resolve(sim).IsApplied);
    }

    [Fact]
    public void BoatHaul_NoWaterRoute_FailsCleanLaden_UnloadRecovers()
    {
        var (sim, dockA, _, boat) = MakeStrait();
        // A second, disconnected pond on the east land mass with its own dock.
        sim.World.Grid.SetBiome(new TileCoord(10, 1), Biome.Water);
        var pondDock = sim.World.AddStructure(new Dock(
            new TileCoord(10, 2), new TileCoord(10, 1)) { OwnerId = 0 });
        dockA.Deposit(Resource.Wood, 150);

        // Both endpoints are docks, so the intent resolves; the pickup at A
        // succeeds; the second leg finds NO route to the pond — the boat
        // fails clean: Idle, still laden, parked at the source slip (the
        // M28 zombie-hauler hardening).
        Assert.True(new HaulIntent(boat.Id, dockA.At, pondDock.At, Resource.Wood)
        { PlayerId = 0 }.Resolve(sim).IsApplied);
        AdvanceTo(sim, 200);
        Assert.Equal(dockA.Slip, boat.Position);
        Assert.Equal(Activity.Idle, boat.Activity);
        Assert.Equal(UnitCargoCatalog.BoatCapacity, boat.CargoAmount);
        Assert.Null(boat.HaulPlan);

        // Recovery: a slip-parked boat unloads into its quay.
        Assert.True(new UnloadCargoIntent(boat.Id) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Equal(0, boat.CargoAmount);
        Assert.Equal(150, dockA.AmountOf(Resource.Wood));   // all of it back home
    }

    // ====================================================================
    // Determinism
    // ====================================================================

    // The full multi-modal chain: castle → (land haul) → dock A →
    // (boat haul) → dock B. The M28 headline scenario. NO NoOp advances —
    // Run(until:) leaves the clock at the LAST EVENT, so both the
    // uninterrupted and the snapshot-and-recover paths must end on the
    // same real event (the boat's deposit) for their hashes to compare.
    // `stopOffset` is ticks past the sail's start: small = mid-sail,
    // large = the whole voyage (the deposit is the final clock event).
    private static (Simulation Sim, long SailStart) RunFreightChain(long stopOffset)
    {
        var (sim, dockA, dockB, boat) = MakeStrait();
        var castle = CastleOf(sim);
        castle.Deposit(Resource.Wood, 100);
        Assert.True(new HaulIntent(1, castle.At, dockA.At, Resource.Wood)
        { PlayerId = 0 }.Resolve(sim).IsApplied);
        sim.Run(until: 1000);   // land leg done; the quay is stocked
        var sailStart = sim.Now;
        Assert.True(new HaulIntent(boat.Id, dockA.At, dockB.At, Resource.Wood)
        { PlayerId = 0 }.Resolve(sim).IsApplied);
        sim.Run(until: sailStart + stopOffset);
        return (sim, sailStart);
    }

    [Fact]
    public void Freight_TwinRun_HashesMatch()
    {
        var (a, _) = RunFreightChain(1000);
        var (b, _) = RunFreightChain(1000);
        // Sanity: the chain actually moved goods across the water.
        var dockB = a.World.Structures.Values.OfType<Dock>()
            .Single(d => d.At.X == 8);
        Assert.True(dockB.AmountOf(Resource.Wood) > 0, "freight never arrived");
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    [Fact]
    public void Freight_SnapshotMidSail_RecoversIdentically()
    {
        // Path A: uninterrupted; the boat's deposit is the final clock event.
        var (a, sailStartA) = RunFreightChain(1000);
        var hashA = Snapshot.Hash(a);

        // Path B: stop on a mid-sail ARRIVAL (a real event — hop cost 6, so
        // offset 13 lands after the second hop), snapshot the laden voyage,
        // restore, and finish. Same final event → same clock → same hash.
        var (b, sailStartB) = RunFreightChain(13);
        Assert.Equal(sailStartA, sailStartB);
        var boat = b.World.Units[50];
        Assert.True(boat.CargoAmount > 0 && boat.HaulPlan is not null,
            "must snapshot mid-haul");
        var restored = Snapshot.Restore(Snapshot.Serialize(b), seed: 0xF8E1);
        restored.Run(until: sailStartB + 1000);

        Assert.Equal(hashA, Snapshot.Hash(restored));
    }

    [Fact]
    public void QuayHoldings_RoundTripThroughSnapshot()
    {
        var (sim, dockA, _, _) = MakeStrait();
        dockA.Deposit(Resource.Wood, 123);
        dockA.Deposit(Resource.Stone, 45);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 0xF8E1);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        var rd = restored.World.Structures.Values.OfType<Dock>().Single(d => d.At.X == 3);
        Assert.Equal(123, rd.AmountOf(Resource.Wood));
        Assert.Equal(45, rd.AmountOf(Resource.Stone));
        Assert.Equal(dockA.Slip, rd.Slip);
    }
}
