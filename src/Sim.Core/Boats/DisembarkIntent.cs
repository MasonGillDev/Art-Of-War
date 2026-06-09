using Sim.Core.World;

namespace Sim.Core.Boats;

// M12 Phase D — instant intent: drop all of a boat's passengers onto
// an adjacent dock tile (the boat's own dock or an ally's).
//
// Preconditions (re-checked at resolution time):
//   * Boat exists; owned by PlayerId; on a water tile.
//   * Boat is 4-adjacent to a Dock owned by PlayerId or an ally.
//   * Boat.Passengers is non-empty (can't disembark from an empty hull).
//
// Effects (atomic):
//   * Each passenger's EmbarkedOn = null.
//   * Each passenger's Position = dock tile.
//   * Boat.Passengers cleared.
//   * AssignmentEpoch bumped on each — any in-flight per-unit event
//     for an unembarked passenger fences cleanly. (Embarked passengers
//     have no live events; embark cleared them and reject solo intents
//     while embarked.)
public sealed class DisembarkIntent : Intent
{
    public int BoatId { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public DisembarkIntent(int boatId) { BoatId = boatId; }

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
        if (boat.Passengers.Count == 0)
            return IntentOutcome.Reject($"boat {BoatId} has no passengers");

        var dockTile = EmbarkIntent.FindEmbarkDockTile(world, boat.Position, PlayerId);
        if (dockTile is null)
            return IntentOutcome.Reject(
                $"boat {BoatId} not adjacent to a dock owned by player {PlayerId}");

        var landingTile = dockTile.Value;
        // Copy passengers off so we can mutate the SortedSet.
        var passengers = new List<int>(boat.Passengers);
        foreach (var pid in passengers)
        {
            if (!world.Units.TryGetValue(pid, out var p))
            {
                // Defensive: a passenger went missing. Drop from the
                // boat's list and continue with the rest.
                continue;
            }
            p.Position = landingTile;
            p.EmbarkedOn = null;
            p.PathRemaining = null;
            p.PathFinalDest = null;
            p.NextArrivalTick = null;
            p.NextArrivalSeq = null;
            // Bump epoch so any latent event (shouldn't exist, but
            // defensive) fences. Idle stays Idle.
            p.BumpEpoch();
            // Reveal vision for the now-on-tile unit.
            Sight.Reveal(world, p.OwnerId, landingTile, Sight.RadiusFor(p.Role), sim.Now);
        }
        boat.Passengers.Clear();

        return IntentOutcome.Applied;
    }

    public override string Describe() => $"Disembark(boat={BoatId})";
}
