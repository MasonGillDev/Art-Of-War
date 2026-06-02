namespace Sim.Core.Movement;

public sealed class MoveArrivalEvent : ScheduledEvent
{
    public int UnitId { get; }
    public TileCoord To { get; }
    public TileCoord FinalDestination { get; }

    // Fencing: captured at schedule time. If the unit's AssignmentEpoch
    // changed between scheduling and firing (e.g. a MoveIntent retasked them,
    // or a Hauling task started/ended), this event is from a stale chain
    // and must no-op without mutating position or scheduling continuations.
    // Otherwise old and new move chains run interleaved and the unit ends
    // up in the wrong place.
    public byte ExpectedEpoch { get; }

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

    public MoveArrivalEvent(int unitId, TileCoord to, TileCoord finalDest, byte expectedEpoch)
    {
        UnitId = unitId;
        To = to;
        FinalDestination = finalDest;
        ExpectedEpoch = expectedEpoch;
    }

    public override void Apply(Simulation sim)
    {
        if (!sim.World.Units.TryGetValue(UnitId, out var unit)) return;
        if (unit.AssignmentEpoch != ExpectedEpoch)
        {
            Outcome = IntentOutcome.Reject("stale (epoch mismatch)");
            return;
        }
        unit.Position = To;
        // Phase C: every real arrival credits the tile entered. Roads emerge
        // from sustained traffic, decay when abandoned. THE one mutation
        // point for road condition. See Roads/Roads.cs.
        Road.CreditTraffic(sim.World, To, sim.Now);
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
