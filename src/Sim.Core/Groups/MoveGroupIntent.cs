using Sim.Core.Movement;

namespace Sim.Core.Groups;

// Moves an Idle or Moving group toward a destination tile. M46 Phase C: THE GROUP
// WALKS (GroupMarch). It plans one lead path from its tile, the members march behind
// it in a column four abreast at the slowest member's pace, and at the destination
// they close into a block (FormationLayout). The group is Idle when the last member
// stands in its place.
//
// Retasking a Moving group drops the march under way (the epoch bump and the dropped
// anchor fence its pending step) and plans afresh from where the lead is. Forming
// groups cannot be moved: they are in their walk-to-rendezvous integrity period. The
// player waits or Disbands.
public sealed class MoveGroupIntent : Intent
{
    public int GroupId { get; }
    public TileCoord Destination { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public MoveGroupIntent(int groupId, TileCoord destination)
    {
        GroupId = groupId;
        Destination = destination;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;

        if (!world.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        if (group.Kind != GroupKind.Units)
            return IntentOutcome.Reject($"group {GroupId} holds groups; muster it instead");
        if (group.State == GroupState.Dismissed)
            return IntentOutcome.Reject($"group {GroupId} is dismissed; muster it first");
        if (group.State == GroupState.Forming)
            return IntentOutcome.Reject($"group {GroupId} is still forming");
        if (!world.Grid.InBounds(Destination))
            return IntentOutcome.Reject(
                $"destination {Destination.X},{Destination.Y} out of bounds");

        if (group.Position == Destination && group.State != GroupState.Moving)
            return IntentOutcome.Applied;   // already there

        // Plan before touching anything: a refusal mutates nothing.
        if (GroupMarch.Plan(world, group, Destination, sim.Now) is not { } path)
            return IntentOutcome.Reject(
                $"no path for group {GroupId} from {group.Position.X},{group.Position.Y} " +
                $"to {Destination.X},{Destination.Y}");

        Halt(sim, group);
        group.BumpEpoch();
        GroupMarch.Begin(sim, group, path);
        return IntentOutcome.Applied;
    }

    // Stop the group's march: the column (if any) is dropped and every member's walk
    // stops where it stands; the group is Idle. (Retasking, a disband.)
    internal static void Halt(Simulation sim, Group group)
    {
        GroupMarch.DropColumn(sim.World, group);
        foreach (var id in group.Members)
            if (sim.World.Units.TryGetValue(id, out var m)) Walk.Stop(m);
        group.PathFinalDest = null;
        group.PendingArrivals = 0;
        if (group.State == GroupState.Moving) group.State = GroupState.Idle;
    }

    public override string Describe() =>
        $"MoveGroupIntent(group={GroupId} -> {Destination.X},{Destination.Y})";
}
