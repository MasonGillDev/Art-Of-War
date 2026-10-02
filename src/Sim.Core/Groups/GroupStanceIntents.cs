using Sim.Core.World;

namespace Sim.Core.Groups;

// M49 — stance (docs/m49-group-stance-spec.md; what each stance means: GroupStances).

// The player's one choice: Passive, Defensive or Aggressive. Set on a group of groups,
// it sets every group under it.
public sealed class SetGroupStanceIntent : Intent
{
    public int GroupId { get; }
    public GroupStance Stance { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SetGroupStanceIntent(int groupId, GroupStance stance)
    {
        GroupId = groupId;
        Stance = stance;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        if (!sim.World.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        if (!Enum.IsDefined(Stance))
            return IntentOutcome.Reject($"{(byte)Stance} is not a stance");
        foreach (var g in GroupMuster.Subtree(sim.World, group)) g.Stance = Stance;
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"SetGroupStanceIntent(group={GroupId}, {Stance})";
}

// An Aggressive group charges a hostile it can see, within its leash: it marches to the
// target's tile (the fight opens on contact) and remembers where to go back to. The
// stance driver (Sim.Server) submits it, and again as the target moves; a player may too.
public sealed class ChargeGroupIntent : Intent
{
    public int GroupId { get; }
    public int TargetUnitId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public ChargeGroupIntent(int groupId, int targetUnitId)
    {
        GroupId = groupId;
        TargetUnitId = targetUnitId;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        var target = world.Units.GetValueOrDefault(TargetUnitId);
        if (GroupStances.ChargeRefusal(world, group, target, TargetUnitId) is { } why)
            return IntentOutcome.Reject(why);
        if (group.PathFinalDest == target!.Position && group.State == GroupState.Moving)
            return IntentOutcome.Applied;   // already on its way there
        if (GroupMarch.Plan(world, group, target.Position, sim.Now) is not { } path)
            return IntentOutcome.Reject($"no way to unit {TargetUnitId}");
        GroupStances.Charge(sim, group, target, path);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"ChargeGroupIntent(group={GroupId} -> unit {TargetUnitId})";
}
