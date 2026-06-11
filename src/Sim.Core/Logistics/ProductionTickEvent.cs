namespace Sim.Core.Logistics;

// Self-rescheduling production event for an Extractor.
//
// Fires every spec.ProductionPeriodTicks. On fire:
//   1. Validate the structure still exists and is the right type (fencing).
//   2. M9 biome-mismatch guard: if the tile's DERIVED biome (BiomeAt) no
//      longer matches the spec's RequiredBiome (i.e. the LumberCamp's Forest
//      tile has degraded to Grassland), go dormant. This is the keystone
//      "extract-forever" fix — see docs/biome-degradation.md §D.
//   3. If workers == 0 or buffer is full, clear TickArmed and reject cleanly.
//      Re-arm comes from AssignWorkersIntent (when workers come back) or
//      Extractor.ArmIfDormant called from a future haul-pickup (Phase E).
//   4. Otherwise compute the discrete extract amount for this period, bump
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

        // Dormancy guards — the closure of "extract forever."
        //
        // M15, claiming kinds (LumberCamp / Farm): the camp works its
        // CLAIMED tiles; it goes dormant when none of them remains in the
        // required biome band ("the cut is exhausted"). The building's own
        // tile is irrelevant — it never degrades. The in-band count is
        // reused below as the production-taper numerator.
        var inBandClaims = extractor.Spec.ClaimCount > 0
            ? Claims.InBandClaimCount(world, extractor, sim.Now)
            : -1;
        if (extractor.Spec.ClaimCount > 0)
        {
            if (inBandClaims == 0)
            {
                // Pre-stop catch-up (TickArmed still true → includes us).
                Sim.Core.Biomes.BiomeDegradation.OnProductionTransition(
                    world, extractor, sim.Now, world.BiomeDegradationConfig);
                extractor.TickArmed = false;
                extractor.NextProductionTickSeq = null;
                Outcome = IntentOutcome.Reject(
                    $"claim exhausted — no claimed tile remains {extractor.Spec.RequiredBiome}");
                return;
            }
        }
        // M9, non-claiming kinds: legacy own-tile biome check. Quarry/Mine
        // are off-ladder so the derived biome always matches — harmless.
        else if (extractor.Spec.RequiredBiome != Biome.None)
        {
            var currentBiome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(
                world, extractor.At, sim.Now, world.BiomeDegradationConfig);
            if (currentBiome != extractor.Spec.RequiredBiome)
            {
                // Pre-stop catch-up (TickArmed still true → includes us).
                Sim.Core.Biomes.BiomeDegradation.OnProductionTransition(
                    world, extractor, sim.Now, world.BiomeDegradationConfig);
                extractor.TickArmed = false;
                extractor.NextProductionTickSeq = null;
                Outcome = IntentOutcome.Reject(
                    $"tile biome is {currentBiome}, requires {extractor.Spec.RequiredBiome}");
                return;
            }
        }

        if (extractor.Workers.Count == 0)
        {
            // M9: catch up tiles in radius using the PRE-STOP rate (this
            // extractor still counts because TickArmed is true here).
            Sim.Core.Biomes.BiomeDegradation.OnProductionTransition(
                world, extractor, sim.Now, world.BiomeDegradationConfig);
            extractor.TickArmed = false;
            extractor.NextProductionTickSeq = null;
            Outcome = IntentOutcome.Reject("no workers");
            return;
        }

        if (extractor.BufferFull())
        {
            Sim.Core.Biomes.BiomeDegradation.OnProductionTransition(
                world, extractor, sim.Now, world.BiomeDegradationConfig);
            extractor.TickArmed = false;
            extractor.NextProductionTickSeq = null;
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

        // M15 production taper: output scales with the live claim —
        // ceil(rate × inBand / ClaimCount). CEIL on purpose: floor could
        // stall at 0 with land still alive and self-reschedule forever
        // (the zero-power combat loop shape); with ceil, output ≥ 1 while
        // any claimed tile lives, and the dormancy guard above is the only
        // stop. inBand is clamped to ClaimCount so a serialized claim list
        // larger than a later-retuned ClaimCount can't amplify production.
        // docs/extraction-claims.md.
        if (spec.ClaimCount > 0 && rate > 0)
        {
            var inBand = Math.Min(inBandClaims, spec.ClaimCount);
            rate = (rate * inBand + spec.ClaimCount - 1) / spec.ClaimCount;
        }

        var extract = (int)Math.Min(rate, extractor.FreeBuffer());
        extractor.Buffer += extract;
        extractor.LastProductionTick = sim.Now;

        if (extractor.Workers.Count > 0 && !extractor.BufferFull())
        {
            extractor.NextProductionTickSeq = sim.Schedule(
                sim.Now + spec.ProductionPeriodTicks,
                new ProductionTickEvent(ExtractorTile));
            extractor.TickArmed = true;
        }
        else
        {
            // M9: buffer-just-filled (or workers vanished mid-tick) — going
            // dormant. Catch up tiles in radius using PRE-STOP rate (TickArmed
            // still true here).
            Sim.Core.Biomes.BiomeDegradation.OnProductionTransition(
                world, extractor, sim.Now, world.BiomeDegradationConfig);
            extractor.TickArmed = false;
            extractor.NextProductionTickSeq = null;
        }
    }

    public override string Describe() => $"ProductionTick(@ {ExtractorTile.X},{ExtractorTile.Y})";
}
