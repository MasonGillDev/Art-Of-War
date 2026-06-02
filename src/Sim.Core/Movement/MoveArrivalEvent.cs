namespace Sim.Core.Movement;

public sealed class MoveArrivalEvent : ScheduledEvent
{
    public int UnitId { get; }
    public TileCoord To { get; }
    public TileCoord FinalDestination { get; }

    public MoveArrivalEvent(int unitId, TileCoord to, TileCoord finalDest)
    {
        UnitId = unitId;
        To = to;
        FinalDestination = finalDest;
    }

    public override void Apply(Simulation sim)
    {
        if (!sim.World.Units.TryGetValue(UnitId, out var unit)) return;
        unit.Position = To;
        if (To == FinalDestination) return;
        MoveIntent.ScheduleNextStep(sim, unit, FinalDestination);
    }

    public override string Describe() =>
        $"Arrive(unit={UnitId} @ {To.X},{To.Y}, dest={FinalDestination.X},{FinalDestination.Y})";
}
