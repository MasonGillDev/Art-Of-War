using Sim.Core.Combat;
using Sim.Core.World;

namespace Sim.Core.Equipment;

// Shared equipment-strip rule (docs/equipment-model.md): one rule, two
// callers — CombatRules.OnUnitDeath (loot drops where you fell) and
// TrainUnitIntent (retrain strips your loadout onto the trainer tile).
public static class Equipment
{
    // Removes every equipment-kind buff from `unit`, converts each back
    // to its item in the ground pile at `tile` (same pile shape as the
    // OnUnitDeath cargo drop — matter is conserved), and reverses each
    // buff's HealthModifier (clamped to min 1 so a strip can't kill).
    //
    // Buffs are scanned in list order (the serialized order); pile
    // increments and health reversals are commutative, so order only
    // matters for readability.
    internal static void DropEquipmentToGround(GameWorld world, Unit unit, TileCoord tile)
    {
        var healthReversal = 0;
        var dropped = false;
        for (var i = 0; i < unit.Buffs.Count; /* increment inside */)
        {
            var b = unit.Buffs[i];
            if (!EquipmentCatalog.TryGetByKind(b.Kind, out var spec))
            {
                i++;
                continue;
            }
            unit.Buffs.RemoveAt(i);
            healthReversal += b.HealthModifier;
            dropped = true;

            if (!world.GroundResources.TryGetValue(tile, out var pile))
            {
                pile = new SortedDictionary<Resource, int>();
                world.GroundResources[tile] = pile;
            }
            pile.TryGetValue(spec.Item, out var existing);
            pile[spec.Item] = existing + 1;
        }

        if (!dropped) return;
        unit.Health -= healthReversal;
        if (unit.Health < 1) unit.Health = 1;
    }
}
