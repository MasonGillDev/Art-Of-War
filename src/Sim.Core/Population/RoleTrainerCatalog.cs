using Sim.Core.World;

namespace Sim.Core.Population;

// Which structure trains which role (docs/military-training.md).
// Civilian roles → School; military roles → Barracks; Boat → none
// (dock-produced, never trained from a citizen).
//
// Exhaustive switch, no silent default: an unmapped role is a bug the
// moment a new UnitRole value lands, and it should throw at the first
// training attempt rather than quietly routing to the School.
public static class RoleTrainerCatalog
{
    public static StructureKind? TrainerFor(UnitRole role) => role switch
    {
        UnitRole.None       => StructureKind.School,
        UnitRole.Builder    => StructureKind.School,
        UnitRole.Farmer     => StructureKind.School,
        UnitRole.Miner      => StructureKind.School,
        UnitRole.Lumberjack => StructureKind.School,
        UnitRole.Quarryman  => StructureKind.School,
        UnitRole.Hauler     => StructureKind.School,
        UnitRole.Scout      => StructureKind.School,
        UnitRole.Soldier    => StructureKind.Barracks,
        UnitRole.Archer     => StructureKind.Barracks,
        UnitRole.Boat       => null,
        _ => throw new InvalidOperationException(
            $"RoleTrainerCatalog has no trainer mapping for {role} — add a row when a new role lands."),
    };
}
