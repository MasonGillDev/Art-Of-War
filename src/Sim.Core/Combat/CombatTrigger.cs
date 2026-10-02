using Sim.Core.World;

namespace Sim.Core.Combat;

// The combat trigger. Called from the tile entry (a walk's step across a tile edge)
// after the unit's position has updated and sight has been revealed: a tile with a hostile
// pair of owners opens a battlefield (Battlefields.OnPresenceChanged, docs/battlefield-grid.md);
// a tile holding attackers alone with a hostile destructible structure runs a siege
// (MaybeBeginSiege, decision D4).
public static class CombatTrigger
{
    public static void MaybeBeginCombatOnTile(Simulation sim, TileCoord tile) =>
        Sim.Core.Battlefields.Battlefields.OnPresenceChanged(sim, tile);

    // The siege rounds run when attackers are alone with a hostile destructible
    // structure (decision D4). Called by Battlefields when a tile holds no hostile
    // unit pair (nobody here is fighting anyone).
    internal static void MaybeBeginSiege(Simulation sim, TileCoord tile, IReadOnlyCollection<Unit> present)
    {
        var world = sim.World;
        if (present.Count == 0 || world.CombatStates.ContainsKey(tile)) return;
        var target = CombatRules.SiegeableStructureOn(world, tile);
        if (target is null) return;
        var owners = present.Select(u => u.OwnerId).Distinct().OrderBy(o => o);
        if (!CombatRules.AnyHostileToStructure(world.Diplomacy, owners, target.OwnerId)) return;
        var state = new CombatState(tile);
        var nextTick = sim.Now + world.CombatConfig.RoundIntervalTicks;
        state.RoundNumber = 1;
        state.NextRoundTick = nextTick;
        state.NextRoundSeq = sim.Schedule(nextTick, new CombatRoundEvent(tile));
        world.CombatStates[tile] = state;
    }
}
