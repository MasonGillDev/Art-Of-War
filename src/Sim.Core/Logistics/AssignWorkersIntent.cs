namespace Sim.Core.Logistics;

// GOAL-SHAPED (M30): "that person works that farm". A named unit already
// standing on the tile takes the post now; one standing anywhere else WALKS
// THERE AND TAKES IT ON ARRIVAL. Travel is a mechanical step between the
// player and their stated goal, so it is the sim's job, not an appointment
// the player has to keep (docs/goal-shaped-intents.md).
//
// If the assignment makes the extractor newly-runnable (workers > 0, buffer
// not full, not already armed), arms the first ProductionTickEvent —
// production picks up after one full ProductionPeriodTicks.
//
// Per-id validation (per docs/intent-validation.md):
//   * Unit exists, owned, not grouped, not embarked, of training age.
//   * Unit.Activity == Idle (a Waiting or Working body belongs to the intent
//     chain that owns it).
//   * Assigning would not exceed extractor.Spec.WorkerCap — counting units
//     already walking here, so a cap-1 extractor doesn't attract five
//     hopefuls who all dissolve on arrival. Advisory only; ground truth is
//     re-checked on arrival.
// Role is not validated — any role can work an extractor; PreferredRole
// only affects rate, not eligibility.
//
// Per-id failures are skipped; valid ids still apply. The intent rejects
// only when nothing changes: missing/wrong-type structure, OR zero
// assignments AND nothing dispatched AND no arming triggered.
public sealed class AssignWorkersIntent : Intent
{
    public TileCoord StructureTile { get; }
    public IReadOnlyList<int> WorkerIds { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public AssignWorkersIntent(TileCoord structureTile, IReadOnlyList<int> workerIds)
    {
        StructureTile = structureTile;
        WorkerIds = workerIds;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Structures.TryGetValue(StructureTile, out var s) || s is not Extractor extractor)
            return IntentOutcome.Reject($"no extractor at {StructureTile.X},{StructureTile.Y}");
        if (extractor.OwnerId != PlayerId)
            return IntentOutcome.Reject(
                $"extractor at {StructureTile.X},{StructureTile.Y} not owned by player {PlayerId}");

        var assigned = 0;
        var dispatched = 0;
        // In-flight walkers count against the cap (see the header note).
        var pending = GoalRules.PendingCountFor(world, StructureTile, GoalKind.AssignWorker);
        foreach (var id in WorkerIds)
        {
            if (extractor.Workers.Count + pending >= extractor.Spec.WorkerCap) break; // cap reached
            if (!world.Units.TryGetValue(id, out var unit)) continue;
            if (unit.OwnerId != PlayerId) continue;  // skip non-owned silently per per-id pattern
            if (unit.GroupId is not null) continue;  // grouped units can't be assigned solo
            if (unit.IsEmbarked) continue;            // embarked units are off-tile
            if (unit.Activity != Activity.Idle) continue;

            if (unit.Position == StructureTile)
            {
                if (WorkAssignment.TryAssignWorker(sim, extractor, unit)) assigned++;
            }
            else if (GoalRules.Begin(sim, unit, new GoalPlan(GoalKind.AssignWorker, StructureTile)))
            {
                dispatched++;
                pending++;
            }
        }

        var armed = false;
        if (!extractor.TickArmed && extractor.Workers.Count > 0 && !extractor.BufferFull())
        {
            extractor.ArmIfDormant(sim);
            armed = extractor.TickArmed;
        }

        if (assigned == 0 && dispatched == 0 && !armed)
            return IntentOutcome.Reject("no eligible workers and no production armed");

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"AssignWorkers(@ {StructureTile.X},{StructureTile.Y}, ids=[{string.Join(",", WorkerIds)}])";
}
