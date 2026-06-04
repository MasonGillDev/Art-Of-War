using Sim.Core.World;

namespace Sim.Core.Combat;

// M7 — pure-read combat rollups + death cleanup (Phase D wires
// OnUnitDeath). Lives separate from the round event so the math is
// testable without scheduling.
public static class CombatRules
{
    // Per-unit combat power. Reads role's BasePower from catalog +
    // sums any active buff modifiers. Today buffs are always empty,
    // so this returns the catalog value verbatim — but the rollup is
    // already buff-aware, so training/armor land later as buff
    // instances with zero round-event change.
    public static int EffectivePower(Unit u)
    {
        var p = UnitCombatCatalog.Spec(u.Role).BasePower;
        foreach (var b in u.Buffs) p += b.PowerModifier;
        return p < 0 ? 0 : p;
    }

    // Sum of EffectivePower across all units on `tile` owned by
    // `ownerId`. The presence-pools-by-owner rollup — a lone unit is a
    // force of one; three of one owner on a tile are a force of three.
    // Group membership is irrelevant here (groups are a movement /
    // command convenience).
    public static int ForcePower(GameWorld world, int ownerId, TileCoord tile)
    {
        var total = 0;
        foreach (var u in world.Units.Values)
        {
            if (u.OwnerId != ownerId) continue;
            if (u.Position != tile) continue;
            total += EffectivePower(u);
        }
        return total;
    }

    // The gather-from-tiles seam. Today reads the single tile; later
    // ranged-from-adjacent will pass [tile, north, south, east, west]
    // (each with a reach modifier). Returns per-owner unit lists in
    // arbitrary order; callers must sort if they need determinism.
    public static IDictionary<int, List<Unit>> GatherForcesOnTile(GameWorld world, TileCoord tile)
    {
        var byOwner = new Dictionary<int, List<Unit>>();
        foreach (var u in world.Units.Values)
        {
            if (u.Position != tile) continue;
            if (!byOwner.TryGetValue(u.OwnerId, out var list))
            {
                list = new List<Unit>();
                byOwner[u.OwnerId] = list;
            }
            list.Add(u);
        }
        return byOwner;
    }

    // M7 Phase D — clean death. Called from CombatRoundEvent when a
    // unit's Health hits 0. Drops cargo to the tile (Phase E hook),
    // removes the unit from its group (attrition-disband if the group
    // hits zero members), clears in-flight movement/haul anchors so
    // pending events fence cleanly, and removes the unit from
    // world.Units.
    public static void OnUnitDeath(Simulation sim, Unit unit)
    {
        var world = sim.World;
        var tile = unit.Position;

        // 1. Drop cargo (Phase E hook): the laden caravan's payload
        //    survives the unit and becomes loose tile resource.
        if (unit.CargoAmount > 0)
        {
            if (!world.GroundResources.TryGetValue(tile, out var pile))
            {
                pile = new SortedDictionary<Resource, int>();
                world.GroundResources[tile] = pile;
            }
            pile.TryGetValue(unit.CargoResource, out var existing);
            pile[unit.CargoResource] = existing + unit.CargoAmount;
            unit.CargoAmount = 0;
        }

        // 2. Group cleanup. Remove from members; attrition-disband if empty.
        if (unit.GroupId is { } gid && world.Groups.TryGetValue(gid, out var group))
        {
            group.Members.Remove(unit.Id);
            if (group.Members.Count == 0)
                world.Groups.Remove(gid);
        }

        // 3. Clear in-flight obligations explicitly. Pending events
        //    (MoveArrival / HaulPickup / HaulDeposit) already fence via
        //    world.Units.TryGetValue when the unit is removed below;
        //    this just makes the dying unit's own state debugger-clear
        //    and closes the M2 landmine described in docs/architecture.md.
        unit.PathRemaining = null;
        unit.PathFinalDest = null;
        unit.NextArrivalTick = null;
        unit.NextArrivalSeq = null;
        unit.HaulPlan = null;
        unit.GroupId = null;

        // 4. Remove from world.
        world.Units.Remove(unit.Id);

        // 5. M8: notify population layer (Phase E). If the removed unit was
        //    a breeding parent, this stops the breeding and frees the
        //    survivor. Combat code never names Breeding directly.
        Sim.Core.Population.Population.OnUnitRemoved(sim, unit);
    }
}
