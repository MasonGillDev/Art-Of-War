using Sim.Core.World;

namespace Sim.Core.Automation;

// Append-only enum (serialized in snapshots AND in durable intent JSON).
//
// THE GROWTH RULE (docs/m18-automation-engine-spec.md): every ActionKind
// maps 1:1 onto an EXISTING intent — an action atom is a parameter bag,
// never new sim semantics. A new gameplay verb gets its own intent (own
// milestone, own validation) FIRST; automation then gains the atom by
// adding a value here and one case in the server-side IntentFactory.
public enum ActionKind : byte
{
    MoveTo          = 1, // MoveIntent(UnitId → TargetTile)
    HaulTrip        = 2, // HaulIntent(UnitId, TargetTile=source, SecondTile=dest, Resource)
    LoadCargo       = 3, // LoadCargoIntent(UnitId, Resource)
    UnloadCargo     = 4, // UnloadCargoIntent(UnitId)
    Train           = 5, // TrainUnitIntent(UnitId, Role)
    Craft           = 6, // CraftEquipmentIntent(TargetTile=barracks, Resource=item)
    AssignWorkers   = 7, // AssignWorkersIntent(TargetTile=extractor, [UnitId])
    UnassignWorkers = 8, // UnassignWorkersIntent(TargetTile=extractor, [UnitId])
}

// One atomic action. Pure data — the server-side IntentFactory turns it into
// an ordinary intent at dispatch time, and THAT intent's Resolve does the
// real validation against the live world (docs/intent-validation.md).
// Unused fields stay at their defaults for the given Kind. Actions that name
// a unit may only name units the order has claimed (validated at Set time).
public readonly record struct ActionSpec(
    ActionKind Kind,
    int UnitId,
    TileCoord TargetTile,
    TileCoord SecondTile,
    Resource Resource,
    UnitRole Role)
{
    public static ActionSpec MoveTo(int unitId, TileCoord dest) =>
        new(ActionKind.MoveTo, unitId, dest, default, Resource.None, UnitRole.None);

    public static ActionSpec HaulTrip(int unitId, TileCoord source, TileCoord dest, Resource resource) =>
        new(ActionKind.HaulTrip, unitId, source, dest, resource, UnitRole.None);

    public static ActionSpec LoadCargo(int unitId, Resource resource) =>
        new(ActionKind.LoadCargo, unitId, default, default, resource, UnitRole.None);

    public static ActionSpec UnloadCargo(int unitId) =>
        new(ActionKind.UnloadCargo, unitId, default, default, Resource.None, UnitRole.None);

    public static ActionSpec Train(int unitId, UnitRole role) =>
        new(ActionKind.Train, unitId, default, default, Resource.None, role);

    public static ActionSpec Craft(TileCoord barracksTile, Resource item) =>
        new(ActionKind.Craft, 0, barracksTile, default, item, UnitRole.None);

    public static ActionSpec AssignWorkers(int unitId, TileCoord extractorTile) =>
        new(ActionKind.AssignWorkers, unitId, extractorTile, default, Resource.None, UnitRole.None);

    public static ActionSpec UnassignWorkers(int unitId, TileCoord extractorTile) =>
        new(ActionKind.UnassignWorkers, unitId, extractorTile, default, Resource.None, UnitRole.None);

    // True when this action kind names a unit in UnitId (and therefore must
    // name a claimed unit). Craft is the only structure-only atom today.
    public bool NamesUnit => Kind != ActionKind.Craft;
}
