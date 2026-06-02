namespace Sim.Core.Logistics;

// Removes one or more workers from an Extractor and returns them to Idle.
// Does NOT touch TickArmed — the next scheduled ProductionTickEvent will fire,
// see workers=0, and go dormant naturally. Same fencing-style pattern Phase C
// uses for ConstructionSite.Pause.
//
// Per-id validation (per docs/intent-validation.md):
//   * Unit exists.
//   * extractor.Workers contains the id.
//   * Unit.Activity == Working && Unit.Assignment == structureTile.
// Per-id failures skip cleanly.
public sealed class UnassignWorkersIntent : Intent
{
    public TileCoord StructureTile { get; }
    public IReadOnlyList<int> WorkerIds { get; }

    public UnassignWorkersIntent(TileCoord structureTile, IReadOnlyList<int> workerIds)
    {
        StructureTile = structureTile;
        WorkerIds = workerIds;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Structures.TryGetValue(StructureTile, out var s) || s is not Extractor extractor)
            return IntentOutcome.Reject($"no extractor at {StructureTile.X},{StructureTile.Y}");

        var removed = 0;
        foreach (var id in WorkerIds)
        {
            if (!extractor.Workers.Contains(id)) continue;
            if (!world.Units.TryGetValue(id, out var unit)) continue;
            if (unit.Activity != Activity.Working || unit.Assignment != StructureTile) continue;
            if (!unit.TrySetActivity(Activity.Idle)) continue;
            extractor.Workers.Remove(id);
            removed++;
        }

        if (removed == 0)
            return IntentOutcome.Reject("no eligible workers to unassign");

        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"UnassignWorkers(@ {StructureTile.X},{StructureTile.Y}, ids=[{string.Join(",", WorkerIds)}])";
}
