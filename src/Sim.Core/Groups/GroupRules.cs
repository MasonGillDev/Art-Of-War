using Sim.Core.World;

namespace Sim.Core.Groups;

public static class GroupConstants
{
    // A group's name, like a route's: trimmed, at most this many characters.
    public const int MaxNameLength = 32;

    // Levels in a group tree: army → regiment → company (user, 2026-10-02).
    public const int MaxDepth = 3;
}

// The rules every group intent shares (docs/m46-groups-spec.md): who may join, the
// tree, the id counter, removal, and the pending-arrival count. One place, so the
// intents can't come to mean different things.
public static class GroupRules
{
    // ---- command -------------------------------------------------------------------

    // Is this unit under its group's command? A member of a dismissed group is free:
    // it works, hauls, trains and builds like anyone else. Membership alone blocks
    // nothing; command does. Pure read.
    public static bool UnderCommand(GameWorld world, Unit unit) =>
        unit.GroupId is { } gid
        && world.Groups.TryGetValue(gid, out var group)
        && group.State != GroupState.Dismissed;

    // ---- membership ----------------------------------------------------------------

    // Why `unit` (id `id`) can't join a group of `playerId`'s, or null when it can.
    // Any foot unit (user, 2026-10-02). A unit is in one group at most, and a route
    // crew member stays off groups until M47 makes the crew a group itself.
    public static string? JoinRefusal(Unit? unit, int id, int playerId)
    {
        if (unit is null) return $"unit {id} does not exist";
        if (unit.OwnerId != playerId) return $"unit {id} not owned by player {playerId}";
        if (unit.Role == UnitRole.Boat || unit.Traversal != Traversal.Foot)
            return $"unit {id} is not a foot unit";
        if (unit.GroupId is { } other) return $"unit {id} is already in group {other}";
        return null;
    }

    // A group's name: trimmed, at most MaxNameLength characters, no control
    // characters. "" means unnamed. Same rule as RouteNames.Clean.
    public static string? CleanName(string? raw, out string clean)
    {
        clean = (raw ?? "").Trim();
        if (clean.Length > GroupConstants.MaxNameLength)
            return $"group name is {clean.Length} characters (cap {GroupConstants.MaxNameLength})";
        foreach (var c in clean)
            if (char.IsControl(c))
                return "group name has a control character";
        return null;
    }

    // ---- ids -----------------------------------------------------------------------

    // The next group id. Monotonic from 1 and never reused: a stale fence from a deleted
    // group must not land on a new one. Stored (GameWorld.NextGroupId), not derived
    // from the ids in use, which would hand a deleted newest group's id back out.
    internal static int NewId(GameWorld world)
    {
        var id = world.NextGroupId;
        if (world.Groups.ContainsKey(id))
            throw new InvalidOperationException(
                $"group id {id} is taken but GameWorld.NextGroupId points at it");
        world.NextGroupId = id + 1;
        return id;
    }

    // ---- the tree ------------------------------------------------------------------

    // The group's level: 1 at the top, +1 per parent above it. Pure read.
    public static int Depth(GameWorld world, Group group)
    {
        var depth = 1;
        for (var p = group.ParentId; p is { } pid && world.Groups.TryGetValue(pid, out var parent); p = parent.ParentId)
            depth++;
        return depth;
    }

    // Levels from this group down to its deepest leaf, itself included. Pure read.
    public static int Height(GameWorld world, Group group)
    {
        var below = 0;
        foreach (var cid in group.Children)
            if (world.Groups.TryGetValue(cid, out var child))
                below = Math.Max(below, Height(world, child));
        return 1 + below;
    }

    // Why `child` (null while it is being created, `childHeight` levels tall) can't sit
    // under `parentId`, or null when it can.
    public static string? ParentRefusal(GameWorld world, int playerId, Group? child, int parentId, int childHeight)
    {
        if (!world.Groups.TryGetValue(parentId, out var parent))
            return $"group {parentId} does not exist";
        if (parent.OwnerId != playerId)
            return $"group {parentId} not owned by player {playerId}";
        if (parent.Kind != GroupKind.Groups)
            return $"group {parentId} holds units, not groups";
        if (child is not null)
            for (Group? g = parent; g is not null; g = g.ParentId is { } up && world.Groups.TryGetValue(up, out var next) ? next : null)
                if (g.Id == child.Id)
                    return $"group {parentId} is group {child.Id} or sits under it";
        if (Depth(world, parent) + childHeight > GroupConstants.MaxDepth)
            return $"groups nest at most {GroupConstants.MaxDepth} deep";
        return null;
    }

    // Move `group` under `parentId` (null = top level), keeping both sides of the link.
    internal static void SetParent(GameWorld world, Group group, int? parentId)
    {
        if (group.ParentId is { } old && world.Groups.TryGetValue(old, out var oldParent))
            oldParent.Children.Remove(group.Id);
        group.ParentId = parentId;
        if (parentId is { } pid) world.Groups[pid].Children.Add(group.Id);
    }

    // ---- removal -------------------------------------------------------------------

    // Remove a group. A crew comes off its route first (M47). A company under command
    // is dismissed, so its members go back to what they were doing; then every member
    // goes solo, its children move to the top level (keeping their own orders), and it
    // leaves its parent.
    internal static void Remove(Simulation sim, Group group)
    {
        var world = sim.World;
        Sim.Core.Hauling.RouteCrews.TakeOff(sim, group);
        // (DismissLeaf has already stopped the company's march or muster, and sent its
        // members back to work: halting again would stop those walks.)
        if (group.Kind == GroupKind.Units) GroupMuster.DismissLeaf(sim, group);
        group.BumpEpoch();
        foreach (var memberId in group.Members)
            if (world.Units.TryGetValue(memberId, out var unit) && unit.GroupId == group.Id)
                unit.GroupId = null;
        foreach (var cid in group.Children)
            if (world.Groups.TryGetValue(cid, out var child)) child.ParentId = null;
        group.Children.Clear();
        SetParent(world, group, null);
        world.Groups.Remove(group.Id);
    }

    // Take an emptied group's record out of the world (a merge): its children move to
    // the top level, it leaves its parent. Its members, if any, are the caller's affair.
    internal static void Discard(GameWorld world, Group group)
    {
        foreach (var cid in group.Children)
            if (world.Groups.TryGetValue(cid, out var child)) child.ParentId = null;
        group.Children.Clear();
        SetParent(world, group, null);
        world.Groups.Remove(group.Id);
    }

    // ---- the pending count ---------------------------------------------------------

    // One member's walk for the group's current order is over: it arrived, its walk was
    // halted, or it died on the way. Every path that ends a member's walk comes here, so
    // none can leave the group Forming or Moving for ever.
    //   Forming (a muster, M46 Phase D): the member settles — counted in at its place,
    //     or sent on toward it — and the last one in forms the group up (GroupMuster).
    //   Moving: the pending count of closing walkers (and stragglers) comes down; at
    //     zero the group is Idle at its destination.
    internal static void OneLessPending(Simulation sim, Group group, Unit unit, bool died = false)
    {
        if (group.State == GroupState.Forming)
        {
            if (died) { group.MusterPlaces.Remove(unit.Id); GroupMuster.Drop(group, unit.Id); }
            else GroupMuster.Settle(sim, group, unit);
            return;
        }
        if (group.State != GroupState.Moving) return;
        group.PendingArrivals--;
        // While the column still marches (GroupMarch), the count is only its stragglers:
        // the group arrives when the column closes, not when they do.
        if (group.MarchPath is not null) { group.PendingArrivals = Math.Max(0, group.PendingArrivals); return; }
        if (group.PendingArrivals > 0) return;
        if (group.PathFinalDest is { } dest) group.Position = dest;
        group.PendingArrivals = 0;
        group.State = GroupState.Idle;
        group.RendezvousTile = null;
        group.PathFinalDest = null;
    }
}
