using Sim.Core.Battlefields;
using Sim.Core.World;

namespace Sim.Core.Groups;

// M50 — march modes and saved formations (docs/m50-march-formations-spec.md).

// Single file or Column, for every march the group makes until switched. Set on a group
// of groups, it sets every group under it. A march under way re-forms at once.
public sealed class SetGroupMarchModeIntent : Intent
{
    public int GroupId { get; }
    public MarchMode Mode { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SetGroupMarchModeIntent(int groupId, MarchMode mode)
    {
        GroupId = groupId;
        Mode = mode;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        if (!sim.World.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        if (!Enum.IsDefined(Mode))
            return IntentOutcome.Reject($"{(byte)Mode} is not a march mode");
        foreach (var g in GroupMuster.Subtree(sim.World, group)) g.MarchMode = Mode;
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"SetGroupMarchModeIntent(group={GroupId}, {Mode})";
}

// Arranging a formation in the world: while the group stands formed, one member walks to a
// chosen subtile near it (tile + subtile 0..3). The group stays formed. Onto a spot another
// member of the group stands on, the two SWAP (each walks to the other's spot, passing
// through each other), so a crowded formation can be rearranged one move at a time.
public sealed class ArrangeGroupMemberIntent : Intent
{
    public const int ArrangeRadius = 4;   // tiles from where the group stands

    public int GroupId { get; }
    public int UnitId { get; }
    public TileCoord Tile { get; }
    public int SubX { get; }
    public int SubY { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public ArrangeGroupMemberIntent(int groupId, int unitId, TileCoord tile, int subX, int subY)
    {
        GroupId = groupId;
        UnitId = unitId;
        Tile = tile;
        SubX = subX;
        SubY = subY;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        if (group.State != GroupState.Idle || group.Kind != GroupKind.Units)
            return IntentOutcome.Reject($"group {GroupId} must be standing formed to arrange it");
        if (!group.Members.Contains(UnitId) || !world.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} is not in group {GroupId}");
        if (UnitAvailability.Busy(world, unit) is var busy and not (BusyReason.None or BusyReason.Walking))
            return IntentOutcome.Reject($"unit {UnitId} is {UnitAvailability.Describe(busy)}");
        if (SubX is < 0 or >= Subtile.Size || SubY is < 0 or >= Subtile.Size || !world.Grid.InBounds(Tile))
            return IntentOutcome.Reject("not a subtile on the map");
        if (Math.Max(Math.Abs(Tile.X - group.Position.X), Math.Abs(Tile.Y - group.Position.Y)) > ArrangeRadius)
            return IntentOutcome.Reject($"too far from the group (within {ArrangeRadius} tiles)");
        var spot = WorldSubtile.Of(Tile, new Subtile(SubX, SubY));
        if (!new SubtileStepRules(world, StepMover.Of(unit), null).CanStand(spot))
            return IntentOutcome.Reject("no one can stand there");
        if (unit.Subtile is not { } sub) return IntentOutcome.Reject($"unit {UnitId} has no place to move from");
        var from = WorldSubtile.Of(unit.Position, sub);
        if (from == spot) return IntentOutcome.Applied;
        Unit? occupant = null;
        if (Sim.Core.Movement.Walk.HeldByStanding(world, unit, Tile, new Subtile(SubX, SubY)))
        {
            occupant = world.Units.Values.FirstOrDefault(o => o != unit && o.Position == Tile
                && o.Subtile == new Subtile(SubX, SubY) && !o.IsWalking && !o.IsEmbarked);
            if (occupant is null || occupant.GroupId != GroupId)
                return IntentOutcome.Reject("someone outside the group is standing there");
        }
        // Plan both walks before taking either: a refusal mutates nothing.
        var mover = StepMover.Of(unit);
        if (Sim.Core.Movement.SubtilePathfinder.Find(world, mover, from, spot, null, now: sim.Now) is not { Count: > 0 })
            return IntentOutcome.Reject($"unit {UnitId} can't get there");
        if (occupant is not null
            && Sim.Core.Movement.SubtilePathfinder.Find(world, StepMover.Of(occupant), spot, from, null, now: sim.Now) is not { Count: > 0 })
            return IntentOutcome.Reject($"unit {occupant.Id} can't make room");
        GroupMuster.WalkToPlace(sim, unit, spot);
        if (occupant is not null) GroupMuster.WalkToPlace(sim, occupant, from);
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"ArrangeGroupMemberIntent(group={GroupId}, unit={UnitId} -> {Tile.X},{Tile.Y}/{SubX},{SubY})";
}

// Save the group's formation as it stands: every member's place relative to the leader,
// with `Front` as the side the group faces. The leader walks the march path; the others
// hold their places around it, turned to face the way the group travels.
public sealed class SaveGroupFormationIntent : Intent
{
    public const int SaveRadius = 8;   // tiles from the leader a member may stand

    public int GroupId { get; }
    public int LeaderId { get; }
    public Heading Front { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SaveGroupFormationIntent(int groupId, int leaderId, Heading front)
    {
        GroupId = groupId;
        LeaderId = leaderId;
        Front = front;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        if (group.State != GroupState.Idle || group.Kind != GroupKind.Units)
            return IntentOutcome.Reject($"group {GroupId} must be standing formed to save its formation");
        if (!Enum.IsDefined(Front))
            return IntentOutcome.Reject($"{(byte)Front} is not a side");
        if (!group.Members.Contains(LeaderId) || !world.Units.TryGetValue(LeaderId, out var leader) || leader.Subtile is not { } ls)
            return IntentOutcome.Reject($"unit {LeaderId} can't lead group {GroupId}");
        var origin = WorldSubtile.Of(leader.Position, ls);

        var slots = new List<FormationSlot>();
        var seen = new HashSet<(int, int)>();
        foreach (var id in group.Members)
        {
            if (!world.Units.TryGetValue(id, out var m)) continue;
            if (UnitAvailability.Busy(world, m) != BusyReason.None || m.Subtile is not { } sub)
                return IntentOutcome.Reject($"unit {id} is not standing in place");
            if (Math.Max(Math.Abs(m.Position.X - leader.Position.X), Math.Abs(m.Position.Y - leader.Position.Y)) > SaveRadius)
                return IntentOutcome.Reject($"unit {id} is too far from the leader (within {SaveRadius} tiles)");
            var here = WorldSubtile.Of(m.Position, sub);
            var (a, b) = Formations.ToFrame(here.X - origin.X, here.Y - origin.Y, Front);
            if (!seen.Add((a, b))) return IntentOutcome.Reject($"two members share a place");
            slots.Add(new FormationSlot(id, m.Role, a, b));
        }
        group.Formation = new SavedFormation { Front = Front, Slots = slots };
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"SaveGroupFormationIntent(group={GroupId}, leader={LeaderId}, front={Front})";
}

// Back to the default formation (four abreast).
public sealed class ClearGroupFormationIntent : Intent
{
    public int GroupId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public ClearGroupFormationIntent(int groupId) { GroupId = groupId; }

    public override IntentOutcome Resolve(Simulation sim)
    {
        if (!sim.World.Groups.TryGetValue(GroupId, out var group))
            return IntentOutcome.Reject($"group {GroupId} does not exist");
        if (group.OwnerId != PlayerId)
            return IntentOutcome.Reject($"group {GroupId} not owned by player {PlayerId}");
        group.Formation = null;
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"ClearGroupFormationIntent(group={GroupId})";
}
