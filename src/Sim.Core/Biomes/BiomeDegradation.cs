using Sim.Core.World;

namespace Sim.Core.Biomes;

// M9 — biome degradation as a SPATIAL LAZY FIELD. A tile's fertility derives
// from the sparse set of in-range producing extractors; storage and math are
// the same shape as M2 road decay (integer-exact, observation-independent,
// completed-boundaries + remainder carry), with one difference: the rate
// changes when an extractor in the radius starts or stops producing. That's
// the only event that forces a catch-up.
//
// Three categories of operation:
//
//   PURE READS (called from views, AI, pathfinders, intents, the production
//     tick's biome-mismatch check):
//       FertilityAt, BiomeAt, IsOnLadder, BaselineFertility, Band.
//     NEVER mutate. The 100×-no-mutation contract is what keeps observation
//     timing out of the simulation hash. See test
//     BiomeFertilityCatchUpTests.FertilityAt_IsPureRead_NoMutation.
//
//   MUTATING WRITES (Phase B+):
//       CatchUp (called on extractor production-state transitions —
//       OnProductionStart / OnProductionStop). The ONE mutation point for
//       world.Fertility outside snapshot restore.
//
//   TEST-ONLY (Phase A) MATH PRIMITIVE:
//       CatchUpWithRate — exposes the integer-exact math with an explicit
//       rate so Phase A can drive observation-independence tests without
//       extractors. Phase B+ wires CatchUp to derive its rate from the
//       extractor set and calls into the same primitive.
//
// THE LATCH IS IMPLICIT. There is no stored "desertLatched" bool. A tile is
// latched iff (baseline + Deviation) < DesertThreshold; DeriveRate forces
// recovery rate → 0 whenever the latch holds. Because rate is constant
// between transitions (invariant), the stored fertility at lastUpdateTick is
// the only point where the latch decision is read — and that decision is
// consistent across reads and writes within the segment.
public static class BiomeDegradation
{
    // ---- biome → baseline + band ---------------------------------------

    public static int BaselineFertility(Biome b, BiomeDegradationConfig config) => b switch
    {
        Biome.Forest    => config.ForestBaseline,
        Biome.Grassland => config.GrasslandBaseline,
        Biome.Desert    => config.DesertBaseline,
        Biome.Hills     => config.HillsBaseline,
        Biome.Mountain  => config.MountainBaseline,
        Biome.Water     => config.WaterBaseline,
        Biome.None      => config.GrasslandBaseline,
        _ => throw new ArgumentOutOfRangeException(nameof(b), b, null),
    };

    // F/G/D participate in the fertility ladder; H/M/W don't.
    public static bool IsOnLadder(Biome b) =>
        b == Biome.Forest || b == Biome.Grassland || b == Biome.Desert;

    // Band a fertility integer into F/G/D. Only meaningful when the tile's
    // worldgen biome is on the ladder (the caller is responsible for checking
    // IsOnLadder first; this method's contract is "given a ladder tile's
    // current fertility, which band is it in?").
    public static Biome Band(int fertility, BiomeDegradationConfig config)
    {
        if (fertility >= config.ForestThreshold) return Biome.Forest;
        if (fertility >= config.DesertThreshold) return Biome.Grassland;
        return Biome.Desert;
    }

    // ---- pure reads ------------------------------------------------------

    // Current fertility at this tile AT THE GIVEN TICK. For ladder tiles,
    // applies the currently-derived rate over the elapsed-since-lastUpdate
    // interval. For off-ladder tiles, returns baseline unchanged.
    //
    // PURE READ. No mutation. Safe to call any number of times.
    public static int FertilityAt(GameWorld world, TileCoord tile, long now, BiomeDegradationConfig config)
    {
        var worldgen = world.Grid.BiomeAt(tile);
        var baseline = BaselineFertility(worldgen, config);
        if (!IsOnLadder(worldgen)) return baseline;
        var (storedDev, lastUpdate) = ReadStored(world, tile);
        var (rateAmount, ratePeriod) = DeriveRate(storedDev, baseline, world, tile, now, config);
        var (newDev, _) = ApplyMath(storedDev, lastUpdate, now, rateAmount, ratePeriod, baseline, config);
        return baseline + newDev;
    }

    // Current biome at this tile AT THE GIVEN TICK. Off-ladder tiles return
    // their worldgen biome unchanged. PURE READ.
    public static Biome BiomeAt(GameWorld world, TileCoord tile, long now, BiomeDegradationConfig config)
    {
        var worldgen = world.Grid.BiomeAt(tile);
        if (!IsOnLadder(worldgen)) return worldgen;
        return Band(FertilityAt(world, tile, now, config), config);
    }

    // ---- catch-up (writes) ----------------------------------------------

    // Advance this tile's stored fertility to `now` using the currently-derived
    // rate. The ONE mutation point for world.Fertility outside snapshot restore.
    //
    // ANCHOR DISCIPLINE: catch-up at a TRANSITION (the only caller is
    // OnProductionTransition) drops the carry-remainder and forces
    // LastUpdateTick = now. This guarantees that for any tile in a
    // transitioning extractor's radius, subsequent reads use elapsed =
    // (read_time - now), NOT (read_time - 0). Without the anchor a never-
    // previously-touched tile would inherit lastUpdateTick=0 and the next read
    // would over-apply the post-transition rate over the full simulation
    // history. The entry is written unconditionally — even when deviation
    // didn't change — because the lastUpdateTick anchor IS the load-bearing
    // outcome of the catch-up.
    //
    // (The test-only CatchUpWithRate keeps the sparse "remove on baseline"
    // behaviour. It's not at a transition; it's a math driver.)
    internal static void CatchUp(GameWorld world, TileCoord tile, long now, BiomeDegradationConfig config)
    {
        var worldgen = world.Grid.BiomeAt(tile);
        if (!IsOnLadder(worldgen)) return;
        var baseline = BaselineFertility(worldgen, config);
        var (storedDev, lastUpdate) = ReadStored(world, tile);
        var (rateAmount, ratePeriod) = DeriveRate(storedDev, baseline, world, tile, now, config);
        var (newDev, _) = ApplyMath(storedDev, lastUpdate, now, rateAmount, ratePeriod, baseline, config);
        // Anchor: drop carry, set lastUpdateTick = now. Always write.
        if (world.Fertility.TryGetValue(tile, out var existing))
        {
            existing.Deviation = newDev;
            existing.LastUpdateTick = now;
        }
        else
        {
            world.Fertility[tile] = new Fertility(newDev, now);
        }
    }

    // Test-only / Phase A entry point: advance the tile's stored fertility
    // using an EXPLICIT rate (skipping DeriveRate). Used to drive the
    // observation-independence tests for the integer math without needing the
    // extractor scaffolding in place. Phase B+ uses CatchUp (which derives
    // rate from world state).
    internal static void CatchUpWithRate(GameWorld world, TileCoord tile, long now,
        int ratePerPeriod, long ratePeriod, BiomeDegradationConfig config)
    {
        var worldgen = world.Grid.BiomeAt(tile);
        if (!IsOnLadder(worldgen)) return;
        var baseline = BaselineFertility(worldgen, config);
        var (storedDev, lastUpdate) = ReadStored(world, tile);
        WriteCaughtUp(world, tile, baseline, storedDev, lastUpdate, now, ratePerPeriod, ratePeriod, config);
    }

    // ---- internals -------------------------------------------------------

    private static (int dev, long lastUpdate) ReadStored(GameWorld world, TileCoord tile) =>
        world.Fertility.TryGetValue(tile, out var f) ? (f.Deviation, f.LastUpdateTick) : (0, 0);

    // Derive the (signed) rate that currently applies to this tile, given its
    // STORED fertility (the value at lastUpdateTick, which is the post-last-
    // transition baseline). PURE — reads world.Fertility / world.Structures /
    // world.Grid; writes nothing.
    //
    // The rate has constant value between extractor production-state
    // transitions (the M9 invariant). That's what makes lazy catch-up exact:
    // a tile's stored deviation+lastUpdateTick is set at the most recent
    // transition, and DeriveRate returns the rate that applies from that
    // moment onward until the next transition forces another catch-up.
    private static (int amount, long period) DeriveRate(
        int storedDev, int baseline,
        GameWorld world, TileCoord tile, long now, BiomeDegradationConfig config)
    {
        var storedFert = baseline + storedDev;
        // M15 — the degrade source is the tile's CLAIMANT (a producing
        // extractor whose claim contains this tile), not a radius scan.
        // Only the claimant suppresses recovery now: a neighboring
        // producing camp no longer touches land it didn't claim. See
        // docs/extraction-claims.md.
        var degradeAmount = Sim.Core.World.Claims.ClaimantDegradeAmount(world, tile);
        // Implicit latch: stored fertility below threshold → permanent desert.
        // Recovery is forced to 0; only degrade applies (which just pushes the
        // deviation further negative — biome stays Desert).
        if (storedFert < config.DesertThreshold)
        {
            return degradeAmount > 0
                ? (-degradeAmount, config.DegradePeriod)
                : (0, 1);
        }
        if (degradeAmount > 0) return (-degradeAmount, config.DegradePeriod);
        // No degrade source. Recovery applies only when deviation < 0 (the
        // tile has something to recover). At deviation == 0 the tile is at
        // baseline; no further movement.
        if (storedDev < 0) return (config.RecoveryAmount, config.RecoveryPeriod);
        return (0, 1);
    }

    // (M15) The M9 radius scan — MaxInRangeProducingDegradeAmount — is
    // retired: the degrade source is Claims.ClaimantDegradeAmount (MAX
    // fold over producing claimants; overlap is structurally impossible
    // but the fold stays order-independent on principle). The O(structures)
    // scaling note moves with it — see Claims.ClaimantAt.

    // Catch up every tile the extractor CLAIMS (its working tiles — the
    // building's own tile is not among them and never degrades) to `now`
    // using the rate that applied JUST BEFORE this call.
    //
    // The caller is responsible for sequencing TickArmed correctly so that
    // the rate read inside CatchUp reflects the pre-change world state:
    //
    //   ON START (dormant → producing):
    //     Call this BEFORE setting TickArmed=true. The calling extractor's
    //     TickArmed is false → Claims.ClaimantDegradeAmount EXCLUDES it
    //     → tiles catch up under the pre-start rate. After this returns,
    //     the caller sets TickArmed=true and the new (higher) rate kicks in.
    //     (The M15 lazy auto-claim assigns ClaimTiles before this call —
    //     safe for the same reason: claim presence without TickArmed
    //     contributes no rate.)
    //
    //   ON STOP (producing → dormant):
    //     Call this BEFORE setting TickArmed=false. The calling extractor's
    //     TickArmed is still true → Claims.ClaimantDegradeAmount
    //     INCLUDES it → tiles catch up under the pre-stop rate. After this
    //     returns, the caller sets TickArmed=false and the new (lower) rate
    //     governs going forward.
    //
    // The catch-up scope is CLAIM-BOUNDED (≤ ClaimCount tiles — never a
    // global sweep; the M9 promise, now tighter than the radius box).
    internal static void OnProductionTransition(
        GameWorld world, Extractor extractor, long now, BiomeDegradationConfig config)
    {
        if (extractor.Spec.DegradeAmount <= 0) return;  // Quarry / Mine: no-op
        foreach (var t in extractor.ClaimTiles)
            CatchUp(world, t, now, config);
    }

    // The integer-exact, observation-independent math. PURE.
    //
    // Two regimes:
    //
    //   RECOVERY (ratePerPeriod > 0): smooth. No band-crossing bonus on
    //     upward — "easy to cut, hard to regrow" is the design intent.
    //       newDeviation  = clamp(storedDev + periods * rate, -baseline, 0)
    //
    //   DEGRADE (ratePerPeriod < 0): STEP-PENALTY on each downward band
    //     crossing. When fertility crosses ForestThreshold going down, snap
    //     to GrasslandBaseline (50, not 74). When it crosses DesertThreshold,
    //     snap to DesertBaseline (10, not 24). Asymmetric to make the
    //     biome flip a real, durable loss rather than a 1-tick blip the
    //     player can sit out — see docs/biome-degradation.md §step-penalty.
    //
    // In both regimes:
    //   newLastUpdate = lastUpdate + periods * ratePeriod   (carry the remainder)
    // The remainder carry is what keeps the math observation-independent
    // within a constant-rate segment.
    private static (int newDev, long newLastUpdate) ApplyMath(
        int storedDev, long lastUpdate, long now,
        int ratePerPeriod, long ratePeriod, int baseline,
        BiomeDegradationConfig config)
    {
        var elapsed = now - lastUpdate;
        if (elapsed <= 0) return (storedDev, lastUpdate);
        if (ratePerPeriod == 0 || ratePeriod <= 0) return (storedDev, lastUpdate);
        var totalPeriods = elapsed / ratePeriod;
        if (totalPeriods <= 0) return (storedDev, lastUpdate);  // sub-period; remainder banked
        var newLastUpdate = lastUpdate + totalPeriods * ratePeriod;

        // ---- recovery: smooth, no step bonus ----
        if (ratePerPeriod > 0)
        {
            long recoverDev = storedDev + totalPeriods * ratePerPeriod;
            if (recoverDev > 0) recoverDev = 0;          // clamp at baseline
            if (recoverDev < -baseline) recoverDev = -baseline;
            return ((int)recoverDev, newLastUpdate);
        }

        // ---- degrade: band-crossing snaps ----
        // Walk forward in periods, detecting each downward crossing. At each
        // crossing, snap fertility to the next band's baseline and continue
        // applying the remaining periods from the new starting point. At most
        // two snaps per call (Forest → Grassland → Desert).
        long dev = storedDev;
        long remaining = totalPeriods;
        int absRate = -ratePerPeriod;
        while (remaining > 0)
        {
            long currentFert = baseline + dev;
            int snapTargetBaseline;
            int nextThreshold;
            if (currentFert >= config.ForestThreshold)
            {
                nextThreshold = config.ForestThreshold;
                snapTargetBaseline = config.GrasslandBaseline;
            }
            else if (currentFert >= config.DesertThreshold)
            {
                nextThreshold = config.DesertThreshold;
                snapTargetBaseline = config.DesertBaseline;
            }
            else
            {
                // Desert band → no further crossings; apply remaining smoothly
                // with the [-baseline, 0] floor clamp.
                dev += remaining * ratePerPeriod;
                if (dev < -baseline) dev = -baseline;
                if (dev > 0) dev = 0;
                break;
            }

            // First period that strictly enters the new band:
            //   N > (currentFert - nextThreshold) / absRate
            // gap is non-negative because currentFert >= nextThreshold here.
            long gap = currentFert - nextThreshold;
            long periodsToCross = (gap / absRate) + 1;

            if (periodsToCross > remaining)
            {
                // Not enough periods to cross the next threshold — apply the
                // rest smoothly inside the current band.
                dev += remaining * ratePerPeriod;
                break;
            }

            // Cross. Snap fertility to the new band's baseline and continue.
            dev = snapTargetBaseline - baseline;
            remaining -= periodsToCross;
        }

        // Defensive clamp; the snap targets are configured ≤ baseline so this
        // is normally a no-op, but it guards against weird configs.
        if (dev > 0) dev = 0;
        if (dev < -baseline) dev = -baseline;
        return ((int)dev, newLastUpdate);
    }

    // Apply the math result back into world.Fertility, maintaining sparsity:
    // when deviation returns to 0, remove the tile from the dict.
    private static void WriteCaughtUp(
        GameWorld world, TileCoord tile, int baseline,
        int storedDev, long lastUpdate, long now,
        int ratePerPeriod, long ratePeriod, BiomeDegradationConfig config)
    {
        var (newDev, newLastUpdate) = ApplyMath(storedDev, lastUpdate, now, ratePerPeriod, ratePeriod, baseline, config);
        if (newDev == storedDev && newLastUpdate == lastUpdate) return;  // no-op
        if (newDev == 0)
        {
            // Recovered to baseline (or was already there). Sparse: drop the entry.
            world.Fertility.Remove(tile);
            return;
        }
        if (world.Fertility.TryGetValue(tile, out var existing))
        {
            existing.Deviation = newDev;
            existing.LastUpdateTick = newLastUpdate;
        }
        else
        {
            world.Fertility[tile] = new Fertility(newDev, newLastUpdate);
        }
    }
}
