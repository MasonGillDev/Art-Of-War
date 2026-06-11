namespace Sim.Core.World;

// Append-only enum (serialized). Biome is the tile's identity — movement cost
// and what an extractor on this tile produces both derive from it.
public enum Biome : byte
{
    None = 0,
    Grassland = 1,
    Forest = 2,
    Hills = 3,
    Mountain = 4,
    Water = 5,
    Desert = 6,
}

public static class Biomes
{
    // Sentinel for impassable. Pathfinding treats any tile with cost ==
    // Impassable as un-enterable.
    public const int Impassable = int.MaxValue;

    // SCALE: 1 tile ≈ 1 km; costs are game-minutes to cross it at a
    // SUSTAINED march pace (~2 km/h on open ground — rest, camps, and
    // terrain included), not a sprint. Crossing the default 256-tile map
    // on grassland ≈ 5.3 game-days — an expedition that sits between a
    // Dock build (10d) and the war-declaration delay (30d), so distance
    // is a strategic cost, not a free action. A maxed road (−66%) brings
    // grassland back to ~6 km/h — infrastructure IS the speed upgrade.
    // Boats (BoatMovementCost.WaterCost = 6) are 5× open-ground pace.
    public static int MoveCost(Biome b) => b switch
    {
        Biome.Grassland => 30,
        Biome.Forest => 90,
        Biome.Hills => 75,
        Biome.Mountain => 135,
        // Water is passable-but-expensive — sidesteps the entire "trapped
        // player / pathfinding returns no route" class of generation bugs.
        // Sane large integer (not Impassable) so A* summing it over a long
        // crossing can't overflow. Real water mechanics live on boats
        // (BoatMovementCost); wading stays a desperation move.
        Biome.Water => 750,
        // Slower than Hills, faster than Mountain. No resources extractable
        // here — no StructureCatalog entry has RequiredBiome = Desert.
        Biome.Desert => 120,
        // None = unset/sentinel. Treat as default Grassland-ish to avoid divide-
        // by-zero in early scenarios; flagged so a forgotten SetBiome call shows up.
        Biome.None => 30,
        _ => throw new ArgumentOutOfRangeException(nameof(b), b, null),
    };

    public static Resource Resource(Biome b) => b switch
    {
        Biome.Grassland => World.Resource.Food,
        Biome.Forest => World.Resource.Wood,
        Biome.Hills => World.Resource.Ore,
        Biome.Mountain => World.Resource.Stone,
        Biome.Water => World.Resource.None,
        Biome.Desert => World.Resource.None,
        Biome.None => World.Resource.None,
        _ => throw new ArgumentOutOfRangeException(nameof(b), b, null),
    };
}
