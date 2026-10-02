using Sim.Core.Movement;

namespace Sim.Core.Groups;

// Moves an Idle or Moving group toward a destination tile. M46 Phase C: THE GROUP
// WALKS (GroupMarch). It plans one lead path from its tile, the members march behind
// it in a column four abreast at the slowest member's pace, and at the destination
// they close into a block (FormationLayout). The group is Idle when the last member
// stands in its place.
//
// Retasking a Moving group drops the march under way (the epoch bump and the dropped
// anchor fence its pending step) and plans afresh from where the lead is. A group still
// mustering (Forming) may be moved: who has arrived marches, members finishing a job
// follow when free. A group of groups moves as its companies, each its own column.
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
        if (!world.Grid.InBounds(Destination))
            return IntentOutcome.Reject(
                $"destination {Destination.X},{Destination.Y} out of bounds");

        // A group of groups moves as its companies, each its own column (M46).
        var leaves = GroupMuster.Leaves(world, group);
        if (leaves.Count == 0)
            return IntentOutcome.Reject($"group {GroupId} has no companies to move");
        if (leaves.Any(l => l.State == GroupState.Dismissed))
            return IntentOutcome.Reject($"group {GroupId} is dismissed; muster it first");

        // Plan before touching anything: a refusal mutates nothing.
        var plans = new List<(Group Leaf, List<Sim.Core.Battlefields.WorldSubtile> Path)>();
        foreach (var leaf in leaves)
        {
            if (leaf.Position == Destination && leaf.State == GroupState.Idle) continue;   // already there
            if (GroupMarch.Plan(world, leaf, Destination, sim.Now) is not { } path)
                return IntentOutcome.Reject(
                    $"no path for group {leaf.Id} from {leaf.Position.X},{leaf.Position.Y} " +
                    $"to {Destination.X},{Destination.Y}");
            plans.Add((leaf, path));
        }

        // A group still mustering is moved too: who has arrived marches, the rest follow.
        foreach (var (leaf, path) in plans)
        {
            Halt(sim, leaf);
            leaf.BumpEpoch();
            GroupMarch.Begin(sim, leaf, path);
        }
        if (group.Kind == GroupKind.Groups) group.Position = Destination;
        return IntentOutcome.Applied;
    }

    // Stop the group's march: the column (if any) is dropped and every member's walk
    // stops where it stands; the group is Idle. (Retasking, a disband.)
    internal static void Halt(Simulation sim, Group group)
    {
        GroupMarch.DropColumn(sim.World, group);
        // A member finishing a job (a hauler delivering) carries on with it.
        foreach (var id in group.Members)
            if (sim.World.Units.TryGetValue(id, out var m) && GroupMuster.Busy(sim.World, m) is null) Walk.Stop(m);
        group.PathFinalDest = null;
        group.PendingArrivals = 0;
        if (group.State is GroupState.Moving or GroupState.Forming) group.State = GroupState.Idle;
        group.RendezvousTile = null;
        group.Awaiting.Clear();
    }

    public override string Describe() =>
        $"MoveGroupIntent(group={GroupId} -> {Destination.X},{Destination.Y})";
}
