using Sim.Core.World;

namespace Sim.Core.Movement;

// M12 — water-only cost table for Traversal.Water units (boats).
//
// Water tile is the cheapest cost in the game (lower than Grassland's
// 10 — the cheapest foot biome) so a boat is unambiguously faster than
// any overland route per tile. The inequality is pinned in
// BoatsTests.Boat_FasterThanAnyFootBiome.
//
// Every land biome returns Biomes.Impassable. Pathfinding's A* treats
// Impassable as un-enterable, so a boat physically cannot path onto
// land. MoveBoatIntent (Phase D) maps "no path → reject" with this as
// the mechanism.
//
// Roads do not apply on water; this function reads only the biome.
public static class BoatMovementCost
{
    public const int WaterCost = 6;

    public static int CostFor(Biome b) => b switch
    {
        Biome.Water => WaterCost,
        // Every land biome (and None) is impassable for boats.
        _ => Sim.Core.World.Biomes.Impassable,
    };
}
