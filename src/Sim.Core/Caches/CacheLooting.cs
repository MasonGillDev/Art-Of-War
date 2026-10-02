using Sim.Core.World;

namespace Sim.Core.Caches;

// The per-unit LOOT implementation, shared by the intent (a unit already
// standing on the cache) and by GoalRules (one that has just walked there) —
// the same one-body-of-code discipline as TrainingRules and EquipRules.
public static class CacheLooting
{
    // Why this body cannot loot at all, or null. Cargo state is the whole of
    // it: looting is a pickup, and a full carrier has nowhere to put the
    // treasure. (M36: cargo can be mixed, so holding something else is fine.)
    //
    // These are checked at firing AND on arrival, because a walk across the map
    // takes game-days and a unit can pick up a load on the way.
    public static string? Blocker(GameWorld world, Unit unit, Resource resource)
    {
        if (resource == Resource.None) return "no resource named";
        if (Sim.Core.Groups.GroupRules.UnderCommand(world, unit)) return "under its group's command";
        if (unit.IsEmbarked) return "embarked";
        if (unit.CargoCapacity - unit.CargoAmount <= 0) return "no cargo space free";
        return null;
    }

    // Take what fits. CARGO-CAPPED on purpose (docs/loot-caches.md): the
    // remainder stays in the cache, which persists and stays re-lootable, so a
    // big haul wants a hauler or several trips — and a rival can take the rest.
    //
    // Returns the amount taken; 0 means nothing happened.
    public static int TryLoot(Simulation sim, Unit unit, Resource resource)
    {
        var world = sim.World;
        if (Blocker(world, unit, resource) is not null) return 0;
        if (!world.Structures.TryGetValue(unit.Position, out var s) || s is not Cache cache) return 0;

        var space = unit.CargoCapacity - unit.CargoAmount;
        var taken = cache.Withdraw(resource, space);
        if (taken == 0) return 0;

        unit.Cargo.Add(resource, taken);
        unit.TrySetActivity(Activity.Idle);
        unit.BumpEpoch();

        RemoveIfEmptied(sim, cache);
        return taken;
    }

    // Consumed when emptied — the treasure is gone, and the tile with it.
    // THE ONE removal path for a cache, called by every way of taking from
    // one: the loot verb above, CargoTransfer.WithdrawFrom (plain loading,
    // which is how bandits steal, and route stops) and HaulPickupEvent (a haul
    // may name any structure as its source). Before 2026-09-23 only the loot
    // verb removed it, so a cache emptied any other way stood forever, empty,
    // and a rumour about it never ended.
    public static void RemoveIfEmptied(Simulation sim, Structure? source)
    {
        if (source is not Cache cache || cache.TotalHeld() > 0) return;
        if (!sim.World.Structures.TryGetValue(cache.At, out var here) || !ReferenceEquals(here, cache)) return;
        sim.World.Structures.Remove(cache.At);
        // M37 — a rumour about this ruin is over (docs/progression.md).
        Sim.Core.Progression.Omens.OnCacheGone(sim.World, cache.At, sim.Now);
        // M38 — an owner watching a charted cache sees it go.
        Sim.Core.Scouting.Charts.OnSecretGone(sim, cache.At);
    }
}
