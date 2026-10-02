namespace Sim.Core.Groups;

// Forms a Group from a set of units at a player-supplied rendezvous tile: since M46,
// CreateGroupIntent and MusterGroupIntent in one (kept for old intent logs and the
// one-click "form here"). Submission-time validation rejects the whole intent if any
// member is ineligible or unreachable (per locked decision: no partial formation).
//
// The group is Forming while its members walk to their places in the block around the
// rendezvous (GroupMuster); the last one in makes it Idle.
//
// Resolution-time re-validation per docs/intent-validation.md: members may
// have died / been grouped by some other intent / moved between submission
// and resolution; every check re-runs at Resolve time, mutates nothing on
// failure.
public sealed class FormGroupIntent : Intent
{
    public IReadOnlyList<int> UnitIds { get; }
    public TileCoord RendezvousTile { get; }

    [System.Text.Json.Serialization.JsonConstructor]
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
            if (unit.IsEmbarked)
                return IntentOutcome.Reject($"unit {id} is embarked on boat {unit.EmbarkedOn}");
        }

        // Reachability — every off-rendezvous member must have a path to it.
        // Reachability only: plain terrain. A road is an arc, not a tile
        // property, and this check needs a yes/no, not a price.
        foreach (var id in UnitIds)
        {
            var unit = world.Units[id];
            if (unit.Position == RendezvousTile) continue;
            var path = Pathfinding.FindPath(
                world.Grid,
                unit.Position,
                RendezvousTile,
                tile => world.Grid.TerrainCost(tile));
            if (path is null)
                return IntentOutcome.Reject(
                    $"unit {id} cannot reach rendezvous {RendezvousTile.X},{RendezvousTile.Y}");
        }

        // All checks passed — create the group and muster it at the rendezvous (M46: one
        // way to form up — each member walks to its own place in the block there).
        var group = new Group(GroupRules.NewId(world)) { OwnerId = PlayerId, State = GroupState.Dismissed };
        foreach (var id in UnitIds)
        {
            group.Members.Add(id);
            world.Units[id].GroupId = group.Id;
        }
        world.Groups[group.Id] = group;
        GroupMuster.Muster(sim, group, RendezvousTile);

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"FormGroupIntent(rendezvous={RendezvousTile.X},{RendezvousTile.Y}, members={UnitIds.Count})";
}
