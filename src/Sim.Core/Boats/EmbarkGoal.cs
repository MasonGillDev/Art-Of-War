using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Boats;

// Boarding as an ERRAND (docs/goal-shaped-intents.md).
//
// The hardest of the goal-shaped set, because a boarding has TWO moving parts
// and the intent used to demand both be already still: every passenger standing
// on the dock, and the hull already tied up beside it. Getting a crew aboard
// therefore meant marching each body to the dock, sailing the boat over, and
// only then — once everything happened to be motionless in the right places —
// issuing the intent. Three appointments for one decision ("these people, that
// boat").
//
// PER-PASSENGER, NOT ATOMIC — and only on the walked path. The standing-there
// intent keeps its all-or-nothing rule, because a player who can see everyone
// on the dock is entitled to "all of them or tell me why". But an errand is
// spread over game-days: insisting every passenger arrive before ANY may board
// would mean one straggler holds the whole crew on the quayside, and a
// straggler who dies holds them there forever. So each body boards the moment
// it can, and anyone who arrives to a full hull dissolves and says so.
//
// TWO WAKE-UPS, because either side can be the one that is late:
//   * a passenger arriving at the dock (GoalRules.OnArrival), and
//   * the BOAT arriving beside it (OnBoatArrived, from MoveArrivalEvent).
// Both are existing events. Nothing polls.
public static class EmbarkGoal
{
    // A passenger has finished walking to the dock. Board if the hull is here
    // and has room; otherwise stand and wait for it.
    public static void OnArrival(Simulation sim, Unit passenger)
    {
        if (passenger.Goal is not { } goal) return;
        var world = sim.World;

        if (!world.Units.TryGetValue(goal.PartnerUnitId, out var boat)
            || boat.Role != UnitRole.Boat
            || boat.OwnerId != passenger.OwnerId)
        {
            GoalRules.Dissolve(sim, passenger, "the boat is gone");
            return;
        }
        if (Blocker(world, passenger, boat) is { } why)
        {
            GoalRules.Dissolve(sim, passenger, why);
            return;
        }

        if (TryBoard(sim, passenger, boat)) { GoalRules.Complete(passenger); return; }

        // Boarding failed, and WHY decides whether this is a wait or an ending.
        // A hull that is alongside and FULL will not empty itself on any
        // schedule the sim can wake on, so waiting for it would be the silent
        // stall the visibility contract forbids. A hull that is merely
        // elsewhere is the ordinary case this errand exists for.
        if (Alongside(sim.World, passenger, boat) && boat.Passengers.Count >= boat.PassengerCap)
        {
            GoalRules.Dissolve(sim, passenger, "the boat is full");
            return;
        }

        // Wait ON the dock — a non-Idle state, so nothing else retasks a body
        // that is queued to sail.
        passenger.TrySetActivity(Activity.Waiting, goal.TargetTile);
    }

    // A boat has just finished a hop. Anyone waiting on an adjacent dock for
    // THIS hull boards now.
    //
    // Cheap for the overwhelmingly common case — a role check and, only for
    // boats, one pass over the units already waiting. Canonical id order so a
    // hull with fewer berths than hopefuls fills deterministically.
    public static void OnBoatArrived(Simulation sim, Unit boat)
    {
        if (boat.Role != UnitRole.Boat) return;

        List<Unit>? waiting = null;
        foreach (var u in sim.World.Units.Values)
        {
            if (u.Activity != Activity.Waiting) continue;
            if (u.Goal is not { Kind: GoalKind.Embark } g) continue;
            if (g.PartnerUnitId != boat.Id) continue;
            (waiting ??= new List<Unit>()).Add(u);
        }
        if (waiting is null) return;

        waiting.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        foreach (var p in waiting)
        {
            if (Blocker(sim.World, p, boat) is { } why) { GoalRules.Dissolve(sim, p, why); continue; }
            if (TryBoard(sim, p, boat)) GoalRules.Complete(p);
            else if (boat.Passengers.Count >= boat.PassengerCap)
                GoalRules.Dissolve(sim, p, "the boat is full");
        }
    }

    // Why this passenger can never board this hull, or null. Everything here is
    // impossible-forever for the pairing; a hull that is merely elsewhere, or
    // merely full right now, is a wait rather than a blocker.
    public static string? Blocker(GameWorld world, Unit passenger, Unit boat)
    {
        if (passenger.IsEmbarked) return "already aboard";
        if (passenger.Role == UnitRole.Boat) return "a boat cannot board a boat";
        if (passenger.GroupId is not null) return "in a group";
        if (Sim.Core.Population.Population.GetActiveBreedingFor(world, passenger.Id) is not null)
            return "locked breeding";
        if (boat.PassengerCap <= 0) return "that hull carries nobody";
        return null;
    }

    // Is the hull tied up at the very tile this passenger is standing on? Not
    // merely beside SOME dock — otherwise a crew would board a boat across the
    // map the moment it touched any quay.
    private static bool Alongside(GameWorld world, Unit passenger, Unit boat)
    {
        if (world.Grid.BiomeAt(boat.Position) != Biome.Water) return false;
        return EmbarkIntent.IsOwnOrAllied(world, DockOwnerAt(world, passenger.Position), passenger.OwnerId)
            && EmbarkIntent.Is4Adjacent(passenger.Position, boat.Position);
    }

    private static int DockOwnerAt(GameWorld world, TileCoord tile) =>
        world.Structures.TryGetValue(tile, out var s) && s is Dock d ? d.OwnerId : int.MinValue;

    // Board if the hull is alongside THIS dock with a berth free.
    private static bool TryBoard(Simulation sim, Unit passenger, Unit boat)
    {
        var world = sim.World;
        if (boat.Passengers.Count >= boat.PassengerCap) return false;
        if (!Alongside(world, passenger, boat)) return false;

        passenger.PathRemaining = null;
        passenger.PathFinalDest = null;
        passenger.NextArrivalTick = null;
        passenger.NextArrivalSeq = null;
        passenger.HaulPlan = null;
        passenger.TrySetActivity(Activity.Idle);
        boat.Passengers.Add(passenger.Id);
        passenger.EmbarkedOn = boat.Id;
        return true;
    }
}
