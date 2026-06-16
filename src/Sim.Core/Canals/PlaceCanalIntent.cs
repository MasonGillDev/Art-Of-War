using System.Text.Json.Serialization;

namespace Sim.Core.Canals;

// M21 — dig a canal: convert an ordered PATH of land tiles into Water as a
// single, expensive build job (docs/canals.md). The whole path is validated
// up front at resolution time (fail-clean, mutates nothing on reject):
//
//   * It must EXTEND FROM WATER — path[0] is 4-adjacent to an existing Water
//     tile, and each later tile is 4-adjacent to its predecessor (which will
//     itself become water). The finished canal is therefore a connected,
//     boat-navigable waterway rooted at a real water source: you REDIRECT
//     water from where it already is, you don't conjure isolated puddles.
//   * Every tile must be diggable land (not Water, not Mountain rock, in
//     bounds) and free of any structure, extraction claim, or other canal
//     reservation.
//
// On success a SINGLE canal ConstructionSite is placed at path[0], with cost
// and build time scaled by the path length (the per-tile catalog numbers ×
// count). Materials haul to the anchor and builders gather there exactly like
// any other build; on completion BuildCompleteEvent floods the whole path and
// irrigates the surrounding land — no resulting structure remains.
public sealed class PlaceCanalIntent : Intent
{
    // The ordered route to dig, from the water source outward.
    public List<TileCoord> Path { get; }

    // A sane upper bound so one intent can't price or iterate unboundedly.
    public const int MaxLength = 64;

    [JsonConstructor]
    public PlaceCanalIntent(List<TileCoord> path) { Path = path; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;

        if (Path is null || Path.Count == 0)
            return IntentOutcome.Reject("canal path is empty");
        if (Path.Count > MaxLength)
            return IntentOutcome.Reject($"canal path too long ({Path.Count} > {MaxLength})");

        // Per-tile eligibility + distinctness (a hostile client can send
        // [t,t,...]; distinctness is part of the contract, as with claims).
        var seen = new HashSet<TileCoord>();
        foreach (var t in Path)
        {
            if (!seen.Add(t))
                return IntentOutcome.Reject($"duplicate canal tile {t.X},{t.Y}");
            var reason = TileEligible(world, t, sim.Now);
            if (reason is not null) return IntentOutcome.Reject(reason);
        }

        // Connectivity: rooted at existing water, then a 4-connected chain.
        if (!HasAdjacentWater(world, Path[0]))
            return IntentOutcome.Reject(
                $"canal must start next to water; {Path[0].X},{Path[0].Y} has no adjacent Water");
        for (var i = 1; i < Path.Count; i++)
            if (!Is4Adjacent(Path[i], Path[i - 1]))
                return IntentOutcome.Reject(
                    $"canal tile {Path[i].X},{Path[i].Y} is not adjacent to the previous tile " +
                    $"{Path[i - 1].X},{Path[i - 1].Y}");

        var site = new ConstructionSite(Path[0], StructureKind.Canal, Path)
        {
            OwnerId = PlayerId,
        };
        world.AddStructure(site);
        return IntentOutcome.Applied;
    }

    // A tile may be dug into a canal iff it is in bounds, not already Water,
    // a diggable biome (Mountain rock is excluded), free of any structure,
    // free of any extraction claim, and not reserved by another in-flight
    // canal. Uses the DERIVED biome (a degraded tile is what it currently is —
    // same rule as site placement and claims).
    private static string? TileEligible(GameWorld world, TileCoord t, long now)
    {
        if (!world.Grid.InBounds(t))
            return $"canal tile {t.X},{t.Y} out of bounds";
        var biome = BiomeDegradation.BiomeAt(world, t, now, world.BiomeDegradationConfig);
        if (biome == Biome.Water)
            return $"canal tile {t.X},{t.Y} is already Water";
        if (biome == Biome.Mountain)
            return $"canal tile {t.X},{t.Y} is Mountain — too solid to dig a canal through";
        if (biome == Biome.None)
            return $"canal tile {t.X},{t.Y} has no biome";
        if (world.Structures.ContainsKey(t))
            return $"canal tile {t.X},{t.Y} has a structure on it";
        if (Claims.ClaimantAt(world, t) is { } c)
            return $"canal tile {t.X},{t.Y} is claimed by the structure at {c.X},{c.Y}";
        if (CanalReservation.IsReserved(world, t))
            return $"canal tile {t.X},{t.Y} is already part of another canal under construction";
        return null;
    }

    private static bool HasAdjacentWater(GameWorld world, TileCoord t)
    {
        foreach (var n in Neighbors4(t))
            if (world.Grid.InBounds(n) && world.Grid.BiomeAt(n) == Biome.Water)
                return true;
        return false;
    }

    private static IEnumerable<TileCoord> Neighbors4(TileCoord t)
    {
        yield return new TileCoord(t.X, t.Y - 1);
        yield return new TileCoord(t.X + 1, t.Y);
        yield return new TileCoord(t.X, t.Y + 1);
        yield return new TileCoord(t.X - 1, t.Y);
    }

    private static bool Is4Adjacent(TileCoord a, TileCoord b)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        return (dx == 1 && dy == 0) || (dx == 0 && dy == 1);
    }

    public override string Describe() =>
        $"PlaceCanal(len={Path?.Count ?? 0}" +
        (Path is { Count: > 0 } ? $" @ {Path[0].X},{Path[0].Y}" : "") + ")";
}
