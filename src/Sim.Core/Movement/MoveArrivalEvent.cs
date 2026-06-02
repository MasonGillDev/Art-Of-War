namespace Sim.Core.Movement;

// Fires when a unit reaches the next tile in its committed path. M4 refactor:
//   * Unit owns the path (Unit.PathRemaining); event no longer carries a
//     follow-up reference.
//   * Final-arrival dispatch derives from Unit.HaulPlan, not from an
//     OnFinalArrival event field. That's what lets the snapshot capture
//     "what happens next" as pure state — RegenerateQueue can rebuild the
//     queue from the unit's anchors alone.
public sealed class MoveArrivalEvent : ScheduledEvent
{
    public int UnitId { get; }
    public TileCoord To { get; }
    public TileCoord FinalDestination { get; }

    // Fencing: captured at schedule time. If the unit's AssignmentEpoch
    // changed between scheduling and firing (e.g. a MoveIntent retasked them,
    // or a Hauling task started/ended), this event is from a stale chain
    // and must no-op without mutating position or scheduling continuations.
    public byte ExpectedEpoch { get; }

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
        // M2 Phase C: every real arrival credits the tile entered. THE one
        // mutation point for road condition. See Roads/Roads.cs.
        Road.CreditTraffic(sim.World, To, sim.Now);
        // M3 Phase B: the arrival also reveals the unit's vision radius for
        // its owner. See Vision/Sight.cs.
        Sight.Reveal(sim.World, unit.OwnerId, To, Sight.RadiusFor(unit.Role));

        // Pop the tile we just entered from the committed path.
        // Defensive: PathRemaining should never be null/empty here on a fresh
        // event, but guard so stale paths don't crash on weird sequencing.
        if (unit.PathRemaining is not null && unit.PathRemaining.Count > 0)
            unit.PathRemaining.RemoveAt(0);

        var atFinalDest = unit.PathRemaining is null || unit.PathRemaining.Count == 0;
        if (atFinalDest)
        {
            // Move chain complete. Clear movement anchors so RegenerateQueue
            // won't try to recreate a finished move on restore.
            unit.PathRemaining = null;
            unit.PathFinalDest = null;
            unit.NextArrivalTick = null;
            unit.NextArrivalSeq  = null;

            // Dispatch any waiting follow-up that lives on Unit state.
            DispatchOnFinalArrival(sim, unit);
            return;
        }

        // Per-hop continuation: schedule the next arrival.
        MoveIntent.ScheduleNextHop(sim, unit);
    }

    // M4 Phase A: derives the "what happens at final arrival" follow-up from
    // unit state (HaulPlan, GroupId) instead of an event field. RegenerateQueue
    // can therefore reconstruct the queue from state alone — no OnFinalArrival
    // event payload to serialize.
    //
    // Dispatch order: HaulPlan first (mutually exclusive with GroupId in
    // practice — solo intents reject grouped units, so a Hauling unit can't
    // be in a group). Group-rendezvous second.
    private static void DispatchOnFinalArrival(Simulation sim, Unit unit)
    {
        if (unit.HaulPlan is { } plan)
        {
            switch (plan.Phase)
            {
                case HaulPhase.ToSource when unit.Position == plan.SourceTile:
                    sim.Schedule(sim.Now,
                        new HaulPickupEvent(unit.Id, plan.SourceTile, plan.DestTile, plan.Resource, unit.AssignmentEpoch));
                    break;
                case HaulPhase.ToDest when unit.Position == plan.DestTile:
                    sim.Schedule(sim.Now,
                        new HaulDepositEvent(unit.Id, plan.DestTile, unit.AssignmentEpoch));
                    break;
                // Otherwise: the unit landed somewhere that doesn't match the
                // haul's expectation. Leave HaulPlan in place; pending haul
                // events fence on epoch if any survive.
            }
            return;
        }

        // M5 Phase B: a member of a Forming group reaching the rendezvous
        // tile decrements the pending count. When zero, the group transitions
        // to Idle. The walk to rendezvous is just MoveArrivalEvents on the
        // individual member; the group is the integrity owner.
        if (unit.GroupId is { } gid
            && sim.World.Groups.TryGetValue(gid, out var group)
            && group.State == GroupState.Forming
            && unit.Position == group.RendezvousTile)
        {
            group.PendingArrivals--;
            if (group.PendingArrivals <= 0)
            {
                group.State = GroupState.Idle;
                group.RendezvousTile = null;
                group.PendingArrivals = 0;
            }
        }
    }

    public override string Describe() =>
        $"Arrive(unit={UnitId} @ {To.X},{To.Y}, dest={FinalDestination.X},{FinalDestination.Y})";
}
