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
    //
    // M31 — THIS TWO-ARG FORM IS THE UNIT'S OWN POWER: catalog + what it
    // carries. It deliberately does NOT include the king's aura, because an
    // aura is a fact about where a unit is STANDING, not about the unit. Every
    // rollup that decides a fight uses the world-aware overload below; this
    // one survives for the stat tests and for any caller that genuinely means
    // "what does this body bring by itself".
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

    // M31 — power AS FOUGHT: own power plus the King's Buff if the unit is
    // standing inside its disc (docs/king-and-dynasty.md).
    //
    // Why the aura is computed here rather than granted as a Buff instance:
    // Unit.Buffs is a STORED two-slot equipment loadout (BuffRules), so an
    // aura-as-buff would eat a sword slot and need grant/revoke churn on every
    // hop by every unit near the king — a mutation storm for something that is
    // a pure function of two positions. So it is a pure read, like every other
    // derived quantity in the sim.
    //
    // Cost is O(1) per unit: the crown is a stored id, so this is one lookup
    // and one integer distance test, not a scan for royalty.
    //
    // Housing (docs/housing-buffs.md) rides the same seam for the same
    // reason: "settled" is a fact about the unit's HOME and its pantry, both
    // world state, so it is a pure read here rather than a stored buff.
    public static int EffectivePower(GameWorld world, Unit u, long now) =>
        EffectivePower(u, now)
        + KingAuraBonus(world, u, now)
        + Sim.Core.Population.Housing.SettledPowerBonus(world, u, now);

    // The aura itself. Zero unless a LIVING, NON-MINOR, NON-EMBARKED king of
    // the unit's OWN owner is within AuraRadius — the three ways a realm can
    // be without a projecting monarch (interregnum, a child on the throne, a
    // king at sea) all collapse to the same zero here, and each is a separate
    // rule that produced it.
    //
    // Integer EUCLIDEAN disc (dx*dx + dy*dy <= r*r), matching Sight.Reveal, so
    // "radius R" means the same shape everywhere in the codebase. Integer math
    // only — no floats anywhere near the sim.
    public static int KingAuraBonus(GameWorld world, Unit u, long now)
    {
        if (u.IsEmbarked) return 0;   // passengers don't fight; nothing to buff
        var cfg = world.RoyaltyConfig;
        if (cfg.AuraPowerBonus == 0 || cfg.AuraRadius <= 0) return 0;

        if (Sim.Core.Royalty.Royalty.ProjectingKing(world, u.OwnerId, now) is not { } king)
            return 0;

        var dx = king.Position.X - u.Position.X;
        var dy = king.Position.Y - u.Position.Y;
        return dx * dx + dy * dy <= cfg.AuraRadius * cfg.AuraRadius ? cfg.AuraPowerBonus : 0;
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
            total += EffectivePower(world, u, now);   // M31 — as fought, aura included
        }
        return total;
    }

    // M24 — siege seam. Returns the structure on `tile` only if it is a
    // legitimate siege target: present, with HP > 0 (the "indestructible"
    // kinds Cache / Canal / Rubble have BaseHealth = 0 and never qualify).
    // Pure read. Callers compose this with diplomacy hostility to decide
    // whether combat starts / continues against the structure.
    public static Structure? SiegeableStructureOn(GameWorld world, TileCoord tile)
    {
        if (!world.Structures.TryGetValue(tile, out var s)) return null;
        if (s.Health <= 0) return null;
        return s;
    }

    // Any of `unitOwners` hostile to `structureOwner` AND able to siege?
    // Linear in owners, bounded by the faction count. The answer is
    // symmetric in the diplomacy axis.
    //
    // Bandits (BanditConstants.OwnerId) are EXEMPT: M16 explicitly defers
    // structure damage by the bandit faction (docs/m16-bandits-spec.md /
    // "structure-damage deferred"). Bandits steal (LoadCargoIntent) and
    // raid — they don't level extractors or storm castles. Without the
    // exemption a wandering bandit party would auto-start a siege on any
    // unguarded outpost and the raid-then-flee loop would never close.
    // Player-vs-player and player-vs-future-faction sieges are unaffected.
    public static bool AnyHostileToStructure(
        Sim.Core.Diplomacy.Diplomacy diplomacy,
        IEnumerable<int> unitOwners,
        int structureOwner)
    {
        foreach (var o in unitOwners)
        {
            if (o == Sim.Core.Bandits.BanditConstants.OwnerId) continue;
            if (diplomacy.AreHostile(o, structureOwner)) return true;
        }
        return false;
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

        // 2b. RETIRE THE JOB. A worker who dies on shift must come off the
        //     building's roster, or the corpse holds its work slot forever:
        //     Workers.Count is checked against WorkerCap at assign time and
        //     read by the WorkersBelow predicate, so a ghost both blocks
        //     manual re-staffing AND convinces a Staff order the building is
        //     fully manned. (Found in play: a farmer starved on shift, the
        //     farm kept listing them, population dropped, and the farm could
        //     never be worked again.) The move path always did this; the
        //     death path never did.
        Sim.Core.Logistics.WorkAssignment.Release(sim, unit);

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
        unit.Pursuit = null;   // M29 — a corpse chases nobody
        unit.GroupId = null;

        // 4. Remove from world.
        world.Units.Remove(unit.Id);

        // 5. M8: notify population layer (Phase E). If the removed unit was
        //    a breeding parent, this stops the breeding and frees the
        //    survivor. Combat code never names Breeding directly.
        Sim.Core.Population.Population.OnUnitRemoved(sim, unit);

        // 6. Extinction check (2026-07-13): a faction whose last soul just
        //    died is OUT — the same consequence as a razed castle
        //    (PlayerDefeatedEvent: intents reject, hostile-to-all, the
        //    game-over accounting), because a kingdom with no people can
        //    never act, recover, or surrender. Every removal path funnels
        //    through this pipeline, so one hook covers combat, starvation
        //    and age alike; the event idempotency-fences, so a death
        //    racing the castle's own razing is safe. Sentinel owners
        //    (bandits/caches) never count. The event's tile is the
        //    kingdom's seat if it still stands, else where the last
        //    subject fell.
        if (unit.OwnerId >= 0
            && world.Players.TryGetValue(unit.OwnerId, out var bereft)
            && !bereft.Defeated && bereft.PopulationCount <= 0)
        {
            var seat = world.Structures.Values.OfType<Castle>()
                .Where(c => c.OwnerId == unit.OwnerId)
                .OrderBy(c => c.At.Y).ThenBy(c => c.At.X)
                .FirstOrDefault()?.At ?? tile;
            sim.Schedule(sim.Now, new Sim.Core.Sieges.PlayerDefeatedEvent(unit.OwnerId, seat));
        }
    }
}
