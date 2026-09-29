using Sim.Core.Battlefields;

namespace Sim.Core.Movement;

// What world pathfinding asks about a tile's shape, for one mover
// (docs/structure-footprints.md, "World movement"): which edges connect, and
// whether it can stop there. Reads each tile's footprint through Crossings.
//
// Two flavours, the same fog contract as the M26 fortification checks:
//   * PLANNING (Planning(world, owner, visible)): a structure the player knows
//     about (its own, or on a tile it can see now) shapes the plan; one in the
//     fog doesn't, and the mover finds out when it walks into it.
//   * GROUND TRUTH (GroundTruth(world, owner)): every structure counts. The
//     per-hop check before each arrival.
//
// A pure read with a per-query cache (A* asks about the same tile many times).
// One instance per query; never stored.
public sealed class CrossingRule
{
    private readonly GameWorld _world;
    private readonly int _owner;
    private readonly HashSet<TileCoord>? _visible;
    private readonly Dictionary<TileCoord, Crossings> _cache = new();

    private CrossingRule(GameWorld world, int owner, HashSet<TileCoord>? visible)
    {
        _world = world;
        _owner = owner;
        _visible = visible;
    }

    public static CrossingRule Planning(GameWorld world, int owner, HashSet<TileCoord> visible) => new(world, owner, visible);

    public static CrossingRule GroundTruth(GameWorld world, int owner) => new(world, owner, null);

    // Tile shapes rule world movement only in grid-combat worlds, like the rest
    // of the battlefield work (docs/battlefield-grid.md): the default (pooled)
    // game moves as it always has until the grid becomes the default.
    public static bool Applies(GameWorld world) => world.CombatConfig.Model == Sim.Core.Combat.CombatModel.Grid;

    private Crossings At(TileCoord t)
    {
        if (_cache.TryGetValue(t, out var c)) return c;
        c = Crossings.Anywhere;
        if (_world.Structures.TryGetValue(t, out var s)
            && (_visible is null || s.OwnerId == _owner || _visible.Contains(t)))
            c = Crossings.Of(Footprints.For(_world, t));
        _cache[t] = c;
        return c;
    }

    private bool Friendly(TileCoord t) =>
        _world.Structures.TryGetValue(t, out var s) && Fortification.IsOwnOrAllied(_world, _owner, s.OwnerId);

    // Does this tile's shape matter at all? Most of the map: no.
    public bool Restricted(TileCoord t) => !At(t).IsAnywhere;

    public bool CanPass(TileCoord t, Heading? entry, Heading exit) => At(t).CanPass(Friendly(t), entry, exit);

    public bool CanStay(TileCoord t, Heading? entry) => At(t).CanStay(Friendly(t), entry);

    // Can the mover come onto `to` across its edge `entry` and get anywhere
    // (stand, or carry on)?
    public bool CanEnter(TileCoord to, Heading entry)
    {
        var c = At(to);
        if (c.IsAnywhere) return true;
        var f = Friendly(to);
        if (c.CanStay(f, entry)) return true;
        foreach (var x in Headings.All)
            if (c.CanPass(f, entry, x)) return true;
        return false;
    }

    // The one-hop question the per-hop check asks: standing on `from` (having
    // come in across `entry`, null if unknown), may the mover step to the
    // 4-adjacent `to`, and stop there if `final`?
    public bool AllowsHop(TileCoord from, Heading? entry, TileCoord to, bool final)
    {
        if (Battlefields.Battlefields.EdgeToward(from, to) is not { } h) return true;
        var into = h.Opposite();
        if (Restricted(from) && !CanPass(from, entry, h)) return false;
        if (!CanEnter(to, into)) return false;
        if (final && Restricted(to) && !CanStay(to, into)) return false;
        return true;
    }

    // The edge of `tile` a unit came in across, from the tile it left.
    public static Heading? EntryEdge(TileCoord tile, TileCoord? cameFrom) =>
        cameFrom is { } from ? Battlefields.Battlefields.EdgeToward(tile, from) : null;
}
