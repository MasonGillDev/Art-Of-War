namespace Sim.Core.Movement;

public sealed class MoveArrivalEvent : ScheduledEvent
{
    public int UnitId { get; }
    public TileCoord To { get; }
    public TileCoord FinalDestination { get; }

    // Optional follow-up event scheduled at sim.Now when the unit reaches
    // FinalDestination. Lets composite intents (haul, future "walk-to-then-X"
    // intents) reuse movement without duplicating pathfinding/scheduling.
    //
    // Carried forward by ScheduleNextStep through every intermediate
    // MoveArrivalEvent in the chain — so any one of them firing knows what
    // happens at the end.
    //
    // Not snapshotted: this and every other queued event is part of the
    // "in-flight correctness gap" — snapshot+restore alone preserves only
    // frozen worlds. Intent-tail replay (persistence milestone) is what
    // makes restore correct for moving worlds. See
    // docs/persistence-model.md, section "The in-flight correctness gap."
    public ScheduledEvent? OnFinalArrival { get; init; }

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
        if (To == FinalDestination)
        {
            if (OnFinalArrival is { } hook) sim.Schedule(sim.Now, hook);
            return;
        }
        MoveIntent.ScheduleNextStep(sim, unit, FinalDestination, OnFinalArrival);
    }

    public override string Describe() =>
        $"Arrive(unit={UnitId} @ {To.X},{To.Y}, dest={FinalDestination.X},{FinalDestination.Y})";
}
