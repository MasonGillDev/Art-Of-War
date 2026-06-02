using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Roads;
using Sim.Core.World;

namespace Sim.Tests;

// Phase D of M2: pathfinding integration. A* uses live road-aware cost via
// the costFn delegate; path queries are pure (no mutation).
public class RoadPathfindingTests
{
    [Fact]
    public void DefaultCostFn_StillUsesBiomeCost()
    {
        // Backwards-compat check: callers that don't pass a costFn get
        // identical behavior to before.
        var grid = new TileGrid(5, 5, Biome.Grassland);
        grid.SetBiome(new TileCoord(2, 2), Biome.Forest);   // expensive detour-worthy
        var path1 = Pathfinding.FindPath(grid, new TileCoord(0, 0), new TileCoord(4, 4));
        var path2 = Pathfinding.FindPath(grid, new TileCoord(0, 0), new TileCoord(4, 4),
            costFn: tile => grid.TerrainCost(tile));
        Assert.NotNull(path1);
        Assert.NotNull(path2);
        Assert.Equal(path1, path2);
    }

    [Fact]
    public void HighConditionCorridor_PreferredOverShorterRawPath()
    {
        // Setup: an L-shape forest band creates two routes — short direct
        // path through Forest (high biome cost) vs longer detour through
        // Grassland with road. Roads should make the detour cheaper.
        //
        // Direct path through forest cost = 5 Forest tiles * 30 = 150.
        // Detour through Grassland with maxed road: 7 tiles * MIN_COST = 7.
        var grid = new TileGrid(8, 3, Biome.Grassland);
        // Forest wall at x=4 across all rows
        for (var y = 0; y < 3; y++) grid.SetBiome(new TileCoord(4, y), Biome.Forest);
        var world = new GameWorld(grid);

        // No road: A* picks the cheapest path, which goes straight through
        // the Forest wall (one Forest tile at cost 30 + Grasslands at 10
        // each = cheaper than going around).
        var sim = new Simulation(world, seed: 1);
        var rawPath = Pathfinding.FindPath(grid, new TileCoord(0, 1), new TileCoord(7, 1),
            costFn: tile => Road.EffectiveCost(world, tile, sim.Now));
        Assert.NotNull(rawPath);
        Assert.Contains(new TileCoord(4, 1), rawPath!);     // goes through Forest

        // Now build a max-condition road along y=0 (going around the wall).
        for (var x = 0; x < 8; x++)
            world.Roads[new TileCoord(x, 0)] = new RoadState(RoadConstants.CONDITION_MAX, 0);

        var roadedPath = Pathfinding.FindPath(grid, new TileCoord(0, 1), new TileCoord(7, 1),
            costFn: tile => Road.EffectiveCost(world, tile, sim.Now));
        Assert.NotNull(roadedPath);
        // Should prefer going up onto the road rather than through the forest.
        Assert.Contains(new TileCoord(0, 0), roadedPath!);   // uses the road row
        Assert.DoesNotContain(new TileCoord(4, 1), roadedPath!);
    }

    [Fact]
    public void Pathfinding_IsPureRead_NoRoadMutation()
    {
        // 100 path queries over a road set — snapshot hash unchanged.
        // This is the pure-read wall, enforced at the integration layer.
        var grid = new TileGrid(8, 8, Biome.Grassland);
        var world = new GameWorld(grid);
        for (var i = 0; i < 5; i++)
            world.Roads[new TileCoord(i, i)] = new RoadState(300 + 100 * i, 7);
        var sim = new Simulation(world, seed: 1);
        var hashBefore = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
        {
            Pathfinding.FindPath(grid, new TileCoord(0, 0), new TileCoord(7, 7),
                costFn: tile => Road.EffectiveCost(world, tile, sim.Now));
        }

        Assert.Equal(hashBefore, Snapshot.Hash(sim));
    }

    [Fact]
    public void CostFloor_HoldsEvenAtMaxCondition()
    {
        // A* arrival time uses the same EffectiveCost. Even on a maxed
        // road, no tile costs less than MIN_COST.
        var grid = new TileGrid(5, 1, Biome.Grassland);
        var world = new GameWorld(grid);
        for (var x = 0; x < 5; x++)
            world.Roads[new TileCoord(x, 0)] = new RoadState(RoadConstants.CONDITION_MAX, 0);

        var sim = new Simulation(world, seed: 1);
        world.AddUnit(new Unit(1, new TileCoord(0, 0)));
        sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(4, 0)));
        sim.Run();

        // 4 tiles entered, each at cost 2 (Grassland 10 - reduction 8 = 2).
        // No tile crossed below MIN_COST. Final tick = 8.
        Assert.Equal(8, sim.Now);
        Assert.True(sim.Now >= 4 * RoadConstants.MIN_COST);
    }

    [Fact]
    public void UnitWalksFasterOnRoad_ThanOnRaw()
    {
        // Build a maxed road on a row. A unit walking it should arrive
        // faster than the same walk over plain biome.
        var grid = new TileGrid(6, 1, Biome.Grassland);
        var worldRaw = new GameWorld(grid);
        var worldRoad = new GameWorld(grid);
        for (var x = 0; x < 6; x++)
            worldRoad.Roads[new TileCoord(x, 0)] = new RoadState(RoadConstants.CONDITION_MAX, 0);

        long Walk(GameWorld w)
        {
            var sim = new Simulation(w, seed: 1);
            w.AddUnit(new Unit(1, new TileCoord(0, 0)));
            sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(5, 0)));
            sim.Run();
            return sim.Now;
        }

        var rawTicks = Walk(worldRaw);
        var roadTicks = Walk(worldRoad);
        Assert.True(roadTicks < rawTicks,
            $"Road walk should be faster: road={roadTicks}, raw={rawTicks}");
        // 5 tiles entered at 10 each = 50; on road, 5 * 2 = 10.
        Assert.Equal(50, rawTicks);
        Assert.Equal(10, roadTicks);
    }
}
