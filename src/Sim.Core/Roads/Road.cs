namespace Sim.Core.Roads;

// All road math lives here. Three categories of operation:
//
//   PURE READS (called from pathfinding, views, AI): EffectiveCost,
//     ConditionAt. NEVER mutate. Path queries can fire any number of times
//     without state drift. THE single most important rule of M2 — a read
//     that wrote would inject nondeterminism via path queries.
//
//   MUTATING WRITES (called only from MoveArrivalEvent — the one mutation
//     point): CreditTraffic. Phase C adds this. CatchUpDecay is internal,
//     used by CreditTraffic to bring stale stored state forward before
//     applying gain.
//
// LAZY DECAY MODEL:
//   Decay is rate × time. We don't tick it globally; we catch up on touch.
//   The math is integer-exact and observation-independent:
//
//     periods = (now - LastDecayTick) / DECAY_PERIOD     // integer floor
//     condition -= periods * DECAY_PER_PERIOD            // completed boundaries only
//     LastDecayTick += periods * DECAY_PERIOD            // carry the remainder
//
//   The CARRY is what makes it observation-independent. If a tile is
//   touched mid-period, the partial elapsed time stays banked in
//   LastDecayTick rather than being silently dropped. So the final
//   condition at tick T is identical whether the tile was touched once
//   or fifty times along the way.
//
//   Constant rate (NOT condition-dependent) — that would be the
//   coupled-interval trap (the same one production-tick math avoided
//   in Phase D of M1).
public static class Road
{
    // Movement cost on a tile, accounting for road condition AT THE GIVEN TICK.
    // Returns plain biome cost if no road exists. Always >= MIN_COST.
    //
    // PURE READ. No mutation. Safe to call any number of times.
    public static int EffectiveCost(GameWorld world, TileCoord tile, long now)
    {
        var biomeCost = world.Grid.TerrainCost(tile);
        var condition = ConditionAt(world, tile, now);
        if (condition <= 0) return biomeCost;
        var reduction = condition * RoadConstants.MAX_REDUCTION / RoadConstants.CONDITION_MAX;
        var cost = biomeCost - reduction;
        return cost < RoadConstants.MIN_COST ? RoadConstants.MIN_COST : cost;
    }

    // Current road condition on a tile AT THE GIVEN TICK, applying any
    // accumulated decay since the stored LastDecayTick. Returns 0 if no
    // road exists (or if decay would have wiped it).
    //
    // PURE READ. Same math as CatchUpDecay but without the write — the
    // two MUST agree on what the condition is at any tick.
    public static int ConditionAt(GameWorld world, TileCoord tile, long now)
    {
        if (!world.Roads.TryGetValue(tile, out var road)) return 0;
        if (road.Condition <= 0) return 0;

        var elapsed = now - road.LastDecayTick;
        if (elapsed <= 0) return road.Condition;

        var periods = elapsed / RoadConstants.DECAY_PERIOD;
        if (periods <= 0) return road.Condition;

        var decay = periods * RoadConstants.DECAY_PER_PERIOD;
        var result = road.Condition - decay;
        return result <= 0 ? 0 : (int)result;
    }

    // Apply a single traversal's traffic credit to a tile. Mutating.
    // The ONE mutation point for road condition outside tests. Called from
    // MoveArrivalEvent.Apply after the unit's position update.
    //
    // Order matters and is deterministic:
    //   1. CatchUpDecay first — read the *current* condition, not the stale stored one.
    //   2. Compute diminishing-returns gain from the post-decay condition.
    //   3. Add gain (clamped at CONDITION_MAX), create the road state if newly > 0.
    //
    // The decay-then-gain order is what makes same-tick traversals stack
    // correctly: the second event sees the first's gain because both share
    // the same `now` (so CatchUpDecay is a no-op for the second), and the
    // gain formula uses the freshly-updated condition.
    internal static void CreditTraffic(GameWorld world, TileCoord tile, long now)
    {
        CatchUpDecay(world, tile, now);

        // Stored state may have been removed (decayed to 0) or never existed.
        // Either way, current condition is 0 here.
        world.Roads.TryGetValue(tile, out var road);
        var current = road?.Condition ?? 0;

        var headroom = RoadConstants.CONDITION_MAX - current;
        if (headroom <= 0) return;        // already at cap; nothing more to gain

        long rawGain = (long)RoadConstants.BASE_GAIN * headroom / RoadConstants.CONDITION_MAX;
        int gain = (int)Math.Max(RoadConstants.GAIN_FLOOR, rawGain);
        if (gain > headroom) gain = headroom;  // never overshoot the cap

        var newCondition = current + gain;
        if (road is null)
            world.Roads[tile] = new RoadState(newCondition, now);
        else
        {
            road.Condition = newCondition;
            road.LastDecayTick = now;       // reset decay clock — the tile is fresh
        }
    }

    // Advance a road tile's decay state to `now`, write-through. Mutating.
    // Internal — only CreditTraffic and tests call this directly; pathfinding
    // and views use the pure-read ConditionAt instead.
    //
    // Removes the tile from world.Roads if condition hits 0 (keeps the set
    // sparse — pure reads on absent tiles return 0 via the fallback path).
    internal static void CatchUpDecay(GameWorld world, TileCoord tile, long now)
    {
        if (!world.Roads.TryGetValue(tile, out var road)) return;
        if (road.Condition <= 0) { world.Roads.Remove(tile); return; }

        var elapsed = now - road.LastDecayTick;
        if (elapsed <= 0) return;

        var periods = elapsed / RoadConstants.DECAY_PERIOD;
        if (periods <= 0) return;        // < one period — leave remainder banked

        var decay = periods * RoadConstants.DECAY_PER_PERIOD;
        var newCondition = road.Condition - decay;

        if (newCondition <= 0)
        {
            world.Roads.Remove(tile);
            return;
        }

        road.Condition = (int)newCondition;
        road.LastDecayTick += periods * RoadConstants.DECAY_PERIOD; // carry the remainder
    }
}
