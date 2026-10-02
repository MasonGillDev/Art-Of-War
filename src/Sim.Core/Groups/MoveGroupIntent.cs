using Sim.Core.Movement;

namespace Sim.Core.Groups;

// Moves an Idle or Moving group toward a destination tile (M43: the members walk).
//
// Each member walks its OWN subtile route to the ordered tile, to a subtile of its own:
// the free subtiles nearest the tile's centre, one each (`reserved` keeps them apart).
// The group is Moving until the last member has stopped walking (PendingArrivals), then
// Idle at the ordered tile. Step 5 of docs/m43-status.md will make a group keep its
// members' relative places while it marches; today they take their own paths and set
// the pace one by one.
//
// Retasking a Moving group replaces every member's walk. Forming groups cannot be
// moved: they are in their walk-to-rendezvous integrity period. The player waits or
// Disbands.
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

        Halt(sim, group);
        group.BumpEpoch();

        // Every member walks to its own subtile of the ordered tile, planned as the
        // owner sees the world (Walk.Begin). Lowest id first, so the same order gives
        // the same places.
        var reserved = new HashSet<Sim.Core.Battlefields.WorldSubtile>();
        var walking = 0;
        foreach (var id in group.Members)
        {
            if (!world.Units.TryGetValue(id, out var unit) || unit.IsEmbarked) continue;
            Walk.Begin(sim, unit, Destination, reserved);
            if (unit.IsWalking) walking++;
        }

        if (walking == 0)
        {
            // Nobody could walk: everyone is already there, or there is nowhere to go.
            if (group.Members.All(id => !world.Units.TryGetValue(id, out var m) || m.Position == Destination || m.IsEmbarked))
            {
                group.Position = Destination;
                return IntentOutcome.Applied;
            }
            return IntentOutcome.Reject(
                $"no path for group {GroupId} from {group.Position.X},{group.Position.Y} " +
                $"to {Destination.X},{Destination.Y}");
        }

        group.PathFinalDest = Destination;
        group.PendingArrivals = walking;
        group.State = GroupState.Moving;
        return IntentOutcome.Applied;
    }

    // Stop the group's march: every member's walk is dropped where it stands and the group
    // is Idle. (Retasking, a fight, a disband: the member walks ARE the group's movement.)
    internal static void Halt(Simulation sim, Group group)
    {
        foreach (var id in group.Members)
            if (sim.World.Units.TryGetValue(id, out var m)) Walk.Stop(m);
        group.PathFinalDest = null;
        group.PendingArrivals = 0;
        if (group.State == GroupState.Moving) group.State = GroupState.Idle;
    }

    public override string Describe() =>
        $"MoveGroupIntent(group={GroupId} -> {Destination.X},{Destination.Y})";
}
