using Sim.Core.Combat;
using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Equipment;

// Equip an item onto a unit (docs/equipment-model.md): consume 1 item
// from an owned storage, add the catalog's buff to the unit.
//
// GOAL-SHAPED. "Arm him with a sword" is the decision; walking to the armoury
// is not, and it used to be the player's job — march him over, wait out the
// trip, find him again, equip. Name a StoreTile and the sim does the marching.
// Omit it and the pre-errand contract holds exactly: the store under his feet.
//
// AND IT WAITS. If the shelf is bare on arrival he stands there as
// "waiting: a sword" until a delivery brings one, because scarcity is a
// precondition and preconditions wait (docs/goal-shaped-intents.md). That wait
// has no deadline — unlike a breeding pair, nobody ages out of being able to
// hold a sword — so it ends when the item arrives or when the player
// countermands with a move. Visible, not silent, which is the whole contract.
//
// Rejections are impossible-forever only:
//   * Unit missing, not yours, grouped, embarked, not Idle.
//   * Item has no EquipmentCatalog spec, or the unit's Role is not in its
//     AllowedRoles (Sword -> Soldier, Bow -> Archer, Shield -> both).
//   * BuffRules.CanAccept: slot cap reached, or a duplicate Kind.
//   * No storehouse of yours at the named tile.
// Notably NOT a rejection: that storehouse being empty of the item.
public sealed class EquipUnitIntent : Intent
{
    public int UnitId { get; }
    public Resource Item { get; }

    // Which storehouse to arm from. Null = "the one he is standing in", the
    // pre-errand contract.
    public TileCoord? StoreTile { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public EquipUnitIntent(int unitId, Resource item, TileCoord? storeTile = null)
    {
        UnitId = unitId;
        Item = item;
        StoreTile = storeTile;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        if (unit.Activity != Activity.Idle)
            return IntentOutcome.Reject($"unit {UnitId} is not Idle (current: {unit.Activity})");
        if (EquipRules.Blocker(unit, Item) is { } why)
            return IntentOutcome.Reject($"unit {UnitId} {why}");

        var tile = StoreTile ?? unit.Position;
        if (!world.Structures.TryGetValue(tile, out var s) || s is not StorageStructure storage)
            return IntentOutcome.Reject($"no storage structure at {tile.X},{tile.Y}");
        if (storage.OwnerId != PlayerId)
            return IntentOutcome.Reject(
                $"storage at {tile.X},{tile.Y} not owned by player {PlayerId}");

        if (unit.Position == tile)
        {
            // Standing in it. The empty-shelf case still REJECTS here rather
            // than parking him where he already is: a player looking at the
            // storehouse is entitled to be told it is empty, not to have their
            // soldier quietly enrolled in an open-ended vigil beside it. The
            // wait is what you opt into by naming a store from a distance.
            if (storage.AmountOf(Item) < 1)
                return IntentOutcome.Reject($"storage at {tile.X},{tile.Y} holds no {Item}");
            return EquipRules.TryEquip(world, unit, Item)
                ? IntentOutcome.Applied
                : IntentOutcome.Reject($"unit {UnitId} could not equip {Item}");
        }

        return GoalRules.Begin(sim, unit, new GoalPlan(GoalKind.Equip, tile, arg: (int)Item))
            ? IntentOutcome.Applied
            : IntentOutcome.Reject($"unit {UnitId} cannot reach the store at {tile.X},{tile.Y}");
    }

    public override string Describe() =>
        StoreTile is { } t
            ? $"Equip(unit={UnitId} <- {Item} @ {t.X},{t.Y})"
            : $"Equip(unit={UnitId} <- {Item})";
}
