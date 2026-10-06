namespace Sim.Core.World;

// Append-only. Existing values keep their byte forever (snapshot + intent payload).
public enum Resource : byte
{
    None = 0,
    Wood = 1,
    Stone = 2,
    CopperOre = 3,   // M51 — the common ore (was Ore); docs/m51-ore-tiers-spec.md
    Food = 4,
    // Equipment items — fungible, stateless; ride the existing storage /
    // haul / ground-pile machinery (docs/equipment-model.md).
    BronzeSword = 5, // M51 — the first sword (was Sword)
    Bow = 6,
    Shield = 7,
    // M-cart — a hauler's cart: equipment that trades move speed for carry
    // capacity. Same fungible-item machinery as the weapons. See docs/cart.md.
    Cart = 8,
    // Refining (docs/refining-structures.md) — the first INTERMEDIATE good:
    // smelted from copper ore + wood at a Smelter, consumed by the Smithy's
    // bronze sword. Fungible and stateless like every other Resource.
    Bronze = 9,      // M51 — was Iron
    // M51 — the ore ladder (docs/m51-ore-tiers-spec.md): iron and steel, each an
    // ore from a farther vein, a bar smelted from it, and a stronger sword.
    IronOre = 10,
    Iron = 11,
    IronSword = 12,
    SteelOre = 13,
    Steel = 14,
    SteelSword = 15,
}
