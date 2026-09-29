using Sim.Core.Battlefields;

namespace Sim.Core.Movement;

// The per-side unit cap (docs/structure-footprints.md, "The unit cap"; user,
// 2026-09-29: "only the amount of units that can stand on a tile can be on a
// tile"). A side (a player and its allies) may have at most as many units on a
// tile as the tile has subtiles that side may stand on: 16 on open ground, 12
// on a school tile, 16 of the owner's in a castle but only 5 attackers.
//
// PER SIDE, NOT TOTAL: an enemy counts against its own side, so it can always
// step onto a tile you have filled and fight.
//
// ALWAYS, NOT JUST IN BATTLE: an arrival that would overfill its side stops on
// the tile before (MoveArrivalEvent / GroupArrivalEvent), the planner routes
// round tiles it can see are full, and anything that makes a unit (a birth, a
// refugee, a landing) puts it on the nearest tile with room (RoomNear).
//
// Grid-combat worlds only, with the rest of the battlefield work. The old flat
// cap (MovementConstants.MaxUnitsPerTile) still applies everywhere.
//
// Pure reads. Capacity counts what a non-archer of the side may stand on: a
// tower's subtile is extra room only an archer could use, left out so a side
// of soldiers is never promised a place it can't take.
public static class TileCapacity
{
    public static bool Applies(GameWorld world) => CrossingRule.Applies(world);

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

    // `owner`'s side on `tile` now: its own units and its allies', off boats.
    public static int SideCount(GameWorld world, TileCoord tile, int owner)
    {
        var n = 0;
        foreach (var u in world.Units.Values)
            if (u.Position == tile && !u.IsEmbarked && Fortification.IsOwnOrAllied(world, owner, u.OwnerId)) n++;
        return n;
    }

    // Is there room on `tile` for `adding` more of `owner`'s side? Always, in a
    // world the cap doesn't apply to.
    public static bool HasRoom(GameWorld world, TileCoord tile, int owner, int adding = 1) =>
        !Applies(world) || SideCount(world, tile, owner) + adding <= For(world, tile, owner);

    // Where a new unit of `owner`'s actually goes: `at` if its side has room
    // there, else the nearest tile that does and that the unit could stand on
    // (land for feet: not water unless a bridge, not a wall; water for a boat),
    // nearest first (Manhattan), then north to south, west to east. `at` itself
    // if nothing within reach has room: better over the cap than lost.
    public static TileCoord RoomNear(GameWorld world, TileCoord at, int owner, Traversal trav = Traversal.Foot, int maxRadius = 8)
    {
        if (HasRoom(world, at, owner)) return at;
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
                    if (grid.InBounds(t) && CanStandOn(world, t, owner, trav) && HasRoom(world, t, owner)) ring.Add(t);
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
