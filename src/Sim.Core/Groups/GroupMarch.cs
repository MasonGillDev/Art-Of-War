using Sim.Core.Battlefields;
using Sim.Core.Movement;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Core.Groups;

// M46 Phase C / M50 — THE GROUP WALKS (docs/m46-groups-spec.md "Formation march";
// docs/m50-march-formations-spec.md).
//
// A march has two stages.
//
// 1. THE MARCH. The group plans one LEAD PATH P, the owner's view at order time, from
//    its tile's centre to the destination's, and stores it (never re-planned on
//    restore). The leader point walks it; every member has a SPOT at each lead step t:
//      * Column (M50): its place in the group's formation (Formations.Assign — the saved
//        formation, else four abreast). A member B subtiles behind and A to the right
//        stands at P[t − B] + A × right(facing), the facing being the path's dominant
//        direction there (Formations.FacingAt), so the shape follows the path's bends,
//        holds on the straights and turns only when the route does. Where that spot
//        can't be stood on, the row's trail point: the formation narrows.
//      * SingleFile (M50): member k in march order stands at P[t − k]. Nobody steps
//        sideways: a caravan moves at its slowest member's walking speed.
//    A spot before the path's start lies on the start's facing, extended backwards.
//
//    EACH BEAT (one GroupStepEvent per group) LANDS, ADVANCES, PLANS — the shape of a
//    solo walk, so the client glides members instead of snapping them:
//      1. land the step each member was announced to be taking;
//      2. move the leader on, unless a member has fallen more than MaxLag subtiles from
//         its spot (an elastic gate: no stop-start for a wobble);
//      3. plan each member's next step toward its spot and announce it (its one-step
//         route, landing at NextStepTick after MarchStepTicks — what the wire shows as
//         the step in flight). The beat lasts the slowest step being taken: the slowest
//         member sets the pace.
//    A member that can't reach its spot or its trail point falls out (a straggler) and
//    walks on alone, so the march never deadlocks.
//
// 2. CLOSING. At the end of the path everyone walks the last steps to their places: a
//    Column group in its formation facing the way it marched, a single file in a block
//    around the stop. The pending count takes the group to Idle when the last one stops.
//
// BATTLES. A member enrolled on a board pauses the march (its anchor is dropped, every
// announced step forgotten); when no member is left on a board it carries on from where
// it stood (WakeColumns). A member in its closing walk is an ordinary walker.
//
// While marching, a member carries a one-step SubtileRoute (the step announced, or its
// own subtile when it stands this beat) with NO step event of its own: it is walking
// (friends pass through it; it takes no room on the tile cap's "standing" count), and
// the group's event is what moves it.
public static class GroupMarch
{
    // How far, in subtiles, a member looks for its way to its slot.
    public const int SearchWindow = 16;

    // The elastic gate: the leader waits only for a member more than this many subtiles
    // (Manhattan) from its spot.
    public const int MaxLag = 2;

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
    internal static WorldSubtile? CentreStand(SubtileStepRules rules, TileCoord tile)
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
        group.MarchStepTicks = 0;
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

        // 1. Land the steps announced last beat (in id order), re-checked against the
        //    ground as it is now.
        foreach (var m in Column(world, group).OrderBy(m => m.Id))
        {
            if (m.SubtileRouteTick is not null || m.SubtileRoute is not [var next]) continue;
            var cur = Here(m);
            if (next == cur || !cur.IsAdjacentTo(next)) continue;
            var truthFor = new SubtileStepRules(world, StepMover.Of(m), null);
            if (truthFor.Problem(cur, next) is not null || SubtileRoutes.MustWaitAtEdge(world, m, cur, next)) continue;
            SubtileRoutes.Advance(sim, m, cur, next);
            if (m.Board is not null) return;   // it walked into a fight: OnEnrolled paused the march
        }

        var column = Column(world, group);
        var slots = Slots(world, group, path, column);

        // 2. The leader moves on unless someone has fallen behind (the elastic gate).
        if (column.All(m => Lag(m, slots[m.Id]) <= MaxLag))
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

        // 3. Plan and announce each member's next step; the beat is the slowest of them.
        long beat = 0;
        var waitingAtEdge = false;
        foreach (var m in column.OrderBy(m => m.Id))
        {
            var cur = Here(m);
            var slot = slots[m.Id];
            if (cur == slot) { Announce(m, cur); continue; }
            var next = Toward(world, m, cur, slot);
            if (next is null && TrailPoint(world, m, path, group.MarchLead) is { } trail && trail != slot)
                next = Toward(world, m, cur, trail);
            if (next is not { } step)
            {
                Straggle(sim, group, m);
                continue;
            }
            if (SubtileRoutes.MustWaitAtEdge(world, m, cur, step)) { waitingAtEdge = true; Announce(m, cur); continue; }
            beat = Math.Max(beat, SubtileRoutes.StepCost(world, m, cur, step, sim.Now));
            Announce(m, step);
        }

        if (Column(world, group).Count == 0)
        {
            Close(sim, group);
            return;
        }
        var delay = beat > 0 ? beat
            : waitingAtEdge ? Math.Max(1, Battlefields.Battlefields.NextBeat(world, sim.Now) - sim.Now)
            : 1;
        group.MarchStepTicks = delay;
        Schedule(sim, group, delay);
    }

    // How far a member stands from its spot, in subtiles.
    private static int Lag(Unit m, WorldSubtile spot)
    {
        var here = Here(m);
        return Math.Abs(here.X - spot.X) + Math.Abs(here.Y - spot.Y);
    }

    // The march reached its end: everyone walks the last steps to their places — a
    // Column group in its formation facing the way it marched, a single file in a block.
    private static void Close(Simulation sim, Group group)
    {
        var world = sim.World;
        var dest = group.PathFinalDest ?? group.Position;
        var path = group.MarchPath;
        DropColumn(world, group);
        group.Position = dest;

        // Members still busy with a job stay stragglers: they come on their own when free.
        var members = new List<Unit>();
        foreach (var m in Members(world, group))
            if (GroupMuster.Busy(world, m) is not null) group.Stragglers.Add(m.Id);
            else members.Add(m);

        var visible = View.VisibleTiles(world, group.OwnerId);
        List<(Unit Member, WorldSubtile? Place)> places;
        if (group.MarchMode == MarchMode.Column && path is { Count: > 0 })
        {
            // The formation stops CENTRED on the destination (its footprint's middle on the
            // tile's centre), facing the way it marched: "move to that tile" leaves the
            // group on that tile, still in its shape.
            var facing = Formations.FacingAt(path, path.Count - 1);
            var shape = Formations.Assign(group, members);
            var world2 = shape.Select(s => Formations.ToWorld(s.A, s.B, facing)).ToList();
            // Centre the footprint as it actually lies (after turning), flooring the middle
            // so an even width straddles the tile's centre subtiles evenly.
            var midX = world2.Count == 0 ? 0 : (world2.Min(w => w.Dx) + world2.Max(w => w.Dx)) >> 1;
            var midY = world2.Count == 0 ? 0 : (world2.Min(w => w.Dy) + world2.Max(w => w.Dy)) >> 1;
            var centred = shape.Select((s, i) =>
            {
                var (a, b) = Formations.ToFrame(world2[i].Dx - midX, world2[i].Dy - midY, facing);
                return (s.Member, a, b);
            }).ToList();
            places = FormationLayout.Shaped(world, group.OwnerId, path[^1], facing, centred, visible);
        }
        else
            places = FormationLayout.Places(world, group.OwnerId, dest, FormationLayout.FillOrder(members), visible);
        var walking = 0;
        foreach (var (m, place) in places)
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

    // Announce the step a marching member is taking this beat (or its own subtile, when
    // it stands): a one-step route with no step event of its own — the group's next beat
    // lands it. The wire shows it as the member's step in flight.
    private static void Announce(Unit m, WorldSubtile step)
    {
        if (m.SubtileRouteTick is not null) return;
        m.SubtileRoute = new List<WorldSubtile> { step };
    }

    // Every member's spot at the leader's current step: its place in the formation
    // (Column) or on the trail (SingleFile). Pure.
    private static Dictionary<int, WorldSubtile> Slots(GameWorld world, Group group, List<WorldSubtile> path, List<Unit> column)
    {
        var slots = new Dictionary<int, WorldSubtile>(column.Count);
        var t = group.MarchLead;
        if (group.MarchMode == MarchMode.SingleFile)
        {
            var order = Formations.FileOrder(group, column);
            for (var k = 0; k < order.Count; k++)
                slots[order[k].Id] = TrailPoint(world, order[k], path, t - k) ?? Here(order[k]);
            return slots;
        }
        foreach (var (m, a, b) in Formations.Assign(group, column))
        {
            if (TrailPoint(world, m, path, t - b) is not { } anchor) { slots[m.Id] = Here(m); continue; }
            var facing = Formations.FacingAt(path, t - b);
            var (dx, dy) = Formations.ToWorld(a, 0, facing);   // B is already the row's place on the trail
            var spot = new WorldSubtile(anchor.X + dx, anchor.Y + dy);
            var rules = new SubtileStepRules(world, StepMover.Of(m), null);
            slots[m.Id] = world.Grid.InBounds(spot.Tile) && rules.CanStand(spot) ? spot : anchor;
        }
        return slots;
    }

    // The trail point at path index `j`: on the path, or — before its start — on the start's
    // facing extended backwards (null where that can't be stood on: the member waits).
    // Past the end, the end.
    private static WorldSubtile? TrailPoint(GameWorld world, Unit m, List<WorldSubtile> path, int j)
    {
        if (j >= 0) return path[Math.Min(j, path.Count - 1)];
        var facing = Formations.FacingAt(path, 0);
        var back = new WorldSubtile(path[0].X + j * facing.Dx(), path[0].Y + j * facing.Dy());
        var rules = new SubtileStepRules(world, StepMover.Of(m), null);
        return world.Grid.InBounds(back.Tile) && rules.CanStand(back) ? back : null;
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
