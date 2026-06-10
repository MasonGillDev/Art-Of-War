namespace Sim.Core.World;

// Append-only. Existing values keep their byte forever (snapshot + intent payload).
public enum Resource : byte
{
    None = 0,
    Wood = 1,
    Stone = 2,
    Ore = 3,
    Food = 4,
    // Equipment items — fungible, stateless; ride the existing storage /
    // haul / ground-pile machinery (docs/equipment-model.md).
    Sword = 5,
    Bow = 6,
    Shield = 7,
}
