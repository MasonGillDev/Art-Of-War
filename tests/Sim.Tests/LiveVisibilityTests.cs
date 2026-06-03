using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Tests;

// M3 Phase C: live visibility is pure-read. Tests pin:
//   - Visibility moves with a moving unit (tile visible while in radius,
//     not after).
//   - Static source (Castle) holds its area.
//   - Scout sees a larger disc than a base-role unit.
//   - 100x VisibleTiles calls don't mutate the hash (THE pure-read wall).
//   - Empty player → empty visible set.
public class LiveVisibilityTests
{
    private static GameWorld MakeWorldWith(params UnitSpawn[] units)
    {
        var spec = new GenesisSpec
        {
            Width = 30, Height = 30,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(15, 15),
                    UnitSpawns = units,
                },
            },
        };
        return Genesis.Build(spec);
    }

    [Fact]
    public void StaticCastle_HoldsItsArea()
    {
        var world = MakeWorldWith();
        var visible = View.VisibleTiles(world, playerId: 0);
        Assert.Contains(new TileCoord(15, 15), visible);
        // Castle radius = 5; 5 tiles away is in.
        Assert.Contains(new TileCoord(20, 15), visible);
        // 6 tiles away is out.
        Assert.DoesNotContain(new TileCoord(21, 15), visible);
    }

    [Fact]
    public void VisibilityMovesWithMovingUnit()
    {
        // Single Hauler (base radius 3) starting at (1,1); no Castle near
        // (15,15) is irrelevant here, we look at the unit's local disc.
        var world = MakeWorldWith(
            new UnitSpawn(1, new TileCoord(1, 1), UnitRole.Hauler));
        var sim = new Simulation(world, seed: 1);

        // At start: tile (4,1) is exactly distance 3 — visible.
        var beforeVisible = View.VisibleTiles(world, 0);
        Assert.Contains(new TileCoord(4, 1), beforeVisible);

        // Move the unit far away.
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(25, 25)));
        sim.Run();

        // Now (4, 1) is far outside the unit's new radius AND far outside
        // the castle's radius. NOT visible anymore — re-fog falls out.
        var afterVisible = View.VisibleTiles(world, 0);
        Assert.DoesNotContain(new TileCoord(4, 1), afterVisible);
        // The destination IS visible.
        Assert.Contains(new TileCoord(25, 25), afterVisible);
    }

    [Fact]
    public void ScoutSeesMoreThanHauler()
    {
        var hauler = MakeWorldWith(new UnitSpawn(1, new TileCoord(1, 1), UnitRole.Hauler));
        var scout  = MakeWorldWith(new UnitSpawn(1, new TileCoord(1, 1), UnitRole.Scout));

        // Castle (radius 5) contributes to BOTH worlds' visibility. To
        // isolate the unit's contribution, count visible tiles outside the
        // castle's disc.
        var castleAt = new TileCoord(15, 15);
        bool OutsideCastle(TileCoord t)
        {
            var dx = t.X - castleAt.X;
            var dy = t.Y - castleAt.Y;
            return dx * dx + dy * dy > 25; // > 5²
        }
        var haulerLocal = View.VisibleTiles(hauler, 0).Count(OutsideCastle);
        var scoutLocal  = View.VisibleTiles(scout,  0).Count(OutsideCastle);
        Assert.True(scoutLocal > haulerLocal,
            $"scout should see more local tiles: scout={scoutLocal}, hauler={haulerLocal}");
    }

    [Fact]
    public void EmptyPlayer_HasEmptyVisible()
    {
        // Player 1 exists in the registry but owns no entities.
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(5, 5) },
            },
        };
        var world = Genesis.Build(spec);
        world.Players[1] = new Player(1);
        var visible = View.VisibleTiles(world, playerId: 1);
        Assert.Empty(visible);
    }

    [Fact]
    public void Tower_HoldsItsArea_AfterBeingBuilt()
    {
        // Build a real Tower on an unexplored-by-castle tile, then confirm
        // its disc shows up in current visibility (the "pinning" property —
        // emergent from being a static source, no special code).
        var world = MakeWorldWith(
            new UnitSpawn(1, new TileCoord(25, 5), UnitRole.Builder));
        var sim = new Simulation(world, seed: 1);
        var towerTile = new TileCoord(25, 5);

        // Place + materialize the build site, pre-deposit materials, assign
        // the builder. (Materials hauled-in is normally a Phase E flow;
        // we shortcut for the test.)
        sim.SubmitIntent(0, new PlaceSiteIntent(towerTile, StructureKind.Tower));
        sim.Run(until: 0);
        var site = (ConstructionSite)sim.World.Structures[towerTile];
        foreach (var (r, n) in StructureCatalog.Spec(StructureKind.Tower).BuildCost)
            site.Deposit(r, n);
        sim.SubmitIntent(sim.Now, new AssignBuildersIntent(towerTile, new[] { 1 }));
        sim.Run();

        Assert.IsType<Tower>(sim.World.Structures[towerTile]);

        // Builder role radius = 3, Tower radius = 7. A tile 5 away on the
        // x-axis is outside the Builder's disc but inside the Tower's.
        var visible = View.VisibleTiles(world, playerId: 0);
        Assert.Contains(new TileCoord(20, 5), visible);
        // 8 tiles away on the x-axis is outside the Tower's disc too.
        Assert.DoesNotContain(new TileCoord(17, 5), visible);
    }

    [Fact]
    public void VisibleTiles_IsPureRead_NoMutation()
    {
        // THE pure-read wall enforcement for Phase C.
        var world = MakeWorldWith(
            new UnitSpawn(1, new TileCoord(1, 1), UnitRole.Scout),
            new UnitSpawn(2, new TileCoord(8, 8), UnitRole.Hauler));
        var sim = new Simulation(world, seed: 1);
        var hashBefore = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
            View.VisibleTiles(world, playerId: 0);

        Assert.Equal(hashBefore, Snapshot.Hash(sim));
    }
}
