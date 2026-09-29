namespace Sim.Core.Groups;

// Moves an Idle or Moving group toward a destination. Retasking a Moving
// group bumps the MovementEpoch, fencing any GroupArrivalEvent already in
// the queue from the prior chain. Same pattern as MoveIntent on a busy
// unit (M2 Phase 0).
//
// Forming groups cannot be moved — they're in their walk-to-rendezvous
// integrity period. Player must wait or Disband.
//
// Path is computed using a GROUP cost delegate: max(memberCost(tile))
// across members. Today every unit pays the same biome+road cost, so the
// max reduces to the base cost. When future unit types differ (carts on
// Mountain, horses on Grassland), the framework handles it without
// engine refactor.
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
        if (group.State == GroupState.Forming)
            return IntentOutcome.Reject($"group {GroupId} is still forming");
        if (!world.Grid.InBounds(Destination))
            return IntentOutcome.Reject(
                $"destination {Destination.X},{Destination.Y} out of bounds");

        if (group.Position == Destination)
        {
            // Trivially "complete"; clear any prior path anchors so a Moving
            // group going home becomes Idle.
            ClearMovementAnchors(group);
            group.State = GroupState.Idle;
            return IntentOutcome.Applied;
        }

        var now = sim.Now;
        // FOG-AWARE PLANNING: the group's owner is who the planner sees as.
        // A* avoids visibly-crowded tiles but cannot route around fog'd
        // congestion. See docs/movement-cost.md.
        var visibleTiles = View.VisibleTiles(world, group.OwnerId);
        var (path, dest) = PlanGroup(world, group, Destination, visibleTiles, now);
        if (path is not null && path.Count == 1)
        {
            // Aimed at water right beside it: the nearest land is where it stands.
            ClearMovementAnchors(group);
            group.State = GroupState.Idle;
            return IntentOutcome.Applied;
        }
        if (path is null || path.Count < 2)
            return IntentOutcome.Reject(
                $"no path for group {GroupId} from {group.Position.X},{group.Position.Y} " +
                $"to {Destination.X},{Destination.Y}");

        // Retask: bump epoch so any stale GroupArrivalEvent from the prior
        // chain no-ops on fire. Fresh chain captures the bumped epoch.
        group.BumpEpoch();
        group.PathRemaining = path.Skip(1).ToList();
        group.PathFinalDest = dest;
        group.State = GroupState.Moving;
        ScheduleNextHop(sim, group);

        return IntentOutcome.Applied;
    }

    // Used by FormGroupIntent for off-rendezvous members — wait, no: members
    // walk solo via MoveIntent.BeginMove. This is the group analogue, for
    // group-level moves. Exposed for re-arm paths in future (e.g. Disband
    // doesn't need it; Split eventually might).
    internal static void BeginGroupMove(Simulation sim, Group group, TileCoord dest)
    {
        if (group.Position == dest)
        {
            ClearMovementAnchors(group);
            return;
        }
        var world = sim.World;
        var now = sim.Now;
        var visibleTiles = View.VisibleTiles(world, group.OwnerId);
        var (path, to) = PlanGroup(world, group, dest, visibleTiles, now);
        if (path is null || path.Count < 2)
        {
            ClearMovementAnchors(group);
            return;
        }
        group.PathRemaining = path.Skip(1).ToList();
        group.PathFinalDest = to;
        ScheduleNextHop(sim, group);
    }

    // The group's plan to `dest`, fog-aware, following tile shapes; aimed at
    // water, it plans to the nearest reachable land instead (MovementCost.LandNear)
    // and says where it ends. A one-tile path: the nearest land is where it is.
    private static (List<TileCoord>? Path, TileCoord Dest) PlanGroup(
        GameWorld world, Group group, TileCoord dest, HashSet<TileCoord> visibleTiles, long now)
    {
        var cost = MovementCost.Planner(world, group.OwnerId, visibleTiles, now);
        var rule = CrossingRule.Applies(world) ? CrossingRule.Planning(world, group.OwnerId, visibleTiles) : null;
        if (!MovementCost.FeetKeepOffWater(world, dest))
            return (Pathfinding.FindPath(world.Grid, group.Position, dest, cost, rule, null), dest);
        foreach (var land in MovementCost.LandNear(world, dest))
        {
            if (land == group.Position) return (new List<TileCoord> { land }, land);
            var path = Pathfinding.FindPath(world.Grid, group.Position, land, cost, rule, null);
            if (path is not null) return (path, land);
        }
        return (null, dest);
    }

    // Schedules the next per-hop GroupArrivalEvent based on PathRemaining[0].
    // Mirrors MoveIntent.ScheduleNextHop. Stashes the assigned Seq on the
    // group's anchor for M4 recovery.
    internal static void ScheduleNextHop(Simulation sim, Group group)
    {
        if (group.PathRemaining is null || group.PathRemaining.Count == 0
            || group.PathFinalDest is null)
            return;
        var next = group.PathRemaining[0];
        // M26 — GROUND-TRUTH fortification gate, the group flavor of
        // MoveIntent.ScheduleNextHop's check: a wall the planner couldn't
        // see (fog) or one that completed mid-march stops the whole column
        // here. Yield-idle + epoch bump (fences any queued arrival), then
        // open a siege if the blocker is hostile. docs/walls-and-gates.md.
        if (Fortification.BlocksMover(sim.World, next, group.OwnerId)
            || (CrossingRule.Applies(sim.World) && !CrossingRule.GroundTruth(sim.World, group.OwnerId).AllowsHop(
                group.Position, null, next, final: next == group.PathFinalDest))
            || MovementCost.ExecutionCost(sim.World, group.Position, next, sim.Now) >= Sim.Core.World.Biomes.Impassable)
        {
            ClearMovementAnchors(group);
            group.State = GroupState.Idle;
            group.BumpEpoch();
            FortSiege.MaybeBeginSiegeAdjacentTo(sim, group.Position);
            return;
        }
        // GROUND-TRUTH HOP COST: source crowding is what makes large groups
        // slow — the group always sits on its own member crowd, so a
        // 10-member group hops more slowly than a 2-member group on the
        // same terrain. Destination crowding stretches caravans landing
        // into bottlenecks (and triggers cap-rejection if it would push
        // the destination over MaxUnitsPerTile — checked at arrival time).
        var arrival = sim.Now + MovementCost.ExecutionCost(sim.World, group.Position, next, sim.Now);
        group.NextArrivalTick = arrival;
        group.NextArrivalSeq  = sim.Schedule(
            arrival,
            new GroupArrivalEvent(group.Id, next, group.PathFinalDest.Value, group.MovementEpoch));
    }

    internal static void ClearMovementAnchors(Group group)
    {
        group.PathRemaining = null;
        group.PathFinalDest = null;
        group.NextArrivalTick = null;
        group.NextArrivalSeq  = null;
    }

    public override string Describe() =>
        $"MoveGroupIntent(group={GroupId} -> {Destination.X},{Destination.Y})";
}
