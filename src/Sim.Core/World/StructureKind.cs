namespace Sim.Core.World;

// Append-only. Existing values keep their byte forever (snapshot + intent payload).
public enum StructureKind : byte
{
    Stockpile = 1,
    ConstructionSite = 2,
    Tower = 3,           // reserved for fog milestone — no impl yet
    Castle = 4,
    LumberCamp = 5,
    Quarry = 6,
    CopperMine = 7,  // M51 — the copper-ore mine (was Mine); docs/m51-ore-tiers-spec.md
    Farm = 8,
    House = 9, // M8 — breeding structure
    Dock = 10, // M12 — boat shipyard + embark/disembark seam
    School = 11, // training — flips a unit's UnitRole
    Barracks = 12, // military training + equipment crafting (storage)
    Lodge = 13, // M20 — intelligence structure; gates DispatchScoutIntent
    Canal = 14, // M21 — build job that floods a path of land into Water (docs/canals.md)
    Cache = 15, // M23 — unowned loot cache scattered in the fog (docs/loot-caches.md)
    Rubble = 16, // M24 — destroyed-structure remains; blocks placement, owner-sentinel -3 (docs/sieges-and-conquest.md)
    Wall = 17, // M26 — blocks movement for everyone; placed as a line via PlaceWallIntent (docs/walls-and-gates.md)
    Gate = 18, // M26 — blocks movement except owner + allies (docs/walls-and-gates.md)
    // Refining (docs/refining-structures.md) — the second hop of the supply chain.
    Smelter = 19,  // refiner: ore + wood (fuel) → a metal bar; the only Extractor with inputs
    Workshop = 20, // civil crafting storage: the Cart is forged here, not at the Barracks
    Smithy = 21,   // weapons crafting storage: swords / Bow / Shield; the Barracks now only trains
    Idol = 22,     // M38 — a statue in the fog; activate it for a timed circle of sight, then it crumbles (docs/scouting-secrets.md)
    BanditCamp = 23, // M39 — bandit-owned: a garrison, raiders on a schedule, a hoard of their takings (docs/bandit-camps.md)
    Bridge = 24,     // a deck across a straight canal tile: the way over a canal; boats pass under (docs/structure-footprints.md)
    IronMine = 25,   // M51 — mines an iron-ore vein; built with bronze (docs/m51-ore-tiers-spec.md)
    SteelMine = 26,  // M51 — mines a steel-ore vein; built with iron
}
