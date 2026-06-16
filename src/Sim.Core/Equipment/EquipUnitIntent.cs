using Sim.Core.Combat;
using Sim.Core.World;

namespace Sim.Core.Equipment;

// Equip an item onto a unit (docs/equipment-model.md): consume 1 item
// from an owned storage on the unit's tile, add the catalog's buff to
// the unit. The modifiers are COPIED into the Buff instance here —
// snapshot-carried — so later catalog retunes never mutate this unit.
//
// Preconditions (re-checked at resolution time; gate order mirrors
// TrainUnitIntent):
//   * Unit exists, owned by PlayerId, not grouped / embarked, Idle.
//   * Item has an EquipmentCatalog spec and the unit's Role is in its
//     AllowedRoles (Sword → Soldier, Bow → Archer, Shield → both).
//   * BuffRules.CanAccept: under the slot cap, no duplicate Kind.
//   * Structure at unit.Position is a StorageStructure owned by
//     PlayerId holding >= 1 of the item (any owned storage — craft at
//     the Barracks, haul to a forward stockpile, equip at the front).
public sealed class EquipUnitIntent : Intent
{
    public int UnitId { get; }
    public Resource Item { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public EquipUnitIntent(int unitId, Resource item)
    {
        UnitId = unitId;
        Item = item;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        if (unit.GroupId is not null)
            return IntentOutcome.Reject($"unit {UnitId} is in a group");
        if (unit.IsEmbarked)
            return IntentOutcome.Reject($"unit {UnitId} is embarked");
        if (unit.Activity != Activity.Idle)
            return IntentOutcome.Reject($"unit {UnitId} is not Idle (current: {unit.Activity})");

        if (!EquipmentCatalog.TryGetSpec(Item, out var spec))
            return IntentOutcome.Reject($"{Item} is not an equippable item");
        if (!spec.AllowedRoles.Contains(unit.Role))
            return IntentOutcome.Reject(
                $"unit {UnitId} role {unit.Role} cannot equip {Item}");
        if (!BuffRules.CanAccept(unit, spec.BuffKind))
            return IntentOutcome.Reject(
                $"unit {UnitId} cannot accept buff '{spec.BuffKind}' " +
                $"(slots {unit.Buffs.Count}/{BuffRules.MaxBuffsPerUnit}, duplicate kinds rejected)");

        if (!world.Structures.TryGetValue(unit.Position, out var s) || s is not StorageStructure storage)
            return IntentOutcome.Reject(
                $"unit {UnitId} is not on a storage structure (at {unit.Position.X},{unit.Position.Y})");
        if (storage.OwnerId != PlayerId)
            return IntentOutcome.Reject(
                $"storage at {unit.Position.X},{unit.Position.Y} not owned by player {PlayerId}");
        if (storage.AmountOf(Item) < 1)
            return IntentOutcome.Reject(
                $"storage at {unit.Position.X},{unit.Position.Y} holds no {Item}");

        // Apply: consume the item, grant the buff, apply HealthModifier
        // to current Health (the Buff.cs apply-time rule — Shield's +10
        // raises Health now and is reversed at strip time).
        storage.Withdraw(Item, 1);
        unit.Buffs.Add(new Buff(spec.BuffKind, spec.PowerModifier, spec.HealthModifier, ExpiresAt: null,
            CargoModifier: spec.CargoModifier, MoveCostPercent: spec.MoveCostPercent));
        unit.Health += spec.HealthModifier;
        unit.BumpEpoch();

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"Equip(unit={UnitId} <- {Item})";
}
