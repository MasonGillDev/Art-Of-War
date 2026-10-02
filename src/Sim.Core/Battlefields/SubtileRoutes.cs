using Sim.Core.Movement;

namespace Sim.Core.Battlefields;

// M42 phase 3 (docs/subtile-movement.md, "Subtile moves: a route the player
// draws, walked exactly"). A route is a chain of 4-adjacent subtiles, named on the
// whole map (WorldSubtile). The unit takes the steps as drawn: no pathfinding.
//
//   * Each step costs a quarter of the world hop onto the terrain it enters,
//     rounded up (SubtileStepRules.StepTicks), less the wear of the road link it
//     steps on, which the step also wears (Roads); a step across a river edge also
//     pays the ford (RiverConstants.CrossingCost).
//   * A route stays within the 3×3 block of world tiles around the tile the unit
//     starts on, and within MaxSteps.
//   * FRIENDS NEVER BLOCK A STEP (M43): a walking unit steps onto any subtile it may
//     enter, whoever is on it. Only STOPPING needs room: a walk that ends where a
//     friend now stands is re-aimed at the nearest free subtile (Walk).
//   * A step that can NEVER work (a wall, closed footprint side, water, a
//     fortification the player couldn't see when drawing) stops the unit and ends
//     the route: waiting would be for ever.
//   * The route is checked when it is given against what the player can SEE
//     (structures in the fog don't shape it, the same contract as world
//     pathfinding), so refusing a drawn route never tells them about a hidden
//     wall; the walk itself checks the ground truth.
//   * A battle opening on the unit's tile turns the route into a battle route,
//     walked one subtile a turn (Battlefields.AdoptRoute).
//
// State: Unit.SubtileRoute (the steps left), anchored by Unit.SubtileRouteTick/Seq
// (the M4 fencing token of the queued SubtileRouteStepEvent; null while paused).
// Grid worlds only.
public static class SubtileRoutes
{
    // Balance knob: the most steps one route may have. The 3×3 block holds 144
    // subtiles; the limit keeps saves and the wire small.
    public const int MaxSteps = 64;

    // ---- checking a drawn route (the player's view) --------------------------------

    // Why `route` can't be walked by `u` as the player knows the world, or null if
    // it can. Pure.
    public static string? Check(GameWorld world, Unit u, IReadOnlyList<WorldSubtile> route)
    {
        if (u.Subtile is not { } sub) return "the unit has no subtile";
        if (route.Count == 0) return "the route is empty";
        if (route.Count > MaxSteps) return $"a route may have at most {MaxSteps} steps";

        var start = WorldSubtile.Of(u.Position, sub);
        var visible = Sim.Core.Vision.View.VisibleTiles(world, u.OwnerId);
        var rules = new SubtileStepRules(world, StepMover.Of(u), visible);
        var prev = start;
        for (var i = 0; i < route.Count; i++)
        {
            var cur = route[i];
            if (!prev.IsAdjacentTo(cur)) return $"step {i + 1} isn't next to the one before it (routes are drawn square by square)";
            var tile = cur.Tile;
            if (Math.Abs(tile.X - u.Position.X) > 1 || Math.Abs(tile.Y - u.Position.Y) > 1)
                return $"step {i + 1} leaves the 3×3 block of tiles around where the unit starts";
            if (rules.Problem(prev, cur) is { } why) return $"step {i + 1}: {why}";
            prev = cur;
        }
        return null;
    }

    // Why the single step from → to can never work for `u`, or null. `visible` =
    // the tiles the player can see (structures elsewhere are ignored, planning);
    // null = ground truth (the walk). The rules are SubtileStepRules', the same the
    // pathfinder plans with.
    internal static string? StepProblem(GameWorld world, Unit u, WorldSubtile from, WorldSubtile to, HashSet<TileCoord>? visible) =>
        new SubtileStepRules(world, StepMover.Of(u), visible).Problem(from, to);

    // ---- walking it -------------------------------------------------------------------

    // Ticks the step onto `to` costs this unit: a quarter of the foot hop onto that
    // tile's ground, plus the ford at a river edge, slowed by its buffs (a cart trades
    // speed for cargo, docs/cart.md: real travel time, not a route change).
    public static long StepCost(GameWorld world, Unit u, WorldSubtile from, WorldSubtile to, long now)
    {
        long cost = SubtileStepRules.StepCost(world, u.Traversal, from, to, now);
        var slow = 0;
        foreach (var b in u.Buffs) slow += b.MoveCostPercent;
        if (slow != 0) cost = Math.Max(1, cost * (100 + slow) / 100);
        return cost;
    }

    public static void Cancel(Unit u)
    {
        u.SubtileRoute = null;
        u.SubtileRouteTick = null;
        u.SubtileRouteSeq = null;
        u.WaitingToEnter = null;
    }

    // Start (or replace) the walk. The caller has checked it and retasked the unit.
    internal static void Begin(Simulation sim, Unit u, IReadOnlyList<WorldSubtile> route)
    {
        u.SubtileRoute = route.ToList();
        Schedule(sim, u);
    }

    private static void Schedule(Simulation sim, Unit u)
    {
        if (u.SubtileRoute is not { Count: > 0 } route || u.Subtile is not { } sub) { Cancel(u); return; }
        var at = sim.Now + StepCost(sim.World, u, WorldSubtile.Of(u.Position, sub), route[0], sim.Now);
        u.SubtileRouteTick = at;
        u.SubtileRouteSeq = sim.Schedule(at, new SubtileRouteStepEvent(u.Id));
    }

    // One step, on the event. Fenced by the anchor. Returns the event's outcome text
    // when the walk ended without finishing (for the log), else null.
    internal static string? Step(Simulation sim, SubtileRouteStepEvent ev, Unit u)
    {
        if (u.SubtileRouteTick != ev.At || u.SubtileRouteSeq != ev.Seq) return null;   // stale
        u.SubtileRouteTick = null;
        u.SubtileRouteSeq = null;
        var world = sim.World;
        if (u.SubtileRoute is not { Count: > 0 } route || u.Subtile is not { } sub || u.IsEmbarked)
        {
            Walk.Stop(u);
            return null;
        }
        if (u.Board is not null) return null;   // a battle has the unit: the board took the walk (AdoptRoute)

        var cur = WorldSubtile.Of(u.Position, sub);
        var next = route[0];
        if (!cur.IsAdjacentTo(next))
        {
            Walk.Stop(u);
            return "the unit was moved off its walk";
        }

        // Ground truth: every structure counts, the ones the planner couldn't see too.
        var rules = new SubtileStepRules(world, StepMover.Of(u), null);
        if (rules.Problem(cur, next) is { } why)
        {
            // Already on the tile it was sent to, and the ground there isn't what the plan
            // thought (a structure in the fog, a footprint over the centre): re-aim at the
            // nearest subtile it really may stand on, or, with none reachable, it has arrived
            // as near as it can get (a hauler at a cache, a unit at a fenced-in tile).
            if (u.PathFinalDest is { } bound && u.Position == bound && Walk.ReAimInTile(sim, u, rules, cur, bound))
                return null;
            Walk.Halt(sim, u);
            return $"walk ended: {why}";
        }

        // The last step must be a place to stand: a friend standing there now sends the
        // walk to the nearest free subtile instead (a friend WALKING through doesn't).
        if (route.Count == 1 && Walk.HeldByStanding(world, u, next.Tile, next.Sub))
        {
            // A tile order takes the free subtile nearest the tile's CENTRE (its rule); a drawn
            // route the nearest free one to the subtile it was drawn to.
            WorldSubtile? free = u.PathFinalDest is not null && Walk.PickGoal(world, u, rules, next.Tile, null) is { } centred
                ? centred
                : Walk.NearestFree(world, u, rules, next);
            if (free is { } spot)
            {
                var way = spot == cur ? new List<WorldSubtile>() : SubtilePathfinder.Find(world, StepMover.Of(u), cur, spot, null, null, true, sim.Now);
                if (way is not null)
                {
                    route.Clear();
                    route.AddRange(way);
                    if (route.Count == 0) { Walk.Finished(sim, u); return null; }
                    Schedule(sim, u);
                    return null;
                }
            }
            Walk.Halt(sim, u);
            return "walk ended: no room to stop";
        }

        // M43 (docs/fix-combat-m43.md): a step ACROSS an edge onto a tile with an open board needs
        // room there. A standing friend on the arrival subtile, or the unit's side at the cap,
        // and the walker WAITS at the edge (on its own tile, off the board) and retries on the
        // board's beats, rather than stepping in and being popped somewhere else. A hostile unit
        // on the arrival subtile is no block: that is a duel. A tile with no board keeps
        // "friends pass through".
        if (MustWaitAtEdge(world, u, cur, next))
            return WaitAtEdge(sim, u, next.Tile);
        u.WaitingToEnter = null;

        route.RemoveAt(0);
        Advance(sim, u, cur, next);

        // A battle opened here and took the unit: it fights by the board's rules and
        // resumes its walk when it leaves (Battlefields).
        if (u.Board is not null)
        {
            // The step that opened the fight was the walk's last: it has arrived, and the errand it
            // carries still runs (a chase that caught its target is over; a hauler caught at the
            // castle still deposits). A walk with steps left was bound for this tile too if it says
            // so: Battlefields.Enroll dispatches that case.
            if (route.Count == 0 && u.PathFinalDest == u.Position) Walk.Finished(sim, u);
            return null;
        }

        if (route.Count == 0) Walk.Finished(sim, u);
        else Schedule(sim, u);
        return null;
    }

    // A step across an edge onto a tile with an open board, where a standing friend holds
    // the arrival subtile or the unit's side is at the cap: the walker waits at the edge.
    internal static bool MustWaitAtEdge(GameWorld world, Unit u, WorldSubtile cur, WorldSubtile next) =>
        cur.Tile != next.Tile && world.Battlefields.ContainsKey(next.Tile)
        && (Walk.HeldByStanding(world, u, next.Tile, next.Sub) || !TileCapacity.HasRoom(world, next.Tile, u.OwnerId));

    // What one step does to the world, for a solo walk and a group's column alike: the step
    // wears its road link, moves the unit (entering the next tile, with everything that
    // entails), and may open a fight there.
    internal static void Advance(Simulation sim, Unit u, WorldSubtile cur, WorldSubtile next)
    {
        // THE mutation point for road wear (Roads/Road.cs): the step just taken wears its link.
        if (u.Traversal == Traversal.Foot) Sim.Core.Roads.Road.CreditTraffic(sim.World, cur, next, sim.Now);
        if (cur.Tile == next.Tile)
            u.Subtile = next.Sub;
        else
        {
            TileEntry.Enter(sim, u, cur.Tile, next.Tile, next.Sub);
            Sim.Core.Combat.CombatTrigger.MaybeBeginCombatOnTile(sim, next.Tile);
        }
    }

    // The walker stays where it is and tries the same step on the board's next beat; after
    // BattleConfig.MaxEntryWaitBeats of waiting the walk ends. Re-uses the step's anchor, so a
    // restore queues the retry exactly (RegenerateQueue).
    private static string? WaitAtEdge(Simulation sim, Unit u, TileCoord tile)
    {
        var world = sim.World;
        if (u.WaitingToEnter != tile) { u.WaitingToEnter = tile; u.WaitingSince = sim.Now; }
        if (sim.Now - u.WaitingSince >= BattleConfig.MaxEntryWaitBeats * world.CombatConfig.RoundIntervalTicks)
        {
            Walk.Halt(sim, u);
            return "walk ended: no room on the battlefield";
        }
        var at = Battlefields.NextBeat(world, sim.Now);
        u.SubtileRouteTick = at;
        u.SubtileRouteSeq = sim.Schedule(at, new SubtileRouteStepEvent(u.Id));
        return null;
    }
}

// One step of a unit's walk (SubtileRoutes).
public sealed class SubtileRouteStepEvent : ScheduledEvent
{
    public int UnitId { get; }

    public SubtileRouteStepEvent(int unitId) { UnitId = unitId; }

    public override void Apply(Simulation sim)
    {
        if (!sim.World.Units.TryGetValue(UnitId, out var unit)) return;
        if (SubtileRoutes.Step(sim, this, unit) is { } note) Outcome = IntentOutcome.Reject(note);
    }

    public override string Describe() => $"SubtileRouteStep(unit={UnitId})";
}
