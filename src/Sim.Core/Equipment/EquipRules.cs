using Sim.Core.Combat;
using Sim.Core.World;

namespace Sim.Core.Equipment;

// The per-unit EQUIP implementation, extracted so the two roads to an armed
// soldier share one body of code — the same reason TrainingRules and
// WorkAssignment's assign helpers exist. EquipUnitIntent applies this to a unit
// already standing in the storehouse; GoalRules applies it to one that has just
// walked there (docs/goal-shaped-intents.md).
//
// Getting this wrong twice would be easy and quiet: the buff modifiers are
// COPIED into the instance (so a later catalog retune never mutates an existing
// unit) and the HealthModifier is applied to current Health at the same moment.
// A second copy that forgot either would produce units that are subtly wrong
// for the rest of the game.
public static class EquipRules
{
    // Can this body wear this item AT ALL, ignoring where it is standing and
    // what any storehouse holds? Returns the reason it cannot, or null.
    //
    // Everything here is IMPOSSIBLE-FOREVER for this unit-and-item pairing, so
    // the errand rejects up front and dissolves if it becomes true mid-walk.
    // Scarcity is deliberately NOT in this list: an item nobody has yet is a
    // precondition, and preconditions wait.
    public static string? Blocker(GameWorld world, Unit unit, Resource item)
    {
        if (Sim.Core.Groups.GroupRules.UnderCommand(world, unit)) return "under its group's command";
        return BodyBlocker(unit, item);
    }

    // The part of Blocker about the body and the item alone (no world): what
    // Grant checks for a unit composed before tick 0, which is in no group.
    private static string? BodyBlocker(Unit unit, Resource item)
    {
        if (unit.IsEmbarked) return "embarked";
        if (!EquipmentCatalog.TryGetSpec(item, out var spec))
            return $"{item} is not an equippable item";
        if (!spec.AllowedRoles.Contains(unit.Role))
            return $"a {unit.Role} cannot equip {item}";
        // M51 — one item per slot; a better one in the same slot swaps in
        // (it takes the old one's place, so the slot count is unchanged).
        if (WornInSlot(unit, spec) is { } worn)
            return spec.Rank > worn.Spec.Rank ? null
                : $"already carries {worn.Spec.Item} (a {spec.SlotName} no worse than {item})";
        if (!BuffRules.CanAccept(unit, spec.BuffKind))
            return $"no free slot for {item} " +
                   $"(slots {unit.Buffs.Count}/{BuffRules.MaxBuffsPerUnit}, no duplicate kinds)";
        return null;
    }

    // The worn item in the slot `spec` fills, if any.
    private static (Buff Buff, EquipmentSpec Spec)? WornInSlot(Unit unit, EquipmentSpec spec)
    {
        foreach (var b in unit.Buffs)
            if (EquipmentCatalog.TryGetByKind(b.Kind, out var wornSpec) && wornSpec.SlotName == spec.SlotName)
                return (b, wornSpec);
        return null;
    }

    // Take a worn item off: its buff goes, its health is reversed (clamped to 1,
    // the strip rule in Equipment.DropEquipmentToGround), and the item is returned.
    private static Resource TakeOff(Unit unit, (Buff Buff, EquipmentSpec Spec) worn)
    {
        unit.Buffs.Remove(worn.Buff);
        unit.Health -= worn.Buff.HealthModifier;
        if (unit.Health < 1) unit.Health = 1;
        return worn.Spec.Item;
    }

    // The storehouse under this unit, if it is one of the player's own. Null
    // otherwise. Ownership of the STORE matters (you cannot raid an ally's
    // armoury); ownership of the tile does not.
    public static StorageStructure? StoreUnder(GameWorld world, Unit unit)
    {
        if (!world.Structures.TryGetValue(unit.Position, out var s)) return null;
        if (s is not StorageStructure storage) return null;
        if (storage.OwnerId != unit.OwnerId) return null;
        return storage;
    }

    // Take one item off the shelf and put it on the unit. False when the shelf
    // is bare — which is a WAIT, not a failure, and the caller decides that.
    public static bool TryEquip(GameWorld world, Unit unit, Resource item)
    {
        if (unit.Activity != Activity.Idle && unit.Activity != Activity.Waiting) return false;
        if (Blocker(world, unit, item) is not null) return false;
        if (!EquipmentCatalog.TryGetSpec(item, out var spec)) return false;

        var storage = StoreUnder(world, unit);
        if (storage is null || storage.AmountOf(item) < 1) return false;

        storage.Withdraw(item, 1);
        // M51 — an upgrade: the old item goes back on the shelf the new one
        // came off (one out, one in, so it always fits).
        if (WornInSlot(unit, spec) is { } worn)
            storage.Deposit(TakeOff(unit, worn), 1);
        Wear(unit, spec);
        unit.TrySetActivity(Activity.Idle);
        unit.BumpEpoch();
        return true;
    }

    // Put an item on a unit that is being CREATED, with no shelf to take it from:
    // the battle sandbox's composed units (docs/battle-sandbox.md). Same rules as
    // the storehouse road (Blocker), same buff instance, same health rule. Returns
    // why it can't, or null. Not an intent and never called in a running game: the
    // unit must have just been added, before tick 0.
    public static string? Grant(Unit unit, Resource item)
    {
        if (BodyBlocker(unit, item) is { } why) return why;
        var spec = EquipmentCatalog.Spec(item);
        if (WornInSlot(unit, spec) is { } worn) TakeOff(unit, worn);   // composing: no shelf to return it to
        Wear(unit, spec);
        return null;
    }

    private static void Wear(Unit unit, EquipmentSpec spec)
    {
        // Modifiers are COPIED into the instance — snapshot-carried — so a later
        // catalog retune never reaches back into a unit already carrying this.
        unit.Buffs.Add(new Buff(spec.BuffKind, spec.PowerModifier, spec.HealthModifier,
            ExpiresAt: null,
            CargoModifier: spec.CargoModifier, MoveCostPercent: spec.MoveCostPercent));
        // The apply-time health rule (Buff.cs): a Shield's +10 raises Health now
        // and is reversed when the item is stripped.
        unit.Health += spec.HealthModifier;
    }

    // A delivery landed in this storehouse. Anyone standing in it waiting for
    // exactly this item is armed now.
    //
    // Rides the existing deposit path rather than adding a sweep or a timer:
    // the only thing that can turn "no sword here" into "a sword here" is a
    // deposit, so the deposit is the wake-up. Canonical id order so a single
    // sword among several hopefuls goes to a determined one.
    public static void OnStoreSupplied(Simulation sim, StorageStructure store, Resource item)
    {
        if (item == Resource.None) return;

        List<Unit>? waiting = null;
        foreach (var u in sim.World.Units.Values)
        {
            if (u.Activity != Activity.Waiting) continue;
            if (u.Goal is not { Kind: GoalKind.Equip } g) continue;
            if (g.TargetTile != store.At || g.Arg != (int)item) continue;
            (waiting ??= new List<Unit>()).Add(u);
        }
        if (waiting is null) return;

        waiting.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        foreach (var u in waiting)
        {
            if (store.AmountOf(item) < 1) break;   // that was the last one
            if (TryEquip(sim.World, u, item)) Sim.Core.Intents.GoalRules.Complete(u);
        }
    }
}
