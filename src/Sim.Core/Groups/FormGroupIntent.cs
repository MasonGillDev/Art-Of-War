namespace Sim.Core.Groups;

// Forms a Group from a set of units at a player-supplied rendezvous tile.
// Submission-time validation rejects the whole intent if any member is
// ineligible or unreachable (per locked decision: no partial formation).
//
// On success, creates the Group at State=Forming, sets every member's
// GroupId, and dispatches MoveIntent.BeginMove for off-rendezvous members.
// Each member's MoveArrivalEvent on reaching the rendezvous decrements
// PendingArrivals via DispatchOnFinalArrival; when zero, the group
// transitions to Idle.
//
// Resolution-time re-validation per docs/intent-validation.md: members may
// have died / been grouped by some other intent / moved between submission
// and resolution; every check re-runs at Resolve time, mutates nothing on
// failure.
public sealed class FormGroupIntent : Intent
{
    public IReadOnlyList<int> UnitIds { get; }
    public TileCoord RendezvousTile { get; }

    public FormGroupIntent(IReadOnlyList<int> unitIds, TileCoord rendezvousTile)
    {
        UnitIds = unitIds;
        RendezvousTile = rendezvousTile;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;

        if (UnitIds.Count == 0)
            return IntentOutcome.Reject("FormGroup requires at least one unit");
        if (!world.Grid.InBounds(RendezvousTile))
            return IntentOutcome.Reject(
                $"rendezvous {RendezvousTile.X},{RendezvousTile.Y} out of bounds");

        // Per-member eligibility.
        foreach (var id in UnitIds)
        {
            if (!world.Units.TryGetValue(id, out var unit))
                return IntentOutcome.Reject($"unit {id} does not exist");
            if (unit.OwnerId != PlayerId)
                return IntentOutcome.Reject($"unit {id} not owned by player {PlayerId}");
            if (unit.Activity != Activity.Idle)
                return IntentOutcome.Reject($"unit {id} is not Idle");
            if (unit.GroupId is not null)
                return IntentOutcome.Reject($"unit {id} is already in a group {unit.GroupId}");
        }

        // Reachability — every off-rendezvous member must have a path to it.
        // Uses the same road-aware cost the move chain will use.
        var now = sim.Now;
        foreach (var id in UnitIds)
        {
            var unit = world.Units[id];
            if (unit.Position == RendezvousTile) continue;
            var path = Pathfinding.FindPath(
                world.Grid,
                unit.Position,
                RendezvousTile,
                tile => Road.EffectiveCost(world, tile, now));
            if (path is null)
                return IntentOutcome.Reject(
                    $"unit {id} cannot reach rendezvous {RendezvousTile.X},{RendezvousTile.Y}");
        }

        // All checks passed — create the group.
        var groupId = NextGroupId(world);
        var group = new Group(groupId) { OwnerId = PlayerId };
        group.Position = RendezvousTile;
        group.RendezvousTile = RendezvousTile;

        var pending = 0;
        foreach (var id in UnitIds)
        {
            group.Members.Add(id);
            var unit = world.Units[id];
            unit.GroupId = groupId;
            if (unit.Position != RendezvousTile) pending++;
        }
        group.PendingArrivals = pending;
        group.State = pending == 0 ? GroupState.Idle : GroupState.Forming;
        if (group.State == GroupState.Idle) group.RendezvousTile = null;

        world.Groups[groupId] = group;

        // Dispatch walks for off-rendezvous members. We add the group to
        // world.Groups BEFORE scheduling so MoveArrivalEvent's
        // DispatchOnFinalArrival can find it.
        foreach (var id in UnitIds)
        {
            var unit = world.Units[id];
            if (unit.Position == RendezvousTile) continue;
            // Bump epoch so any stale move chain on the unit fences out before
            // we begin a fresh chain.
            unit.BumpEpoch();
            MoveIntent.BeginMove(sim, unit, RendezvousTile);
        }

        return IntentOutcome.Applied;
    }

    private static int NextGroupId(GameWorld world)
    {
        // Monotonic from 1. Group ids never reuse — that would break stale
        // GroupArrivalEvents from a defunct group firing on a new one.
        var max = 0;
        foreach (var k in world.Groups.Keys) if (k > max) max = k;
        return max + 1;
    }

    public override string Describe() =>
        $"FormGroupIntent(rendezvous={RendezvousTile.X},{RendezvousTile.Y}, members={UnitIds.Count})";
}
