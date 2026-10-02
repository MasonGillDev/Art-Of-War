namespace Sim.Core.Groups;

// Dissolves a Group; members become solo at their current positions. Valid
// in any group state (Forming, Idle, Moving).
//
// Cleanup discipline:
//   * Members' GroupId is cleared.
//   * If members were mid-walk (Forming-state rendezvous walks OR a Moving
//     group's per-hop arrivals), their pending events fence cleanly:
//       - the members' walks are dropped (Walk.Stop).
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

        // Drop every member's walk (a Moving group's march or a Forming member's
        // rendezvous walk); they stay wherever they are.
        group.BumpEpoch();
        MoveGroupIntent.Halt(sim, group);
        foreach (var memberId in group.Members)
            if (world.Units.TryGetValue(memberId, out var unit)) unit.GroupId = null;

        world.Groups.Remove(GroupId);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"DisbandGroupIntent(group={GroupId})";
}
