namespace Sim.Core.Logistics;

// Assigns one or more units as workers at an Extractor. If the assignment
// makes the extractor newly-runnable (workers > 0, buffer not full, not
// already armed), arms the first ProductionTickEvent — production picks up
// after one full ProductionPeriodTicks.
//
// Per-id validation (per docs/intent-validation.md):
//   * Unit exists.
//   * Unit on the extractor's tile.
//   * Unit.Activity == Idle.
//   * Assigning would not exceed extractor.Spec.WorkerCap.
// Role is not validated — any role can work an extractor; PreferredRole
// only affects rate, not eligibility.
//
// Per-id failures are skipped; valid ids still apply. The intent rejects
// only when nothing changes: missing/wrong-type structure, OR zero
// assignments AND no arming triggered.
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
        foreach (var id in WorkerIds)
        {
            if (extractor.Workers.Count >= extractor.Spec.WorkerCap) break; // cap reached
            if (!world.Units.TryGetValue(id, out var unit)) continue;
            if (unit.OwnerId != PlayerId) continue;  // skip non-owned silently per per-id pattern
            if (unit.GroupId is not null) continue;  // grouped units can't be assigned solo
            if (unit.IsEmbarked) continue;            // embarked units are off-tile
            if (unit.Position != StructureTile) continue;
            if (unit.Activity != Activity.Idle) continue;
            // M8: training-age gate — extractor workers are role-tied
            // assignments (the role bonus affects rate). Children can't
            // be worker-assigned; they can still haul to the camp.
            if (!Sim.Core.Population.Population.CanTrain(unit, sim.Now, world.PopulationConfig)) continue;
            if (!unit.TrySetActivity(Activity.Working, StructureTile)) continue;
            extractor.Workers.Add(id);
            assigned++;
            // M19 — auto-assignment trigger 2 (home follows work): the
            // worker re-homes to the nearest house with a free bed near
            // the workplace; none in radius → home stays. Their CURRENT
            // home qualifies even when full (they hold one of its beds).
            if (Sim.Core.Population.Population.NearestHouseWithBed(world, PlayerId, StructureTile,
                    Sim.Core.Food.FoodConsumptionConstants.HomeAssignRadius, unit.Home)
                is { } bed)
                Sim.Core.Population.Population.SetHome(sim, unit, bed.At);
        }

        var armed = false;
        if (!extractor.TickArmed && extractor.Workers.Count > 0 && !extractor.BufferFull())
        {
            extractor.ArmIfDormant(sim);
            armed = extractor.TickArmed;
        }

        if (assigned == 0 && !armed)
            return IntentOutcome.Reject("no eligible workers and no production armed");

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"AssignWorkers(@ {StructureTile.X},{StructureTile.Y}, ids=[{string.Join(",", WorkerIds)}])";
}
