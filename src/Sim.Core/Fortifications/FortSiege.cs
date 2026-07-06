using Sim.Core.Combat;

namespace Sim.Core.Fortifications;

// M26 — besieging a fortification (docs/walls-and-gates.md). Nobody can
// stand on a Wall/Gate tile, so the M24 stand-on-the-tile siege cannot
// reach one. Instead the siege anchors its CombatState on the FORT's tile
// (one combat per tile, the same fencing anchor, RegenerateQueue recovery
// for free) while forces gather from the tile's 4-neighborhood.
//
// Trigger discipline (presence-gated, the M24 idiom): a siege opens when a
// mover ENDS its march beside a hostile standing fortification, or when a
// fortification physically STOPS it (the blocked-hop yield). Mid-path hops
// alongside enemy walls are just travel — marching past does not attack.
//
// NO DEFENDER SHIELDING — deliberate divergence from the M24 castle rule.
// That rule works because attackers co-locate with defenders and combat
// clears them first; on an un-enterable tile any shielding rule would let
// one unreachable unit make the wall permanently indestructible. The wall
// IS the shield; defending it means sallying out and killing the attackers
// on their own tiles (ordinary M7 combat, which runs independently).
public static class FortSiege
{
    // Open a siege on every hostile standing fortification 4-adjacent to
    // `tile` (canonical N/E/S/W order so Seq assignment is deterministic).
    // Called from MoveArrivalEvent / GroupArrivalEvent at FINAL arrival and
    // from the blocked-hop yield paths. Owners present on `tile` decide
    // hostility; bandits are exempt (M16 — bandits raid, they don't raze).
    public static void MaybeBeginSiegeAdjacentTo(Simulation sim, TileCoord tile)
    {
        var world = sim.World;

        var owners = new SortedSet<int>();
        foreach (var u in world.Units.Values)
            if (u.Position == tile && !u.IsEmbarked) owners.Add(u.OwnerId);
        if (owners.Count == 0) return;

        foreach (var n in Neighbors4(tile))
        {
            if (!world.Grid.InBounds(n)) continue;
            if (!world.Structures.TryGetValue(n, out var s)) continue;
            if (!Fortification.IsStandingFortification(s)) continue;
            if (!CombatRules.AnyHostileToStructure(world.Diplomacy, owners, s.OwnerId)) continue;
            // Already besieged → the next round's re-gather picks up the
            // newcomer, exactly like reinforcement on a contested tile.
            if (world.CombatStates.ContainsKey(n)) continue;

            var state = new CombatState(n);
            var nextTick = sim.Now + world.CombatConfig.RoundIntervalTicks;
            state.RoundNumber = 1;
            state.NextRoundTick = nextTick;
            state.NextRoundSeq = sim.Schedule(nextTick, new CombatRoundEvent(n));
            world.CombatStates[n] = state;
        }
    }

    // Round resolution for a fort tile. Returns true iff `tile` holds a
    // standing fortification and this call fully handled the round (the
    // caller — CombatRoundEvent.Apply — must then return). False means the
    // tile is not a fort siege (razed forts leave Rubble and fall through
    // to the normal path, which sees no hostiles and ends the combat).
    //
    // Damage = Σ EffectivePower of every non-bandit unit hostile to the
    // fort's owner standing ON the fort tile (the rare trapped-on-parapet
    // case) or on a 4-neighbor. Order-independent sum over
    // world.Units.Values, so dictionary iteration order can't leak into
    // the hash. No unit-vs-unit damage here: co-location on a blocking
    // tile is impossible by construction, and attackers on distinct
    // adjacent tiles fight their own per-tile combats.
    internal static bool TryResolveFortRound(Simulation sim, TileCoord tile, CombatState state)
    {
        var world = sim.World;
        if (!world.Structures.TryGetValue(tile, out var fort)
            || !Fortification.IsStandingFortification(fort))
            return false;

        var diplomacy = world.Diplomacy;
        var damage = 0;
        foreach (var u in world.Units.Values)
        {
            if (u.IsEmbarked) continue;
            if (u.OwnerId == Sim.Core.Bandits.BanditConstants.OwnerId) continue;
            if (!diplomacy.AreHostile(u.OwnerId, fort.OwnerId)) continue;
            if (u.Position != tile && !Is4Adjacent(u.Position, tile)) continue;
            damage += CombatRules.EffectivePower(u, sim.Now);
        }

        // Nobody hostile in reach (they marched off / died / made peace) or
        // zero-power besiegers: the siege can never progress — end it.
        if (damage <= 0)
        {
            world.CombatStates.Remove(tile);
            return true;
        }

        fort.Health -= damage;
        if (fort.Health <= 0)
        {
            // The breach: Rubble replaces the fort (M24 pipeline) and the
            // tile becomes walkable ground again.
            Sim.Core.Sieges.SiegeDamage.RazeStructure(sim, fort);
            world.CombatStates.Remove(tile);
            return true;
        }

        var nextTick = sim.Now + world.CombatConfig.RoundIntervalTicks;
        state.RoundNumber++;
        state.NextRoundTick = nextTick;
        state.NextRoundSeq = sim.Schedule(nextTick, new CombatRoundEvent(tile));
        return true;
    }

    private static IEnumerable<TileCoord> Neighbors4(TileCoord t)
    {
        yield return new TileCoord(t.X, t.Y - 1);
        yield return new TileCoord(t.X + 1, t.Y);
        yield return new TileCoord(t.X, t.Y + 1);
        yield return new TileCoord(t.X - 1, t.Y);
    }

    private static bool Is4Adjacent(TileCoord a, TileCoord b)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        return (dx == 1 && dy == 0) || (dx == 0 && dy == 1);
    }
}
