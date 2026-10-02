using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Boats;

// M12 Phase D — put passengers aboard a boat.
//
// GOAL-SHAPED. A boarding has two moving parts, and this intent used to demand
// both be already still: every passenger standing on the dock AND the hull tied
// up beside it. Getting a crew to sea therefore meant marching each body over,
// sailing the boat across, and only then — once everything happened to be
// motionless in the right places — issuing this. Three appointments for one
// decision ("these people, that boat").
//
// Now: anyone not on the dock WALKS there and boards when they and the hull are
// both present, whichever arrives last (see EmbarkGoal). Anyone already on the
// dock boards immediately, exactly as before.
//
// THE ALL-OR-NOTHING RULE STILL HOLDS FOR THE IMMEDIATE PATH and deliberately
// does NOT for the walked one. A player looking at a crew standing on a quay is
// entitled to "all of them, or tell me why" — so if every named passenger is
// present, this validates the whole list before mutating anything, as it always
// did. An errand spread over game-days cannot honour that: insisting everyone
// arrive before ANY may board lets one straggler hold the crew on the quayside,
// and a straggler who dies holds them there forever. So walked passengers board
// one at a time, and whoever finds the hull full dissolves and says so.
//
// Rejections (impossible-forever only):
//   * Boat missing, not a boat, not yours, not on water.
//   * No dock of yours or an ally's 4-adjacent to it.
//   * Empty passenger list.
//   * A passenger missing, not yours, already aboard, itself a boat, in a
//     group, or locked breeding.
//   * Cap exceeded by the passengers who are ALREADY on the dock.
// Notably NOT a rejection: a passenger standing somewhere else.
public sealed class EmbarkIntent : Intent
{
    public int BoatId { get; }
    public IReadOnlyList<int> UnitIds { get; }

    // Which quay to gather at. Null = "wherever the hull is tied up now", the
    // pre-errand contract — which only works while she IS tied up.
    //
    // Naming it is what makes the boat-arrives-last case expressible at all: a
    // hull out at sea is beside no dock, so a crew could not be told to meet
    // her anywhere. It also settles the ambiguity the other way round, since a
    // boat can sit between two quays and a player is entitled to say which.
    public TileCoord? DockTile { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public EmbarkIntent(int boatId, IReadOnlyList<int> unitIds, TileCoord? dockTile = null)
    {
        BoatId = boatId;
        UnitIds = unitIds;
        DockTile = dockTile;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(BoatId, out var boat))
            return IntentOutcome.Reject($"boat {BoatId} does not exist");
        if (boat.Role != UnitRole.Boat)
            return IntentOutcome.Reject($"unit {BoatId} is not a boat");
        if (boat.OwnerId != PlayerId)
            return IntentOutcome.Reject($"boat {BoatId} not owned by player {PlayerId}");
        if (world.Grid.BiomeAt(boat.Position) != Biome.Water)
            return IntentOutcome.Reject($"boat {BoatId} not on a water tile");

        // Where the crew gathers: the named quay, or the one the hull happens
        // to be tied up at.
        TileCoord? dockTile;
        if (DockTile is { } named)
        {
            if (!world.Structures.TryGetValue(named, out var ds) || ds is not Dock dock)
                return IntentOutcome.Reject($"no dock at {named.X},{named.Y}");
            if (!IsOwnOrAllied(world, dock.OwnerId, PlayerId))
                return IntentOutcome.Reject(
                    $"dock at {named.X},{named.Y} is not yours or an ally's");
            dockTile = named;
        }
        else
        {
            dockTile = FindEmbarkDockTile(world, boat.Position, PlayerId);
            if (dockTile is null)
                return IntentOutcome.Reject(
                    $"boat {BoatId} not adjacent to a dock owned by player {PlayerId}");
        }

        if (UnitIds.Count == 0)
            return IntentOutcome.Reject("EmbarkIntent passenger list is empty");

        // Validate every named passenger before any mutation, whether they are
        // on the quay or halfway across the map. Everything here is
        // impossible-forever; being elsewhere is not on the list.
        var here = new List<int>();
        var away = new List<int>();
        foreach (var pid in UnitIds)
        {
            if (!world.Units.TryGetValue(pid, out var p))
                return IntentOutcome.Reject($"passenger {pid} does not exist");
            if (p.OwnerId != PlayerId)
                return IntentOutcome.Reject($"passenger {pid} not owned by player {PlayerId}");
            if (EmbarkGoal.Blocker(world, p, boat) is { } why)
                return IntentOutcome.Reject($"passenger {pid} {why}");
            if (p.Activity != Activity.Idle)
                return IntentOutcome.Reject(
                    $"passenger {pid} is not Idle (current: {p.Activity})");

            if (p.Position == dockTile.Value) here.Add(pid);
            else away.Add(pid);
        }

        // The cap is checked against the people who can board RIGHT NOW. The
        // walkers are not counted: berths taken in the meantime are a fact they
        // will meet on arrival, and pre-reserving space for a crew that may die
        // on the road would strand berths nobody can use.
        if (boat.Passengers.Count + here.Count > boat.PassengerCap)
            return IntentOutcome.Reject(
                $"boat {BoatId} cap {boat.PassengerCap} exceeded " +
                $"(have {boat.Passengers.Count}, adding {here.Count})");

        // All validated — apply. The quayside group is atomic, as it always was.
        foreach (var pid in here)
        {
            var p = world.Units[pid];
            // Drop any in-flight obligations cleanly. Idle → bump epoch.
            Sim.Core.Movement.Walk.Stop(p);
            p.HaulPlan = null;
            p.TrySetActivity(Activity.Idle);
            boat.Passengers.Add(pid);
            p.EmbarkedOn = BoatId;
            p.Subtile = null;   // M42 — a passenger stands on no subtile
            Sim.Core.Healing.Rest.Interrupt(p);   // no healing aboard (docs/unit-healing.md)
        }

        // Everyone else walks to the quay and boards when they and the hull are
        // both there. The goal names the HULL, not the berth: several boats can
        // share a dock, and this one may sail before they arrive.
        var dispatched = 0;
        foreach (var pid in away)
            if (GoalRules.Begin(sim, world.Units[pid],
                    new GoalPlan(GoalKind.Embark, dockTile.Value, partnerUnitId: BoatId)))
                dispatched++;

        if (here.Count == 0 && dispatched == 0)
            return IntentOutcome.Reject("no passenger could board or be sent to the dock");

        return IntentOutcome.Applied;
    }

    // Returns the (own or allied) dock tile 4-adjacent to `boatTile` if
    // any; otherwise null. Iterates structures in canonical (y, x) order
    // for determinism in the rare multi-dock case.
    internal static TileCoord? FindEmbarkDockTile(GameWorld world, TileCoord boatTile, int playerId)
    {
        Dock? best = null;
        TileCoord bestAt = default;
        foreach (var s in world.Structures.Values)
        {
            if (s is not Dock d) continue;
            if (!Is4Adjacent(d.At, boatTile)) continue;
            if (!IsOwnOrAllied(world, d.OwnerId, playerId)) continue;
            if (best is null || LessYThenX(d.At, bestAt))
            {
                best = d;
                bestAt = d.At;
            }
        }
        return best?.At;
    }

    internal static bool Is4Adjacent(TileCoord a, TileCoord b)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        return (dx == 1 && dy == 0) || (dx == 0 && dy == 1);
    }

    internal static bool IsOwnOrAllied(GameWorld world, int ownerA, int ownerB)
    {
        if (ownerA == ownerB) return true;
        return world.Diplomacy.RelationshipBetween(ownerA, ownerB)
            == Sim.Core.Diplomacy.RelationshipState.Ally;
    }

    private static bool LessYThenX(TileCoord a, TileCoord b) =>
        a.Y < b.Y || (a.Y == b.Y && a.X < b.X);

    public override string Describe() =>
        $"Embark(boat={BoatId}, units=[{string.Join(",", UnitIds)}])";
}
