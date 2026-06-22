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

        // 2) Determine belligerent state. Combat continues this round if
        //    there is a hostile UNIT pair OR a siege case (any unit hostile
        //    to a destructible structure on the tile). Otherwise end.
        var diplomacy = world.Diplomacy;
        var owners = forces.Keys.OrderBy(k => k).ToList();
        var hasHostilePair = false;
        for (var i = 0; i < owners.Count && !hasHostilePair; i++)
            for (var j = i + 1; j < owners.Count && !hasHostilePair; j++)
                if (diplomacy.AreHostile(owners[i], owners[j])) hasHostilePair = true;

        // M24 — siege gating. SiegeableStructureOn returns null for the
        // indestructible kinds (Cache / Canal / Rubble) and for any
        // already-razed structure (Health == 0), so the round naturally
        // skips them. Defender shielding is start-of-round: if any unit
        // of the structure's owner is on the tile at gather, siege damage
        // is SUPPRESSED this round even if they die in step 4 below — the
        // defender's death must "cost a round" to justify their presence.
        var siegeTarget = CombatRules.SiegeableStructureOn(world, Tile);
        var hostileSiege = siegeTarget is not null
            && CombatRules.AnyHostileToStructure(diplomacy, owners, siegeTarget.OwnerId);
        var hadDefendersAtStart = siegeTarget is not null
            && forces.ContainsKey(siegeTarget.OwnerId);

        if (!hasHostilePair && !hostileSiege)
        {
            world.CombatStates.Remove(Tile);
            return;
        }

        // 3) Per-owner start-of-round power (damage budget is computed from
        //    these snapshot values — simultaneous resolution).
        var startPower = new Dictionary<int, int>();
        foreach (var (ownerId, units) in forces)
            startPower[ownerId] = units.Sum(u => CombatRules.EffectivePower(u, sim.Now));

        // 3b) No-progress guard. If no belligerent can deal positive damage
        //     (e.g. two hostile but power-0 forces — empty boats pinned by the
        //     engagement trigger; or a zero-power siege), the fight can never
        //     resolve. End it now instead of rescheduling a zero-damage round
        //     forever.
        var anyDamage = false;
        // Unit-vs-unit?
        foreach (var a in owners)
        {
            foreach (var b in owners)
                if (b != a && diplomacy.AreHostile(a, b) && startPower[b] > 0) { anyDamage = true; break; }
            if (anyDamage) break;
        }
        // Siege damage (only counts when defenders aren't shielding,
        // and bandits never contribute — same rule as the AnyHostile
        // gate in CombatRules)?
        if (!anyDamage && hostileSiege && !hadDefendersAtStart)
        {
            foreach (var (oid, p) in startPower)
            {
                if (p <= 0) continue;
                if (oid == Sim.Core.Bandits.BanditConstants.OwnerId) continue;
                if (diplomacy.AreHostile(oid, siegeTarget!.OwnerId)) { anyDamage = true; break; }
            }
        }
        if (!anyDamage) { world.CombatStates.Remove(Tile); return; }

        // 4) Apply damage to units. Each owner takes damage = sum of all
        //    hostile counterparts' start-of-round power.
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

        // 4b) Apply siege damage to the structure (M24). Only fires when no
        //     defending units of the structure's owner were on the tile at
        //     ROUND START — a defender's death this round still costs the
        //     attackers a round of shielding. Damage = sum of all attackers'
        //     start-of-round power (any unit hostile to the structure owner).
        //     On HP → 0 the structure becomes Rubble; if it was the owner's
        //     Castle, a PlayerDefeatedEvent is scheduled (Phase D).
        if (siegeTarget is not null && !hadDefendersAtStart && siegeTarget.Health > 0)
        {
            var siegeDamage = 0;
            foreach (var (oid, p) in startPower)
            {
                if (oid == Sim.Core.Bandits.BanditConstants.OwnerId) continue;
                if (diplomacy.AreHostile(oid, siegeTarget.OwnerId)) siegeDamage += p;
            }
            if (siegeDamage > 0)
            {
                siegeTarget.Health -= siegeDamage;
                if (siegeTarget.Health <= 0)
                {
                    Sim.Core.Sieges.SiegeDamage.RazeStructure(sim, siegeTarget);
                    siegeTarget = null;  // gone — subsequent reads must re-look up
                }
            }
        }

        // 5) Re-check after damage. Combat continues if a hostile UNIT pair
        //    remains OR a hostile siege remains. Otherwise end combat.
        var post = CombatRules.GatherForcesOnTile(world, Tile);
        var postOwners = post.Keys.OrderBy(k => k).ToList();
        var stillHostile = false;
        for (var i = 0; i < postOwners.Count && !stillHostile; i++)
            for (var j = i + 1; j < postOwners.Count && !stillHostile; j++)
                if (diplomacy.AreHostile(postOwners[i], postOwners[j])) stillHostile = true;

        var postSiegeTarget = CombatRules.SiegeableStructureOn(world, Tile);
        var stillSiege = postSiegeTarget is not null
            && CombatRules.AnyHostileToStructure(diplomacy, postOwners, postSiegeTarget.OwnerId);

        if (!stillHostile && !stillSiege)
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
