namespace Sim.Core.Hauling;

// M40 — salvage (docs/salvage.md): a haul job that carries EVERYTHING from a
// secret or a pile that isn't anyone's (a cache, a ruin's spill, a burned
// camp's hoard) to one of the player's buildings.
//
// KNOWLEDGE COMES FROM PRESENCE. A player may name a source only if their chart
// says something is there (a returned scout reported it), or they can see it
// right now. The haulers walk out on that knowledge; the job ends when one of
// them arrives and finds nothing left: the same moment their eyes strike the
// chart. Nothing about the source's contents is consulted before that.
public static class Salvage
{
    // Why `playerId` cannot salvage at `tile`, or null.
    public static string? Blocker(GameWorld world, int playerId, TileCoord tile)
    {
        if (!world.Grid.InBounds(tile)) return $"{tile.X},{tile.Y} is out of bounds";

        // A building that is somebody's is theirs: salvage takes only what is
        // nobody's (a cache) or loose on the ground.
        var structure = world.Structures.TryGetValue(tile, out var s) ? s : null;
        if (structure is not null && structure is not Cache && structure is not Rubble)
            return $"the {structure.Kind} at {tile.X},{tile.Y} is not salvage";

        // Charted: a returned scout said something is there.
        if (Sim.Core.Scouting.Charts.Of(world, playerId) is { } chart
            && chart.TryGetValue(tile, out var entry)
            && entry.State == Sim.Core.Scouting.ChartState.Known)
            return null;

        // Seen: in live sight right now, and there is something to take.
        if (Sim.Core.Vision.View.Sees(world, playerId, tile)
            && (structure is Cache
                || (world.GroundResources.TryGetValue(tile, out var pile) && pile.Count > 0)))
            return null;

        return $"nothing you know of to salvage at {tile.X},{tile.Y} (chart it, or see it)";
    }

    // Take everything that fits from the cache and the ground pile at `tile`
    // into `unit`'s cargo, resource by resource in canonical order. Returns
    // how much was taken. Cache first, then the pile.
    internal static int TakeAll(Simulation sim, Unit unit, TileCoord tile)
    {
        var world = sim.World;
        var space = unit.CargoCapacity - unit.CargoAmount;
        var took = 0;

        if (world.Structures.TryGetValue(tile, out var s) && s is Cache cache)
        {
            foreach (var r in new List<Resource>(cache.Holdings.Keys))
            {
                if (took >= space) break;
                var n = cache.Withdraw(r, space - took);
                if (n <= 0) continue;
                unit.Cargo.Add(r, n);
                took += n;
            }
            Sim.Core.Caches.CacheLooting.RemoveIfEmptied(sim, cache);
        }

        if (took < space && world.GroundResources.TryGetValue(tile, out var pile))
        {
            foreach (var r in new List<Resource>(pile.Keys))
            {
                if (took >= space) break;
                var n = Math.Min(pile[r], space - took);
                if (n <= 0) continue;
                var left = pile[r] - n;
                if (left <= 0) pile.Remove(r); else pile[r] = left;
                unit.Cargo.Add(r, n);
                took += n;
            }
            if (pile.Count == 0) world.GroundResources.Remove(tile);
        }
        return took;
    }
}
