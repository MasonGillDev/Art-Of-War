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
//   5. Refiners (Spec.IsRefiner — docs/refining-structures.md): the same
//      shape with one more dormancy guard (no whole batch of inputs) and
//      one more mutation (the batch is paid from Extractor.Inputs before
//      the output lands). Re-arm on the deposit that restocks an input.
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

        // Refining (docs/refining-structures.md): a refiner with workers and
        // room but no whole batch of inputs goes dormant like an empty-
        // handed extractor. Re-arm comes from the haul deposit that brings
        // the missing input (CargoTransfer.DepositInto → ArmIfDormant).
        // M51 — the recipe this tick makes: the best one it can afford.
        var recipe = extractor.IsRefiner ? extractor.NextRecipe() : null;
        if (extractor.IsRefiner && recipe is null)
        {
            extractor.TickArmed = false;
            extractor.NextProductionTickSeq = null;
            Outcome = IntentOutcome.Reject("inputs exhausted");
            return;
        }

        var spec = extractor.Spec;
        // The formula lives in ProductionRate so views read the same number
        // the tick spends. `At` is this event's tick, i.e. sim.Now.
        var rate = ProductionRate.PerPeriod(world, extractor, At);

        var extract = (int)Math.Min(rate, extractor.FreeBuffer());
        // Refining: every unit of output is one BATCH of the tick's recipe,
        // paid in full from the input store before the output lands. Integer,
        // all-or-nothing per batch; the guard above promised ≥ 1. One recipe
        // a tick (M51).
        var made = spec.OutputResource;
        if (recipe is not null)
        {
            extract = Math.Min(extract, extractor.AffordableBatches(recipe));
            extractor.ConsumeBatches(recipe, extract);
            made = recipe.Output;
        }
        extractor.AddOutput(made, extract);
        extractor.LastProductionTick = sim.Now;
        // M37 — what a refiner makes counts toward the owner's progress.
        if (recipe is not null)
            Sim.Core.Progression.Progression.Bump(sim, extractor.OwnerId,
                Sim.Core.Progression.ProgressKey.Refined(made), extract);

        if (extractor.CanProduce())
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
