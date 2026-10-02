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
        if (Sim.Core.Intents.Retask.Refusal(sim, unit) is { } refusal)
            return IntentOutcome.Reject(refusal);
        if (!sim.World.Grid.InBounds(Destination))
            return IntentOutcome.Reject($"destination {Destination.X},{Destination.Y} out of bounds");

        // A march is authoritative: the player has retasked this unit. Shared
        // with the assign intents (Sim.Core.Intents.Retask) so a march and a
        // "go work there" pull a busy body off its old job identically.
        Sim.Core.Intents.Retask.Release(sim, unit);

        BeginMove(sim, unit, Destination);
        return IntentOutcome.Applied;
    }

    // Start a new walk to the tile `finalDest` (M43: Walk.Begin). Called by:
    //   * MoveIntent.Resolve (player-issued move)
    //   * HaulIntent.Resolve (move to source)
    //   * HaulPickupEvent.Apply (after pickup, move to dest)
    //   * the drivers, goals, pursuit, scouts: every march
    // The path is STORED on the unit (Unit.SubtileRoute) rather than recomputed per step
    // or per restore: a committed path was chosen against the world at command time.
    internal static void BeginMove(Simulation sim, Unit unit, TileCoord finalDest) =>
        Walk.Begin(sim, unit, finalDest);

    public override string Describe() =>
        $"MoveIntent(unit={UnitId} -> {Destination.X},{Destination.Y})";
}
