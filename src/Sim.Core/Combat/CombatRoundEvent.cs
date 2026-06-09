using Sim.Core.World;

namespace Sim.Core.Combat;

// M7 Phase C — fires per round on a contested tile. Resolves one
// round of attrition, removes dead units, and re-schedules the next
// round if hostile forces remain.
//
// Fencing: the event carries the tile (the dictionary key). On Apply,
// reads the CombatState on the tile and checks that (At, Seq) match
// the state's NextRoundTick/NextRoundSeq. Mismatch → stale, no-op.
//
// End condition: when no hostile pair of owners remains on the tile,
// clears CombatStates[tile] and returns without rescheduling.
public sealed class CombatRoundEvent : ScheduledEvent
{
    public TileCoord Tile { get; }

    public CombatRoundEvent(TileCoord tile) { Tile = tile; }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;

        // Fence on the tile's anchor.
        if (!world.CombatStates.TryGetValue(Tile, out var state))
        {
            Outcome = IntentOutcome.Reject($"no combat state on tile {Tile}");
            return;
        }
        if (state.NextRoundTick != At || state.NextRoundSeq != Seq)
        {
            Outcome = IntentOutcome.Reject(
                $"stale combat round event for {Tile} " +
                $"(state=({state.NextRoundTick},{state.NextRoundSeq}), event=({At},{Seq}))");
            return;
        }

        // 1) Gather start-of-round forces by owner.
        var forces = CombatRules.GatherForcesOnTile(world, Tile);

        // 2) Filter to belligerent set: keep only owners that have at least
        //    one hostile counterpart in the gather. Single-owner tiles or
        //    fully-friendly tiles end combat.
        var diplomacy = world.Diplomacy;
        var owners = forces.Keys.OrderBy(k => k).ToList();
        var hasHostilePair = false;
        for (var i = 0; i < owners.Count && !hasHostilePair; i++)
            for (var j = i + 1; j < owners.Count && !hasHostilePair; j++)
                if (diplomacy.AreHostile(owners[i], owners[j])) hasHostilePair = true;
        if (!hasHostilePair)
        {
            world.CombatStates.Remove(Tile);
            return;
        }

        // 3) Per-owner start-of-round power (damage budget is computed from
        //    these snapshot values — simultaneous resolution).
        var startPower = new Dictionary<int, int>();
        foreach (var (ownerId, units) in forces)
            startPower[ownerId] = units.Sum(CombatRules.EffectivePower);

        // 3b) No-progress guard. If no belligerent can deal positive damage
        //     (e.g. two hostile but power-0 forces — empty boats pinned by the
        //     engagement trigger), the fight can never resolve. End it now
        //     instead of rescheduling a zero-damage round forever.
        var anyDamage = false;
        foreach (var a in owners)
        {
            foreach (var b in owners)
                if (b != a && diplomacy.AreHostile(a, b) && startPower[b] > 0) { anyDamage = true; break; }
            if (anyDamage) break;
        }
        if (!anyDamage) { world.CombatStates.Remove(Tile); return; }

        // 4) Apply damage. Each owner takes damage = sum of all hostile
        //    counterparts' start-of-round power.
        foreach (var ownerId in owners)
        {
            var damage = 0;
            foreach (var otherId in owners)
            {
                if (otherId == ownerId) continue;
                if (!diplomacy.AreHostile(ownerId, otherId)) continue;
                damage += startPower[otherId];
            }
            if (damage <= 0) continue;
            ApplyDamageToOwnerForce(sim, ownerId, forces[ownerId], damage);
        }

        // 5) Re-check after damage. If hostile pairs still exist on the
        //    tile (re-gather from current state — dead units removed),
        //    schedule next round. Otherwise end combat.
        var post = CombatRules.GatherForcesOnTile(world, Tile);
        var postOwners = post.Keys.OrderBy(k => k).ToList();
        var stillHostile = false;
        for (var i = 0; i < postOwners.Count && !stillHostile; i++)
            for (var j = i + 1; j < postOwners.Count && !stillHostile; j++)
                if (diplomacy.AreHostile(postOwners[i], postOwners[j])) stillHostile = true;

        if (!stillHostile)
        {
            world.CombatStates.Remove(Tile);
            return;
        }

        var nextTick = sim.Now + world.CombatConfig.RoundIntervalTicks;
        state.RoundNumber++;
        state.NextRoundTick = nextTick;
        state.NextRoundSeq = sim.Schedule(nextTick, new CombatRoundEvent(Tile));
    }

    // Distribute a damage budget across an owner's units on the tile,
    // lowest-Health-first (tiebreak lowest-Id). Kills units whose Health
    // drops to <= 0 via CombatRules.OnUnitDeath (Phase D).
    private static void ApplyDamageToOwnerForce(Simulation sim, int ownerId, List<Unit> ownersUnits, int damage)
    {
        // Sort by (Health ASC, Id ASC) — deterministic, observable, intuitive.
        var ordered = ownersUnits
            .OrderBy(u => u.Health)
            .ThenBy(u => u.Id)
            .ToList();

        foreach (var u in ordered)
        {
            if (damage <= 0) break;
            if (u.Health <= damage)
            {
                damage -= u.Health;
                u.Health = 0;
                CombatRules.OnUnitDeath(sim, u);
            }
            else
            {
                u.Health -= damage;
                damage = 0;
            }
        }
    }

    public override string Describe() => $"CombatRound(@ {Tile.X},{Tile.Y})";
}
