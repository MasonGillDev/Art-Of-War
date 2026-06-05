using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Tests;

// Crowding cost on movement. Two-cost model:
//   PLAN cost  : fog-aware, player-perspective unit count (own units always;
//                non-own only on visible tiles)
//   EXEC cost  : ground truth, max(banded source crowding, banded dest crowding)
// Hard cap at MovementConstants.MaxUnitsPerTile (50) — MoveArrivalEvent /
// GroupArrivalEvent reject overshooting arrivals.
//
// See docs/movement-cost.md for the model rationale.
public class MovementCrowdingTests
{
    // ====================================================================
    // The curve
    // ====================================================================

    [Fact]
    public void BandedCrowdingCost_HitsExpectedTiers()
    {
        // 1-3 units : +0   (normal play; no penalty)
        Assert.Equal(0, MovementConstants.BandedCrowdingCost(1));
        Assert.Equal(0, MovementConstants.BandedCrowdingCost(2));
        Assert.Equal(0, MovementConstants.BandedCrowdingCost(3));
        // 4-7 units : +10
        Assert.Equal(10, MovementConstants.BandedCrowdingCost(4));
        Assert.Equal(10, MovementConstants.BandedCrowdingCost(7));
        // 8-15 units : +25
        Assert.Equal(25, MovementConstants.BandedCrowdingCost(8));
        Assert.Equal(25, MovementConstants.BandedCrowdingCost(15));
        // 16+ units : +50
        Assert.Equal(50, MovementConstants.BandedCrowdingCost(16));
        Assert.Equal(50, MovementConstants.BandedCrowdingCost(1000));
    }

    // ====================================================================
    // Pure-read primitives are observation-safe
    // ====================================================================

    [Fact]
    public void CrowdingCountAndCost_Are_PureReads_NoMutation()
    {
        var (sim, _) = BuildWorld(gridSize: 8);
        // Pack 5 units onto a tile.
        for (var i = 1; i <= 5; i++)
            sim.World.AddUnit(new Unit(i, new TileCoord(3, 3)));
        var hashBefore = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
        {
            MovementCost.CountUnitsOnTile(sim.World, new TileCoord(3, 3));
            MovementCost.ExecutionCost(sim.World, new TileCoord(2, 2), new TileCoord(3, 3), sim.Now);
        }

        Assert.Equal(hashBefore, Snapshot.Hash(sim));
    }

    // ====================================================================
    // Execution cost: source + destination crowding
    // ====================================================================

    [Fact]
    public void ExecutionCost_SoloUnit_NoCrowdingAddedToTerrain()
    {
        // Single unit on Grassland (terrain cost 10) → cost = 10, no penalty.
        var (sim, _) = BuildWorld(gridSize: 6, biome: Biome.Grassland);
        sim.World.AddUnit(new Unit(1, new TileCoord(2, 2)));

        var cost = MovementCost.ExecutionCost(sim.World, new TileCoord(2, 2), new TileCoord(3, 2), sim.Now);
        Assert.Equal(10, cost);
    }

    [Fact]
    public void ExecutionCost_LargeStackOnSource_PaysSourceCrowding()
    {
        // 10 units bunched on (2,2). Grassland terrain = 10. Source-side
        // crowding for 10 units = +25 (8-15 band). Destination empty.
        // max(25, 0) = 25. Total cost 35.
        var (sim, _) = BuildWorld(gridSize: 6, biome: Biome.Grassland);
        for (var i = 1; i <= 10; i++)
            sim.World.AddUnit(new Unit(i, new TileCoord(2, 2)));

        var cost = MovementCost.ExecutionCost(sim.World, new TileCoord(2, 2), new TileCoord(3, 2), sim.Now);
        Assert.Equal(10 + 25, cost);
    }

    [Fact]
    public void ExecutionCost_DestCrowdedSourceClear_PaysDestCrowding()
    {
        // 1 unit on (2,2) (source), 6 units already on (3,2) (dest). The
        // 1-unit source is +0, 6 units on dest is +10 (4-7 band).
        // max(0, 10) = 10. Total = 10 + 10 = 20.
        var (sim, _) = BuildWorld(gridSize: 6, biome: Biome.Grassland);
        sim.World.AddUnit(new Unit(1, new TileCoord(2, 2)));
        for (var i = 2; i <= 7; i++)
            sim.World.AddUnit(new Unit(i, new TileCoord(3, 2)));

        var cost = MovementCost.ExecutionCost(sim.World, new TileCoord(2, 2), new TileCoord(3, 2), sim.Now);
        Assert.Equal(10 + 10, cost);
    }

    [Fact]
    public void ExecutionCost_BothCrowded_TakesMaxNotSum()
    {
        // 5 units source (band +10), 9 units dest (band +25). max=25, not 35.
        var (sim, _) = BuildWorld(gridSize: 6, biome: Biome.Grassland);
        for (var i = 1; i <= 5; i++)
            sim.World.AddUnit(new Unit(i, new TileCoord(2, 2)));
        for (var i = 6; i <= 14; i++)
            sim.World.AddUnit(new Unit(i, new TileCoord(3, 2)));

        var cost = MovementCost.ExecutionCost(sim.World, new TileCoord(2, 2), new TileCoord(3, 2), sim.Now);
        Assert.Equal(10 + 25, cost);
    }

    // ====================================================================
    // Plan cost: fog of war
    // ====================================================================

    [Fact]
    public void PlanCost_OwnUnitsAlwaysCounted_EvenInFog()
    {
        // 4 own units on a tile in deep fog (no vision sources). Plan cost
        // counts them (own units are never fogged from the player) so the
        // crowding penalty fires.
        var (sim, _) = BuildWorld(gridSize: 6, biome: Biome.Grassland);
        for (var i = 1; i <= 4; i++)
            sim.World.AddUnit(new Unit(i, new TileCoord(5, 5)) { OwnerId = 0 });
        // No vision sources → visible set empty.
        var visible = new HashSet<TileCoord>();

        var cost = MovementCost.PlanCost(sim.World, new TileCoord(5, 5), playerId: 0, visible, sim.Now);
        Assert.Equal(10 + 10, cost);  // grassland + 4-7 band
    }

    [Fact]
    public void PlanCost_EnemyUnitsInFog_AreInvisible()
    {
        // 5 ENEMY units on a tile that player 0 cannot see. Plan cost from
        // player 0's perspective sees an EMPTY tile (terrain cost only).
        // This is what enables "stumble into hidden army" gameplay.
        var (sim, _) = BuildWorld(gridSize: 6, biome: Biome.Grassland);
        for (var i = 1; i <= 5; i++)
            sim.World.AddUnit(new Unit(i, new TileCoord(5, 5)) { OwnerId = 1 });
        // Player 0 has no vision sources covering (5,5).
        var visible = new HashSet<TileCoord>();

        var cost = MovementCost.PlanCost(sim.World, new TileCoord(5, 5), playerId: 0, visible, sim.Now);
        Assert.Equal(10, cost);  // pure terrain, fog hid the crowd

        // Ground truth disagrees: ExecutionCost sees all 5 enemies.
        var actualCost = MovementCost.ExecutionCost(
            sim.World, new TileCoord(4, 5), new TileCoord(5, 5), sim.Now);
        Assert.Equal(10 + 10, actualCost);  // 4-7 band kicks in
    }

    [Fact]
    public void PlanCost_EnemyUnitsVisible_AreSeenAndPenalized()
    {
        // Same 5 enemies on (5,5) — but now player 0 can SEE the tile.
        // Plan cost includes them just like own units.
        var (sim, _) = BuildWorld(gridSize: 6, biome: Biome.Grassland);
        for (var i = 1; i <= 5; i++)
            sim.World.AddUnit(new Unit(i, new TileCoord(5, 5)) { OwnerId = 1 });
        var visible = new HashSet<TileCoord> { new(5, 5) };

        var cost = MovementCost.PlanCost(sim.World, new TileCoord(5, 5), playerId: 0, visible, sim.Now);
        Assert.Equal(10 + 10, cost);
    }

    [Fact]
    public void PlanCost_TileAtHardCap_IsImpassable()
    {
        // 50 visible units on a tile → cap reached → A* sees Impassable so
        // it strictly routes around.
        var (sim, _) = BuildWorld(gridSize: 6, biome: Biome.Grassland);
        for (var i = 1; i <= MovementConstants.MaxUnitsPerTile; i++)
            sim.World.AddUnit(new Unit(i, new TileCoord(3, 3)) { OwnerId = 0 });
        var visible = new HashSet<TileCoord> { new(3, 3) };

        var cost = MovementCost.PlanCost(sim.World, new TileCoord(3, 3), playerId: 0, visible, sim.Now);
        Assert.Equal(Sim.Core.World.Biomes.Impassable, cost);
    }

    // ====================================================================
    // Pathfinding interaction: route around visible crowds, NOT around
    // fog'd ones (the load-bearing fog-of-war contract).
    // ====================================================================

    [Fact]
    public void Path_AvoidsVisibleCrowd_RoutesAround()
    {
        // 8-wide grid. Place a Tower at (4,2) — radius 7 covers (4,4) cleanly.
        // Pile 16 enemies onto (4,4) → visible cluster, +50 cost band. A
        // direct path from (0,4) to (7,4) would go straight through (4,4);
        // the planner detours since the +50 makes a 1-tile-up bypass cheaper.
        var (sim, world) = BuildWorld(gridSize: 8, biome: Biome.Grassland);
        var tower = world.AddStructure(new Tower(new TileCoord(4, 2)) { OwnerId = 0 });
        Sight.Reveal(world, 0, tower.At, Sight.RadiusFor(StructureKind.Tower), now: 0);

        for (var i = 1; i <= 16; i++)
            world.AddUnit(new Unit(i, new TileCoord(4, 4)) { OwnerId = 1 });
        var hero = world.AddUnit(new Unit(100, new TileCoord(0, 4)) { OwnerId = 0 });

        var visible = View.VisibleTiles(world, 0);
        Assert.Contains(new TileCoord(4, 4), visible);   // sanity: cluster IS visible

        var path = Pathfinding.FindPath(world.Grid, hero.Position, new TileCoord(7, 4),
            tile => MovementCost.PlanCost(world, tile, hero.OwnerId, visible, sim.Now));
        Assert.NotNull(path);
        Assert.DoesNotContain(new TileCoord(4, 4), path!);   // detours around the crowd
    }

    [Fact]
    public void Path_DoesNotAvoidFoggedCrowd_RoutesStraightThrough()
    {
        // THE LOAD-BEARING TEST. Same scenario as above but the cluster is
        // in fog (no vision source nearby). The planner can't see them, so
        // it picks the straight path — and the unit will walk into the
        // hidden army at execution time. Cost of ignorance.
        var (sim, world) = BuildWorld(gridSize: 8, biome: Biome.Grassland);
        // No Castle, no vision sources. Player 0's visible set will be empty.

        for (var i = 1; i <= 16; i++)
            world.AddUnit(new Unit(i, new TileCoord(4, 4)) { OwnerId = 1 });
        var hero = world.AddUnit(new Unit(100, new TileCoord(0, 4)) { OwnerId = 0 });

        var visible = View.VisibleTiles(world, 0);
        Assert.DoesNotContain(new TileCoord(4, 4), visible);   // sanity: fog'd

        var path = Pathfinding.FindPath(world.Grid, hero.Position, new TileCoord(7, 4),
            tile => MovementCost.PlanCost(world, tile, hero.OwnerId, visible, sim.Now));
        Assert.NotNull(path);
        Assert.Contains(new TileCoord(4, 4), path!);   // walks straight into the hidden army
    }

    // ====================================================================
    // Hard cap rejection
    // ====================================================================

    [Fact]
    public void MoveArrival_RejectedAtHardCap_UnitGoesIdleOnPreviousTile()
    {
        // Fill a tile to the cap, then attempt to walk one more unit onto
        // it. The arrival rejects; the unit yields as Idle on its prior tile
        // with no committed path.
        var (sim, world) = BuildWorld(gridSize: 6, biome: Biome.Grassland);
        var fullTile = new TileCoord(3, 3);
        for (var i = 1; i <= MovementConstants.MaxUnitsPerTile; i++)
            world.AddUnit(new Unit(i, fullTile));
        var hero = world.AddUnit(new Unit(999, new TileCoord(2, 3)));
        var heroStart = hero.Position;

        // Force the move: planning sees the cluster (we're not testing fog
        // here; we're testing that the arrival event correctly rejects).
        sim.SubmitIntent(0, new MoveIntent(hero.Id, fullTile));
        sim.Run();

        // Hero never made it onto the full tile.
        Assert.NotEqual(fullTile, hero.Position);
        // Hero is idle with no committed path (the rejection cleared it).
        Assert.Equal(Activity.Idle, hero.Activity);
        Assert.Null(hero.PathRemaining);
        Assert.Null(hero.PathFinalDest);
    }

    // ====================================================================
    // Determinism — twin runs match
    // ====================================================================

    [Fact]
    public void Crowding_TwinRun_HashesMatch()
    {
        Simulation Build()
        {
            var sim = MakeSim(seed: 0xABCD, gridSize: 6);
            // 5 own units on (1,1), a 3-unit chain destination at (4,4).
            for (var i = 1; i <= 5; i++)
                sim.World.AddUnit(new Unit(i, new TileCoord(1, 1)) { OwnerId = 0 });
            for (var i = 6; i <= 8; i++)
                sim.World.AddUnit(new Unit(i, new TileCoord(4, 4)) { OwnerId = 0 });
            // Move one unit across — crowding affects every hop.
            sim.SubmitIntent(0, new MoveIntent(1, new TileCoord(4, 4)));
            sim.Run();
            return sim;
        }

        var a = Build();
        var b = Build();
        Assert.Equal(Snapshot.Hash(a), Snapshot.Hash(b));
    }

    // ====================================================================
    // Group movement: atomic arrival preserved; cost scales with group size
    // ====================================================================

    [Fact]
    public void Group_HopCost_ScalesWithMemberCount_ViaSourceCrowding()
    {
        // Two scenarios, identical except group size (3 vs 10). The larger
        // group pays a higher per-hop cost via source crowding.
        long Hop(int size)
        {
            var sim = MakeSim(seed: 1, gridSize: 8);
            var members = new List<int>();
            for (var i = 1; i <= size; i++)
            {
                sim.World.AddUnit(new Unit(i, new TileCoord(1, 1)) { OwnerId = 0 });
                members.Add(i);
            }
            sim.SubmitIntent(0, new FormGroupIntent(members, new TileCoord(1, 1)));
            sim.Run();
            sim.SubmitIntent(sim.Now, new MoveGroupIntent(1, new TileCoord(2, 1)));
            sim.Run();
            return sim.Now;
        }

        var smallTime = Hop(3);   // 1-3 band: +0 → only terrain cost
        var bigTime   = Hop(10);  // 8-15 band: +25
        Assert.True(bigTime > smallTime,
            $"expected 10-member hop ({bigTime}) to take longer than 3-member hop ({smallTime})");
    }

    [Fact]
    public void Group_AtomicArrival_AllMembersOnSameTile_AfterHop()
    {
        // The M5 invariant must hold after a crowded hop: all members land
        // on the destination together in one event.
        var sim = MakeSim(seed: 1, gridSize: 8);
        var members = new List<int>();
        for (var i = 1; i <= 5; i++)
        {
            sim.World.AddUnit(new Unit(i, new TileCoord(1, 1)) { OwnerId = 0 });
            members.Add(i);
        }
        sim.SubmitIntent(0, new FormGroupIntent(members, new TileCoord(1, 1)));
        sim.Run();
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(1, new TileCoord(3, 1)));
        sim.Run();

        var positions = members.Select(id => sim.World.Units[id].Position).ToHashSet();
        Assert.Single(positions);
        Assert.Equal(new TileCoord(3, 1), positions.Single());
    }

    [Fact]
    public void Group_HopBlockedByCap_GroupGoesIdleAtSource()
    {
        // Cap is 50. Move the group DIRECTLY to a tile with 48 existing
        // units — 48 + 5 = 53 > 50 → arrival rejects. (We aim the move at
        // the full tile so there's no detour around it; the rejection
        // exercises the GroupArrivalEvent cap branch.)
        var sim = MakeSim(seed: 1, gridSize: 4);
        var members = new List<int>();
        for (var i = 1; i <= 5; i++)
        {
            sim.World.AddUnit(new Unit(i, new TileCoord(1, 1)) { OwnerId = 0 });
            members.Add(i);
        }
        var fullTile = new TileCoord(2, 1);
        for (var i = 6; i <= 53; i++)
            sim.World.AddUnit(new Unit(i, fullTile) { OwnerId = 0 });

        sim.SubmitIntent(0, new FormGroupIntent(members, new TileCoord(1, 1)));
        sim.Run();
        var startPos = sim.World.Groups[1].Position;
        sim.SubmitIntent(sim.Now, new MoveGroupIntent(1, fullTile));
        sim.Run();

        var group = sim.World.Groups[1];
        Assert.Equal(GroupState.Idle, group.State);
        Assert.Equal(startPos, group.Position);
        Assert.Null(group.PathRemaining);
        foreach (var id in members)
            Assert.Equal(startPos, sim.World.Units[id].Position);
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private static Simulation MakeSim(ulong seed, int gridSize, Biome biome = Biome.Grassland)
    {
        var grid = new TileGrid(gridSize, gridSize, biome);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        return new Simulation(world, seed);
    }

    private static (Simulation sim, GameWorld world) BuildWorld(int gridSize, Biome biome = Biome.Grassland)
    {
        var sim = MakeSim(seed: 1, gridSize: gridSize, biome);
        return (sim, sim.World);
    }
}
