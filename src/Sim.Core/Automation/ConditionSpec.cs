using Sim.Core.World;

namespace Sim.Core.Automation;

// Append-only enum (serialized in snapshots AND in durable intent JSON).
// Existing values keep their byte forever; new condition atoms get the next
// available byte. The comparison direction is part of the kind (StoreAtLeast
// vs StoreBelow) — no separate comparator field to mis-combine.
public enum ConditionKind : byte
{
    Always       = 1,
    StoreAtLeast = 2, // structure at SubjectTile holds >= Threshold of Resource
    StoreBelow   = 3, // structure at SubjectTile holds <  Threshold of Resource
    CargoFull    = 4, // unit SubjectUnitId cargo == capacity
    CargoEmpty   = 5, // unit SubjectUnitId cargo == 0
    UnitAtTile   = 6, // unit SubjectUnitId stands on SubjectTile
    ElapsedTicks = 7, // now - cursor.StepEnteredTick >= Threshold
}

// One atomic predicate over what the order's OWNER can see. Pure data —
// evaluation lives in the server-side driver (docs/automation-layers.md);
// Sim.Core only stores, validates, and round-trips it. All fields are
// integers/enums/coords (determinism contract §3.1); unused fields stay at
// their defaults for the given Kind.
//
// FOG CONTRACT (enforced by the evaluator, pinned here): a condition on a
// subject the owner cannot currently see does NOT evaluate true — automation
// can never react to fogged state.
public readonly record struct ConditionSpec(
    ConditionKind Kind,
    int SubjectUnitId,
    TileCoord SubjectTile,
    Resource Resource,
    long Threshold)
{
    public static ConditionSpec Always() =>
        new(ConditionKind.Always, 0, default, Resource.None, 0);

    public static ConditionSpec StoreAtLeast(TileCoord structureTile, Resource resource, long threshold) =>
        new(ConditionKind.StoreAtLeast, 0, structureTile, resource, threshold);

    public static ConditionSpec StoreBelow(TileCoord structureTile, Resource resource, long threshold) =>
        new(ConditionKind.StoreBelow, 0, structureTile, resource, threshold);

    public static ConditionSpec CargoFull(int unitId) =>
        new(ConditionKind.CargoFull, unitId, default, Resource.None, 0);

    public static ConditionSpec CargoEmpty(int unitId) =>
        new(ConditionKind.CargoEmpty, unitId, default, Resource.None, 0);

    public static ConditionSpec UnitAtTile(int unitId, TileCoord tile) =>
        new(ConditionKind.UnitAtTile, unitId, tile, Resource.None, 0);

    public static ConditionSpec ElapsedTicks(long ticks) =>
        new(ConditionKind.ElapsedTicks, 0, default, Resource.None, ticks);
}
