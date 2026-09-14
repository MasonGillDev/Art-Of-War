using System.Text.Json.Serialization;
using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Caches;

// M23 — loot a discovered cache (docs/loot-caches.md). A unit takes one named
// resource into its cargo, CARGO-CAPPED: it loads up to its free space and the
// remainder stays in the cache, which persists and stays re-lootable — a big
// haul wants a hauler or several trips, and a rival can grab the leftovers. The
// cache is removed once emptied; the treasure is gone.
//
// GOAL-SHAPED. "Go take that cache" is one decision and the trek across the fog
// is execution, so naming a CacheTile sends the body there and loots on
// arrival. Omit it and the old contract holds: the cache under their feet.
//
// IT NEVER WAITS, and that is the difference between treasure and trade. An
// empty shelf in a storehouse will be restocked, so an equip errand stands and
// waits; a cache does not refill, so arriving to an emptied one is the end of
// the errand and it dissolves saying so. Somebody else got there first is a
// real outcome of a race the design wants you to feel.
//
// Source ownership is irrelevant — a cache belongs to no one, and whoever
// reaches it first may loot it. That is the exploration-and-speed reward.
public sealed class LootCacheIntent : Intent
{
    public int UnitId { get; }
    public Resource Resource { get; }

    // Which cache. Null = "the one under their feet", the pre-errand contract.
    public TileCoord? CacheTile { get; }

    [JsonConstructor]
    public LootCacheIntent(int unitId, Resource resource, TileCoord? cacheTile = null)
    {
        UnitId = unitId;
        Resource = resource;
        CacheTile = cacheTile;
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
        if (CacheLooting.Blocker(unit, Resource) is { } why)
            return IntentOutcome.Reject($"unit {UnitId} {why}");

        var tile = CacheTile ?? unit.Position;
        if (!world.Structures.TryGetValue(tile, out var s) || s is not Cache)
            return IntentOutcome.Reject($"no cache at {tile.X},{tile.Y}");

        if (unit.Position == tile)
            return CacheLooting.TryLoot(world, unit, Resource) > 0
                ? IntentOutcome.Applied
                : IntentOutcome.Reject($"cache at {tile.X},{tile.Y} has no {Resource}");

        return GoalRules.Begin(sim, unit, new GoalPlan(GoalKind.Loot, tile, arg: (int)Resource))
            ? IntentOutcome.Applied
            : IntentOutcome.Reject($"unit {UnitId} cannot reach the cache at {tile.X},{tile.Y}");
    }

    public override string Describe() => CacheTile is { } t
        ? $"LootCache(unit={UnitId} {Resource} @ {t.X},{t.Y})"
        : $"LootCache(unit={UnitId} {Resource})";
}
