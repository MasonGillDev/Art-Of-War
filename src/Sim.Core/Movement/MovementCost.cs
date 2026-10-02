namespace Sim.Core.Movement;

// What the GROUND costs to walk (M43). A step's price lives in SubtileStepRules.StepCost
// (a quarter of the tile's hop, less a worn road link, plus the ford); this is the terrain
// half of it: which tiles feet may enter (water, bridges, canals) and what a tile hop costs
// as the tile-level route sees it. Units cost nothing here: moving friends pass through each
// other (docs/subtile-movement.md), so there is no crowding price, and the old flat
// per-tile cap and its banded crowding cost are gone.
//
// Pure reads throughout: a path search calls these many times per query.
public static class MovementCost
{
    // ---- terrain-cost dispatcher --------------------------------------

    // M12 — pick the terrain-cost table based on the moving unit's
    // movement domain. Foot reads the destination biome's cost (roads live on
    // subtile links now and price the STEP: SubtileStepRules.StepCost) plus the
    // river surcharge for the hop from → to
    // (docs/rivers.md); Water reads BoatMovementCost (water cheap, land
    // Impassable). Roads and rivers do not apply on water.
    public static int TerrainCostFor(GameWorld world, TileCoord from, TileCoord to, long now, Traversal trav) =>
        trav switch
        {
            Traversal.Water => BoatMovementCost.CostFor(world.Grid.BiomeAt(to)),
            // A bridge walks like grassland, and a road across it helps as anywhere.
            _ when IsBridge(world, to) => Sim.Core.World.Biomes.MoveCost(Sim.Core.World.Biome.Grassland)
                                          + Sim.Core.Rivers.River.CrossingCostFor(world.Grid, from, to),
            _ when FeetKeepOffWater(world, to) => Sim.Core.World.Biomes.Impassable,
            _ => world.Grid.TerrainCost(to)
                 + Sim.Core.Rivers.River.CrossingCostFor(world.Grid, from, to),
        };

    // Only boats go on water (user, 2026-09-28; docs/structure-footprints.md).
    //
    // A canal tile is water too, at world scale: feet follow a canal on the land
    // beside it and cross it by a bridge. Its banks exist on the battle board
    // only. Walking the banks inside canal tiles would need each unit to know
    // which bank it is on (an edge of a canal tile touches both), which world
    // movement doesn't track.
    public static bool FeetKeepOffWater(GameWorld world, TileCoord to) =>
        world.Grid.BiomeAt(to) == Sim.Core.World.Biome.Water && !IsBridge(world, to);

    // A bridge, or one being built (its scaffolding is already a deck, so its
    // builders can walk on): the canal tiles feet may enter.
    public static bool IsBridge(GameWorld world, TileCoord t) =>
        world.Structures.TryGetValue(t, out var s) && Bridge.IsDeck(s);

    public static bool IsCanal(GameWorld world, TileCoord t) =>
        world.Structures.TryGetValue(t, out var s) && s.Kind == StructureKind.Canal;

    // A foot move aimed at water ends on the nearest land instead: the tiles
    // around `goal`, nearest first (Manhattan), then north to south, west to
    // east. Never an error: rejecting a move because its target is water would
    // let a player map the fog's water by clicking into it.
    public static IEnumerable<TileCoord> LandNear(GameWorld world, TileCoord goal, int maxRadius = 6)
    {
        var grid = world.Grid;
        for (var r = 1; r <= maxRadius; r++)
        {
            var ring = new List<TileCoord>();
            for (var dy = -r; dy <= r; dy++)
            {
                var dx = r - Math.Abs(dy);
                foreach (var x in dx == 0 ? new[] { goal.X } : new[] { goal.X - dx, goal.X + dx })
                {
                    var t = new TileCoord(x, goal.Y + dy);
                    if (grid.InBounds(t) && grid.BiomeAt(t) != Sim.Core.World.Biome.Water
                        && grid.TerrainCost(t) < Sim.Core.World.Biomes.Impassable)
                        ring.Add(t);
                }
            }
            foreach (var t in ring.OrderBy(t => t.Y).ThenBy(t => t.X)) yield return t;
        }
    }

    // ---- the tile-level route's price (M43) ------------------------------------------------
    // The subtile pathfinder finds the tile route first (SubtileStepRules.TileHopCost); these are
    // the same prices as free functions, for callers and tests that plan on TILES. Units cost
    // nothing.

    // Total unit count on a tile: everyone but the embarked. Reporting only.
    public static int CountUnitsOnTile(GameWorld world, TileCoord tile)
    {
        var count = 0;
        foreach (var u in world.Units.Values)
            if (!u.IsEmbarked && u.Position == tile) count++;
        return count;
    }

    // The tile hop from -> to as the planner sees it (player perspective): a fortification the
    // planner KNOWS about (its own, or on a tile it can see) is a hard no-go, an unseen one is
    // invisible (the walk finds out by bonking into it); otherwise the terrain and the ford.
    public static int PlanCost(
        GameWorld world, TileCoord from, TileCoord to, int playerId, HashSet<TileCoord> visibleTiles, long now,
        Traversal trav = Traversal.Foot)
    {
        if (Fortification.BlocksPlan(world, to, playerId, visibleTiles)) return Sim.Core.World.Biomes.Impassable;
        return TerrainCostFor(world, from, to, now, trav);
    }

    public static Func<TileCoord, TileCoord, int> Planner(
        GameWorld world, int playerId, HashSet<TileCoord> visibleTiles, long now, Traversal trav = Traversal.Foot) =>
        (from, to) => PlanCost(world, from, to, playerId, visibleTiles, now, trav);

    // The tile hop from -> to on the ground as it is (ground truth): the terrain and the ford.
    public static int ExecutionCost(GameWorld world, TileCoord from, TileCoord to, long now, Traversal trav = Traversal.Foot) =>
        TerrainCostFor(world, from, to, now, trav);
}
