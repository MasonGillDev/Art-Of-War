using Sim.Core.Battlefields;
using Sim.Core.Movement;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Core.Groups;

// M46 Phase C — THE GROUP WALKS (docs/m46-groups-spec.md, "Formation march";
// design in docs/m46-status.md).
//
// A march has two stages.
//
// 1. THE COLUMN. The group plans one LEAD PATH, the owner's view at order time, from
//    its tile's centre to the destination's, and stores it (never re-planned on
//    restore). Members march behind the lead four abreast: member k (FillOrder) has
//    rank k / 4 and file k % 4, and its SLOT at lead step t is the trail point
//    P[t − rank], shifted sideways by file − 1 at right angles to the path there.
//    Where that side-step can't be stood on, the slot is the trail point itself: the
//    column narrows at a bridge or a gate, friends passing through each other.
//    A rank whose trail point is still behind the start (t < rank) waits where it
//    stands; the column unspools from the block it formed up in.
//
//    ONE EVENT PER GROUP STEP moves every member one step toward its slot (a short
//    local search, ground truth: the member is right there). THE LEAD ADVANCES ONLY
//    WHEN EVERY MEMBER STANDS ON ITS SLOT: that is the pace. The column waits for its
//    slowest member, and its outer files wheel at a turn. The next step comes after
//    the slowest step just taken. A member that can't reach its slot or the trail
//    point falls out (a straggler) and walks on alone, so the column never deadlocks.
//
// 2. CLOSING. When the lead reaches the end of its path and the column is in place,
//    every member walks its own short walk to its place in the block around the
//    destination (FormationLayout), and the Phase A pending count takes the group to
//    Idle when the last one stops.
//
// BATTLES. A member enrolled on a board pauses the column (its anchor is dropped);
// when no member is left on a board the column carries on from where it stood
// (WakeColumns). A member in its closing walk is an ordinary walker and resumes on
// its own (Walk.Resume).
//
// While in the column, a member carries a one-step SubtileRoute to its slot with NO
// step event of its own: it is walking (friends pass through it; it takes no room on
// the tile cap's "standing" count), and the group's event is what moves it.
public static class GroupMarch
{
    // How far, in subtiles, a member looks for its way to its slot.
    public const int SearchWindow = 16;

    // ---- planning (pure) ------------------------------------------------------------

    // The lead path from the group's tile to `dest` as the owner sees the world, start
    // included; null when there is none. A foot march aimed at water ends on the
    // nearest land. Pure read.
    public static List<WorldSubtile>? Plan(GameWorld world, Group group, TileCoord dest, long now)
    {
        var visible = View.VisibleTiles(world, group.OwnerId);
        var mover = new StepMover(group.OwnerId, Traversal.Foot, false);
        var rules = new SubtileStepRules(world, mover, visible);
        if (CentreStand(rules, group.Position) is not { } start) return null;
        IEnumerable<TileCoord> targets = MovementCost.FeetKeepOffWater(world, dest)
            ? MovementCost.LandNear(world, dest)
            : new[] { dest };
        foreach (var target in targets)
        {
            if (CentreStand(rules, target) is not { } goal) continue;
            if (goal == start) return new List<WorldSubtile> { start };
            if (SubtilePathfinder.Find(world, mover, start, goal, visible, now: now) is not { } path) continue;
            var full = new List<WorldSubtile>(path.Count + 1) { start };
            full.AddRange(path);
            return full;
        }
        return null;
    }

    // The subtile of `tile` nearest its centre that a foot unit may stand on (as the
    // rules know the ground), whoever stands there now: the lead is not a body.
    private static WorldSubtile? CentreStand(SubtileStepRules rules, TileCoord tile)
    {
        foreach (var s in Walk.CentreOut)
        {
            var at = WorldSubtile.Of(tile, s);
            if (rules.CanStand(at)) return at;
        }
        return null;
    }

    // ---- starting, stopping ---------------------------------------------------------

    // Set the group marching along `path` (from Plan). The caller has halted any march
    // already under way.
    internal static void Begin(Simulation sim, Group group, List<WorldSubtile> path)
    {
        var world = sim.World;
        foreach (var m in Members(world, group))
            if (m.Subtile is null) Placement.Seat(world, m);
        group.MarchPath = path;
        group.MarchLead = 0;
        group.Stragglers.Clear();
        // A member still finishing a job (a delivery, a fight elsewhere) doesn't hold the
        // column up: it follows on its own once it is free (GroupMuster.OnFreed).
        foreach (var m in Members(world, group))
            if (GroupMuster.Busy(world, m) is not null) group.Stragglers.Add(m.Id);
        group.PathFinalDest = path[^1].Tile;
        group.PendingArrivals = 0;
        group.State = GroupState.Moving;
        if (!AnyOnBoard(world, group)) Schedule(sim, group, 0);
    }

    // Drop the column: the step anchor, the path, every member's marker route.
    internal static void DropColumn(GameWorld world, Group group)
    {
        if (group.MarchPath is not null)
            foreach (var m in Members(world, group))
                if (!group.Stragglers.Contains(m.Id) && m.SubtileRouteTick is null) SubtileRoutes.Cancel(m);
        group.MarchPath = null;
        group.MarchLead = 0;
        group.NextStepTick = null;
        group.NextStepSeq = null;
        group.Stragglers.Clear();
    }

    // A member was just enrolled on a board: the column waits (its markers drop, so the
    // members standing still take their room like anyone standing).
    internal static void OnEnrolled(Simulation sim, Unit u)
    {
        if (u.GroupId is not { } gid || !sim.World.Groups.TryGetValue(gid, out var group) || group.MarchPath is null) return;
        group.NextStepTick = null;
        group.NextStepSeq = null;
        foreach (var m in Members(sim.World, group))
            if (!group.Stragglers.Contains(m.Id) && m.SubtileRouteTick is null) SubtileRoutes.Cancel(m);
    }

    // A board let units go (it closed, or one left it): every paused column with no
    // member left on a board marches on. Bounded by the number of groups, run only
    // when a board changes, never on a timer.
    internal static void WakeColumns(Simulation sim)
    {
        foreach (var group in sim.World.Groups.Values)
            if (group.MarchPath is not null && group.NextStepTick is null && !AnyOnBoard(sim.World, group))
                Schedule(sim, group, 0);
    }

    // ---- the step -------------------------------------------------------------------

    internal static void Step(Simulation sim, GroupStepEvent ev, Group group)
    {
        if (group.NextStepTick != ev.At || group.NextStepSeq != ev.Seq) return;   // stale
        group.NextStepTick = null;
        group.NextStepSeq = null;
        if (group.MarchPath is not { } path) return;
        var world = sim.World;
        if (AnyOnBoard(world, group)) return;   // paused; WakeColumns resumes it

        var column = Column(world, group);
        var slots = Slots(world, group, path, column);

        // The lead moves on when the whole column stands in place.
        if (column.All(m => Here(m) == slots[m.Id]))
        {
            if (group.MarchLead >= path.Count - 1 || column.Count == 0)
            {
                Close(sim, group);
                return;
            }
            var truth = new SubtileStepRules(world, new StepMover(group.OwnerId, Traversal.Foot, false), null);
            var from = path[group.MarchLead];
            var to = path[group.MarchLead + 1];
            if (!truth.CanStand(to) || truth.Problem(from, to) is not null)
            {
                // The ground isn't what the plan thought (a wall in the fog): plan again
                // from here, with what the owner can see now.
                group.Position = from.Tile;
                if (Plan(world, group, path[^1].Tile, sim.Now) is not { } again || again.Count < 2)
                {
                    StopHere(sim, group, from.Tile);
                    return;
                }
                group.MarchPath = path = again;
                group.MarchLead = 0;
            }
            group.MarchLead++;
            group.Position = path[group.MarchLead].Tile;
            slots = Slots(world, group, path, column);
        }

        // Every member one step toward its slot, in id order.
        long slowest = 0;
        var waitingAtEdge = false;
        foreach (var m in column.OrderBy(m => m.Id))
        {
            var cur = Here(m);
            var slot = slots[m.Id];
            if (cur == slot) { Mark(m, slot); continue; }
            var next = Toward(world, m, cur, slot);
            if (next is null && Trail(group, path, column.IndexOf(m)) is { } trail && trail != slot)
                next = Toward(world, m, cur, trail);
            if (next is not { } step)
            {
                Straggle(sim, group, m);
                continue;
            }
            if (SubtileRoutes.MustWaitAtEdge(world, m, cur, step)) { waitingAtEdge = true; continue; }
            slowest = Math.Max(slowest, SubtileRoutes.StepCost(world, m, cur, step, sim.Now));
            SubtileRoutes.Advance(sim, m, cur, step);
            if (m.Board is not null) return;   // it walked into a fight: OnEnrolled paused the column
            Mark(m, slot);
        }

        if (Column(world, group).Count == 0)
        {
            Close(sim, group);
            return;
        }
        var delay = slowest > 0 ? slowest
            : waitingAtEdge ? Math.Max(1, Battlefields.Battlefields.NextBeat(world, sim.Now) - sim.Now)
            : 1;
        Schedule(sim, group, delay);
    }

    // The column is in place at the end of the path: everyone walks to their place in
    // the block around the destination.
    private static void Close(Simulation sim, Group group)
    {
        var world = sim.World;
        var dest = group.PathFinalDest ?? group.Position;
        DropColumn(world, group);
        group.Position = dest;

        // Members still busy with a job stay stragglers: they come on their own when free.
        var members = new List<Unit>();
        foreach (var m in Members(world, group))
            if (GroupMuster.Busy(world, m) is not null) group.Stragglers.Add(m.Id);
            else members.Add(m);

        var visible = View.VisibleTiles(world, group.OwnerId);
        var walking = 0;
        foreach (var (m, place) in FormationLayout.Places(world, group.OwnerId, dest, FormationLayout.FillOrder(members), visible))
        {
            Walk.Stop(m);
            if (place is { } spot && GroupMuster.WalkToPlace(sim, m, spot)) walking++;
        }
        group.PendingArrivals = walking;
        if (walking == 0) StopHere(sim, group, dest);
    }

    // The march is over where it stands.
    private static void StopHere(Simulation sim, Group group, TileCoord at)
    {
        var world = sim.World;
        var stillAway = group.Stragglers.Where(id => world.Units.TryGetValue(id, out var m) && GroupMuster.Busy(world, m) is not null).ToList();
        DropColumn(world, group);
        foreach (var id in stillAway) group.Stragglers.Add(id);
        group.Position = at;
        group.PathFinalDest = null;
        group.PendingArrivals = 0;
        group.State = GroupState.Idle;
        GroupStances.ReturnIfDone(sim, group);   // M49: back from helping or a charge
    }

    // A member that can't find its way to its slot walks on alone to the destination.
    private static void Straggle(Simulation sim, Group group, Unit m)
    {
        group.Stragglers.Add(m.Id);
        SubtileRoutes.Cancel(m);
        if (group.PathFinalDest is { } dest) Walk.Begin(sim, m, dest);
        if (m.IsWalking) group.PendingArrivals++;
    }

    private static void Schedule(Simulation sim, Group group, long delay)
    {
        var at = sim.Now + delay;
        group.NextStepTick = at;
        group.NextStepSeq = sim.Schedule(at, new GroupStepEvent(group.Id));
    }

    // ---- the column's shape ---------------------------------------------------------

    // The members marching in the column, in fill order: alive, ashore, seated, not
    // fallen out.
    private static List<Unit> Column(GameWorld world, Group group) =>
        FormationLayout.FillOrder(Members(world, group)
            .Where(m => !group.Stragglers.Contains(m.Id) && m.Subtile is not null));

    private static IEnumerable<Unit> Members(GameWorld world, Group group)
    {
        foreach (var id in group.Members)
            if (world.Units.TryGetValue(id, out var m) && !m.IsEmbarked) yield return m;
    }

    private static bool AnyOnBoard(GameWorld world, Group group) =>
        Members(world, group).Any(m => m.Board is not null && !group.Stragglers.Contains(m.Id));

    private static WorldSubtile Here(Unit m) => WorldSubtile.Of(m.Position, m.Subtile!.Value);

    // A column member is walking: a one-step route to its slot, no step event of its own.
    private static void Mark(Unit m, WorldSubtile slot)
    {
        if (m.SubtileRouteTick is not null) return;
        m.SubtileRoute = new List<WorldSubtile> { slot };
    }

    private static Dictionary<int, WorldSubtile> Slots(GameWorld world, Group group, List<WorldSubtile> path, List<Unit> column)
    {
        var slots = new Dictionary<int, WorldSubtile>(column.Count);
        for (var k = 0; k < column.Count; k++)
        {
            var m = column[k];
            var rank = k / 4;
            var j = group.MarchLead - rank;
            if (j < 0) { slots[m.Id] = Here(m); continue; }   // not yet unspooled: wait here
            var q = path[j];
            var (hx, hy) = HeadingAt(path, j);
            var side = k % 4 - 1;                                   // files −1, 0, +1, +2
            var spot = new WorldSubtile(q.X - hy * side, q.Y + hx * side);   // right of the heading
            var rules = new SubtileStepRules(world, StepMover.Of(m), null);
            slots[m.Id] = side != 0 && world.Grid.InBounds(spot.Tile) && rules.CanStand(spot) ? spot : q;
        }
        return slots;
    }

    // The trail point for the k-th member in fill order (its slot with no side-step), or
    // null while its rank waits behind the start.
    private static WorldSubtile? Trail(Group group, List<WorldSubtile> path, int k)
    {
        var j = group.MarchLead - k / 4;
        return j < 0 ? null : path[j];
    }

    // The path's heading at index j: the step into it (out of it, at the start).
    private static (int Dx, int Dy) HeadingAt(List<WorldSubtile> path, int j)
    {
        if (path.Count < 2) return (0, -1);
        var (a, b) = j >= 1 ? (path[j - 1], path[j]) : (path[0], path[1]);
        return (b.X - a.X, b.Y - a.Y);
    }

    // The first step of the shortest way from `cur` to `target` within SearchWindow, by
    // the ground truth (the member is right there), or null. Breadth-first, N E S W.
    private static WorldSubtile? Toward(GameWorld world, Unit m, WorldSubtile cur, WorldSubtile target)
    {
        var rules = new SubtileStepRules(world, StepMover.Of(m), null);
        if (!world.Grid.InBounds(target.Tile) || !rules.CanStand(target)) return null;
        if (cur.IsAdjacentTo(target) && rules.Problem(cur, target) is null) return target;
        var firstStep = new Dictionary<WorldSubtile, WorldSubtile> { [cur] = cur };
        var frontier = new Queue<WorldSubtile>();
        frontier.Enqueue(cur);
        while (frontier.Count > 0)
        {
            var s = frontier.Dequeue();
            foreach (var (dx, dy) in Around)
            {
                var n = new WorldSubtile(s.X + dx, s.Y + dy);
                if (Math.Abs(n.X - cur.X) > SearchWindow || Math.Abs(n.Y - cur.Y) > SearchWindow) continue;
                if (firstStep.ContainsKey(n) || !world.Grid.InBounds(n.Tile)) continue;
                if (!rules.CanStand(n) || rules.Problem(s, n) is not null) continue;
                var first = s == cur ? n : firstStep[s];
                if (n == target) return first;
                firstStep[n] = first;
                frontier.Enqueue(n);
            }
        }
        return null;
    }

    private static readonly (int Dx, int Dy)[] Around = { (0, -1), (1, 0), (0, 1), (-1, 0) };   // N, E, S, W
}

// One step of a group's column (GroupMarch). Fenced by the group's anchor.
public sealed class GroupStepEvent : ScheduledEvent
{
    public int GroupId { get; }

    public GroupStepEvent(int groupId) { GroupId = groupId; }

    public override void Apply(Simulation sim)
    {
        if (sim.World.Groups.TryGetValue(GroupId, out var group)) GroupMarch.Step(sim, this, group);
    }

    public override string Describe() => $"GroupStepEvent(group={GroupId})";
}
