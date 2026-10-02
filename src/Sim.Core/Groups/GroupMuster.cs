using Sim.Core.Battlefields;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Core.Groups;

// M46 Phase D — MUSTER AND DISMISS (docs/m46-groups-spec.md, "Muster" and "Dismiss").
//
// A muster calls every leaf under a group to one anchor. Each member gets its place in
// one block laid out for all of them together (FormationLayout: leaves in tree order,
// members in fill order, so each company stands together), and:
//   * a member doing a STANDING job (working a building, building a site) leaves it now
//     and keeps its slot: a held work slot (Extractor.HeldBy), and the job as its saved
//     task (Unit.SavedTask);
//   * a member walking to a job drops the errand and keeps it as its saved task;
//   * a member in the middle of something that must END first — delivering cargo
//     (a hauler with loot does not walk it into danger), breeding, surveying,
//     scouting, fighting, sitting aboard a boat — finishes, and the hook at the end of
//     that task (OnFreed) sends it on;
//   * everyone else comes now.
// The leaf is Forming until every member it waits for (Awaiting) is in place.
//
// A dismiss sends every member back to its saved task through the ordinary goal path
// (GoalRules.Begin): the worker walks to its held slot and takes it. A task that can no
// longer happen leaves the member free, and the goal's dissolved notice says why.
//
// LATEST ORDER WINS: a leaf mustered again, moved or dismissed drops what it was doing.
public static class GroupMuster
{
    // ---- the tree -------------------------------------------------------------------

    // The group and every group under it, depth first, children in id order.
    public static List<Group> Subtree(GameWorld world, Group group)
    {
        var all = new List<Group>();
        void Visit(Group g)
        {
            all.Add(g);
            foreach (var cid in g.Children)
                if (world.Groups.TryGetValue(cid, out var c)) Visit(c);
        }
        Visit(group);
        return all;
    }

    // The leaves (groups of units) under `group`, itself included, in tree order.
    public static List<Group> Leaves(GameWorld world, Group group) =>
        Subtree(world, group).Where(g => g.Kind == GroupKind.Units).ToList();

    // ---- muster ---------------------------------------------------------------------

    // The anchor a muster to `tile` forms around, or why it can't (the caller rejects).
    public static string? AnchorRefusal(GameWorld world, int owner, TileCoord tile)
    {
        if (!world.Grid.InBounds(tile)) return $"tile {tile.X},{tile.Y} is off the map";
        var rules = new SubtileStepRules(world, new StepMover(owner, Traversal.Foot, false), View.VisibleTiles(world, owner));
        foreach (var s in Subtile.All())
            if (rules.CanStand(WorldSubtile.Of(tile, s))) return null;
        return $"no one can stand on {tile.X},{tile.Y}";
    }

    internal static void Muster(Simulation sim, Group group, TileCoord anchor)
    {
        var world = sim.World;
        foreach (var g in Subtree(world, group))
        {
            g.Position = anchor;
            if (g.Kind == GroupKind.Groups) { g.State = GroupState.Idle; continue; }
            MoveGroupIntent.Halt(sim, g);
            g.BumpEpoch();
            g.State = GroupState.Forming;
            g.RendezvousTile = anchor;
            g.PendingArrivals = 0;
            g.Awaiting.Clear();
            g.MusterPlaces.Clear();
        }

        var leaves = Leaves(world, group);
        var ordered = new List<Unit>();
        foreach (var leaf in leaves) ordered.AddRange(FormationLayout.FillOrder(Living(world, leaf)));
        var places = FormationLayout.Places(world, group.OwnerId, anchor, ordered, View.VisibleTiles(world, group.OwnerId));

        foreach (var (m, place) in places)
        {
            var leaf = world.Groups[m.GroupId!.Value];
            if (place is not { } spot) continue;   // no room within reach: Progress says so
            leaf.MusterPlaces[m.Id] = spot;
            leaf.Awaiting.Add(m.Id);
            SaveAndRelease(sim, m);
        }
        foreach (var (m, _) in places)
            if (world.Groups.TryGetValue(m.GroupId!.Value, out var leaf)) Settle(sim, leaf, m);
        foreach (var leaf in leaves)
            if (leaf.State == GroupState.Forming && leaf.Awaiting.Count == 0) Formed(leaf);
    }

    // Newcomers to a group under command are called to it: each gets a place in the block
    // around where the group stands (its own members' subtiles count as taken), and the
    // group waits for them (Forming) as it would at a muster.
    internal static void CallIn(Simulation sim, Group leaf, IReadOnlyList<Unit> newcomers)
    {
        var world = sim.World;
        if (leaf.State == GroupState.Idle) { leaf.State = GroupState.Forming; leaf.RendezvousTile = leaf.Position; }
        var places = FormationLayout.Places(world, leaf.OwnerId, leaf.Position,
            FormationLayout.FillOrder(newcomers), View.VisibleTiles(world, leaf.OwnerId));
        foreach (var (m, place) in places)
        {
            if (place is not { } spot) continue;
            leaf.MusterPlaces[m.Id] = spot;
            leaf.Awaiting.Add(m.Id);
            SaveAndRelease(sim, m);
        }
        foreach (var (m, _) in places) Settle(sim, leaf, m);
        if (leaf.State == GroupState.Forming && leaf.Awaiting.Count == 0) Formed(leaf);
    }

    // Leave whatever the member was doing, remembering it. A member that must finish
    // first keeps its task untouched.
    private static void SaveAndRelease(Simulation sim, Unit m)
    {
        var world = sim.World;
        if (Busy(world, m) is not null) return;

        if (m.Goal is { } goal)
        {
            m.SavedTask = goal;
            GoalRules.Dissolve(sim, m, "called to the muster");
        }
        if (m.Activity == Activity.Working && m.Assignment is { } workTile
            && world.Structures.TryGetValue(workTile, out var s) && s is Extractor extractor)
        {
            m.SavedTask = new GoalPlan(GoalKind.AssignWorker, workTile);
            WorkAssignment.Release(sim, m);
            extractor.HeldBy.Add(m.Id);
        }
        else if (m.Activity == Activity.Building && m.Assignment is { } siteTile)
        {
            m.SavedTask = new GoalPlan(GoalKind.AssignBuilder, siteTile);
        }
        if (m.Pursuit is not null) Sim.Core.Combat.PursuitRules.Release(m);   // a chase is not a job
        Retask.Release(sim, m);   // an empty haul trip, a plain walk: dropped
        Walk.Stop(m);
    }

    // Why this member must finish what it is doing before it answers, or null.
    public static string? Busy(GameWorld world, Unit m)
    {
        if (m.Board is not null) return "fighting";
        if (m.IsEmbarked) return "aboard a boat";
        if (m.HaulPlan is { } plan && (plan.Phase == HaulPhase.ToDest || !m.Cargo.IsEmpty)) return "delivering";
        if (m.Survey is not null) return "surveying";
        if (world.ScoutMissions.TryGetValue(m.Id, out var mission)
            && mission.State != Sim.Core.Scouting.ScoutMissionState.Returned) return "scouting";
        if (Sim.Core.Population.Population.GetActiveBreedingFor(world, m.Id) is not null) return "breeding";
        return null;
    }

    // Move this member on toward its place, or count it in. Called when the muster
    // starts, when its walk ends, and when it finishes the job it had to finish.
    internal static void Settle(Simulation sim, Group leaf, Unit m)
    {
        if (leaf.State is GroupState.Moving or GroupState.Idle && leaf.Stragglers.Contains(m.Id))
        {
            JoinMarch(sim, leaf, m);
            return;
        }
        if (leaf.State != GroupState.Forming || !leaf.Awaiting.Contains(m.Id)) return;
        var world = sim.World;
        if (Busy(world, m) is not null || m.IsWalking) return;
        if (!leaf.MusterPlaces.TryGetValue(m.Id, out var place)) { Drop(leaf, m.Id); return; }
        if (m.Subtile is null) Placement.Seat(world, m);
        if (m.Subtile is { } sub && WorldSubtile.Of(m.Position, sub) == place) { Drop(leaf, m.Id); return; }
        if (!WalkToPlace(sim, m, place)) Drop(leaf, m.Id);   // no way there: it stays where it is
    }

    // A member that finished its job after its group set off: it follows on its own (a
    // straggler) to where the group is going, or, when the group has already arrived, to
    // where it stands.
    private static void JoinMarch(Simulation sim, Group leaf, Unit m)
    {
        if (m.IsWalking || Busy(sim.World, m) is not null) return;
        if (leaf.State == GroupState.Idle)
        {
            leaf.Stragglers.Remove(m.Id);
            Walk.Begin(sim, m, leaf.Position);
            return;
        }
        if (leaf.PathFinalDest is not { } dest) return;
        Walk.Begin(sim, m, dest);
        if (m.IsWalking) leaf.PendingArrivals++;
    }

    // The muster no longer waits for this member (it arrived, it can't get there, it
    // died). The last one in forms the group up.
    internal static void Drop(Group leaf, int unitId)
    {
        if (!leaf.Awaiting.Remove(unitId)) return;
        if (leaf.State == GroupState.Forming && leaf.Awaiting.Count == 0) Formed(leaf);
    }

    private static void Formed(Group leaf)
    {
        leaf.State = GroupState.Idle;
        leaf.RendezvousTile = null;
        leaf.PendingArrivals = 0;
    }

    // A unit finished a job it had to finish before answering a muster (a delivery, a
    // birth, a survey, a scout's mission, a fight, a landing): if its group is calling,
    // it goes now.
    public static void OnFreed(Simulation sim, Unit unit)
    {
        if (unit.GroupId is { } gid && sim.World.Groups.TryGetValue(gid, out var leaf)) Settle(sim, leaf, unit);
    }

    // Walk `m` to the exact subtile `spot`, planned as its owner sees the world. False
    // when there is no way there.
    internal static bool WalkToPlace(Simulation sim, Unit m, WorldSubtile spot)
    {
        var world = sim.World;
        if (m.Subtile is not { } sub) return false;
        var cur = WorldSubtile.Of(m.Position, sub);
        if (cur == spot) return false;
        var visible = View.VisibleTiles(world, m.OwnerId);
        if (SubtilePathfinder.Find(world, StepMover.Of(m), cur, spot, visible, now: sim.Now) is not { Count: > 0 } way)
            return false;
        SubtileRoutes.Cancel(m);
        m.PathFinalDest = spot.Tile;
        SubtileRoutes.Begin(sim, m, way);
        return true;
    }

    // ---- dismiss --------------------------------------------------------------------

    internal static void Dismiss(Simulation sim, Group group)
    {
        var world = sim.World;
        foreach (var g in Subtree(world, group))
        {
            if (g.Kind == GroupKind.Groups) g.State = GroupState.Dismissed;
            else DismissLeaf(sim, g);
        }
    }

    // One company stands down: its march or muster stops, and each member goes back to
    // its saved task.
    internal static void DismissLeaf(Simulation sim, Group leaf)
    {
        if (leaf.State == GroupState.Dismissed) return;
        MoveGroupIntent.Halt(sim, leaf);
        leaf.BumpEpoch();
        leaf.State = GroupState.Dismissed;
        leaf.RendezvousTile = null;
        leaf.PendingArrivals = 0;
        leaf.Awaiting.Clear();
        leaf.MusterPlaces.Clear();
        foreach (var m in Living(sim.World, leaf)) Return(sim, m);
    }

    // A member leaves a group under command (split out, or moved to a dismissed group):
    // the muster stops waiting for it, it stops walking to the block, and it goes back to
    // its saved task.
    internal static void Release(Simulation sim, Group leaf, Unit m)
    {
        leaf.MusterPlaces.Remove(m.Id);
        Drop(leaf, m.Id);
        leaf.Stragglers.Remove(m.Id);
        if (Busy(sim.World, m) is null) Walk.Stop(m);
        Return(sim, m);
    }

    // Back to the saved task, if there is one. A member still finishing its own job
    // simply carries on with it.
    private static void Return(Simulation sim, Unit m)
    {
        if (m.SavedTask is not { } task) return;
        m.SavedTask = null;
        if (task.Kind == GoalKind.AssignWorker
            && sim.World.Structures.TryGetValue(task.TargetTile, out var s) && s is Extractor extractor)
            extractor.HeldBy.Remove(m.Id);   // the walking goal reserves the slot from here on
        if (m.Activity != Activity.Idle) return;
        // A target that is gone is said here; an unreachable one is said by the goal's own
        // dissolve (GoalRules.Begin).
        if (!sim.World.Structures.ContainsKey(task.TargetTile))
            sim.Schedule(sim.Now, new GoalDissolvedEvent(m.Id, task.Kind, task.TargetTile, "nothing to go back to"));
        else
            GoalRules.Begin(sim, m, task);
    }

    // ---- held slots -----------------------------------------------------------------

    // Forget a unit's hold on the work slot its saved task points at (it died, or the
    // player gave the slot away).
    internal static void ReleaseHold(GameWorld world, Unit m)
    {
        if (m.SavedTask is { Kind: GoalKind.AssignWorker } task
            && world.Structures.TryGetValue(task.TargetTile, out var s) && s is Extractor extractor)
            extractor.HeldBy.Remove(m.Id);
    }

    // ---- progress (pure) ------------------------------------------------------------

    // Where each member of a group (and the groups under it) stands with the muster:
    // here, on the way, finishing a job (and which), or with no room in reach. Pure read.
    public static MusterProgress Progress(GameWorld world, Group group)
    {
        var p = new MusterProgress();
        foreach (var leaf in Leaves(world, group))
            foreach (var m in Living(world, leaf))
            {
                if (leaf.State is not (GroupState.Forming or GroupState.Idle)) continue;
                if (!leaf.MusterPlaces.ContainsKey(m.Id)) { p.NoRoom++; continue; }
                if (!leaf.Awaiting.Contains(m.Id)) { p.Here++; continue; }
                if (Busy(world, m) is { } why) { p.Finishing.Add((m.Id, why)); continue; }
                p.OnTheWay++;
            }
        return p;
    }

    private static List<Unit> Living(GameWorld world, Group leaf)
    {
        var list = new List<Unit>(leaf.Members.Count);
        foreach (var id in leaf.Members)
            if (world.Units.TryGetValue(id, out var m)) list.Add(m);
        return list;
    }
}

// A muster's state of play, for the player (GroupMuster.Progress).
public sealed class MusterProgress
{
    public int Here { get; set; }
    public int OnTheWay { get; set; }
    public int NoRoom { get; set; }
    public List<(int UnitId, string Why)> Finishing { get; } = new();
}
