namespace Sim.Core.Groups;

// Per-hop arrival for a Group. Fences on MovementEpoch (same M2 pattern as
// Unit's MoveArrivalEvent). Updates every member's position atomically —
// the whole group enters the tile in one event. Per-member side effects
// (road traffic, vision reveal) fire once per member; the second member's
// CreditTraffic reads the post-first state, so diminishing returns stack
// naturally inside the burst.
public sealed class GroupArrivalEvent : ScheduledEvent
{
    public int GroupId { get; }
    public TileCoord To { get; }
    public TileCoord FinalDestination { get; }
    public byte ExpectedEpoch { get; }

    public GroupArrivalEvent(int groupId, TileCoord to, TileCoord finalDest, byte expectedEpoch)
    {
        GroupId = groupId;
        To = to;
        FinalDestination = finalDest;
        ExpectedEpoch = expectedEpoch;
    }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Groups.TryGetValue(GroupId, out var group))
        {
            Outcome = IntentOutcome.Reject($"group {GroupId} no longer exists");
            return;
        }
        if (group.MovementEpoch != ExpectedEpoch)
        {
            Outcome = IntentOutcome.Reject("stale (epoch mismatch)");
            return;
        }

        // HARD CAP: a group hop would deposit `group.Members.Count` units
        // onto `To` (all members at once, atomic arrival semantics from M5).
        // The current occupants of `To` are all non-members (group invariant:
        // members are at group.Position == From). Reject the whole arrival
        // if it'd exceed the cap; group yields on its previous tile, becomes
        // Idle, the player can re-task. Bumps MovementEpoch so any queued
        // GroupArrivalEvents from this chain fence on fire.
        var existingOnDest = MovementCost.CountUnitsOnTile(world, To);
        if (existingOnDest + group.Members.Count > MovementConstants.MaxUnitsPerTile)
        {
            MoveGroupIntent.ClearMovementAnchors(group);
            group.State = GroupState.Idle;
            group.BumpEpoch();
            Outcome = IntentOutcome.Reject(
                $"tile {To.X},{To.Y} cannot hold group ({existingOnDest} existing + {group.Members.Count} members > cap {MovementConstants.MaxUnitsPerTile})");
            return;
        }

        // Atomic per-member position update + per-member side effects.
        foreach (var memberId in group.Members)
        {
            if (!world.Units.TryGetValue(memberId, out var unit)) continue;
            unit.Position = To;
            // Each member is a real arrival on this tile.
            Road.CreditTraffic(world, To, sim.Now);
            Sight.Reveal(world, unit.OwnerId, To, Sight.RadiusFor(unit.Role), sim.Now);
        }

        // Group's own position follows.
        group.Position = To;

        // M7: presence-gated combat trigger. The entire group lands on To
        // in one event, so a single trigger check on the arrived tile
        // suffices (each member's owner is already represented in the
        // gather).
        Sim.Core.Combat.CombatTrigger.MaybeBeginCombatOnTile(sim, To);

        // Pop the tile we just entered from the committed path.
        if (group.PathRemaining is not null && group.PathRemaining.Count > 0)
            group.PathRemaining.RemoveAt(0);

        var atFinal = group.PathRemaining is null || group.PathRemaining.Count == 0;
        if (atFinal)
        {
            MoveGroupIntent.ClearMovementAnchors(group);
            group.State = GroupState.Idle;
            return;
        }

        // Per-hop continuation: schedule next arrival.
        MoveGroupIntent.ScheduleNextHop(sim, group);
    }

    public override string Describe() =>
        $"GroupArrival(group={GroupId} @ {To.X},{To.Y}, dest={FinalDestination.X},{FinalDestination.Y})";
}
