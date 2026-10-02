namespace Sim.Core.Groups;

// M46 — the group as a lasting record (docs/m46-groups-spec.md, "Intents").
// Creating, naming, nesting, joining and deleting a group. None of these moves a
// unit: a new group starts Dismissed, its members free.

// A new group, Dismissed. HoldsGroups = a parent (an army holding companies), which
// starts with no units; otherwise a leaf of the named units, all or nothing. ParentId
// places it in a tree at once.
public sealed class CreateGroupIntent : Intent
{
    public string Name { get; }
    public IReadOnlyList<int> UnitIds { get; }
    public int? ParentId { get; }
    public bool HoldsGroups { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public CreateGroupIntent(string name, IReadOnlyList<int> unitIds, int? parentId = null, bool holdsGroups = false)
    {
        Name = name;
        UnitIds = unitIds ?? Array.Empty<int>();
        ParentId = parentId;
        HoldsGroups = holdsGroups;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (GroupRules.CleanName(Name, out var name) is { } badName)
            return IntentOutcome.Reject(badName);
        if (HoldsGroups && UnitIds.Count > 0)
            return IntentOutcome.Reject("a group of groups holds no units");
        if (UnitIds.Distinct().Count() != UnitIds.Count)
            return IntentOutcome.Reject("a unit is named twice");
        foreach (var id in UnitIds)
            if (GroupRules.JoinRefusal(world.Units.GetValueOrDefault(id), id, PlayerId) is { } why)
                return IntentOutcome.Reject(why);
        if (ParentId is { } pid && GroupRules.ParentRefusal(world, PlayerId, null, pid, 1) is { } badParent)
            return IntentOutcome.Reject(badParent);

        var group = new Group(GroupRules.NewId(world))
        {
            OwnerId = PlayerId,
            Kind = HoldsGroups ? GroupKind.Groups : GroupKind.Units,
            Name = name,
            State = GroupState.Dismissed,
        };
        foreach (var id in UnitIds)
        {
            group.Members.Add(id);
            world.Units[id].GroupId = group.Id;
        }
        world.Groups[group.Id] = group;
        if (ParentId is { } parentId) GroupRules.SetParent(world, group, parentId);
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"CreateGroupIntent(\"{Name}\", {(HoldsGroups ? "groups" : $"{UnitIds.Count} units")}, parent={ParentId?.ToString() ?? "none"})";
}

public sealed class RenameGroupIntent : Intent
{
    public int GroupId { get; }
    public string Name { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public RenameGroupIntent(int groupId, string name)
    {
        GroupId = groupId;
        Name = name;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        if (!sim.World.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        if (GroupRules.CleanName(Name, out var name) is { } bad)
            return IntentOutcome.Reject(bad);
        group.Name = name;
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"RenameGroupIntent(group={GroupId}, \"{Name}\")";
}

// Nest a group under a parent (a group of groups), or lift it to the top level
// (ParentId null). No cycles; at most GroupConstants.MaxDepth levels.
public sealed class SetGroupParentIntent : Intent
{
    public int GroupId { get; }
    public int? ParentId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SetGroupParentIntent(int groupId, int? parentId)
    {
        GroupId = groupId;
        ParentId = parentId;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        if (group.ParentId == ParentId) return IntentOutcome.Applied;
        if (ParentId is { } pid
            && GroupRules.ParentRefusal(world, PlayerId, group, pid, GroupRules.Height(world, group)) is { } why)
            return IntentOutcome.Reject(why);
        GroupRules.SetParent(world, group, ParentId);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"SetGroupParentIntent(group={GroupId}, parent={ParentId?.ToString() ?? "none"})";
}

// Units join a leaf group, all or nothing. Joining a group under command calls the
// newcomers to it (GroupMuster.CallIn); a marching group takes nobody until it stops.
public sealed class AddToGroupIntent : Intent
{
    public int GroupId { get; }
    public IReadOnlyList<int> UnitIds { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public AddToGroupIntent(int groupId, IReadOnlyList<int> unitIds)
    {
        GroupId = groupId;
        UnitIds = unitIds ?? Array.Empty<int>();
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        if (group.Kind != GroupKind.Units)
            return IntentOutcome.Reject($"group {GroupId} holds groups, not units");
        if (group.State == GroupState.Moving)
            return IntentOutcome.Reject($"group {GroupId} is marching; add to it when it stops");
        if (UnitIds.Count == 0)
            return IntentOutcome.Reject("no units named");
        if (UnitIds.Distinct().Count() != UnitIds.Count)
            return IntentOutcome.Reject("a unit is named twice");
        foreach (var id in UnitIds)
            if (GroupRules.JoinRefusal(world.Units.GetValueOrDefault(id), id, PlayerId) is { } why)
                return IntentOutcome.Reject(why);

        foreach (var id in UnitIds)
        {
            group.Members.Add(id);
            world.Units[id].GroupId = group.Id;
        }
        var newcomers = UnitIds.Select(id => world.Units[id]).ToList();
        if (Sim.Core.Hauling.RouteCrews.Find(world, group) is not null && !group.RouteSuspended)
        {
            // M47 — joining a crew on its route: the route is the newcomer's task now; it
            // leaves its job and falls in at the crew's next march.
            foreach (var u in newcomers)
                if (GroupMuster.Busy(world, u) is null) { Sim.Core.Intents.Retask.Release(sim, u); Sim.Core.Movement.Walk.Stop(u); }
        }
        else if (group.State != GroupState.Dismissed)
            GroupMuster.CallIn(sim, group, newcomers);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"AddToGroupIntent(group={GroupId}, {UnitIds.Count} units)";
}

// Remove a group: its members go solo where they stand (walks stop), its children
// move to the top level. The record is gone; its id is never reused.
public sealed class DeleteGroupIntent : Intent
{
    public int GroupId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public DeleteGroupIntent(int groupId) { GroupId = groupId; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        if (!sim.World.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        GroupRules.Remove(sim, group);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"DeleteGroupIntent(group={GroupId})";
}
