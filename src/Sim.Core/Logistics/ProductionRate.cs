namespace Sim.Core.Logistics;

// The per-period output of an extractor at its current staffing and soil —
// the ONE formula. ProductionTickEvent spends it (then clamps to free buffer
// and, for refiners, affordable batches); views read it to tell the player
// what a building is making right now (docs/structure-rates-on-the-wire.md).
//
// PURE READ: workers, their roles and homes, and the live fertility of the
// claimed tiles. Nothing here writes, so a view may call it every tick.
public static class ProductionRate
{
    public static long PerPeriod(GameWorld world, Extractor extractor, long now)
    {
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
            // Housing (docs/housing-buffs.md): a settled worker — homed at a
            // fed own House — adds a flat bonus per period. Pure read of the
            // worker's home; a castle mouth or a starving household adds 0.
            rate += Sim.Core.Population.Housing.SettledWorkBonus(world, worker, now);
        }

        // M15 production taper, weighted by LIVE FERTILITY since M35
        // (docs/environmental-fertility.md decision 2):
        //   ceil(rate × Σ_{in-band claims} fert_t / (ClaimCount × BandBaseline))
        // At uniform baseline fertility the sum is inBand × BandBaseline and
        // this is exactly the M15 ceil(rate × inBand / ClaimCount); a
        // riverside claim sits above the band baseline and out-produces a
        // dry one, and a tiring field slows before its band flips. CEIL on
        // purpose: floor could stall at 0 with land still alive and self-
        // reschedule forever (the zero-power combat loop shape); with ceil,
        // output ≥ 1 while any claimed tile lives, and the tick's dormancy
        // guard is the only stop. The sum counts at most ClaimCount tiles so
        // a serialized claim list larger than a later-retuned ClaimCount
        // can't amplify production. docs/extraction-claims.md.
        if (spec.ClaimCount > 0 && rate > 0)
        {
            var bandBaseline = Sim.Core.Biomes.BiomeDegradation.BaselineFertility(
                spec.RequiredBiome, world.BiomeDegradationConfig);
            if (bandBaseline <= 0)
                throw new InvalidOperationException(
                    $"{spec.Kind} claims {spec.RequiredBiome} tiles, whose band baseline is {bandBaseline}; the fertility taper needs a positive baseline");
            var fertSum = Claims.InBandClaimFertilitySum(world, extractor, now, spec.ClaimCount);
            var denom = (long)spec.ClaimCount * bandBaseline;
            rate = (rate * fertSum + denom - 1) / denom;
        }
        return rate;
    }

    // The same rate over one game day. Every catalog period divides a day,
    // so this is exact for today's catalog; a future period that does not
    // divide it floors per day rather than inventing a fraction.
    public static long PerDay(GameWorld world, Extractor extractor, long now) =>
        extractor.Spec.ProductionPeriodTicks > 0
            ? PerPeriod(world, extractor, now) * Time.Day / extractor.Spec.ProductionPeriodTicks
            : 0;
}
