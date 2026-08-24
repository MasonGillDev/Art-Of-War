using Sim.Core.World;

namespace Sim.Core.Royalty;

// M31 — the dynasty (docs/king-and-dynasty.md).
//
// PURE-READ WALL, the same discipline Population keeps: everything here reads
// state and never mutates. Views, intent validation and the combat rollup use
// these freely. The ONE mutator of Player.KingUnitId is Succession.OnRoyalRemoved
// — no second writer, no post-hoc sweep.
//
// ROYALTY IS DERIVED, NOT FLAGGED. Given stored parentage plus one stored
// KingUnitId per player, "is royal" is a predicate:
//
//     royal(u) = u is the king || u is a child of the king
//
// That is not a shortcut — it IS the narrow-line rule of the design doc. When
// the crown moves, every sibling branch lapses to commoner in the same
// instant, with no sweep, no per-unit mutation, and nothing that can drift out
// of sync. A stored IsRoyal flag would need exactly that sweep and would be a
// second source of truth about the same fact.
//
// Consequence: the STRICT narrow line (the king's own children only) is free.
// The gentler variant the design doc allows (+ the previous king's surviving
// children) would cost one more stored field and one more clause here; it stays
// unbuilt until play says the line feels too thin.
public static class Royalty
{
    // The reigning monarch, or null during an interregnum / after the line is
    // extinct. O(1): the crown is stored precisely so the per-unit aura check
    // in the combat rollup does not have to scan for it.
    public static Unit? King(GameWorld world, int ownerId)
    {
        if (!world.Players.TryGetValue(ownerId, out var player)) return null;
        if (player.KingUnitId is not { } id) return null;
        return world.Units.TryGetValue(id, out var king) ? king : null;
    }

    public static bool IsKing(GameWorld world, Unit unit) =>
        world.Players.TryGetValue(unit.OwnerId, out var p) && p.KingUnitId == unit.Id;

    // A child of the CURRENT king. Deliberately not "a child of any king" —
    // see the narrow-line note above.
    public static bool IsRoyalChild(GameWorld world, Unit unit)
    {
        if (!world.Players.TryGetValue(unit.OwnerId, out var p)) return false;
        if (p.KingUnitId is not { } kingId) return false;
        return unit.ParentAId == kingId || unit.ParentBId == kingId;
    }

    public static bool IsRoyal(GameWorld world, Unit unit) =>
        IsKing(world, unit) || IsRoyalChild(world, unit);

    // THE HEIR-APPARENT: the eldest living child of the current king. Exactly
    // one exists at any time, by construction rather than by bookkeeping —
    // which is why the design's "the buff cannot be farmed" needs no anti-farm
    // rule anywhere.
    //
    // Eldest = lowest BornTick, ties broken by lowest Id. That is the canonical
    // ordering StarvationDeathEvent already uses to pick its oldest victim
    // (src/Sim.Core/Food/StarvationDeathEvent.cs), reused here so "eldest"
    // means one thing in the codebase.
    //
    // Order-independent: iterating world.Units (sorted by id) and comparing on
    // (BornTick, Id) yields the same answer whatever order the children were
    // inserted in.
    public static Unit? HeirApparent(GameWorld world, int ownerId)
    {
        if (world.Players.TryGetValue(ownerId, out var p) && p.KingUnitId is { } kingId)
            return HeirApparentOf(world, kingId, ownerId);
        return null;
    }

    // The heir of a NAMED monarch, whether or not he is still in world.Units.
    //
    // This overload exists because succession must not depend on the order its
    // caller happens to use. CombatRules.OnUnitDeath removes the unit from the
    // world and THEN notifies the population layer, while the age and
    // starvation paths notify first; a heir search that started from "look up
    // the current king" therefore found nothing on the combat path and
    // silently declared the line extinct — a king killed in battle would end
    // his dynasty, which is precisely the catastrophic-failure mode this
    // design rejects. Searching from the id removes the dependency entirely.
    public static Unit? HeirApparentOf(GameWorld world, int kingUnitId, int ownerId)
    {
        Unit? eldest = null;
        foreach (var u in world.Units.Values)
        {
            if (u.Id == kingUnitId) continue;
            if (u.OwnerId != ownerId) continue;
            if (u.ParentAId != kingUnitId && u.ParentBId != kingUnitId) continue;
            if (eldest is null
                || u.BornTick < eldest.BornTick
                || (u.BornTick == eldest.BornTick && u.Id < eldest.Id))
                eldest = u;
        }
        return eldest;
    }

    // A crowned child. Derived from age like every other gate in the sim —
    // nothing is stored, nothing is scheduled, and majority arrives on its own.
    public static bool IsMinor(GameWorld world, Unit unit, long now) =>
        Population.Population.AgeYears(unit, now, world.PopulationConfig)
            < world.RoyaltyConfig.MajorityAge;

    // Does this player currently have a monarch capable of projecting the
    // King's Buff? Three different situations produce the same "no" —
    // interregnum (no king), a child monarch (minority), and an embarked king
    // (off the tile grid, invisible to combat, so his aura must not leak
    // through the hull). The distinctions matter to the chronicler, not to the
    // rollup, so the rollup asks exactly this one question.
    public static Unit? ProjectingKing(GameWorld world, int ownerId, long now)
    {
        if (King(world, ownerId) is not { } king) return null;
        if (king.IsEmbarked) return null;
        if (IsMinor(world, king, now)) return null;
        return king;
    }
}
