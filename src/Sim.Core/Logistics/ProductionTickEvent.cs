namespace Sim.Core.Logistics;

// Self-rescheduling production event for an Extractor.
//
// Fires every spec.ProductionPeriodTicks. On fire:
//   1. Validate the structure still exists and is the right type (fencing).
//   2. If workers == 0 or buffer is full, clear TickArmed and reject cleanly.
//      Re-arm comes from AssignWorkersIntent (when workers come back) or
//      Extractor.ArmIfDormant called from a future haul-pickup (Phase E).
//   3. Otherwise compute the discrete extract amount for this period, bump
//      the buffer, and reschedule the next tick.
//
// Each tick is a discrete event producing a discrete integer amount of work.
// No "integrate rate over the interval since last fire" math — that path
// would couple production timing to observation timing and break determinism.
public sealed class ProductionTickEvent : ScheduledEvent
{
    public TileCoord ExtractorTile { get; }

    public ProductionTickEvent(TileCoord extractorTile) { ExtractorTile = extractorTile; }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Structures.TryGetValue(ExtractorTile, out var s) || s is not Extractor extractor)
        {
            Outcome = IntentOutcome.Reject($"no extractor at {ExtractorTile.X},{ExtractorTile.Y}");
            return;
        }

        if (extractor.Workers.Count == 0)
        {
            extractor.TickArmed = false;
            Outcome = IntentOutcome.Reject("no workers");
            return;
        }

        if (extractor.BufferFull())
        {
            extractor.TickArmed = false;
            Outcome = IntentOutcome.Reject("buffer full");
            return;
        }

        var spec = extractor.Spec;
        long rate = 0;
        foreach (var workerId in extractor.Workers)
        {
            // Worker may have been removed from world via some future intent;
            // skip rather than throw — fail-clean per docs/intent-validation.md.
            if (!world.Units.TryGetValue(workerId, out var worker)) continue;
            rate += worker.Role == spec.PreferredRole
                ? (long)spec.BaseRatePerWorker * spec.RoleBonusNumerator / spec.RoleBonusDenominator
                : spec.BaseRatePerWorker;
        }

        var extract = (int)Math.Min(rate, extractor.FreeBuffer());
        extractor.Buffer += extract;
        extractor.LastProductionTick = sim.Now;

        if (extractor.Workers.Count > 0 && !extractor.BufferFull())
        {
            sim.Schedule(sim.Now + spec.ProductionPeriodTicks, new ProductionTickEvent(ExtractorTile));
            extractor.TickArmed = true;
        }
        else
        {
            extractor.TickArmed = false;
        }
    }

    public override string Describe() => $"ProductionTick(@ {ExtractorTile.X},{ExtractorTile.Y})";
}
