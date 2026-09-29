namespace Sim.Core.Movement;

public sealed class MoveIntent : Intent
{
    public int UnitId { get; }
    public TileCoord Destination { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public MoveIntent(int unitId, TileCoord destination)
    {
        UnitId = unitId;
        Destination = destination;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        if (!sim.World.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        if (unit.GroupId is not null)
            return IntentOutcome.Reject($"unit {UnitId} is in group {unit.GroupId}");
        // M12 — embarked units are off-tile passengers; solo intents
        // are blocked until the boat disembarks them. (Note: boats
        // themselves use MoveIntent for water travel; their Traversal
        // selects the BoatMovementCost table inside BeginMove. MoveBoat
        // is not a separate intent type.)
        if (unit.IsEmbarked)
            return IntentOutcome.Reject($"unit {UnitId} is embarked on boat {unit.EmbarkedOn}");
        // M8 follow-up: breeding is a commitment, not a retaskable assignment.
        // A parent in an active breeding cycle is locked to the house until
        // BirthEvent (or stop-on-removal via combat / aging death) frees them.
        // No cancel — the player can't back out. This is the one case where
        // MoveIntent is NOT authoritative.
        if (Sim.Core.Population.Population.GetActiveBreedingFor(sim.World, UnitId) is { } breedingHouse)
            return IntentOutcome.Reject(
                $"unit {UnitId} is breeding at house ({breedingHouse.At.X},{breedingHouse.At.Y}) and cannot be moved");
        if (!sim.World.Grid.InBounds(Destination))
            return IntentOutcome.Reject($"destination {Destination.X},{Destination.Y} out of bounds");

        // M30 — a march countermands a goal. The player has restated where
        // this body should be, which overrides where the sim was taking it;
        // dissolving here (rather than letting the anchor ride) is what keeps
        // a countermanded goal from firing on arrival at the NEW destination.
        // The one goal that outranks a move is a breeding cycle already
        // conceived — rejected above, before this line.
        // docs/goal-shaped-intents.md.
        if (unit.Goal is not null)
            Sim.Core.Intents.GoalRules.Dissolve(sim, unit, "countermanded by a new order");

        // Move-on-busy: a MoveIntent is authoritative — the player has retasked
        // this unit, and any structure depending on them gets cleaned up.
        // Cargo on a Hauling unit stays with them (they walk holding it; the
        // player can issue a new haul later). Pending per-unit events from the
        // old task fence on AssignmentEpoch and no-op when they fire.
        if (unit.Activity != Activity.Idle)
            CleanUpAssignment(sim, unit);   // bumps epoch via TrySetActivity(Idle)
        else
            unit.BumpEpoch();                // explicit bump for Idle→Idle move so
                                             // any prior move chain's MoveArrivalEvents fence out

        // M36 — a countermanded haul is OVER. Its plan used to ride along: the
        // unit arrived somewhere else, went Idle, and kept a dead HaulPlan
        // forever — every "is this hauler free?" test (the haul queue, the
        // automation substrate) read it as busy, and the queue counted its
        // cargo as "on the way" to a job it would never reach. Found in play:
        // a breed order walked a laden queue hauler into a house mid-trip.
        // The cargo stays aboard (unload or haul it later), only the plan goes.
        unit.HaulPlan = null;

        BeginMove(sim, unit, Destination);
        return IntentOutcome.Applied;
    }

    // Delegates to the shared release (Sim.Core.Logistics.WorkAssignment) so
    // the move path and the DEATH path retire a job identically — they used to
    // differ, and a dead worker stayed on an extractor's payroll forever.
    private static void CleanUpAssignment(Simulation sim, Unit unit) =>
        Sim.Core.Logistics.WorkAssignment.Release(sim, unit);

    // M4 Phase A: start a new movement chain to `finalDest`. Computes the full
    // committed path once, stores it on the unit, and schedules the first
    // MoveArrivalEvent. Called by:
    //   * MoveIntent.Resolve (player-issued move)
    //   * HaulIntent.Resolve (move to source)
    //   * HaulPickupEvent.Apply (after pickup, move to dest)
    //
    // The path is STORED on the unit (Unit.PathRemaining) rather than
    // recomputed per hop or per restore. Recomputing against current road
    // conditions could yield a different path than the live sim took,
    // breaking determinism.
    internal static void BeginMove(Simulation sim, Unit unit, TileCoord finalDest)
    {
        if (unit.Position == finalDest)
        {
            // Already at destination — clear any stale path anchors.
            unit.PathRemaining = null;
            unit.PathFinalDest = null;
            unit.NextArrivalTick = null;
            unit.NextArrivalSeq  = null;
            return;
        }

        var world = sim.World;
        var now = sim.Now;
        // FOG-AWARE PLANNING: the cost the planner sees on each tile is what
        // the OWNING PLAYER could see — own units always counted, non-own
        // only on currently-visible tiles. A* will route around visible
        // crowds but cannot see through the fog. The unit may stumble into
        // hidden congestion (paying ground-truth ExecutionCost) — that's the
        // "cost of ignorance" gameplay loop. See docs/movement-cost.md.
        var visibleTiles = View.VisibleTiles(world, unit.OwnerId);
        var trav = unit.Traversal;
        // Tile shapes (docs/structure-footprints.md): a castle is entered by its
        // gate, a canal followed, not crossed. The mover starts from the edge
        // it came onto this tile by.
        var cost = MovementCost.Planner(world, unit.OwnerId, visibleTiles, now, trav);
        var rule = trav == Traversal.Foot && CrossingRule.Applies(world) ? CrossingRule.Planning(world, unit.OwnerId, visibleTiles) : null;
        var entry = CrossingRule.EntryEdge(unit.Position, unit.EnteredFrom);
        List<TileCoord>? path;
        if (trav == Traversal.Foot && MovementCost.FeetKeepOffWater(world, finalDest))
        {
            // Aimed at water: walk to the nearest land that can be reached.
            path = null;
            foreach (var land in MovementCost.LandNear(world, finalDest))
            {
                if (land == unit.Position) { finalDest = land; path = new List<TileCoord> { land }; break; }
                path = Pathfinding.FindPath(world.Grid, unit.Position, land, cost, rule, entry);
                if (path is not null) { finalDest = land; break; }
            }
            if (unit.Position == finalDest && unit.Board is null)
            {
                unit.PathRemaining = null;
                unit.PathFinalDest = null;
                unit.NextArrivalTick = null;
                unit.NextArrivalSeq  = null;
                return;
            }
        }
        else
            path = Pathfinding.FindPath(world.Grid, unit.Position, finalDest, cost, rule, entry);
        if ((path is null || path.Count < 2) && unit.Board is not null)
        {
            Sim.Core.Battlefields.Battlefields.DeferWorldMove(sim, unit, path, finalDest);
            return;
        }
        if (path is null || path.Count < 2)
        {
            unit.PathRemaining = null;
            unit.PathFinalDest = null;
            unit.NextArrivalTick = null;
            unit.NextArrivalSeq  = null;
            return;
        }

        // M41 — on a battlefield the path is committed but not walked: the
        // board steers the unit to the edge it leaves by (Battlefields).
        if (unit.Board is not null)
        {
            Sim.Core.Battlefields.Battlefields.DeferWorldMove(sim, unit, path, finalDest);
            return;
        }

        // path[0] == unit.Position; the rest is the committed itinerary.
        unit.PathRemaining = path.Skip(1).ToList();
        unit.PathFinalDest = finalDest;
        ScheduleNextHop(sim, unit);
    }

    // Pop nothing — just schedule the arrival for PathRemaining[0]. Used by
    // BeginMove (initial schedule) and MoveArrivalEvent.Apply (per-hop
    // continuation after popping the head).
    internal static void ScheduleNextHop(Simulation sim, Unit unit)
    {
        if (unit.PathRemaining is null || unit.PathRemaining.Count == 0 || unit.PathFinalDest is null)
            return;
        var next = unit.PathRemaining[0];
        var world = sim.World;
        // M26 — GROUND-TRUTH fortification gate. The committed path may run
        // through a wall the planner couldn't see (fog) or one that completed
        // after planning. The unit yields HERE — same semantics as the
        // arrival-time cap rejection — and, having been stopped face-to-wall,
        // opens a siege if the blocker is hostile (docs/walls-and-gates.md).
        // The tile-shape gate, ground truth: the planner may not have known a
        // structure (fog), or one went up after planning. Same yield.
        if (Fortification.BlocksMover(world, next, unit.OwnerId)
            || (unit.Traversal == Traversal.Foot && CrossingRule.Applies(world)
                && !CrossingRule.GroundTruth(world, unit.OwnerId).AllowsHop(
                    unit.Position, CrossingRule.EntryEdge(unit.Position, unit.EnteredFrom), next,
                    final: next == unit.PathFinalDest)))
        {
            unit.PathRemaining = null;
            unit.PathFinalDest = null;
            unit.NextArrivalTick = null;
            unit.NextArrivalSeq  = null;
            unit.TrySetActivity(Activity.Idle);
            FortSiege.MaybeBeginSiegeAdjacentTo(sim, unit.Position);
            return;
        }
        // GROUND-TRUTH HOP COST: includes BOTH source and destination
        // crowding (whichever is more crowded), regardless of fog. The
        // unit pays the real cost of this hop even if it differs from
        // what the plan assumed. See MovementCost.ExecutionCost.
        var hopCost = MovementCost.ExecutionCost(world, unit.Position, next, sim.Now, unit.Traversal);
        // The ground under the path changed since it was planned (a canal dug
        // across it): the hop can't be walked, so the unit stops here rather
        // than scheduling an arrival an Impassable number of ticks away.
        if (hopCost >= Sim.Core.World.Biomes.Impassable)
        {
            unit.PathRemaining = null;
            unit.PathFinalDest = null;
            unit.NextArrivalTick = null;
            unit.NextArrivalSeq  = null;
            unit.TrySetActivity(Activity.Idle);
            return;
        }
        // M-cart — per-unit move-cost buffs (a cart trades speed for cargo).
        // Applied to the EXECUTION cost only — the slowdown is real travel
        // time, not a route change, so the planned path is unaffected. Summed
        // across buffs; guarded so an Impassable cost never overflows; long
        // intermediate so the *(100+slow) can't wrap. docs/cart.md.
        if (hopCost < Sim.Core.World.Biomes.Impassable)
        {
            var slow = 0;
            foreach (var b in unit.Buffs) slow += b.MoveCostPercent;
            if (slow != 0) hopCost = (int)((long)hopCost * (100 + slow) / 100);
        }
        var arrival = sim.Now + hopCost;
        unit.NextArrivalTick = arrival;
        unit.NextArrivalSeq  = sim.Schedule(arrival,
            new MoveArrivalEvent(unit.Id, next, unit.PathFinalDest.Value, unit.AssignmentEpoch));
    }

    public override string Describe() =>
        $"MoveIntent(unit={UnitId} -> {Destination.X},{Destination.Y})";
}
