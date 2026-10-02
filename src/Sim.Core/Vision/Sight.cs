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
    // The sight radii, in tiles (= km). ONE place: every vision source,
    // player or bandit, unit or structure, reads its radius from here.
    public const int BaseUnitRadius = 3;     // 7×7 area
    public const int ScoutRadius    = 6;     // 13×13 — the role's whole point
    // M16 — raiders hunt by sight (the driver targets only what the party
    // can SEE — bandits get fog too). Scout-grade eyes make wandering
    // parties actually find things; their vision is never player-facing
    // (Reveal skips the faction).
    public const int BanditRadius   = ScoutRadius;
    public const int CastleRadius   = 5;
    public const int TowerRadius    = 7;     // the "pin vision" structure

    public static int RadiusFor(UnitRole role) => role switch
    {
        UnitRole.Scout      => ScoutRadius,
        UnitRole.Bandit     => BanditRadius,
        _                   => BaseUnitRadius,
    };

    // Extractor / Stockpile / ConstructionSite are NOT vision sources —
    // they're economic. Returns 0 for non-sources so callers can union
    // safely (Disc yields nothing and Within is false for r <= 0).
    public static int RadiusFor(StructureKind kind) => kind switch
    {
        StructureKind.Castle => CastleRadius,
        StructureKind.Tower  => TowerRadius,
        _                    => 0,
    };

    // THE SHAPE OF SIGHT — Euclidean disc, compared as squared distance
    // against r*r so the math stays integer-exact and deterministic.
    //
    // These two are the only definition of "which tiles a source sees".
    // Explored reveal (Reveal), live visibility (View.VisibleTiles /
    // View.Sees), bandit hunting (BanditRules / BanditDriver) and scout
    // reports (ScoutObservation) all go through them, so the disc can
    // never drift between the write path and the read paths. Change the
    // shape here and only here.

    // Is `tile` within sight radius `r` of `center`? False for r <= 0.
    public static bool Within(TileCoord center, TileCoord tile, int r)
    {
        if (r <= 0) return false;
        var dx = tile.X - center.X;
        var dy = tile.Y - center.Y;
        return dx * dx + dy * dy <= r * r;
    }

    // Every in-bounds tile within radius `r` of `center`, in canonical
    // (y, x) order — callers that build ordered output (scout sightings)
    // depend on that order. Yields nothing for r <= 0.
    public static IEnumerable<TileCoord> Disc(TileGrid grid, TileCoord center, int r)
    {
        if (r <= 0) yield break;
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
                    yield return new TileCoord(x, y);
            }
        }
    }

    // INVERTED PURE-READ WALL — this is the ONE write path for
    // GameWorld.Explored AND GameWorld.RememberedBiome. Called only from
    // deterministic events:
    //   * MoveArrivalEvent.Apply (after position update + road credit)
    //   * BuildCompleteEvent.Apply (vision structures becoming visible)
    //   * Genesis.Build (initial Castle + unit placements)
    //
    // M37/M38 — every caller that runs inside the sim follows its reveal with
    // AfterReveal: the explored-tiles gauge may have moved (docs/progression.md)
    // and the owner's eyes may have fallen on a charted secret
    // (docs/scouting-secrets.md). A new caller must do the same.
    //
    // Reveal the sight disc (Sight.Disc) of radius `r` around `center`
    // into player `playerId`'s explored set.
    //
    // M9: also writes RememberedBiome[playerId][tile] = BiomeAt(world, tile,
    // now, config) for each tile in the disc. This is the "last-seen biome"
    // record consulted by View.BuildPlayerView when the tile is explored-
    // but-not-visible. Genesis passes now=0; event-driven callers pass
    // sim.Now.
    //
    // No-op when r <= 0 (non-vision structure kinds return 0 from
    // RadiusFor, so callers can union safely).
    // What follows a reveal made inside the sim (see the note above Reveal).
    internal static void AfterReveal(Simulation sim, int playerId, TileCoord center, int r)
    {
        Sim.Core.Scouting.Charts.OnSight(sim, playerId, center, r);
        Sim.Core.Mining.Veins.OnSight(sim, playerId, center, r);   // M44 — seeing a mine teaches its vein
        Sim.Core.Progression.Progression.Check(sim, playerId);
    }

    internal static void Reveal(GameWorld world, int playerId, TileCoord center, int r, long now)
    {
        if (r <= 0) return;
        // M16 — bandits keep no remembered map. They hunt from LIVE sight
        // only (View.VisibleTiles works for any owner without Explored
        // rows), and skipping the write keeps snapshots lean: a faction
        // that wanders the whole map would otherwise drag an Explored set
        // the size of the world through every snapshot.
        if (playerId == Sim.Core.Bandits.BanditConstants.OwnerId) return;
        if (!world.Explored.TryGetValue(playerId, out var set))
        {
            set = new HashSet<TileCoord>();
            world.Explored[playerId] = set;
        }
        if (!world.RememberedBiome.TryGetValue(playerId, out var remembered))
        {
            remembered = new Dictionary<TileCoord, Biome>();
            world.RememberedBiome[playerId] = remembered;
        }
        var config = world.BiomeDegradationConfig;
        foreach (var tile in Disc(world.Grid, center, r))
        {
            set.Add(tile);
            // M9: refresh the last-seen biome.
            remembered[tile] = Sim.Core.Biomes.BiomeDegradation.BiomeAt(
                world, tile, now, config);
        }
    }
}
