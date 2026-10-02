using Sim.Core.World;

namespace Sim.Core.Groups;

// M46 Phase D — calling a group together and sending it home
// (docs/m46-groups-spec.md, "Muster" and "Dismiss"; GroupMuster does the work).

// Call a group, and every group under it, to a tile. Members finish the jobs that must
// finish (a delivery, a fight, a birth), keep the slots of the jobs they leave, and
// form up in one block around the anchor, spilling onto the tiles around it.
public sealed class MusterGroupIntent : Intent
{
    public int GroupId { get; }
    public TileCoord Anchor { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public MusterGroupIntent(int groupId, TileCoord anchor)
    {
        GroupId = groupId;
        Anchor = anchor;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        if (GroupMuster.AnchorRefusal(world, PlayerId, Anchor) is { } why)
            return IntentOutcome.Reject(why);
        GroupMuster.Muster(sim, group, Anchor);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"MusterGroupIntent(group={GroupId} -> {Anchor.X},{Anchor.Y})";
}

// Stand a group, and every group under it, down: each member goes back to what it was
// doing before the muster. The group keeps its name, members and place in the tree.
public sealed class DismissGroupIntent : Intent
{
    public int GroupId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public DismissGroupIntent(int groupId) { GroupId = groupId; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        if (!sim.World.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        GroupMuster.Dismiss(sim, group);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"DismissGroupIntent(group={GroupId})";
}
