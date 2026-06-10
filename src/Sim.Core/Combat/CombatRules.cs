using Sim.Core.World;

namespace Sim.Core.Combat;

// M7 — pure-read combat rollups + death cleanup (Phase D wires
// OnUnitDeath). Lives separate from the round event so the math is
// testable without scheduling.
public static class CombatRules
{
    // Per-unit combat power. Reads role's BasePower from catalog +
    // sums active buff modifiers (equipment via EquipUnitIntent; later
    // training/armor land as more buff instances with zero round-event
    // change).
    //
    // `now` is sim event time (sim.Now inside an event/intent) — never
    // wall clock — so the lazy expiry filter is deterministic and
    // observation-independent. A buff expiring AT `now` is already
    // inactive (ExpiresAt <= now). This is a PURE READ: expired buffs
    // are filtered, never pruned here (pruning happens at deterministic
    // mutation sites when timed buffs land; see docs/equipment-model.md).
    public static int EffectivePower(Unit u, long now)
    {
        var p = UnitCombatCatalog.Spec(u.Role).BasePower;
        foreach (var b in u.Buffs)
        {
            if (b.ExpiresAt is { } expiry && expiry <= now) continue;
            p += b.PowerModifier;
        }
        return p < 0 ? 0 : p;
    }

    // Sum of EffectivePower across all units on `tile` owned by
    // `ownerId`. The presence-pools-by-owner rollup — a lone unit is a
    // force of one; three of one owner on a tile are a force of three.
    // Group membership is irrelevant here (groups are a movement /
    // command convenience).
    public static int ForcePower(GameWorld world, int ownerId, TileCoord tile, long now)
    {
        var total = 0;
        foreach (var u in world.Units.Values)
        {
            if (u.IsEmbarked) continue;  // M12 — passengers don't fight
            if (u.OwnerId != ownerId) continue;
            if (u.Position != tile) continue;
            total += EffectivePower(u, now);
        }
        return total;
    }

    // The gather-from-tiles seam. Today reads the single tile; later
    // ranged-from-adjacent will pass [tile, north, south, east, west]
    // (each with a reach modifier). Returns per-owner unit lists in
    // arbitrary order; callers must sort if they need determinism.
    //
    // M12 — embarked units are excluded; they're inside the boat and
    // not on the contact tile.
    public static IDictionary<int, List<Unit>> GatherForcesOnTile(GameWorld world, TileCoord tile)
    {
        var byOwner = new Dictionary<int, List<Unit>>();
        foreach (var u in world.Units.Values)
        {
            if (u.IsEmbarked) continue;
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

        // M12 — if the dying unit is embarked on a boat, remove it
        // from that boat's Passengers list. Without this, a passenger
        // killed by starvation / age / combat-on-boat-tile would leave
        // a stale entry the boat would carry forever.
        if (unit.EmbarkedOn is int carrierId
            && world.Units.TryGetValue(carrierId, out var carrier))
        {
            carrier.Passengers.Remove(unit.Id);
        }

        // M12 — if the dying unit IS a boat with passengers, every
        // passenger drowns. We recursively death-pipeline each one:
        // they're removed from world.Units (and from passengers list by
        // the hook above, but here we explicitly clear the list at the
        // end anyway). Cargo on the boat goes to the wreck tile (a
        // water tile — same drop-to-tile pile mechanism applies); that
        // happens via the existing cargo-drop step below.
        if (unit.Role == UnitRole.Boat && unit.Passengers.Count > 0)
        {
            // Snapshot the list to avoid mutating during iteration.
            var drowning = new List<int>(unit.Passengers);
            unit.Passengers.Clear();
            foreach (var pid in drowning)
            {
                if (!world.Units.TryGetValue(pid, out var passenger)) continue;
                // Clear EmbarkedOn first so the OnUnitDeath call below
                // doesn't try to re-touch this boat's (already-emptied)
                // Passengers list.
                passenger.EmbarkedOn = null;
                OnUnitDeath(sim, passenger);
            }
        }

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

        // 1b. Drop equipment: each equipment-kind buff converts back to
        //     its item on the death tile (docs/equipment-model.md) —
        //     same loot economy as the cargo drop above. Kill the
        //     equipped soldier, haul the sword home.
        Sim.Core.Equipment.Equipment.DropEquipmentToGround(world, unit, tile);

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
