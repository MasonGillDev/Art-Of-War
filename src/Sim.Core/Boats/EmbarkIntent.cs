using Sim.Core.World;

namespace Sim.Core.Boats;

// M12 Phase D — instant intent: move passengers from the dock tile
// into a boat's Passengers list.
//
// Preconditions (re-checked at resolution time):
//   * Boat exists; owned by PlayerId; on a water tile.
//   * Boat is 4-adjacent to a Dock owned by PlayerId or an ally.
//   * Every unit id is owned by PlayerId.
//   * Every unit is on that dock tile.
//   * No unit is already embarked.
//   * No unit is in a Group (M5 solo-rejection symmetry).
//   * No unit is breeding (M8 lock).
//   * Passengers.Count + unitIds.Count <= PassengerCap.
//
// Effects (atomic; mutates only on full success):
//   * Each unit added to the boat's Passengers (SortedSet → ascending).
//   * Each unit's EmbarkedOn = boat id.
//   * Each unit becomes Idle and is removed from the per-tile crowd
//     (`Position` is left at the dock tile but `IsEmbarked` excludes it
//     from all iterations that filter on it).
//   * AssignmentEpoch bumped on each — any in-flight per-unit event
//     fences harmlessly.
public sealed class EmbarkIntent : Intent
{
    public int BoatId { get; }
    public IReadOnlyList<int> UnitIds { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public EmbarkIntent(int boatId, IReadOnlyList<int> unitIds)
    {
        BoatId = boatId;
        UnitIds = unitIds;
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

        // Find an own-or-allied dock 4-adjacent to the boat.
        var dockTile = FindEmbarkDockTile(world, boat.Position, PlayerId);
        if (dockTile is null)
            return IntentOutcome.Reject(
                $"boat {BoatId} not adjacent to a dock owned by player {PlayerId}");

        if (UnitIds.Count == 0)
            return IntentOutcome.Reject("EmbarkIntent passenger list is empty");

        // Validate each candidate passenger before any mutation.
        foreach (var pid in UnitIds)
        {
            if (!world.Units.TryGetValue(pid, out var p))
                return IntentOutcome.Reject($"passenger {pid} does not exist");
            if (p.OwnerId != PlayerId)
                return IntentOutcome.Reject($"passenger {pid} not owned by player {PlayerId}");
            if (p.IsEmbarked)
                return IntentOutcome.Reject($"passenger {pid} is already embarked");
            if (p.Role == UnitRole.Boat)
                return IntentOutcome.Reject($"passenger {pid} is itself a boat");
            if (p.GroupId is not null)
                return IntentOutcome.Reject($"passenger {pid} is in a group");
            if (Sim.Core.Population.Population.GetActiveBreedingFor(world, pid) is not null)
                return IntentOutcome.Reject($"passenger {pid} is locked breeding");
            if (p.Position != dockTile.Value)
                return IntentOutcome.Reject(
                    $"passenger {pid} not on the dock tile {dockTile.Value.X},{dockTile.Value.Y}");
        }

        if (boat.Passengers.Count + UnitIds.Count > boat.PassengerCap)
            return IntentOutcome.Reject(
                $"boat {BoatId} cap {boat.PassengerCap} exceeded " +
                $"(have {boat.Passengers.Count}, adding {UnitIds.Count})");

        // All validated — apply.
        foreach (var pid in UnitIds)
        {
            var p = world.Units[pid];
            // Drop any in-flight obligations cleanly. Idle → bump epoch.
            p.PathRemaining = null;
            p.PathFinalDest = null;
            p.NextArrivalTick = null;
            p.NextArrivalSeq = null;
            p.HaulPlan = null;
            p.TrySetActivity(Activity.Idle);
            boat.Passengers.Add(pid);
            p.EmbarkedOn = BoatId;
        }

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
