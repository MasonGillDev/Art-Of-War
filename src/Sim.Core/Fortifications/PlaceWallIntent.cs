using System.Text.Json.Serialization;

namespace Sim.Core.Fortifications;

// M26 — place a wall LINE: one intent carries the ordered path, validated
// atomically (fail-clean, mutates nothing on reject), then expands into N
// INDEPENDENT per-tile ConstructionSites (docs/walls-and-gates.md). This is
// deliberately NOT the canal shape: a canal is one site whose completion
// floods the whole path at once (forced by its extend-from-water ordering
// constraint); wall tiles are independently valid, each segment hauls its
// own materials, completes on its own schedule, and stands as its own Wall
// structure with its own Health. A half-built line protects the finished
// segments already — and exposes the unfinished ones (site HP 25).
//
// No wall-specific reservation system: the N sites occupy their tiles the
// moment this intent applies, so a competing placement rejects via the
// ordinary "tile already has a structure" check.
public sealed class PlaceWallIntent : Intent
{
    // The ordered line to build. 4-connected so the gesture is a real
    // wall line, not a scatter of segments (a lone segment is a path of 1).
    public List<TileCoord> Path { get; }

    // Same sanity cap as PlaceCanalIntent.MaxLength.
    public const int MaxLength = 64;

    [JsonConstructor]
    public PlaceWallIntent(List<TileCoord> path) { Path = path; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;

        if (Path is null || Path.Count == 0)
            return IntentOutcome.Reject("wall path is empty");
        if (Path.Count > MaxLength)
            return IntentOutcome.Reject($"wall path too long ({Path.Count} > {MaxLength})");

        // Per-tile eligibility + distinctness, ALL validated before any site
        // is placed — a wall line with a silent hole (one mid-line tile
        // claimed or occupied) is worse than a clean rejection.
        var seen = new HashSet<TileCoord>();
        foreach (var t in Path)
        {
            if (!seen.Add(t))
                return IntentOutcome.Reject($"duplicate wall tile {t.X},{t.Y}");
            var reason = TileEligible(world, t, sim.Now);
            if (reason is not null) return IntentOutcome.Reject(reason);
        }

        // Connectivity: a 4-connected chain (no water-root requirement —
        // that constraint was the canal's, not ours).
        for (var i = 1; i < Path.Count; i++)
            if (!Is4Adjacent(Path[i], Path[i - 1]))
                return IntentOutcome.Reject(
                    $"wall tile {Path[i].X},{Path[i].Y} is not adjacent to the previous tile " +
                    $"{Path[i - 1].X},{Path[i - 1].Y}");

        // Expand: one ordinary ConstructionSite per segment, each priced at
        // the per-tile catalog numbers (no scaling — N sites IS the
        // multiplication). Materials haul and builders gather per segment.
        var god = Sim.Core.Logistics.Construction.IsGodBuild(world, PlayerId);
        foreach (var t in Path)
        {
            // Each segment faces away from the owner's castle: its outer side,
            // which sets how its line turns at a corner (docs/structure-footprints.md).
            var site = world.AddStructure(new ConstructionSite(t, StructureKind.Wall)
            {
                OwnerId = PlayerId,
                Facing = Sim.Core.Battlefields.Footprints.DefaultFacing(world, StructureKind.Wall, PlayerId, t),
            });
            // God mode (docs/god-mode.md): each segment stands as it is placed.
            if (god) Sim.Core.Logistics.Construction.Complete(sim, site);
        }
        return IntentOutcome.Applied;
    }

    // A wall segment may stand iff the tile is in bounds, land (Water/None
    // rejected; Mountain ALLOWED — unlike a canal you build ON the rock,
    // you don't dig through it: fortifying a mountain pass is the point),
    // structure-free, claim-free, and not promised to an in-flight canal.
    // Uses the DERIVED biome, same rule as site placement and canals.
    private static string? TileEligible(GameWorld world, TileCoord t, long now)
    {
        if (!world.Grid.InBounds(t))
            return $"wall tile {t.X},{t.Y} out of bounds";
        var biome = BiomeDegradation.BiomeAt(world, t, now, world.BiomeDegradationConfig);
        if (biome == Biome.Water)
            return $"wall tile {t.X},{t.Y} is Water — walls need land";
        if (biome == Biome.None)
            return $"wall tile {t.X},{t.Y} has no biome";
        if (world.Structures.ContainsKey(t))
            return $"wall tile {t.X},{t.Y} has a structure on it";
        if (Claims.ClaimantAt(world, t) is { } c)
            return $"wall tile {t.X},{t.Y} is claimed by the structure at {c.X},{c.Y}";
        if (CanalReservation.IsReserved(world, t))
            return $"wall tile {t.X},{t.Y} is reserved by a canal under construction";
        return null;
    }

    private static bool Is4Adjacent(TileCoord a, TileCoord b)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        return (dx == 1 && dy == 0) || (dx == 0 && dy == 1);
    }

    public override string Describe() =>
        $"PlaceWall(len={Path?.Count ?? 0}" +
        (Path is { Count: > 0 } ? $" @ {Path[0].X},{Path[0].Y}" : "") + ")";
}
