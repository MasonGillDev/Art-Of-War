using Sim.Core.Groups;
using Sim.Core.World;

namespace Sim.Core.Combat;

// M7 Phase B — the combat trigger. Called from MoveArrivalEvent.Apply
// and GroupArrivalEvent.Apply *after* the unit/group's position has
// updated and sight has been revealed. Checks the arrived-at tile for
// hostile co-occupation; if any hostile pair of owners is co-located,
// pins every belligerent's in-flight movement (zone-of-control) and —
// if no combat is already active here — schedules round 1 and stores a
// per-tile anchor on world.CombatStates.
//
// ORDER NOTE: the contested-tile early-return is below the pin call by
// design. Pinning must also run when a newcomer joins an active fight
// (otherwise reinforcement walks straight through), so we cannot bail
// before pinning. Don't "simplify" by hoisting the contested check.
//
// Fence: an arrival landing on an already-contested tile pins the
// newcomer and returns without rescheduling. The next CombatRoundEvent's
// re-gather picks up the new arrival on its own — that's how
// reinforcement composes into the round loop.
public static class CombatTrigger
{
    public static void MaybeBeginCombatOnTile(Simulation sim, TileCoord tile)
    {
        var world = sim.World;
        var diplomacy = world.Diplomacy;

        // Distinct owners physically present. Embarked passengers are off-tile
        // (Position frozen at the dock) and don't fight — exclude them, mirroring
        // CombatRules.GatherForcesOnTile. Without this filter a hostile boat
        // arriving at a dock with embarked passengers would write a phantom
        // CombatStates entry the next round would immediately clear.
        var owners = new SortedSet<int>();
        foreach (var u in world.Units.Values)
            if (u.Position == tile && !u.IsEmbarked) owners.Add(u.OwnerId);
        if (owners.Count < 2) return;

        // Any hostile pair? Iterate in canonical order so the decision is
        // deterministic across runs.
        var ownerArr = owners.ToArray();
        var hostile = false;
        for (var i = 0; i < ownerArr.Length && !hostile; i++)
            for (var j = i + 1; j < ownerArr.Length && !hostile; j++)
                if (diplomacy.AreHostile(ownerArr[i], ownerArr[j])) hostile = true;
        if (!hostile) return;

        // ENGAGEMENT PIN: a force cannot walk THROUGH a hostile one. Cancel the
        // committed movement of every belligerent on the tile so it stops here
        // and fights. Without this a mover on a fast road (any hop <
        // RoundIntervalTicks) leaves before round 1 fires and takes zero damage
        // — defeating blockades and caravan-raiding (design §8.6/§9.1). Runs on
        // combat START *and* when a newcomer joins an active fight, so
        // reinforcements that march in also stop.
        PinBelligerents(sim, tile, owners);

        // Already contested → round already scheduled; next round's re-gather
        // picks up the newcomer (reinforcement falls out for free).
        if (world.CombatStates.ContainsKey(tile)) return;

        var state = new CombatState(tile);
        var nextTick = sim.Now + world.CombatConfig.RoundIntervalTicks;
        state.RoundNumber = 1;
        state.NextRoundTick = nextTick;
        state.NextRoundSeq = sim.Schedule(nextTick, new CombatRoundEvent(tile));
        world.CombatStates[tile] = state;
    }

    // Cancel in-flight movement for every belligerent on `tile` (a unit whose
    // owner is hostile to another owner present — neutrals passing through are
    // NOT pinned). Solo / rendezvous-walk movement lives on the unit; group
    // movement lives on the group — pin whichever applies. Bumping the epoch
    // fences the already-queued onward arrival (Move/GroupArrivalEvent both
    // no-op on ExpectedEpoch mismatch).
    private static void PinBelligerents(Simulation sim, TileCoord tile, SortedSet<int> owners)
    {
        var world = sim.World;
        var diplomacy = world.Diplomacy;
        HashSet<int>? pinnedGroups = null;

        foreach (var u in world.Units.Values)
        {
            if (u.Position != tile || u.IsEmbarked) continue;

            var belligerent = false;
            foreach (var o in owners)
                if (o != u.OwnerId && diplomacy.AreHostile(u.OwnerId, o)) { belligerent = true; break; }
            if (!belligerent) continue;

            // Solo unit or Forming-group rendezvous walker (unit-level anchors).
            if (u.PathRemaining is not null || u.NextArrivalSeq is not null)
            {
                u.PathRemaining = null;
                u.PathFinalDest = null;
                u.NextArrivalTick = null;
                u.NextArrivalSeq = null;
                u.BumpEpoch();
            }

            // Moving group (group-level anchors) — pin once per group.
            if (u.GroupId is int gid)
            {
                pinnedGroups ??= new HashSet<int>();
                if (pinnedGroups.Add(gid)
                    && world.Groups.TryGetValue(gid, out var group)
                    && (group.PathRemaining is not null || group.NextArrivalSeq is not null))
                {
                    MoveGroupIntent.ClearMovementAnchors(group);
                    group.State = GroupState.Idle;
                    group.BumpEpoch();
                }
            }
        }
    }
}
