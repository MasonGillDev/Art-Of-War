using Sim.Core.Combat;
using Sim.Core.Fortifications;
using Sim.Core.Movement;

namespace Sim.Core.Battlefields;

// Who is walking, as the step rules see them (M43): the owner (whose structures are
// friendly), the movement domain, and whether they are an archer (towers).
public readonly record struct StepMover(int Owner, Traversal Traversal, bool Archer)
{
    public static StepMover Of(Unit u) => new(u.OwnerId, u.Traversal, UnitCombatCatalog.Spec(u.Role).Ranged);
}

// THE ONE ANSWER to "may this mover take the step from one subtile to the next?"
// (docs/subtile-movement.md, docs/m43-status.md step 1). The pathfinder plans with it
// and the walk checks each step with it, so a plan and the walk can only disagree
// about what the player couldn't see, never about the rules.
//
// A pure read with a per-query cache (a search asks about the same tile many times):
// one instance per query, never stored.
//
// Two flavours, the same fog contract as the M26 fortification checks:
//   * PLANNING (visible given): a structure in the fog (not the mover's own, not on a
//     visible tile) is treated as open ground; the mover finds out when it walks into
//     it. Fortifications are checked with BlocksPlan.
//   * GROUND TRUTH (visible null): every structure counts. Fortifications with
//     BlocksMover.
public sealed class SubtileStepRules
{
    private readonly GameWorld _world;
    private readonly StepMover _mover;
    private readonly HashSet<TileCoord>? _visible;
    private readonly Dictionary<TileCoord, (SubtileLayer Layer, BoardMover Mover)> _layers = new();

    public SubtileStepRules(GameWorld world, StepMover mover, HashSet<TileCoord>? visible)
    {
        _world = world;
        _mover = mover;
        _visible = visible;
    }

    public StepMover Mover => _mover;

    // The tile's layer as the viewer knows it, with the mover's view of it.
    private (SubtileLayer Layer, BoardMover Mover) Shape(TileCoord tile)
    {
        if (_layers.TryGetValue(tile, out var hit)) return hit;
        var layer = SubtileLayer.Open;
        // Boats sail under bridges and past docks: a tile's footprint doesn't shape their water.
        if (_mover.Traversal != Traversal.Water && _world.Structures.TryGetValue(tile, out var s) && (_visible is null || s.OwnerId == _mover.Owner || _visible.Contains(tile)))
            layer = Battlefields.LayerFor(_world, tile);
        var friendly = layer.Owner is { } o && Fortification.IsOwnOrAllied(_world, _mover.Owner, o);
        hit = (layer, new BoardMover(friendly, _mover.Archer));
        _layers[tile] = hit;
        return hit;
    }

    // May the mover stand on this subtile at all (as it knows the ground)?
    public bool CanStand(WorldSubtile at)
    {
        var (layer, mover) = Shape(at.Tile);
        return layer.CanStand(at.Sub, mover) && TileProblem(at.Tile) is null;
    }

    // Why the tile itself can't be walked, or null. Terrain and water for the domain.
    // (A fortification is a matter of crossing a tile edge: see Problem.)
    private string? TileProblem(TileCoord tile)
    {
        if (!_world.Grid.InBounds(tile)) return "that is off the map";
        if (_mover.Traversal == Traversal.Water)
            return _world.Grid.BiomeAt(tile) == Sim.Core.World.Biome.Water ? null : "boats stay on the water";
        if (_world.Grid.BiomeAt(tile) == Sim.Core.World.Biome.Water && !MovementCost.IsBridge(_world, tile)) return "that is water";
        if (_world.Grid.TerrainCost(tile) >= Sim.Core.World.Biomes.Impassable) return "that ground can't be walked";
        return null;
    }

    // Why the single step from → to can never work, or null. `from` and `to` must be
    // 4-adjacent.
    public string? Problem(WorldSubtile from, WorldSubtile to)
    {
        var ft = from.Tile;
        var tt = to.Tile;
        if (ft != tt)
        {
            if (TileProblem(tt) is { } why) return why;
            var blocked = _visible is null
                ? Fortification.BlocksMover(_world, tt, _mover.Owner)
                : Fortification.BlocksPlan(_world, tt, _mover.Owner, _visible);
            if (blocked) return "a fortification blocks it";
        }

        var (lf, mf) = Shape(ft);
        if (ft == tt)
        {
            if (TileProblem(tt) is { } sameTile) return sameTile;
            return lf.CanStep(from.Sub, to.Sub, mf) ? null : "the ground doesn't allow that step";
        }

        var (lt, mt) = Shape(tt);
        var h = from.HeadingTo(to)!.Value;
        if (!lf.CanStep(from.Sub, from.Sub.Step(h), mf)) return "that side of the tile is closed";
        if (!lt.CanStep(to.Sub.Step(h.Opposite()), to.Sub, mt)) return "it can't come onto that subtile from there";
        return null;
    }

    public bool Allows(WorldSubtile from, WorldSubtile to) => Problem(from, to) is null;

    // What the TILE hop a → b costs, for the tile-level route that shapes the corridor:
    // Biomes.Impassable when the tile can't be walked or a fortification blocks it. Not a
    // step cost (steps are a quarter of this); only the tile route's currency.
    public int TileHopCost(TileCoord a, TileCoord b)
    {
        if (TileProblem(b) is not null) return Sim.Core.World.Biomes.Impassable;
        var blocked = _visible is null
            ? Fortification.BlocksMover(_world, b, _mover.Owner)
            : Fortification.BlocksPlan(_world, b, _mover.Owner, _visible);
        if (blocked) return Sim.Core.World.Biomes.Impassable;
        if (_mover.Traversal == Traversal.Water) return BoatMovementCost.CostFor(_world.Grid.BiomeAt(b));
        return FootTerrainCost(_world, b) + Sim.Core.Rivers.River.CrossingCostFor(_world.Grid, a, b);
    }

    // A quarter of the hop onto this tile's ground, rounded up: what one step onto the
    // tile costs (the combat prototype's world pace). No road bonus here (M43 step 3).
    public static long StepTicks(GameWorld world, TileCoord tile, Traversal trav = Traversal.Foot)
    {
        var cost = trav == Traversal.Water
            ? BoatMovementCost.CostFor(world.Grid.BiomeAt(tile))
            : FootTerrainCost(world, tile);
        if (cost >= Sim.Core.World.Biomes.Impassable || cost <= 0) cost = 30;
        return Math.Max(1, (cost + Subtile.Size - 1) / Subtile.Size);
    }

    // What the ground of a tile costs feet: a bridge deck walks like grassland (as
    // MovementCost.TerrainCostFor prices it), the rest by the tile's terrain.
    public static int FootTerrainCost(GameWorld world, TileCoord tile) =>
        MovementCost.IsBridge(world, tile)
            ? Sim.Core.World.Biomes.MoveCost(Sim.Core.World.Biome.Grassland)
            : world.Grid.TerrainCost(tile);

    // Ticks the step onto `to` costs AT TICK `now`: a quarter of the foot hop onto that
    // tile's ground, rounded up, reduced by the wear of the road link stepped on (feet
    // only: a worn step is cheaper), plus the ford when it crosses a river edge.
    // PURE READ.
    public static int StepCost(GameWorld world, Traversal trav, WorldSubtile from, WorldSubtile to, long now)
    {
        var cost = (int)StepTicks(world, to.Tile, trav);
        if (trav == Traversal.Foot) cost = Sim.Core.Roads.Road.EffectiveCost(world, from, to, now, cost);
        if (from.Tile != to.Tile) cost += Sim.Core.Rivers.River.CrossingCostFor(world.Grid, from.Tile, to.Tile);
        return cost;
    }

    // The cheapest a single step can cost on a map (or a corridor of it): what a search's
    // heuristic may assume per step. With roads on the map a worn step is cheaper than
    // the plain floor, so the floor drops to the cheapest a road can make it.
    public static int CheapestStep(GameWorld world, int plainFloor) =>
        world.Roads.Count == 0
            ? plainFloor
            : Math.Max(Sim.Core.Roads.RoadConstants.MIN_COST,
                (int)((long)plainFloor * (100 - Sim.Core.Roads.RoadConstants.MAX_REDUCTION_PERCENT) / 100));
}
