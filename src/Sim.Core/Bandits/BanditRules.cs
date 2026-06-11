using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Core.Bandits;

// M16 — pure reads used by the spawn/despawn validation (and by the
// server-side driver to pre-filter candidates before submitting).
//
// PURE-READ WALL: nothing here writes. Both checks are derived from unit
// and structure positions at `now`, so the same intent validates
// identically live and on replay.
public static class BanditRules
{
    // Is `tile` inside ANY non-bandit faction's live vision? MUST mirror
    // View.VisibleTiles exactly (Euclidean disc, squared compare,
    // embarked units excluded, structure radius 0 = not a source) —
    // a divergence would let "dark" spawns land on a player's screen.
    // The equivalence is pinned by BanditSpawnTests.
    //
    // Direct source scan instead of unioning per-player sets: validating
    // one tile is O(sources), not O(sources × radius²).
    public static bool IsSeenByAnyPlayer(GameWorld world, TileCoord tile)
    {
        foreach (var u in world.Units.Values)
        {
            if (u.OwnerId == BanditConstants.OwnerId) continue;
            if (u.IsEmbarked) continue;   // passengers don't see out (View rule)
            if (WithinEuclidean(u.Position, tile, Sight.RadiusFor(u.Role))) return true;
        }
        foreach (var s in world.Structures.Values)
        {
            if (s.OwnerId == BanditConstants.OwnerId) continue;
            if (WithinEuclidean(s.At, tile, Sight.RadiusFor(s.Kind))) return true;
        }
        return false;
    }

    // Chebyshev distance from `tile` to the nearest non-bandit unit or
    // structure — ANY presence counts, embarked or not, vision source or
    // not (the MinSpawnDistance floor exists precisely because economic
    // structures cast no vision). int.MaxValue when the world is empty.
    public static int ChebyshevToNearestPlayerPresence(GameWorld world, TileCoord tile)
    {
        var best = int.MaxValue;
        foreach (var u in world.Units.Values)
        {
            if (u.OwnerId == BanditConstants.OwnerId) continue;
            best = Math.Min(best, Chebyshev(u.Position, tile));
        }
        foreach (var s in world.Structures.Values)
        {
            if (s.OwnerId == BanditConstants.OwnerId) continue;
            best = Math.Min(best, Chebyshev(s.At, tile));
        }
        return best;
    }

    private static bool WithinEuclidean(TileCoord src, TileCoord tile, int r)
    {
        if (r <= 0) return false;
        var dx = src.X - tile.X;
        var dy = src.Y - tile.Y;
        return dx * dx + dy * dy <= r * r;
    }

    private static int Chebyshev(TileCoord a, TileCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
