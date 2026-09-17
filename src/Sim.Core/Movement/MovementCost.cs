namespace Sim.Core.Movement;

// Movement cost is split into two functions with different fog-of-war
// semantics:
//
//   PlanCost (called from MoveIntent.Resolve / MoveGroupIntent.Resolve while
//     A* explores candidate paths): PLAYER-PERSPECTIVE. Counts own units
//     unconditionally; counts non-own units only on tiles currently visible
//     to the planning player. Tiles in fog appear empty of strangers — so
//     the planner can't auto-route around enemies the player can't see.
//     This is the "cost of ignorance" gameplay: setting a path into the
//     unknown carries real risk of stumbling into hidden congestion.
//
//   ExecutionCost (called when scheduling each hop's arrival in
//     MoveIntent.ScheduleNextHop / MoveGroupIntent.ScheduleNextHop):
//     GROUND TRUTH. Counts every unit physically on the source AND
//     destination tiles; pays max(source-crowding, destination-crowding).
//     Source-side crowding is what makes large groups slow (a group always
//     sits on its own member-crowd). Destination-side crowding is what
//     makes solo-unit caravans elongate as they walk into bottlenecks.
//
// Both functions are PURE READS. A* calls PlanCost many times per query;
// the per-hop scheduler calls ExecutionCost once per hop. Reading
// world.Units.Values is O(units) per call — same scaling concern flagged
// on ConstructionSite.BuildersPresent (architecture §4 rule 10). At
// current entity counts this is fine; a per-tile unit index would make it
// O(units-on-tile) when needed.
public static class MovementCost
{
    // ---- pure-read crowding primitives ---------------------------------

    // Total unit count on a tile. Ground truth: counts every unit
    // regardless of owner or visibility. Used by ExecutionCost.
    //
    // M12 — embarked units (IsEmbarked) are off-tile; they don't crowd.
    public static int CountUnitsOnTile(GameWorld world, TileCoord tile)
    {
        var count = 0;
        foreach (var u in world.Units.Values)
        {
            if (u.IsEmbarked) continue;
            if (u.Position == tile) count++;
        }
        return count;
    }

    // Player-perspective unit count on a tile. Own units always counted
    // (the player knows their own positions); non-own units only if the
    // tile is currently visible to the player. Used by PlanCost so the
    // pathfinder can't see through the fog.
    //
    // M12 — embarked units are off-tile; they don't crowd.
    public static int CountVisibleUnitsOnTile(
        GameWorld world, TileCoord tile, int playerId, HashSet<TileCoord> visibleTiles)
    {
        var tileIsVisible = visibleTiles.Contains(tile);
        var count = 0;
        foreach (var u in world.Units.Values)
        {
            if (u.IsEmbarked) continue;
            if (u.Position != tile) continue;
            if (u.OwnerId == playerId) { count++; continue; }
            if (tileIsVisible) count++;
        }
        return count;
    }

    // ---- terrain-cost dispatcher --------------------------------------

    // M12 — pick the terrain-cost table based on the moving unit's
    // movement domain. Foot reads Road.EffectiveCost (biome + road
    // condition) plus the river surcharge for the hop from → to
    // (docs/rivers.md); Water reads BoatMovementCost (water cheap, land
    // Impassable). Roads and rivers do not apply on water.
    public static int TerrainCostFor(GameWorld world, TileCoord from, TileCoord to, long now, Traversal trav) =>
        trav switch
        {
            Traversal.Water => BoatMovementCost.CostFor(world.Grid.BiomeAt(to)),
            _ => Road.EffectiveCost(world, to, now)
                 + Sim.Core.Rivers.River.CrossingCostFor(world.Grid, from, to),
        };

    // The A* cost delegate MoveIntent / MoveGroupIntent hand to
    // Pathfinding.FindPath — one place, so a new per-hop term (rivers today,
    // bridges tomorrow) lands once. Captures pure reads only.
    public static Func<TileCoord, TileCoord, int> Planner(
        GameWorld world, int playerId, HashSet<TileCoord> visibleTiles, long now,
        Traversal trav = Traversal.Foot) =>
        (from, to) => PlanCost(world, from, to, playerId, visibleTiles, now, trav);

    // ---- A* plan cost (player-perspective, destination-side) -----------

    // Cost of the hop `from` → `to` from the planning player's perspective.
    // Terrain (including road condition for Foot; BoatMovementCost for
    // Water) + banded crowding from the units the player can see on the
    // tile.
    //
    // A tile already at the hard cap returns Biomes.Impassable so A* will
    // route strictly around it — but only when the player can SEE that
    // it's at cap. A cap-saturated tile in fog still looks empty to the
    // planner; the unit walking through it will be rejected at arrival
    // time and yield as Idle. (Ignorance has consequences.)
    //
    // M12: `trav` selects the terrain table. Defaults to Foot so call
    // sites that don't know about traversal yet keep their behaviour.
    public static int PlanCost(
        GameWorld world, TileCoord from, TileCoord to,
        int playerId, HashSet<TileCoord> visibleTiles, long now,
        Traversal trav = Traversal.Foot)
    {
        var tile = to;
        // M26 — fortifications. A blocking structure the planner KNOWS about
        // (own, or on a currently-visible tile) is a hard no-go; an unseen
        // one stays invisible to A* — the mover learns about it by bonking
        // into the ground-truth checks. Same fog contract as crowding below.
        // docs/walls-and-gates.md.
        if (Fortification.BlocksPlan(world, tile, playerId, visibleTiles))
            return Sim.Core.World.Biomes.Impassable;
        var visibleCount = CountVisibleUnitsOnTile(world, tile, playerId, visibleTiles);
        if (visibleCount >= MovementConstants.MaxUnitsPerTile)
            return Sim.Core.World.Biomes.Impassable;
        var terrain = TerrainCostFor(world, from, tile, now, trav);
        // Short-circuit: an Impassable terrain (e.g. land tile for a
        // Water-traversal unit) stays Impassable — adding crowding would
        // wrap into nonsense.
        if (terrain == Sim.Core.World.Biomes.Impassable) return terrain;
        return terrain + MovementConstants.BandedCrowdingCost(visibleCount);
    }

    // ---- execution cost (ground truth, source + destination) -----------

    // Cost of a single hop from `from` to `to` at sim tick `now`. Ground
    // truth — includes ALL units on both tiles regardless of owner or
    // visibility. Terrain pays for entering `to`; crowding pays for
    // whichever of source/destination is more crowded.
    //
    // Source crowding is what slows large groups: a group always sits on
    // its own member crowd, so its per-hop cost naturally scales with
    // size. Destination crowding is what stretches solo caravans: the
    // last unit to enter a piling tile pays more than the first.
    //
    // M12: `trav` selects the terrain table. Defaults to Foot.
    public static int ExecutionCost(
        GameWorld world, TileCoord from, TileCoord to, long now,
        Traversal trav = Traversal.Foot)
    {
        var terrain = TerrainCostFor(world, from, to, now, trav);
        if (terrain == Sim.Core.World.Biomes.Impassable) return terrain;
        var fromCrowd = MovementConstants.BandedCrowdingCost(CountUnitsOnTile(world, from));
        var toCrowd = MovementConstants.BandedCrowdingCost(CountUnitsOnTile(world, to));
        return terrain + Math.Max(fromCrowd, toCrowd);
    }
}
