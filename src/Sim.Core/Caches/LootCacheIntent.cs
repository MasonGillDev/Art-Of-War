using System.Text.Json.Serialization;

namespace Sim.Core.Caches;

// M23 — loot a discovered cache (docs/loot-caches.md). A unit standing on a
// Cache tile takes one named resource into its cargo, CARGO-CAPPED: it loads
// up to its free cargo space and the remainder stays in the cache (which
// persists, re-lootable — a big haul wants a hauler or several trips, and a
// rival can grab the leftovers). The cache is removed once emptied — the
// treasure is gone.
//
// Mirrors LoadCargoIntent's preconditions (the M16 cargo atom); cargo is
// single-resource, so the caller names which resource to take. Source
// ownership is irrelevant — a cache belongs to no one; whoever reaches it
// first may loot it (the exploration-and-speed reward).
public sealed class LootCacheIntent : Intent
{
    public int UnitId { get; }
    public Resource Resource { get; }

    [JsonConstructor]
    public LootCacheIntent(int unitId, Resource resource)
    {
        UnitId = unitId;
        Resource = resource;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (Resource == Resource.None)
            return IntentOutcome.Reject("no resource named");
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
        if (unit.CargoAmount > 0 && unit.CargoResource != Resource)
            return IntentOutcome.Reject(
                $"unit {UnitId} already carries {unit.CargoResource} (unload first)");

        var space = unit.CargoCapacity - unit.CargoAmount;
        if (space <= 0)
            return IntentOutcome.Reject($"unit {UnitId} has no cargo space free");

        if (!world.Structures.TryGetValue(unit.Position, out var s) || s is not Cache cache)
            return IntentOutcome.Reject(
                $"no cache at {unit.Position.X},{unit.Position.Y}");

        var taken = cache.Withdraw(Resource, space);
        if (taken == 0)
            return IntentOutcome.Reject($"cache has no {Resource}");

        unit.CargoResource = Resource;
        unit.CargoAmount += taken;
        unit.BumpEpoch();   // defensive: fence any latent per-unit event (Idle had none)

        // Consumed when emptied.
        if (cache.TotalHeld() == 0)
            world.Structures.Remove(unit.Position);

        return IntentOutcome.Applied;
    }

    public override string Describe() => $"LootCache(unit={UnitId} {Resource})";
}
