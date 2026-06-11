namespace Sim.Core.World;

// M15 — extraction-claim rules (docs/extraction-claims.md). All PURE
// READS: validation, deterministic auto-selection, claimant lookup, and
// the in-band rollup. Mutation of claim lists happens only at the four
// audited writers (PlaceSiteIntent, BuildCompleteEvent transfer,
// Extractor.ArmIfDormant lazy fill, Snapshot restore); this class never
// writes.
public static class Claims
{
    // Validate an explicit claim list for a claiming kind placed at
    // `siteTile`. Returns null when valid, else the rejection reason
    // (callers surface it verbatim — resolution-time validation per
    // docs/intent-validation.md).
    public static string? Validate(
        GameWorld world, TileCoord siteTile, StructureSpec spec,
        IReadOnlyList<TileCoord> tiles, long now)
    {
        if (tiles.Count != spec.ClaimCount)
            return $"claim must be exactly {spec.ClaimCount} tiles (got {tiles.Count})";
        var seen = new HashSet<TileCoord>();
        foreach (var t in tiles)
        {
            // Explicit duplicate check — a hostile client can send
            // [t,t,t,...]; distinctness is part of the contract.
            if (!seen.Add(t))
                return $"duplicate claim tile {t.X},{t.Y}";
            var reason = ValidateOne(world, siteTile, spec, t, now);
            if (reason is not null) return reason;
        }
        return null;
    }

    // Deterministic auto-selection: candidates ordered by (Chebyshev
    // distance, y, x); first ClaimCount valid tiles win. Returns the
    // claim re-sorted to canonical (y, x) — so an auto-selected claim
    // and an identical hand-painted claim serialize byte-identically —
    // or null when the land can't support the claim (caller rejects:
    // no partial claims; the count IS the knob).
    public static List<TileCoord>? AutoSelect(
        GameWorld world, TileCoord siteTile, StructureSpec spec, long now)
    {
        var picked = new List<TileCoord>(spec.ClaimCount);
        for (var d = 1; d <= spec.ClaimRange && picked.Count < spec.ClaimCount; d++)
        {
            // One Chebyshev ring, swept in (y, x) order.
            for (var dy = -d; dy <= d && picked.Count < spec.ClaimCount; dy++)
            {
                for (var dx = -d; dx <= d && picked.Count < spec.ClaimCount; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != d) continue;
                    var t = new TileCoord(siteTile.X + dx, siteTile.Y + dy);
                    if (ValidateOne(world, siteTile, spec, t, now) is null)
                        picked.Add(t);
                }
            }
        }
        if (picked.Count < spec.ClaimCount) return null;
        picked.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        return picked;
    }

    // Who claims `tile`? Scans both carriers (finished extractors AND
    // pending construction sites — claims reserve at placement). Returns
    // the claimant's structure tile, or null. O(structures × claim size);
    // claim size ≤ 6 — same scaling shape the radius scan had. A per-tile
    // claim index is the future optimization if structure counts demand it.
    public static TileCoord? ClaimantAt(GameWorld world, TileCoord tile)
    {
        foreach (var s in world.Structures.Values)
        {
            switch (s)
            {
                case Extractor e when e.ClaimTiles.Contains(tile): return e.At;
                case ConstructionSite c when c.ClaimTiles.Contains(tile): return c.At;
            }
        }
        return null;
    }

    // Degrade amount applying to `tile` from its producing claimant.
    // MAX fold on purpose: the one-claimant-per-tile invariant makes
    // overlap structurally impossible, but a fold stays order-independent
    // even if that invariant were ever violated — no silent first-match
    // (architecture §4 rule 9). Sites never produce, so only extractors
    // contribute. PURE READ.
    public static int ClaimantDegradeAmount(GameWorld world, TileCoord tile)
    {
        var max = 0;
        foreach (var s in world.Structures.Values)
        {
            if (s is not Extractor e) continue;
            if (!e.TickArmed) continue;
            if (e.Spec.DegradeAmount <= 0) continue;
            if (!e.ClaimTiles.Contains(tile)) continue;
            if (e.Spec.DegradeAmount > max) max = e.Spec.DegradeAmount;
        }
        return max;
    }

    // How many of the extractor's claimed tiles are still in its required
    // biome band (derived BiomeAt — live catch-up math). Drives dormancy
    // ("the cut is exhausted" at zero) and the production taper. PURE READ.
    public static int InBandClaimCount(GameWorld world, Extractor extractor, long now)
    {
        var count = 0;
        foreach (var t in extractor.ClaimTiles)
        {
            var biome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(
                world, t, now, world.BiomeDegradationConfig);
            if (biome == extractor.Spec.RequiredBiome) count++;
        }
        return count;
    }

    private static string? ValidateOne(
        GameWorld world, TileCoord siteTile, StructureSpec spec, TileCoord t, long now)
    {
        if (!world.Grid.InBounds(t))
            return $"claim tile {t.X},{t.Y} out of bounds";
        if (t == siteTile)
            return $"claim tile {t.X},{t.Y} is the building tile";
        var dist = Math.Max(Math.Abs(t.X - siteTile.X), Math.Abs(t.Y - siteTile.Y));
        if (dist > spec.ClaimRange)
            return $"claim tile {t.X},{t.Y} outside range {spec.ClaimRange}";
        // Derived biome, not worldgen — a degraded tile is what it
        // currently is (same rule as site placement).
        var biome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(
            world, t, now, world.BiomeDegradationConfig);
        if (biome != spec.RequiredBiome)
            return $"claim tile {t.X},{t.Y} is {biome}, requires {spec.RequiredBiome}";
        // Full structural exclusion: claim tiles must be structure-free...
        if (world.Structures.ContainsKey(t))
            return $"claim tile {t.X},{t.Y} has a structure on it";
        // ...and unclaimed by anyone, any kind (one claimant per tile).
        if (ClaimantAt(world, t) is { } c)
            return $"claim tile {t.X},{t.Y} already claimed by structure at {c.X},{c.Y}";
        return null;
    }
}
