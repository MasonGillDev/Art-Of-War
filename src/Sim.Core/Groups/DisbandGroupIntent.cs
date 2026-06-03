namespace Sim.Core.Groups;

// Dissolves a Group; members become solo at their current positions. Valid
// in any group state (Forming, Idle, Moving).
//
// Cleanup discipline:
//   * Members' GroupId is cleared.
//   * If members were mid-walk (Forming-state rendezvous walks OR a Moving
//     group's per-hop arrivals), their pending events fence cleanly:
//       - Group.MovementEpoch bumps → in-flight GroupArrivalEvents fence.
//       - For each Forming member walking solo, Unit.BumpEpoch() →
//         their MoveArrivalEvents fence and the walk stops at next pop.
//     The members stay wherever the previous arrival left them.
//   * Group removed from world.Groups.
public sealed class DisbandGroupIntent : Intent
{
    public int GroupId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public DisbandGroupIntent(int groupId) { GroupId = groupId; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");

        // Fence any in-flight GroupArrivalEvent from the group's own chain.
        group.BumpEpoch();
        MoveGroupIntent.ClearMovementAnchors(group);

        // Clear membership + cancel any per-member rendezvous walk.
        foreach (var memberId in group.Members)
        {
            if (!world.Units.TryGetValue(memberId, out var unit)) continue;
            unit.GroupId = null;
            // If the member was walking solo to a rendezvous, fence their
            // MoveArrivalEvents and clear the path. Their position stays at
            // wherever the last arrival left them.
            if (unit.PathRemaining is not null)
            {
                unit.BumpEpoch();
                unit.PathRemaining = null;
                unit.PathFinalDest = null;
                unit.NextArrivalTick = null;
                unit.NextArrivalSeq  = null;
            }
        }

        world.Groups.Remove(GroupId);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"DisbandGroupIntent(group={GroupId})";
}
