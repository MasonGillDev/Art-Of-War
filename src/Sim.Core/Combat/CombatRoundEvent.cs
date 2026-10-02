using Sim.Core.World;

namespace Sim.Core.Combat;

// The SIEGE round (M24; M43: the only pooled round left). Fires per round on a tile where
// attackers stand alone with a hostile destructible structure: fights between units are the
// battlefield's (docs/battlefield-grid.md), and only when a board closes leaving attackers
// alone with a structure do these rounds run (decision D4). Each round drains the structure's
// HP by the attackers' summed power; at zero it is razed.
//
// Fencing: the event carries the tile (the dictionary key). On Apply, reads the CombatState on
// the tile and checks that (At, Seq) match the state's NextRoundTick/NextRoundSeq. Mismatch →
// stale, no-op.
//
// End condition: when no unit hostile to the structure's owner remains, clears
// CombatStates[tile] and returns without rescheduling.
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

        // M26 — fortification siege round. A standing Wall/Gate on this tile means nobody can
        // co-locate with it: forces gather from the tile's 4-neighborhood instead and only the
        // structure takes damage. Handles the round entirely when it applies; a razed fort
        // leaves Rubble and falls through to the normal path below, which sees no siege and
        // ends the combat. docs/walls-and-gates.md.
        if (Sim.Core.Fortifications.FortSiege.TryResolveFortRound(sim, Tile, state)) return;

        // The attackers on the tile at the start of the round, by owner.
        var forces = CombatRules.GatherForcesOnTile(world, Tile);
        var diplomacy = world.Diplomacy;
        var owners = forces.Keys.OrderBy(k => k).ToList();

        // SiegeableStructureOn returns null for the indestructible kinds (Cache / Canal /
        // Rubble) and for any already-razed structure (Health == 0).
        var siegeTarget = CombatRules.SiegeableStructureOn(world, Tile);
        if (siegeTarget is null || !CombatRules.AnyHostileToStructure(diplomacy, owners, siegeTarget.OwnerId))
        {
            EndCombat(sim, Tile);
            return;
        }

        // Damage = the sum of the attackers' start-of-round power (any unit hostile to the
        // structure's owner). M31 — as fought: the King's Buff is part of what a side brings.
        // Bandits never contribute (their camps are razed by others, M16 deferral honoured).
        var startPower = new Dictionary<int, int>();
        foreach (var (ownerId, units) in forces)
            startPower[ownerId] = units.Sum(u => CombatRules.EffectivePower(world, u, sim.Now));
        var siegeDamage = 0;
        foreach (var (oid, p) in startPower)
        {
            if (oid == Sim.Core.Bandits.BanditConstants.OwnerId) continue;
            if (diplomacy.AreHostile(oid, siegeTarget.OwnerId)) siegeDamage += p;
        }

        // No-progress guard: a zero-power siege can never resolve. End it now instead of
        // rescheduling a zero-damage round forever.
        if (siegeDamage <= 0) { EndCombat(sim, Tile); return; }

        // On HP → 0 the structure becomes Rubble; if it was the owner's Castle, a
        // PlayerDefeatedEvent is scheduled (Phase D). A razed camp credits its razers (M39).
        CombatRules.DealSiegeDamage(sim, siegeTarget, siegeDamage, startPower.Keys);

        // Re-check: the siege goes on while the structure stands and attackers remain.
        var post = CombatRules.GatherForcesOnTile(world, Tile);
        var postTarget = CombatRules.SiegeableStructureOn(world, Tile);
        if (postTarget is null || !CombatRules.AnyHostileToStructure(diplomacy, post.Keys.OrderBy(k => k).ToList(), postTarget.OwnerId))
        {
            EndCombat(sim, Tile);
            return;
        }

        var nextTick = sim.Now + world.CombatConfig.RoundIntervalTicks;
        state.RoundNumber++;
        state.NextRoundTick = nextTick;
        state.NextRoundSeq = sim.Schedule(nextTick, new CombatRoundEvent(Tile));
    }

    // THE ONE siege-end site: drop the tile's anchor, then resume whoever stands here with an
    // interrupted errand (docs/combat-pin-strands-hauls.md). Fort sieges end in FortSiege.
    private static void EndCombat(Simulation sim, TileCoord tile)
    {
        sim.World.CombatStates.Remove(tile);
        CombatRules.ResumeInterrupted(sim, tile);
    }

    public override string Describe() => $"CombatRound(@ {Tile.X},{Tile.Y})";
}
