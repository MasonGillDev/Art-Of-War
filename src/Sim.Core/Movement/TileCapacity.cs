using Sim.Core.Battlefields;

namespace Sim.Core.Movement;

// The per-side unit cap (docs/structure-footprints.md, "The unit cap"; user,
// 2026-09-29: "only the amount of units that can stand on a tile can be on a
// tile"). A side (a player and its allies) may have at most as many units on a
// tile as the tile has subtiles that side may stand on: 16 on open ground, 12
// on a school tile, 14 in a castle (the keep's two subtiles are nobody's) for
// the owner and attackers alike, since anyone inside may climb its walls
// (2026-10-01; before that attackers had the gap and courtyard, 5).
//
// PER SIDE, NOT TOTAL: an enemy counts against its own side, so it can always
// step onto a tile you have filled and fight.
//
// STANDING, NOT PASSING (M43): the cap is about units that STAND on a tile. A unit walking
// through never waits on it (moving friends pass through each other); a walk that would
// STOP on a tile with no room for its side is re-aimed at the nearest free subtile
// (Walk), and anything that makes a unit (a birth, a refugee, a landing) puts it on the
// nearest tile with room (RoomNear).
//
// Pure reads. Capacity counts what a unit of the side may stand on; since
// 2026-10-01 a tower's subtile takes anyone, so it counts like a wall's.
public static class TileCapacity
{
    // How many of `owner`'s side may stand on `tile`. `knows` (planning): a
    // structure the player can't see counts as open ground (16), the same fog
    // contract as the crossing rule.
    public static int For(GameWorld world, TileCoord tile, int owner, Func<TileCoord, bool>? knows = null)
    {
        if (!world.Structures.TryGetValue(tile, out var s) || (knows is not null && s.OwnerId != owner && !knows(tile)))
            return Subtile.Count;
        var layer = Footprints.For(world, tile);
        var mover = new BoardMover(layer.Owner is { } o && Fortification.IsOwnOrAllied(world, owner, o), Archer: false);
        var n = 0;
        foreach (var c in Subtile.All())
            if (layer.CanStand(c, mover)) n++;
        return n;
    }

    // Who takes `owner`'s room on `tile` now: its own units, its allies' and every
    // neutral's (M42: only enemies share a subtile, so everyone who isn't hostile
    // stands on a subtile of their own out of the same 16), off boats, and only the
    // ones STANDING: a unit walking through takes no room (M43). An enemy never counts
    // against you: it can always step in and fight.
    public static int SideCount(GameWorld world, TileCoord tile, int owner)
    {
        var n = 0;
        foreach (var u in world.Units.Values)
            if (u.Position == tile && !u.IsEmbarked && !u.IsWalking && !world.Diplomacy.AreHostile(owner, u.OwnerId)) n++;
        return n;
    }

    // Is there room on `tile` for `adding` more of `owner`'s side?
    public static bool HasRoom(GameWorld world, TileCoord tile, int owner, int adding = 1) =>
        SideCount(world, tile, owner) + adding <= For(world, tile, owner);

    // Where a new unit of `owner`'s actually goes: `at` if its side has room
    // there, else the nearest tile that does and that the unit could stand on
    // (land for feet: not water unless a bridge, not a wall; water for a boat),
    // nearest first (Manhattan), then north to south, west to east. `at` itself
    // if nothing within reach has room: better over the cap than lost. `except`
    // is a tile not to use (M42: the tile a unit is being popped off).
    public static TileCoord RoomNear(GameWorld world, TileCoord at, int owner, Traversal trav = Traversal.Foot, int maxRadius = 8,
        TileCoord? except = null)
    {
        if (at != except && HasRoom(world, at, owner)) return at;
        var grid = world.Grid;
        for (var r = 1; r <= maxRadius; r++)
        {
            var ring = new List<TileCoord>();
            for (var dy = -r; dy <= r; dy++)
            {
                var dx = r - Math.Abs(dy);
                foreach (var x in dx == 0 ? new[] { at.X } : new[] { at.X - dx, at.X + dx })
                {
                    var t = new TileCoord(x, at.Y + dy);
                    if (grid.InBounds(t) && t != except && CanStandOn(world, t, owner, trav) && HasRoom(world, t, owner)) ring.Add(t);
                }
            }
            if (ring.Count > 0) return ring.OrderBy(t => t.Y).ThenBy(t => t.X).First();
        }
        return at;
    }

    private static bool CanStandOn(GameWorld world, TileCoord t, int owner, Traversal trav)
    {
        var water = world.Grid.BiomeAt(t) == Sim.Core.World.Biome.Water;
        if (trav == Traversal.Water) return water;
        if (water && !MovementCost.IsBridge(world, t)) return false;
        if (world.Grid.TerrainCost(t) >= Sim.Core.World.Biomes.Impassable) return false;
        return !Fortification.BlocksMover(world, t, owner);
    }
}
