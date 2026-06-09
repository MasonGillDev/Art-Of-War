using Sim.Core.Boats;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M12 Phase A — Traversal enum + BoatMovementCost. No boats exist
// yet; every existing unit has Traversal.Foot, and existing
// pathfinding tests must stay green (covered by the regression suite).
public class BoatsPhaseATests
{
    [Fact]
    public void Default_Unit_Has_FootTraversal()
    {
        var u = new Unit(1, new TileCoord(0, 0));
        Assert.Equal(Traversal.Foot, u.Traversal);
    }

    [Fact]
    public void Traversal_AppendOnly_FootIsZero_WaterIsOne()
    {
        // Snapshot serializes the enum as a byte; the byte values are a
        // permanent contract per architecture §3 rule 5.
        Assert.Equal(0, (byte)Traversal.Foot);
        Assert.Equal(1, (byte)Traversal.Water);
    }

    [Fact]
    public void BoatMovementCost_Water_IsCheap()
    {
        Assert.Equal(BoatMovementCost.WaterCost, BoatMovementCost.CostFor(Biome.Water));
    }

    [Fact]
    public void BoatMovementCost_LandBiomes_AllImpassable()
    {
        foreach (var biome in new[]
        {
            Biome.Grassland, Biome.Forest, Biome.Hills,
            Biome.Mountain, Biome.Desert, Biome.None,
        })
        {
            Assert.Equal(Biomes.Impassable, BoatMovementCost.CostFor(biome));
        }
    }

    [Fact]
    public void Boat_FasterThanAnyFootBiome()
    {
        // The pinned invariant from docs/boats.md "Why faster than any
        // biome on foot": water cost for a boat is strictly less than
        // the cheapest foot biome.
        var landMin = int.MaxValue;
        foreach (var biome in new[]
        {
            Biome.Grassland, Biome.Forest, Biome.Hills,
            Biome.Mountain, Biome.Desert,
        })
        {
            landMin = Math.Min(landMin, Biomes.MoveCost(biome));
        }
        Assert.True(BoatMovementCost.WaterCost < landMin,
            $"BoatMovementCost.WaterCost ({BoatMovementCost.WaterCost}) " +
            $"must be < min(landBiomeFootCost) ({landMin}).");
    }

    [Fact]
    public void Pathfinding_ForWaterTraversal_RoutesOverWater()
    {
        // 5×1 strip with all-water tiles. A boat (Traversal.Water) can
        // find a path; a foot unit can also find it (water is cost 250)
        // but the boat's path-COST is much lower.
        var grid = new TileGrid(5, 1, Biome.Water);
        var world = new GameWorld(grid);
        var path = Pathfinding.FindPath(
            grid, new TileCoord(0, 0), new TileCoord(4, 0),
            tile => BoatMovementCost.CostFor(grid.BiomeAt(tile)));
        Assert.NotNull(path);
        Assert.Equal(5, path!.Count);
    }

    [Fact]
    public void Pathfinding_ForWaterTraversal_RejectsLandTiles()
    {
        // 3×1 strip: [Water, Grassland, Water]. A boat can't cross the
        // grassland in the middle (Impassable for Water), so no path.
        var grid = new TileGrid(3, 1, Biome.Water);
        grid.SetBiome(new TileCoord(1, 0), Biome.Grassland);
        var path = Pathfinding.FindPath(
            grid, new TileCoord(0, 0), new TileCoord(2, 0),
            tile => BoatMovementCost.CostFor(grid.BiomeAt(tile)));
        Assert.Null(path);
    }

    [Fact]
    public void MovementCost_PlanCost_DefaultsToFoot()
    {
        // The Traversal parameter defaults to Foot so all existing
        // callers (none updated to pass Traversal) keep their behaviour.
        var grid = new TileGrid(3, 3, Biome.Grassland);
        var world = new GameWorld(grid);
        var visible = new HashSet<TileCoord>();
        var cost = MovementCost.PlanCost(
            world, new TileCoord(1, 1), playerId: 0, visible, now: 0);
        Assert.Equal(Biomes.MoveCost(Biome.Grassland), cost);
    }

    [Fact]
    public void MovementCost_PlanCost_WaterTraversal_PicksBoatTable()
    {
        var grid = new TileGrid(3, 3, Biome.Water);
        var world = new GameWorld(grid);
        var visible = new HashSet<TileCoord>();
        var cost = MovementCost.PlanCost(
            world, new TileCoord(1, 1), playerId: 0, visible, now: 0, trav: Traversal.Water);
        Assert.Equal(BoatMovementCost.WaterCost, cost);
    }

    [Fact]
    public void MovementCost_PlanCost_WaterTraversal_OnLand_IsImpassable()
    {
        var grid = new TileGrid(3, 3, Biome.Grassland);
        var world = new GameWorld(grid);
        var visible = new HashSet<TileCoord>();
        var cost = MovementCost.PlanCost(
            world, new TileCoord(1, 1), playerId: 0, visible, now: 0, trav: Traversal.Water);
        Assert.Equal(Biomes.Impassable, cost);
    }

    [Fact]
    public void Unit_Traversal_SnapshotRoundTrip()
    {
        var spec = new GenesisSpec
        {
            Width = 5, Height = 5,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(0, 0), UnitRole.Builder, OwnerId: 0),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xB047);
        // Hand-add a Water-traversal unit (Phase C will properly wire
        // this through Boat production).
        sim.World.AddUnit(new Unit(50, new TileCoord(0, 0))
        {
            Role = UnitRole.None, OwnerId = 0, Traversal = Traversal.Water,
            BornTick = 0,
        });
        Assert.Equal(Traversal.Water, sim.World.Units[50].Traversal);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xB047);
        Assert.Equal(Traversal.Water, restored.World.Units[50].Traversal);
        Assert.Equal(Traversal.Foot, restored.World.Units[1].Traversal);
    }
}

// M12 Phase B — Dock structure placement validation + persistence.
public class BoatsPhaseBTests
{
    private static Simulation MakeCoastalSim()
    {
        // 5×5 grid: rightmost column is Water, everything else is Grassland.
        // Dock placement target: tile (3, 2) with slip at (4, 2).
        var spec = new GenesisSpec
        {
            Width = 5, Height = 5,
            DefaultBiome = Biome.Grassland,
            Biomes = new Dictionary<TileCoord, Biome>
            {
                [new TileCoord(4, 0)] = Biome.Water,
                [new TileCoord(4, 1)] = Biome.Water,
                [new TileCoord(4, 2)] = Biome.Water,
                [new TileCoord(4, 3)] = Biome.Water,
                [new TileCoord(4, 4)] = Biome.Water,
            },
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                },
            },
        };
        return new Simulation(spec, seed: 0xD0CC);
    }

    [Fact]
    public void PlaceSite_Dock_ValidCoastWithSlip_Accepted()
    {
        var sim = MakeCoastalSim();
        var intent = new PlaceSiteIntent(
            new TileCoord(3, 2), StructureKind.Dock,
            dockSlip: new TileCoord(4, 2)) { PlayerId = 0 };
        var outcome = intent.Resolve(sim);
        Assert.True(outcome.IsApplied);
        var s = sim.World.Structures[new TileCoord(3, 2)];
        Assert.IsType<ConstructionSite>(s);
        Assert.Equal(new TileCoord(4, 2), ((ConstructionSite)s).DockSlip);
    }

    [Fact]
    public void PlaceSite_Dock_OnWaterTile_Rejected()
    {
        var sim = MakeCoastalSim();
        var intent = new PlaceSiteIntent(
            new TileCoord(4, 2), StructureKind.Dock,
            dockSlip: new TileCoord(4, 3)) { PlayerId = 0 };
        var outcome = intent.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void PlaceSite_Dock_MissingSlip_Rejected()
    {
        var sim = MakeCoastalSim();
        var intent = new PlaceSiteIntent(
            new TileCoord(3, 2), StructureKind.Dock) { PlayerId = 0 };
        var outcome = intent.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void PlaceSite_Dock_SlipNot4Adjacent_Rejected()
    {
        var sim = MakeCoastalSim();
        var intent = new PlaceSiteIntent(
            new TileCoord(3, 2), StructureKind.Dock,
            dockSlip: new TileCoord(4, 4)) { PlayerId = 0 };
        var outcome = intent.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void PlaceSite_Dock_SlipNotWater_Rejected()
    {
        var sim = MakeCoastalSim();
        // (3, 3) is Grassland (not Water) but is 4-adjacent to dock (3, 2).
        var intent = new PlaceSiteIntent(
            new TileCoord(3, 2), StructureKind.Dock,
            dockSlip: new TileCoord(3, 3)) { PlayerId = 0 };
        var outcome = intent.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void PlaceSite_Dock_InlandTile_NoAdjacentWater_Rejected()
    {
        // tile (1, 1) is grassland with no adjacent water.
        var sim = MakeCoastalSim();
        var intent = new PlaceSiteIntent(
            new TileCoord(1, 1), StructureKind.Dock,
            dockSlip: new TileCoord(1, 2)) { PlayerId = 0 };
        // (1, 2) is Grassland not Water — slip biome check rejects.
        var outcome = intent.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void Dock_PlacedSite_SnapshotRoundTrip_PreservesSlip()
    {
        var sim = MakeCoastalSim();
        new PlaceSiteIntent(
            new TileCoord(3, 2), StructureKind.Dock,
            dockSlip: new TileCoord(4, 2)) { PlayerId = 0 }.Resolve(sim);

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xD0CC);
        var site = (ConstructionSite)restored.World.Structures[new TileCoord(3, 2)];
        Assert.Equal(new TileCoord(4, 2), site.DockSlip);
    }
}

// M12 Phase C — Boat unit fields + dock-as-shipyard production.
public class BoatsPhaseCTests
{
    private static (Simulation sim, Dock dock) MakeCoastalSimWithBuiltDock()
    {
        // Build a sim with the dock already constructed at (3, 2) and
        // slip at (4, 2). Skip the full construction flow by hand-
        // planting the Dock and invoking DockArmer.OnDockBuilt.
        var spec = new GenesisSpec
        {
            Width = 6, Height = 6,
            DefaultBiome = Biome.Grassland,
            Biomes = new Dictionary<TileCoord, Biome>
            {
                [new TileCoord(4, 0)] = Biome.Water,
                [new TileCoord(4, 1)] = Biome.Water,
                [new TileCoord(4, 2)] = Biome.Water,
                [new TileCoord(4, 3)] = Biome.Water,
                [new TileCoord(4, 4)] = Biome.Water,
                [new TileCoord(5, 0)] = Biome.Water,
                [new TileCoord(5, 1)] = Biome.Water,
                [new TileCoord(5, 2)] = Biome.Water,
                [new TileCoord(5, 3)] = Biome.Water,
                [new TileCoord(5, 4)] = Biome.Water,
            },
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xC012);
        var dock = sim.World.AddStructure(new Dock(new TileCoord(3, 2), new TileCoord(4, 2)) { OwnerId = 0 });
        DockArmer.OnDockBuilt(sim, dock);
        return (sim, dock);
    }

    [Fact]
    public void Unit_Defaults_PassengerCapZero_PassengersEmpty()
    {
        var u = new Unit(1, new TileCoord(0, 0));
        Assert.Equal(0, u.PassengerCap);
        Assert.Empty(u.Passengers);
        Assert.Null(u.EmbarkedOn);
        Assert.False(u.IsEmbarked);
    }

    [Fact]
    public void NewlyBuiltDock_ArmsProduction_AndSchedulesTick()
    {
        var (sim, dock) = MakeCoastalSimWithBuiltDock();
        Assert.True(dock.ProductionArmed);
        Assert.NotNull(dock.NextProductionTickSeq);
    }

    [Fact]
    public void Dock_AutoProduces_BoatOnSlip_AfterPeriod()
    {
        var (sim, dock) = MakeCoastalSimWithBuiltDock();
        var period = StructureCatalog.Spec(StructureKind.Dock).ProductionPeriodTicks;
        sim.Run(until: period);

        // A boat should now exist on the slip.
        var boats = sim.World.Units.Values.Where(u => u.Role == UnitRole.Boat).ToList();
        Assert.Single(boats);
        Assert.Equal(dock.Slip, boats[0].Position);
        Assert.Equal(Traversal.Water, boats[0].Traversal);
        Assert.Equal(BoatConstants.DefaultPassengerCap, boats[0].PassengerCap);
        Assert.Equal(0, boats[0].OwnerId);
    }

    [Fact]
    public void Dock_Production_Stalls_WhenSlipOccupied()
    {
        var (sim, dock) = MakeCoastalSimWithBuiltDock();
        // Park a foot unit on the slip BEFORE the first production
        // tick fires. The dock should stall.
        sim.World.AddUnit(new Unit(50, dock.Slip)
        {
            Role = UnitRole.Builder, OwnerId = 0, BornTick = 0,
        });

        var period = StructureCatalog.Spec(StructureKind.Dock).ProductionPeriodTicks;
        sim.Run(until: period);

        Assert.False(dock.ProductionArmed);
        Assert.Null(dock.NextProductionTickSeq);
        Assert.Empty(sim.World.Units.Values.Where(u => u.Role == UnitRole.Boat));
    }

    [Fact]
    public void Dock_Production_ReArmsAfterSlipClears_OnMoveArrival()
    {
        var (sim, dock) = MakeCoastalSimWithBuiltDock();
        // Park a foot unit on the slip; let stall happen.
        var blocker = sim.World.AddUnit(new Unit(50, dock.Slip)
        {
            Role = UnitRole.Builder, OwnerId = 0, BornTick = 0,
        });
        var period = StructureCatalog.Spec(StructureKind.Dock).ProductionPeriodTicks;
        sim.Run(until: period);
        Assert.False(dock.ProductionArmed);

        // Move the blocker off the slip via MoveIntent. The
        // OnUnitLeftTile hook should re-arm the dock.
        new Sim.Core.Movement.MoveIntent(50, new TileCoord(4, 1))
            { PlayerId = 0 }.Resolve(sim);
        // The MoveArrivalEvent that lands the unit on (4, 1) is the one
        // that triggers OnUnitLeftTile(blockerPrevTile = slip).
        sim.Run(until: 100_000);

        Assert.True(sim.World.Units.Values.Any(u => u.Role == UnitRole.Boat));
    }

    [Fact]
    public void Boat_Unit_SnapshotRoundTrip_PreservesCarrierFields()
    {
        var (sim, dock) = MakeCoastalSimWithBuiltDock();
        var period = StructureCatalog.Spec(StructureKind.Dock).ProductionPeriodTicks;
        sim.Run(until: period);
        var boat = sim.World.Units.Values.First(u => u.Role == UnitRole.Boat);
        boat.Passengers.Add(99);
        boat.Passengers.Add(7);
        boat.CargoResource = Resource.Food;
        boat.CargoAmount = 25;

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xC012);
        var rb = restored.World.Units[boat.Id];
        Assert.Equal(Traversal.Water, rb.Traversal);
        Assert.Equal(BoatConstants.DefaultPassengerCap, rb.PassengerCap);
        Assert.Equal(new[] { 7, 99 }, rb.Passengers.ToArray());
        Assert.Equal(Resource.Food, rb.CargoResource);
        Assert.Equal(25, rb.CargoAmount);
    }

    [Fact]
    public void Dock_ProductionAnchor_SurvivesSnapshotRoundTrip()
    {
        var (sim, dock) = MakeCoastalSimWithBuiltDock();
        var armedSeq = dock.NextProductionTickSeq;
        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xC012);
        var rd = (Dock)restored.World.Structures[dock.At];
        Assert.True(rd.ProductionArmed);
        Assert.Equal(armedSeq, rd.NextProductionTickSeq);
        Assert.Equal(dock.Slip, rd.Slip);

        // The restored sim should still produce the boat on schedule.
        var period = StructureCatalog.Spec(StructureKind.Dock).ProductionPeriodTicks;
        restored.Run(until: period);
        Assert.Single(restored.World.Units.Values.Where(u => u.Role == UnitRole.Boat));
    }
}

// M12 Phase D — Embark / MoveBoat (via MoveIntent) / Disembark intents
// + solo-intent rejection + vision filter.
public class BoatsPhaseDTests
{
    // Setup with a dock + slip + a boat already on the slip + a citizen
    // standing on the dock tile.
    private static (Simulation sim, Dock dock, Unit boat, Unit citizen) MakeReadyToEmbark()
    {
        var spec = new GenesisSpec
        {
            Width = 8, Height = 4,
            DefaultBiome = Biome.Grassland,
            Biomes = WaterColumn(xMin: 4, xMax: 7, height: 4),
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(3, 1), UnitRole.Builder, OwnerId: 0),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xE0BA);
        var dock = sim.World.AddStructure(new Dock(
            new TileCoord(3, 1), new TileCoord(4, 1)) { OwnerId = 0 });
        DockArmer.OnDockBuilt(sim, dock);
        // Skip waiting — directly spawn a boat on the slip.
        var boat = sim.World.AddUnit(new Unit(50, dock.Slip)
        {
            Role = UnitRole.Boat, OwnerId = 0, Traversal = Traversal.Water,
            PassengerCap = BoatConstants.DefaultPassengerCap,
            CargoCapacity = BoatConstants.DefaultCargoCapacity,
            BornTick = 0,
        });
        var citizen = sim.World.Units[1];
        return (sim, dock, boat, citizen);
    }

    private static Dictionary<TileCoord, Biome> WaterColumn(int xMin, int xMax, int height)
    {
        var dict = new Dictionary<TileCoord, Biome>();
        for (var x = xMin; x <= xMax; x++)
            for (var y = 0; y < height; y++)
                dict[new TileCoord(x, y)] = Biome.Water;
        return dict;
    }

    [Fact]
    public void Embark_ValidAtDock_PutsPassengersInBoat()
    {
        var (sim, dock, boat, citizen) = MakeReadyToEmbark();
        var outcome = new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsApplied);
        Assert.Contains(citizen.Id, boat.Passengers);
        Assert.Equal(boat.Id, citizen.EmbarkedOn);
        Assert.True(citizen.IsEmbarked);
    }

    [Fact]
    public void Embark_BoatNotAdjacentToDock_Rejected()
    {
        var (sim, dock, boat, citizen) = MakeReadyToEmbark();
        // Move the boat to (6, 2) — not adjacent to dock (3, 1).
        boat.Position = new TileCoord(6, 2);
        var outcome = new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void Embark_PassengerNotOnDock_Rejected()
    {
        var (sim, dock, boat, citizen) = MakeReadyToEmbark();
        citizen.Position = new TileCoord(2, 1);
        var outcome = new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void Embark_ExceedsCap_Rejected()
    {
        var (sim, dock, boat, citizen) = MakeReadyToEmbark();
        // Pile cap-many citizens onto the dock; +1 should reject.
        var extras = new List<int>();
        for (var i = 0; i < BoatConstants.DefaultPassengerCap; i++)
        {
            var id = 100 + i;
            sim.World.AddUnit(new Unit(id, dock.At)
            {
                Role = UnitRole.Builder, OwnerId = 0, BornTick = 0,
            });
            extras.Add(id);
        }
        // First embark fills the cap.
        var ok = new EmbarkIntent(boat.Id, extras) { PlayerId = 0 }.Resolve(sim);
        Assert.True(ok.IsApplied);
        // Citizen (still on dock) won't fit.
        var failed = new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }.Resolve(sim);
        Assert.False(failed.IsApplied);
    }

    [Fact]
    public void Embark_AlreadyEmbarked_Rejected()
    {
        var (sim, dock, boat, citizen) = MakeReadyToEmbark();
        new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }.Resolve(sim);
        // Try again.
        var outcome = new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void EmbarkedUnit_RejectsMoveIntent()
    {
        var (sim, dock, boat, citizen) = MakeReadyToEmbark();
        new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }.Resolve(sim);
        var outcome = new Sim.Core.Movement.MoveIntent(citizen.Id, new TileCoord(2, 1))
            { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void EmbarkedUnit_ContributesNoVision()
    {
        var (sim, dock, boat, citizen) = MakeReadyToEmbark();
        var visibleBefore = Sim.Core.Vision.View.VisibleTiles(sim.World, 0);
        new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }.Resolve(sim);
        var visibleAfter = Sim.Core.Vision.View.VisibleTiles(sim.World, 0);
        // The citizen's prior disc is gone (since the boat doesn't sit
        // at the citizen's tile). Embarked vision contribution = 0.
        Assert.True(visibleAfter.Count <= visibleBefore.Count);
    }

    [Fact]
    public void EmbarkedUnit_DoesNotCrowdTile()
    {
        var (sim, dock, boat, citizen) = MakeReadyToEmbark();
        var before = Sim.Core.Movement.MovementCost.CountUnitsOnTile(sim.World, dock.At);
        new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }.Resolve(sim);
        var after = Sim.Core.Movement.MovementCost.CountUnitsOnTile(sim.World, dock.At);
        Assert.Equal(before - 1, after);
    }

    [Fact]
    public void BoatOnWater_MoveIntent_PathfindsOnWater()
    {
        var (sim, dock, boat, citizen) = MakeReadyToEmbark();
        // Move boat from (4, 1) to (7, 1). All water.
        var outcome = new Sim.Core.Movement.MoveIntent(boat.Id, new TileCoord(7, 1))
            { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsApplied);
        // PathRemaining is set; FinalDest is the destination.
        Assert.NotNull(boat.PathRemaining);
        Assert.Equal(new TileCoord(7, 1), boat.PathFinalDest);
    }

    [Fact]
    public void Boat_CannotMoveOntoLand()
    {
        var (sim, dock, boat, citizen) = MakeReadyToEmbark();
        // Try to send the boat onto a land tile.
        var outcome = new Sim.Core.Movement.MoveIntent(boat.Id, new TileCoord(0, 0))
            { PlayerId = 0 }.Resolve(sim);
        // Resolve returns Applied (the intent itself was structurally ok),
        // but no path was found so the boat's PathRemaining is null.
        Assert.True(outcome.IsApplied);
        Assert.Null(boat.PathRemaining);
    }

    [Fact]
    public void Disembark_AtOwnDock_LandsPassengersOnDockTile()
    {
        var (sim, dock, boat, citizen) = MakeReadyToEmbark();
        new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }.Resolve(sim);
        Assert.True(citizen.IsEmbarked);

        var outcome = new DisembarkIntent(boat.Id) { PlayerId = 0 }.Resolve(sim);
        Assert.True(outcome.IsApplied);
        Assert.Null(citizen.EmbarkedOn);
        Assert.Equal(dock.At, citizen.Position);
        Assert.Empty(boat.Passengers);
    }

    [Fact]
    public void Disembark_BoatNotAdjacent_Rejected()
    {
        var (sim, dock, boat, citizen) = MakeReadyToEmbark();
        new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }.Resolve(sim);
        boat.Position = new TileCoord(7, 3); // way out
        var outcome = new DisembarkIntent(boat.Id) { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void Disembark_AtEnemyDock_Rejected()
    {
        // Two-faction scenario: an enemy dock at (3, 2), slip at (4, 2).
        // Boat sails to (4, 2). Disembark must reject.
        var spec = new GenesisSpec
        {
            Width = 8, Height = 4,
            DefaultBiome = Biome.Grassland,
            Biomes = WaterColumn(xMin: 4, xMax: 7, height: 4),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(0, 0) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(7, 3) },
            },
        };
        var sim = new Simulation(spec, seed: 0xE0BB);
        var enemyDock = sim.World.AddStructure(new Dock(
            new TileCoord(3, 2), new TileCoord(4, 2)) { OwnerId = 1 });
        var boat = sim.World.AddUnit(new Unit(50, new TileCoord(4, 2))
        {
            Role = UnitRole.Boat, OwnerId = 0, Traversal = Traversal.Water,
            PassengerCap = BoatConstants.DefaultPassengerCap, BornTick = 0,
        });
        boat.Passengers.Add(99);  // fake passenger so disembark gets past the empty-list check
        var outcome = new DisembarkIntent(boat.Id) { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void Disembark_EmptyBoat_Rejected()
    {
        var (sim, dock, boat, citizen) = MakeReadyToEmbark();
        var outcome = new DisembarkIntent(boat.Id) { PlayerId = 0 }.Resolve(sim);
        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void FullSailLoop_EmbarkSailDisembark_PlacesPassengersAtRemoteDock()
    {
        // Build an allied dock at (3, 2) → slip (4, 2). Boat (own) at (4, 1).
        // Embark citizen on own dock; sail to (4, 2); disembark at the ally's dock.
        var spec = new GenesisSpec
        {
            Width = 8, Height = 4,
            DefaultBiome = Biome.Grassland,
            Biomes = WaterColumn(xMin: 4, xMax: 7, height: 4),
            // Long lifespan + ample starting food so the citizen doesn't
            // die mid-test from starvation or aging.
            Population = new Sim.Core.Population.PopulationConfig
            {
                LifespanMinYears = 1000, LifespanMaxYears = 1001,
            },
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = new SortedDictionary<Resource, int>
                    {
                        [Resource.Food] = 5000,
                    },
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(3, 1), UnitRole.Builder, OwnerId: 0),
                    },
                },
                new FactionStartSpec
                {
                    OwnerId = 1,
                    CastlePosition = new TileCoord(7, 3),
                    CastleHoldings = new SortedDictionary<Resource, int>
                    {
                        [Resource.Food] = 1000,
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xE0BC);
        var ownDock = sim.World.AddStructure(new Dock(
            new TileCoord(3, 1), new TileCoord(4, 1)) { OwnerId = 0 });
        var alliedDock = sim.World.AddStructure(new Dock(
            new TileCoord(3, 2), new TileCoord(4, 2)) { OwnerId = 1 });
        // Set ally relationship between 0 and 1.
        sim.World.Diplomacy.SetState(
            Sim.Core.Diplomacy.FactionPair.Of(0, 1),
            Sim.Core.Diplomacy.RelationshipState.Ally);
        var boat = sim.World.AddUnit(new Unit(50, new TileCoord(4, 1))
        {
            Role = UnitRole.Boat, OwnerId = 0, Traversal = Traversal.Water,
            PassengerCap = BoatConstants.DefaultPassengerCap, BornTick = 0,
        });
        var citizen = sim.World.Units[1];

        Assert.True(new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }
            .Resolve(sim).IsApplied);

        // Sail to (4, 2).
        Assert.True(new Sim.Core.Movement.MoveIntent(boat.Id, new TileCoord(4, 2))
            { PlayerId = 0 }.Resolve(sim).IsApplied);
        sim.Run(until: 10_000);
        Assert.Equal(new TileCoord(4, 2), boat.Position);

        // Disembark at the allied dock.
        Assert.True(new DisembarkIntent(boat.Id) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.Null(citizen.EmbarkedOn);
        Assert.Equal(alliedDock.At, citizen.Position);
    }
}

// M12 Phase E — sink-drowns + combat integration.
public class BoatsPhaseETests
{
    [Fact]
    public void BoatKilled_WithPassengers_AllPassengersDrown()
    {
        // 1 boat, 2 embarked passengers. Kill the boat directly via
        // CombatRules.OnUnitDeath. Both passengers should be removed
        // from world.Units.
        var spec = MakeWaterScenarioSpec();
        var sim = new Simulation(spec, seed: 0xD0EA);
        var boat = sim.World.AddUnit(new Unit(50, new TileCoord(4, 1))
        {
            Role = UnitRole.Boat, OwnerId = 0, Traversal = Traversal.Water,
            PassengerCap = BoatConstants.DefaultPassengerCap, BornTick = 0,
        });
        // Two passengers embarked off-tile.
        for (var i = 0; i < 2; i++)
        {
            var pid = 100 + i;
            sim.World.AddUnit(new Unit(pid, new TileCoord(0, 0))
            {
                Role = UnitRole.Builder, OwnerId = 0, BornTick = 0,
            });
            sim.World.Units[pid].EmbarkedOn = 50;
            boat.Passengers.Add(pid);
        }
        Assert.Equal(2, boat.Passengers.Count);

        // Kill the boat.
        Sim.Core.Combat.CombatRules.OnUnitDeath(sim, boat);

        Assert.False(sim.World.Units.ContainsKey(50));
        Assert.False(sim.World.Units.ContainsKey(100));
        Assert.False(sim.World.Units.ContainsKey(101));
    }

    [Fact]
    public void BoatKilled_Empty_NoExtraSideEffects()
    {
        var spec = MakeWaterScenarioSpec();
        var sim = new Simulation(spec, seed: 0xD0EB);
        var boat = sim.World.AddUnit(new Unit(50, new TileCoord(4, 1))
        {
            Role = UnitRole.Boat, OwnerId = 0, Traversal = Traversal.Water,
            PassengerCap = BoatConstants.DefaultPassengerCap, BornTick = 0,
        });
        var citizenId = 1;
        Sim.Core.Combat.CombatRules.OnUnitDeath(sim, boat);
        Assert.False(sim.World.Units.ContainsKey(50));
        Assert.True(sim.World.Units.ContainsKey(citizenId));
    }

    [Fact]
    public void PassengerInBoat_DiesByOtherCause_BoatPassengersListUpdated()
    {
        // A passenger killed by some other path (combat, age) should
        // be removed from the carrier's Passengers list cleanly.
        var spec = MakeWaterScenarioSpec();
        var sim = new Simulation(spec, seed: 0xD0EC);
        var boat = sim.World.AddUnit(new Unit(50, new TileCoord(4, 1))
        {
            Role = UnitRole.Boat, OwnerId = 0, Traversal = Traversal.Water,
            PassengerCap = BoatConstants.DefaultPassengerCap, BornTick = 0,
        });
        sim.World.AddUnit(new Unit(100, new TileCoord(0, 0))
        {
            Role = UnitRole.Builder, OwnerId = 0, BornTick = 0,
        });
        sim.World.Units[100].EmbarkedOn = 50;
        boat.Passengers.Add(100);

        Sim.Core.Combat.CombatRules.OnUnitDeath(sim, sim.World.Units[100]);
        Assert.DoesNotContain(100, boat.Passengers);
        Assert.True(sim.World.Units.ContainsKey(50));
    }

    private static GenesisSpec MakeWaterScenarioSpec()
    {
        var biomes = new Dictionary<TileCoord, Biome>();
        for (var x = 4; x < 8; x++)
            for (var y = 0; y < 4; y++)
                biomes[new TileCoord(x, y)] = Biome.Water;
        return new GenesisSpec
        {
            Width = 8, Height = 4,
            DefaultBiome = Biome.Grassland,
            Biomes = biomes,
            Population = new Sim.Core.Population.PopulationConfig
            {
                LifespanMinYears = 1000, LifespanMaxYears = 1001,
            },
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Food] = 5000 },
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(0, 0), UnitRole.Builder, OwnerId: 0),
                    },
                },
            },
        };
    }
}

// M12 Phase F — persistence + headline tests.
public class BoatsPhaseFTests
{
    [Fact]
    public void Boats_TwinRun_HashesMatch()
    {
        // M12 HEADLINE: two identical scenarios (dock + boat + embark
        // + sail + disembark) hash-match end-to-end.
        var sim1 = MakeFullScenario();
        var sim2 = MakeFullScenario();
        RunFullScenario(sim1);
        RunFullScenario(sim2);
        Assert.Equal(Snapshot.Hash(sim1), Snapshot.Hash(sim2));
    }

    [Fact]
    public void Boats_MidSailRecovery_HashesMatch()
    {
        // Snapshot mid-sail; restore; continue; final state matches an
        // uninterrupted run.
        var sim1 = MakeFullScenario();
        var sim2 = MakeFullScenario();

        // Drive both to the same mid-sail tick.
        RunUntilMidSail(sim1);
        RunUntilMidSail(sim2);

        // Snapshot+restore sim2 at that point.
        var bytes = Snapshot.Serialize(sim2);
        var sim2Restored = Snapshot.Restore(bytes, seed: 0xF1F1);

        Assert.Equal(Snapshot.Hash(sim2), Snapshot.Hash(sim2Restored));

        // Finish both.
        FinishScenario(sim1);
        FinishScenario(sim2Restored);

        Assert.Equal(Snapshot.Hash(sim1), Snapshot.Hash(sim2Restored));
    }

    [Fact]
    public void BoatProduction_AnchorRoundTrip_ResumesOnRestore()
    {
        // After dock build, snapshot before the production tick fires;
        // restore; verify the production still happens.
        var sim = MakeFullScenario();
        // Don't sail; just let the dock production tick fire after restore.
        var period = Sim.Core.World.StructureCatalog.Spec(Sim.Core.World.StructureKind.Dock).ProductionPeriodTicks;
        sim.Run(until: period / 2);
        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xF1F1);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        restored.Run(until: period + 1);
        Assert.True(restored.World.Units.Values.Any(u => u.Role == UnitRole.Boat));
    }

    // ---- scenario helpers ----

    private static Simulation MakeFullScenario()
    {
        var biomes = new Dictionary<TileCoord, Biome>();
        for (var x = 4; x < 8; x++)
            for (var y = 0; y < 4; y++)
                biomes[new TileCoord(x, y)] = Biome.Water;
        var spec = new GenesisSpec
        {
            Width = 8, Height = 4,
            DefaultBiome = Biome.Grassland,
            Biomes = biomes,
            Population = new Sim.Core.Population.PopulationConfig
            {
                LifespanMinYears = 1000, LifespanMaxYears = 1001,
            },
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    CastleHoldings = new SortedDictionary<Resource, int>
                    {
                        [Resource.Food] = 5000,
                    },
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(3, 1), UnitRole.Builder, OwnerId: 0),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xF1F1);
        // Hand-plant a dock at (3, 1) with slip at (4, 1). Skipping the
        // PlaceSite + build flow keeps the test focused; the build flow
        // is covered by Phase B tests.
        var dock = sim.World.AddStructure(new Dock(
            new TileCoord(3, 1), new TileCoord(4, 1)) { OwnerId = 0 });
        DockArmer.OnDockBuilt(sim, dock);
        return sim;
    }

    private static void RunFullScenario(Simulation sim)
    {
        var period = Sim.Core.World.StructureCatalog.Spec(Sim.Core.World.StructureKind.Dock).ProductionPeriodTicks;
        sim.Run(until: period); // boat spawns on slip
        var boat = sim.World.Units.Values.First(u => u.Role == UnitRole.Boat);
        var citizen = sim.World.Units[1];

        // Embark, sail to (7, 1), disembark back at (4, 1). The dock at
        // (3, 1) accepts the same boat for re-disembark since the boat
        // returned to slip-adjacent water.
        Assert.True(new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }
            .Resolve(sim).IsApplied);
        Assert.True(new Sim.Core.Movement.MoveIntent(boat.Id, new TileCoord(7, 1))
            { PlayerId = 0 }.Resolve(sim).IsApplied);
        sim.Run(until: 50_000);
        Assert.True(new Sim.Core.Movement.MoveIntent(boat.Id, new TileCoord(4, 1))
            { PlayerId = 0 }.Resolve(sim).IsApplied);
        sim.Run(until: 100_000);
        Assert.True(new DisembarkIntent(boat.Id) { PlayerId = 0 }.Resolve(sim).IsApplied);
    }

    private static void RunUntilMidSail(Simulation sim)
    {
        var period = Sim.Core.World.StructureCatalog.Spec(Sim.Core.World.StructureKind.Dock).ProductionPeriodTicks;
        sim.Run(until: period); // boat spawns
        var boat = sim.World.Units.Values.First(u => u.Role == UnitRole.Boat);
        var citizen = sim.World.Units[1];
        Assert.True(new EmbarkIntent(boat.Id, new[] { citizen.Id }) { PlayerId = 0 }
            .Resolve(sim).IsApplied);
        Assert.True(new Sim.Core.Movement.MoveIntent(boat.Id, new TileCoord(7, 1))
            { PlayerId = 0 }.Resolve(sim).IsApplied);
        // Run a short while — partway through the sail.
        sim.Run(until: period + 10);
    }

    private static void FinishScenario(Simulation sim)
    {
        sim.Run(until: 50_000);
        var boat = sim.World.Units.Values.First(u => u.Role == UnitRole.Boat);
        Assert.True(new Sim.Core.Movement.MoveIntent(boat.Id, new TileCoord(4, 1))
            { PlayerId = 0 }.Resolve(sim).IsApplied);
        sim.Run(until: 100_000);
        Assert.True(new DisembarkIntent(boat.Id) { PlayerId = 0 }.Resolve(sim).IsApplied);
    }
}
