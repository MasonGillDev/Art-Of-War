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
}

public static class Biomes
{
    // Sentinel for impassable. Pathfinding treats any tile with cost ==
    // Impassable as un-enterable.
    public const int Impassable = int.MaxValue;

    public static int MoveCost(Biome b) => b switch
    {
        Biome.Grassland => 10,
        Biome.Forest => 30,
        Biome.Hills => 35,
        Biome.Mountain => 45,
        // Water is passable-but-expensive — sidesteps the entire "trapped
        // player / pathfinding returns no route" class of generation bugs.
        // Sane large integer (not Impassable) so A* summing it over a long
        // crossing can't overflow. Real water mechanics (boats, swim, deep
        // water impassable) are a later design pass.
        Biome.Water => 250,
        // None = unset/sentinel. Treat as default Grassland-ish to avoid divide-
        // by-zero in early scenarios; flagged so a forgotten SetBiome call shows up.
        Biome.None => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(b), b, null),
    };

    public static Resource Resource(Biome b) => b switch
    {
        Biome.Grassland => World.Resource.Food,
        Biome.Forest => World.Resource.Wood,
        Biome.Hills => World.Resource.Ore,
        Biome.Mountain => World.Resource.Stone,
        Biome.Water => World.Resource.None,
        Biome.None => World.Resource.None,
        _ => throw new ArgumentOutOfRangeException(nameof(b), b, null),
    };
}
