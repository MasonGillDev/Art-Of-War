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
//   * Unit exists, owned, not grouped, not embarked, not mid-breed.
//   * A BUSY unit is RETASKED, exactly as a march would (Sim.Core.Intents.
//     Retask): its old post, build, haul or goal is released first. Until
//     2026-10-01 non-Idle units were skipped and did not move at all.
//   * Assigning would not exceed extractor.Spec.WorkerCap — counting units
//     already walking here, so a cap-1 extractor doesn't attract five
//     hopefuls who all dissolve on arrival. Advisory only; ground truth is
//     re-checked on arrival. The OVERFLOW still goes: anyone past the cap is
//     marched to the tile as a plain move, because the player's gesture was
//     "send these people there" and a body that stays put reads as a refusal.
// Role is not validated — any role can work an extractor; PreferredRole
// only affects rate, not eligibility.
//
// Per-id failures are skipped; valid ids still apply. The intent rejects
// only when nothing changes: missing/wrong-type structure, OR zero
// assignments AND nothing dispatched or marched AND no arming triggered.
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
        var marched = 0;
        // In-flight walkers count against the cap (see the header note).
        var pending = GoalRules.PendingCountFor(world, StructureTile, GoalKind.AssignWorker);
        foreach (var id in WorkerIds)
        {
            if (!world.Units.TryGetValue(id, out var unit)) continue;
            if (unit.OwnerId != PlayerId) continue;  // skip non-owned silently per per-id pattern
            if (Retask.Refusal(sim, unit) is not null) continue;  // grouped, embarked, breeding
            if (extractor.Workers.Contains(unit.Id)) continue;    // already on the payroll here
            if (unit.Goal is { Kind: GoalKind.AssignWorker } g && g.TargetTile == StructureTile)
                continue;                              // already on this errand: don't restart the walk

            var slotFree = extractor.Workers.Count + pending < extractor.Spec.WorkerCap;
            if (!slotFree)
            {
                // Past the cap: still go there, as a plain march.
                if (unit.Position == StructureTile) continue;
                Retask.Release(sim, unit);
                MoveIntent.BeginMove(sim, unit, StructureTile);
                if (unit.IsWalking) marched++;
                continue;
            }

            Retask.Release(sim, unit);
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

        // M46 — the player's choice wins over a held slot (docs/m46-groups-spec.md, rule 8):
        // when this order fills the building past its cap counting the slots held for
        // workers away at a muster, the newest holds give way, and those workers' saved
        // tasks are cancelled with a notice.
        if (assigned + dispatched > 0) GiveAwayHeldSlots(sim, extractor);

        var armed = false;
        if (!extractor.TickArmed && extractor.Workers.Count > 0 && !extractor.BufferFull())
        {
            extractor.ArmIfDormant(sim);
            armed = extractor.TickArmed;
        }

        if (assigned == 0 && dispatched == 0 && marched == 0 && !armed)
            return IntentOutcome.Reject("no eligible workers and no production armed");

        return IntentOutcome.Applied;
    }

    private static void GiveAwayHeldSlots(Simulation sim, Extractor extractor)
    {
        var world = sim.World;
        var taken = extractor.Workers.Count + GoalRules.PendingCountFor(world, extractor.At, GoalKind.AssignWorker);
        while (extractor.HeldBy.Count > 0 && taken + extractor.HeldBy.Count > extractor.Spec.WorkerCap)
        {
            var holder = extractor.HeldBy.Max;
            extractor.HeldBy.Remove(holder);
            if (!world.Units.TryGetValue(holder, out var away)) continue;
            if (away.SavedTask is { Kind: GoalKind.AssignWorker } task && task.TargetTile == extractor.At)
            {
                away.SavedTask = null;
                sim.Schedule(sim.Now, new GoalDissolvedEvent(holder, GoalKind.AssignWorker, extractor.At, "its place was given to another"));
            }
        }
    }

    public override string Describe() =>
        $"AssignWorkers(@ {StructureTile.X},{StructureTile.Y}, ids=[{string.Join(",", WorkerIds)}])";
}
