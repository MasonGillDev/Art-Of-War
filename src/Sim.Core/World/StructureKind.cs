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
}
