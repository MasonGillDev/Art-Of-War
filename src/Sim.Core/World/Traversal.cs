namespace Sim.Core.World;

// M12 — per-unit movement domain. Foot units use Biomes.MoveCost (the
// existing tile-property model); Water units use BoatMovementCost (water
// cheap, every land biome Impassable).
//
// docs/movement-cost.md explicitly listed "per-unit speed nerf" as
// out-of-scope; M12 deliberately breaks that assumption for a different
// reason: a per-unit movement DOMAIN (the same tile is fundamentally
// passable/expensive vs. cheap/exclusive depending on the carrier),
// not a per-unit speed multiplier. See docs/boats.md §"Why a per-unit
// Traversal enum — not a tile-property override".
//
// APPEND-ONLY (serialized).
public enum Traversal : byte
{
    Foot = 0,
    Water = 1,
}
