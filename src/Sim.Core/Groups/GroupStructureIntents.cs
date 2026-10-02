using Sim.Core.World;

namespace Sim.Core.Groups;

// M46 Phase E — reshaping groups (docs/m46-groups-spec.md, "Intents"): merging two
// groups into one, and splitting members out.

// Fold group `FromId` into group `IntoId`, then remove `FromId`. Both hold the same
// kind of thing. Units take `IntoId`'s orders: into a dismissed group they go back to
// their saved tasks; into a group under command they are called to its block. A
// marching group takes nobody until it stops.
public sealed class MergeGroupsIntent : Intent
{
    public int FromId { get; }
    public int IntoId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public MergeGroupsIntent(int fromId, int intoId)
    {
        FromId = fromId;
        IntoId = intoId;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (FromId == IntoId) return IntentOutcome.Reject("a group can't merge into itself");
        if (!world.Groups.TryGetValue(FromId, out var from))
            return IntentOutcome.Reject($"group {FromId} does not exist");
        if (!world.Groups.TryGetValue(IntoId, out var into))
            return IntentOutcome.Reject($"group {IntoId} does not exist");
        if (from.OwnerId != PlayerId || into.OwnerId != PlayerId)
            return IntentOutcome.Reject($"both groups must be player {PlayerId}'s");
        if (from.Kind != into.Kind)
            return IntentOutcome.Reject("a group of units and a group of groups can't merge");
        if (from.State == GroupState.Moving || into.State == GroupState.Moving)
            return IntentOutcome.Reject("a marching group can't merge; stop it first");
        if (Sim.Core.Hauling.RouteCrews.Find(world, from) is not null || Sim.Core.Hauling.RouteCrews.Find(world, into) is not null)
            return IntentOutcome.Reject("a group crewing a route can't merge; take it off the route first");

        if (from.Kind == GroupKind.Groups)
        {
            // Every child must fit under `into`: no cycle, no tree past MaxDepth.
            foreach (var cid in from.Children)
                if (world.Groups.TryGetValue(cid, out var child)
                    && GroupRules.ParentRefusal(world, PlayerId, child, IntoId, GroupRules.Height(world, child)) is { } why)
                    return IntentOutcome.Reject(why);
            foreach (var cid in from.Children.ToList())
                if (world.Groups.TryGetValue(cid, out var child)) GroupRules.SetParent(world, child, IntoId);
            GroupRules.Discard(world, from);
            return IntentOutcome.Applied;
        }

        var units = from.Members.Where(world.Units.ContainsKey).Select(id => world.Units[id]).ToList();
        MoveGroupIntent.Halt(sim, from);
        foreach (var u in units)
        {
            from.Members.Remove(u.Id);
            into.Members.Add(u.Id);
            u.GroupId = into.Id;
        }
        if (into.State == GroupState.Dismissed)
        {
            // Into a dismissed group: anyone called to `from` goes back to work.
            if (from.State != GroupState.Dismissed)
                foreach (var u in units) GroupMuster.Release(sim, into, u);
        }
        else GroupMuster.CallIn(sim, into, units);
        GroupRules.Discard(world, from);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"MergeGroupsIntent({FromId} into {IntoId})";
}

// Take members out of a group: into a new group of their own (NewName given: it sits
// under the same parent and starts dismissed), or solo (NewName null). Anyone who was
// under the group's command goes back to its saved task. A marching group splits once
// it has stopped.
public sealed class SplitGroupIntent : Intent
{
    public int GroupId { get; }
    public IReadOnlyList<int> UnitIds { get; }
    public string? NewName { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SplitGroupIntent(int groupId, IReadOnlyList<int> unitIds, string? newName = null)
    {
        GroupId = groupId;
        UnitIds = unitIds ?? Array.Empty<int>();
        NewName = newName;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        if (group.Kind != GroupKind.Units)
            return IntentOutcome.Reject($"group {GroupId} holds groups; nest or lift them instead");
        if (group.State == GroupState.Moving)
            return IntentOutcome.Reject($"group {GroupId} is marching; split it when it stops");
        if (UnitIds.Count == 0) return IntentOutcome.Reject("no units named");
        if (UnitIds.Distinct().Count() != UnitIds.Count) return IntentOutcome.Reject("a unit is named twice");
        foreach (var id in UnitIds)
            if (!group.Members.Contains(id) || !world.Units.ContainsKey(id))
                return IntentOutcome.Reject($"unit {id} is not in group {GroupId}");
        var name = "";
        if (NewName is not null && GroupRules.CleanName(NewName, out name) is { } badName)
            return IntentOutcome.Reject(badName);

        Group? split = null;
        if (NewName is not null)
        {
            split = new Group(GroupRules.NewId(world)) { OwnerId = PlayerId, Name = name, State = GroupState.Dismissed };
            world.Groups[split.Id] = split;
            if (group.ParentId is { } pid) GroupRules.SetParent(world, split, pid);
        }
        foreach (var id in UnitIds)
        {
            var u = world.Units[id];
            group.Members.Remove(id);
            if (split is not null) { split.Members.Add(id); u.GroupId = split.Id; }
            else u.GroupId = null;
            if (group.State != GroupState.Dismissed) GroupMuster.Release(sim, group, u);
        }
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"SplitGroupIntent(group={GroupId}, {UnitIds.Count} units{(NewName is null ? ", solo" : $" → \"{NewName}\"")})";
}
