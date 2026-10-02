using Sim.Core.Battlefields;
using Sim.Core.Vision;

namespace Sim.Core.Movement;

// M43 — THE ONE MOVEMENT (docs/subtile-movement.md, "all movement on subtiles";
// docs/m43-status.md). A unit going anywhere WALKS: the steps left are
// Unit.SubtileRoute, one queued SubtileRouteStepEvent takes the next, and a step
// across a tile edge is a tile entry (TileEntry.Enter). There are no tile hops, no
// entry lanes and no walking in.
//
// This file starts a walk (a tile order → the pathfinder's path), ends one (the
// errand dispatch that used to sit in MoveArrivalEvent), and picks where a walk
// ENDS: the free subtile nearest the tile's centre. Stepping is SubtileRoutes.
//
// THE RULES OF ROOM (the user, 2026-09-29):
//   * Only enemies share a subtile while they STAND. Friends pass through each
//     other freely: a moving unit is never held by anyone, so a busy road doesn't jam.
//   * Only a unit that STOPS needs a subtile of its own. Stopping where a friend now
//     stands re-aims the walk at the nearest free subtile.
// "Standing" = not walking.
public static class Walk
{
    // ---- starting a walk ---------------------------------------------------------------

    // M43 step 6: can a tile order for `unit` find a walk to `dest` right now? The same plan
    // Begin makes (the owner's view of the world, the nearest land for water-aimed feet), so
    // a driver can skip a target that would leave its party re-ordering every think and never
    // arriving. A pure read: nothing is queued or changed.
    public static bool CanReach(GameWorld world, Unit unit, TileCoord dest)
    {
        if (unit.IsEmbarked || !world.Grid.InBounds(dest)) return false;
        if (unit.Position == dest) return true;
        if (unit.Subtile is not { } sub) return false;
        var visible = View.VisibleTiles(world, unit.OwnerId);
        var start = WorldSubtile.Of(unit.Position, sub);
        var mover = StepMover.Of(unit);
        var rules = new SubtileStepRules(world, mover, visible);
        IEnumerable<TileCoord> targets = unit.Traversal == Traversal.Foot && MovementCost.FeetKeepOffWater(world, dest)
            ? MovementCost.LandNear(world, dest)
            : new[] { dest };
        foreach (var target in targets)
        {
            if (target == unit.Position) return true;
            if (PickGoal(world, unit, rules, target, null) is { } goal
                && SubtilePathfinder.Find(world, mover, start, goal, visible) is not null) return true;
        }
        return false;
    }

    // Start (or replace) a unit's walk to the tile `finalDest`: the free subtile
    // nearest that tile's centre, by the pathfinder's path, planned as the unit's
    // owner sees the world (structures in the fog plan as open ground). Nothing
    // happens if the unit is already on the tile (its callers act where it stands),
    // if there is no path, or if there is no room. Water-aimed foot moves end on
    // the nearest reachable land. `reserved` = subtiles other walkers of the same
    // order are already going to (a group), so they don't all pick one.
    internal static void Begin(Simulation sim, Unit unit, TileCoord finalDest, HashSet<WorldSubtile>? reserved = null)
    {
        var world = sim.World;
        SubtileRoutes.Cancel(unit);
        unit.PathFinalDest = null;
        if (unit.IsEmbarked || !world.Grid.InBounds(finalDest)) return;
        if (unit.Subtile is null) Placement.Seat(world, unit);
        if (unit.Subtile is not { } sub) return;

        var visible = View.VisibleTiles(world, unit.OwnerId);
        var start = WorldSubtile.Of(unit.Position, sub);
        var mover = StepMover.Of(unit);
        var rules = new SubtileStepRules(world, mover, visible);

        // Foot moves aimed at water end on the nearest land they can reach.
        IEnumerable<TileCoord> targets = unit.Traversal == Traversal.Foot && MovementCost.FeetKeepOffWater(world, finalDest)
            ? MovementCost.LandNear(world, finalDest)
            : new[] { finalDest };
        foreach (var target in targets)
        {
            if (target == unit.Position && unit.Board is null) return;   // it stands on the nearest land
            var goal = PickGoal(world, unit, rules, target, reserved);
            if (goal is null) continue;
            var path = SubtilePathfinder.Find(world, mover, start, goal.Value, visible, now: sim.Now);
            if (path is null) continue;
            reserved?.Add(goal.Value);
            unit.PathFinalDest = target;
            if (unit.Board is not null)
            {
                // In a battle the walk is a battle order: its steps inside this tile are taken
                // a turn at a time, and it leaves and carries on by itself (Battlefields).
                unit.SubtileRoute = path;
                Battlefields.Battlefields.AdoptRoute(unit);
                if (Battlefields.Battlefields.TryGet(world, unit, out var bf)) Battlefields.Battlefields.Wake(sim, bf);
                return;
            }
            if (path.Count == 0) { Finished(sim, unit); return; }
            SubtileRoutes.Begin(sim, unit, path);
            return;
        }
    }

    // A unit's walk resumes toward the tile it was heading for (a battle held it, it
    // left a board, a fight ended). Planned afresh from where it stands: a pure function
    // of the world at the tick this is called, so it is deterministic. Already there:
    // the walk is over and its errand runs.
    internal static void Resume(Simulation sim, Unit unit)
    {
        if (unit.PathFinalDest is not { } dest || unit.IsWalking || unit.Board is not null) return;
        if (unit.Position == dest)
        {
            Finished(sim, unit);
            return;
        }
        Begin(sim, unit, dest);
        if (!unit.IsWalking) unit.PathFinalDest = null;   // nowhere to go: the errand can't go on
    }

    // A unit that has just come to rest somewhere it may not stay (a friend already stands
    // on its subtile, as after stepping off a board onto an edge lane): it walks to the
    // nearest free one.
    internal static void Settle(Simulation sim, Unit unit)
    {
        if (unit.IsWalking || unit.Board is not null || unit.IsEmbarked || unit.Subtile is not { } sub) return;
        var world = sim.World;
        if (!HeldByStanding(world, unit, unit.Position, sub)) return;
        var rules = new SubtileStepRules(world, StepMover.Of(unit), null);
        var here = WorldSubtile.Of(unit.Position, sub);
        if (NearestFree(world, unit, rules, here) is not { } free || free == here) return;
        if (SubtilePathfinder.Find(world, StepMover.Of(unit), here, free) is { Count: > 0 } path)
            SubtileRoutes.Begin(sim, unit, path);
    }

    // Cut a walk short at the first tile it enters (a chase's leg): the steps inside the
    // current tile and the one across its edge remain, and that tile is where the walk is
    // bound. The end of the leg is then an arrival, and the chase re-plans.
    internal static void EndAtNextTile(Unit unit)
    {
        if (unit.SubtileRoute is not { Count: > 0 } route) return;
        for (var i = 0; i < route.Count; i++)
        {
            if (route[i].Tile == unit.Position) continue;
            route.RemoveRange(i + 1, route.Count - i - 1);
            unit.PathFinalDest = route[i].Tile;
            return;
        }
    }

    // The unit is on the tile it was sent to and its next step isn't allowed (ground truth,
    // against what the plan knew): walk to the nearest subtile the ground really lets it
    // stand on, or, if none can be reached, count the walk as done. True: handled (the walk
    // was replaced or finished); false: not applicable.
    internal static bool ReAimInTile(Simulation sim, Unit unit, SubtileStepRules truth, WorldSubtile cur, TileCoord tile)
    {
        var world = sim.World;
        var mover = StepMover.Of(unit);
        if (PickGoal(world, unit, truth, tile, null) is { } goal && goal != cur
            && SubtilePathfinder.Find(world, mover, cur, goal, null, null, true, sim.Now) is { Count: > 0 } path
            && truth.Allows(cur, path[0]))
        {
            SubtileRoutes.Begin(sim, unit, path);
            return true;
        }
        Finished(sim, unit);
        return true;
    }

    // ---- ending a walk -----------------------------------------------------------------

    // The walk's last step is taken: an errand walk dispatches what waits on its arrival;
    // any walk ends by looking for a siege beside the tile it stopped on.
    internal static void Finished(Simulation sim, Unit unit)
    {
        SubtileRoutes.Cancel(unit);
        var errand = unit.PathFinalDest is not null;
        unit.PathFinalDest = null;
        if (errand) DispatchOnArrival(sim, unit);
        if (!unit.IsWalking)
            Sim.Core.Fortifications.FortSiege.MaybeBeginSiegeAdjacentTo(sim, unit.Position);
    }

    // The walk can't go on (a wall the planner couldn't see, ground that changed): the
    // unit stops where it is and becomes Idle, as a hop refused by the ground truth always
    // did, and looks for a siege beside it if the blocker is hostile.
    internal static void Halt(Simulation sim, Unit unit)
    {
        var errand = unit.PathFinalDest is not null;
        SubtileRoutes.Cancel(unit);
        unit.PathFinalDest = null;
        if (errand) unit.TrySetActivity(Activity.Idle);
        // A chase whose walk was halted (no room at a battlefield's edge, a wall it couldn't
        // see) is over: an anchor left on a unit that has stopped moving is a permanent brick
        // (PursuitRules). A patrol sees the target again at its next think and re-engages.
        if (unit.Pursuit is not null) Sim.Core.Combat.PursuitRules.Release(unit);
        // M44 — a walk to a survey slope that can't go on abandons the survey
        // out loud rather than leaving a plan on an idle body.
        if (unit.Survey is { CompleteTick: null }) Sim.Core.Mining.SurveyRules.Cancel(sim, unit, "the way to the slope is blocked");
        // Only a walk with an errand counted for its group: a settling step after arriving
        // was never on the pending count.
        if (errand) NoteWalkEnded(sim, unit);
        Sim.Core.Fortifications.FortSiege.MaybeBeginSiegeAdjacentTo(sim, unit.Position);
        // A walk can end mid-pass, on a subtile a friend stands on (friends pass through each
        // other; "no room to stop" on a full tile is exactly that). Stopping there would break
        // the rule of room, so it walks to the nearest free subtile instead.
        Settle(sim, unit);
    }

    // Drop a unit's walk without a word (it was retasked, it died, a fight took it): no
    // dispatch, no siege check. The callers that used to clear the four path fields.
    internal static void Stop(Unit unit)
    {
        SubtileRoutes.Cancel(unit);
        unit.PathFinalDest = null;
    }

    // ---- what waits on an arrival -------------------------------------------------------

    // Derives "what happens at the end of the walk" from unit state (HaulPlan,
    // GroupId, Pursuit, Goal, a scout mission) instead of an event field, so
    // RegenerateQueue can rebuild the queue from state alone (M4 Phase A).
    //
    // Dispatch order: pursuit, scout mission, haul, goal, group. Pursuit first
    // deliberately: a chase re-plans at every step it commits, and a hostile encounter
    // always wins over an errand rather than racing it.
    internal static void DispatchOnArrival(Simulation sim, Unit unit)
    {
        // M29 — PURSUIT. PursuitRules either takes the next leg toward where the target is
        // NOW or ends the chase and frees the body. The combat trigger for this tile has
        // already run, so a catch is already a fight.
        if (unit.Pursuit is not null)
        {
            Sim.Core.Combat.PursuitRules.Step(sim, unit);
            return;
        }

        // M20 — a scout on an active/returning mission advances to its next waypoint (or
        // home). A Returned (or absent) mission falls through to ordinary handling.
        if (sim.World.ScoutMissions.TryGetValue(unit.Id, out var mission)
            && mission.State != Sim.Core.Scouting.ScoutMissionState.Returned)
        {
            Sim.Core.Scouting.ScoutMissionRunner.Advance(sim, unit);
            return;
        }

        if (unit.HaulPlan is { } plan)
        {
            // M28 — AtStop, not position equality: a boat's stop for a dock tile is the
            // dock's SLIP. Foot haulers use the exact rule (position == plan tile).
            switch (plan.Phase)
            {
                case HaulPhase.ToSource when HaulStops.AtStop(sim.World, unit, plan.SourceTile):
                    sim.Schedule(sim.Now,
                        new HaulPickupEvent(unit.Id, plan.SourceTile, plan.DestTile, plan.Resource, unit.AssignmentEpoch));
                    break;
                case HaulPhase.ToDest when HaulStops.AtStop(sim.World, unit, plan.DestTile):
                    sim.Schedule(sim.Now,
                        new HaulDepositEvent(unit.Id, plan.DestTile, unit.AssignmentEpoch));
                    break;
                // Otherwise the unit stopped somewhere that doesn't match the haul's
                // expectation. Leave HaulPlan in place; pending haul events fence on epoch.
            }
            return;
        }

        // M30 — a goal-shaped intent finishing its travel leg. Last among the errands: pursuit
        // and scouting outrank an assignment, and a unit carrying a Goal never carries a
        // HaulPlan or Pursuit anyway. docs/goal-shaped-intents.md.
        if (unit.Goal is not null)
        {
            Sim.Core.Intents.GoalRules.OnArrival(sim, unit);
            return;
        }

        // M44 — a Miner reaching the slope he was sent to survey starts digging.
        if (unit.Survey is not null)
        {
            Sim.Core.Mining.SurveyRules.OnArrival(sim, unit);
            return;
        }

        // M5 — a member of a Forming or Moving group whose walk ended comes off the
        // group's pending count.
        NoteWalkEnded(sim, unit);
    }

    // A member of a Forming or Moving group stopped walking (arrived, or halted): the
    // group is Idle again when the last one has. Not only on the rendezvous tile: a walk
    // aimed at water ends on the nearest land, and that member is as done as it will get.
    internal static void NoteWalkEnded(Simulation sim, Unit unit)
    {
        if (unit.GroupId is { } gid && sim.World.Groups.TryGetValue(gid, out var group))
            GroupRules.OneLessPending(sim, group, unit);
    }

    // ---- where a walk ends ----------------------------------------------------------------

    // A unit that is not walking holds its subtile against any non-hostile unit that
    // wants to stop there.
    public static bool HeldByStanding(GameWorld world, Unit u, TileCoord tile, Subtile sub)
    {
        foreach (var o in world.Units.Values)
        {
            if (o == u || o.Position != tile || o.Subtile != sub || o.IsEmbarked || o.IsWalking) continue;
            if (!world.Diplomacy.AreHostile(u.OwnerId, o.OwnerId)) return true;
        }
        return false;
    }

    // The free subtile nearest the tile's centre that the mover may stand on: the four
    // centre subtiles first, then outward by distance from the centre (ties by row, then
    // column). Null if the tile has no room for this unit.
    internal static WorldSubtile? PickGoal(
        GameWorld world, Unit u, SubtileStepRules rules, TileCoord tile, HashSet<WorldSubtile>? reserved)
    {
        foreach (var s in CentreOut)
        {
            var at = WorldSubtile.Of(tile, s);
            if (reserved is not null && reserved.Contains(at)) continue;
            if (!rules.CanStand(at) || HeldByStanding(world, u, tile, s)) continue;
            return at;
        }
        return null;
    }

    // The subtiles of a tile nearest its centre first: squared distance to the centre
    // point (1.5, 1.5), then north to south, west to east.
    internal static readonly Subtile[] CentreOut = Subtile.All()
        .OrderBy(s => (2 * s.X - 3) * (2 * s.X - 3) + (2 * s.Y - 3) * (2 * s.Y - 3))
        .ThenBy(s => s.Y).ThenBy(s => s.X)
        .ToArray();

    // The nearest free place to stop, found outward from `origin` by steps the mover may
    // take (never through a wall), N, E, S, W, at most `MaxSettleDepth` steps: the subtile
    // itself if it is free. Null if there is none. Pure.
    internal static WorldSubtile? NearestFree(GameWorld world, Unit u, SubtileStepRules rules, WorldSubtile origin)
    {
        var seen = new HashSet<WorldSubtile> { origin };
        var frontier = new List<WorldSubtile> { origin };
        for (var depth = 0; depth <= MaxSettleDepth; depth++)
        {
            foreach (var s in frontier)
                if (rules.CanStand(s) && !HeldByStanding(world, u, s.Tile, s.Sub)) return s;
            var next = new List<WorldSubtile>();
            foreach (var s in frontier)
                foreach (var (dx, dy) in Around)
                {
                    var n = new WorldSubtile(s.X + dx, s.Y + dy);
                    if (!seen.Add(n) || !world.Grid.InBounds(n.Tile) || !rules.Allows(s, n)) continue;
                    next.Add(n);
                }
            frontier = next;
            if (frontier.Count == 0) break;
        }
        return null;
    }

    private const int MaxSettleDepth = 12;
    private static readonly (int Dx, int Dy)[] Around = { (0, -1), (1, 0), (0, 1), (-1, 0) };   // N, E, S, W
}
