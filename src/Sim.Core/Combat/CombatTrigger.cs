using Sim.Core.World;

namespace Sim.Core.Combat;

// M7 Phase B — the combat trigger. Called from MoveArrivalEvent.Apply
// and GroupArrivalEvent.Apply *after* the unit/group's position has
// updated and sight has been revealed. Checks the arrived-at tile for
// hostile co-occupation; if any hostile pair of owners is co-located
// AND no combat is already active there, schedules round 1 and stores
// a per-tile anchor on world.CombatStates.
//
// Fence: an arrival landing on an already-contested tile finds the
// CombatStates entry, returns. The next CombatRoundEvent's re-gather
// will pick up the new arrival on its own — that's how reinforcement
// works without special-case wiring.
public static class CombatTrigger
{
    public static void MaybeBeginCombatOnTile(Simulation sim, TileCoord tile)
    {
        var world = sim.World;

        // Already contested → re-gather happens next round, no new schedule.
        if (world.CombatStates.ContainsKey(tile)) return;

        // Find distinct owner ids with units on the tile.
        var owners = new SortedSet<int>();
        foreach (var u in world.Units.Values)
        {
            if (u.Position == tile) owners.Add(u.OwnerId);
        }
        if (owners.Count < 2) return; // need at least two owners

        // Any hostile pair? Iterate in canonical order so the decision is
        // deterministic across runs.
        var diplomacy = world.Diplomacy;
        var ownerArr = owners.ToArray();
        var hostile = false;
        for (var i = 0; i < ownerArr.Length && !hostile; i++)
            for (var j = i + 1; j < ownerArr.Length && !hostile; j++)
                if (diplomacy.AreHostile(ownerArr[i], ownerArr[j])) hostile = true;
        if (!hostile) return;

        // Start combat: anchor + round-1 schedule.
        var state = new CombatState(tile);
        var nextTick = sim.Now + world.CombatConfig.RoundIntervalTicks;
        state.RoundNumber = 1;
        state.NextRoundTick = nextTick;
        state.NextRoundSeq = sim.Schedule(nextTick, new CombatRoundEvent(tile));
        world.CombatStates[tile] = state;
    }
}
