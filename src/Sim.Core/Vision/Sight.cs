namespace Sim.Core.Vision;

// Vision-source radius lookup. Drives both "explored" reveal (Phase B)
// and "live visibility" derivation (Phase C).
//
// Shape: Euclidean (circle). Compared via squared distance against r*r
// so the math stays integer-exact and deterministic — see the
// observation-independent pattern from M2 lazy decay.
//
// Phase A adds only the radius lookup. Reveal (write path) lands in
// Phase B; VisibleTiles (pure read) lands in Phase C.
public static class Sight
{
    // Base unit radius: 3 (7×7 area).
    // Scout: 6 (13×13) — the role's whole point.
    // Other roles: base.
    public static int RadiusFor(UnitRole role) => role switch
    {
        UnitRole.Scout      => 6,
        _                   => 3,
    };

    // Castle: 5. Tower: 7 (the "pin vision" structure). Extractor /
    // Stockpile / ConstructionSite are NOT vision sources — they're
    // economic. Returns 0 for non-sources so View can union safely.
    public static int RadiusFor(StructureKind kind) => kind switch
    {
        StructureKind.Castle => 5,
        StructureKind.Tower  => 7,
        _                    => 0,
    };

    // INVERTED PURE-READ WALL — this is the ONE write path for
    // GameWorld.Explored. Called only from deterministic events:
    //   * MoveArrivalEvent.Apply (after position update + road credit)
    //   * BuildCompleteEvent.Apply (vision structures becoming visible)
    //   * Genesis.Build (initial Castle + unit placements)
    //
    // Reveal a Euclidean disc of radius `r` around `center` into player
    // `playerId`'s explored set. Squared-distance comparison keeps the
    // math integer-exact and runtime-deterministic. Clamps to grid bounds.
    //
    // No-op when r <= 0 (non-vision structure kinds return 0 from
    // RadiusFor, so callers can union safely).
    internal static void Reveal(GameWorld world, int playerId, TileCoord center, int r)
    {
        if (r <= 0) return;
        if (!world.Explored.TryGetValue(playerId, out var set))
        {
            set = new HashSet<TileCoord>();
            world.Explored[playerId] = set;
        }
        var grid = world.Grid;
        var rSquared = r * r;
        var xLo = Math.Max(0, center.X - r);
        var xHi = Math.Min(grid.Width  - 1, center.X + r);
        var yLo = Math.Max(0, center.Y - r);
        var yHi = Math.Min(grid.Height - 1, center.Y + r);
        for (var y = yLo; y <= yHi; y++)
        {
            var dy = y - center.Y;
            var dy2 = dy * dy;
            for (var x = xLo; x <= xHi; x++)
            {
                var dx = x - center.X;
                if (dx * dx + dy2 <= rSquared)
                    set.Add(new TileCoord(x, y));
            }
        }
    }
}
