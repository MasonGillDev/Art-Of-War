using Sim.Core.World;

namespace Sim.Core.Caches;

// The per-unit LOOT implementation, shared by the intent (a unit already
// standing on the cache) and by GoalRules (one that has just walked there) —
// the same one-body-of-code discipline as TrainingRules and EquipRules.
public static class CacheLooting
{
    // Why this body cannot loot at all, or null. Cargo state is the whole of
    // it: looting is a pickup, and a carrier that is full or already holding
    // something else has nowhere to put the treasure.
    //
    // These are checked at firing AND on arrival, because a walk across the map
    // takes game-days and a unit can pick up a load on the way.
    public static string? Blocker(Unit unit, Resource resource)
    {
        if (resource == Resource.None) return "no resource named";
        if (unit.GroupId is not null) return "in a group";
        if (unit.IsEmbarked) return "embarked";
        if (unit.CargoAmount > 0 && unit.CargoResource != resource)
            return $"already carrying {unit.CargoResource} (unload first)";
        if (unit.CargoCapacity - unit.CargoAmount <= 0) return "no cargo space free";
        return null;
    }

    // Take what fits. CARGO-CAPPED on purpose (docs/loot-caches.md): the
    // remainder stays in the cache, which persists and stays re-lootable, so a
    // big haul wants a hauler or several trips — and a rival can take the rest.
    //
    // Returns the amount taken; 0 means nothing happened.
    public static int TryLoot(GameWorld world, Unit unit, Resource resource)
    {
        if (Blocker(unit, resource) is not null) return 0;
        if (!world.Structures.TryGetValue(unit.Position, out var s) || s is not Cache cache) return 0;

        var space = unit.CargoCapacity - unit.CargoAmount;
        var taken = cache.Withdraw(resource, space);
        if (taken == 0) return 0;

        unit.CargoResource = resource;
        unit.CargoAmount += taken;
        unit.TrySetActivity(Activity.Idle);
        unit.BumpEpoch();

        // Consumed when emptied — the treasure is gone, and the tile with it.
        if (cache.TotalHeld() == 0)
            world.Structures.Remove(cache.At);
        return taken;
    }
}
