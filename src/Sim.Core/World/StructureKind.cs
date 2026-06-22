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
    Mine = 7,
    Farm = 8,
    House = 9, // M8 — breeding structure
    Dock = 10, // M12 — boat shipyard + embark/disembark seam
    School = 11, // training — flips a unit's UnitRole
    Barracks = 12, // military training + equipment crafting (storage)
    Lodge = 13, // M20 — intelligence structure; gates DispatchScoutIntent
    Canal = 14, // M21 — build job that floods a path of land into Water (docs/canals.md)
    Cache = 15, // M23 — unowned loot cache scattered in the fog (docs/loot-caches.md)
    Rubble = 16, // M24 — destroyed-structure remains; blocks placement, owner-sentinel -3 (docs/sieges-and-conquest.md)
}
