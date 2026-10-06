namespace Sim.Core.Mining;

// M44 — ore veins and surveying (docs/stone-and-ore-land.md). World-level,
// set at genesis from GenesisSpec.Veins, immutable after, snapshotted (v45).
//
//   OneIn        — a Mountain tile holds a vein when its seeded hash is
//                  divisible by this: 3 ≈ a third of all mountain tiles
//                  (the user, 2026-10-01). Plus the per-range safety net
//                  (Veins.Seed): no mountain range is ever barren.
//   Seed         — the vein layout's own seed. WorldFactory passes the map
//                  seed. Seeding draws NO sim Rng, so every other random
//                  stream is byte-identical with or without veins.
//   SurveyTicks  — how long a Miner digs at the slope before he reports.
//   SurveyRadius — Chebyshev reach of one sweep around the surveyed tile.
//   SteelSharePercent / IronSharePercent — M51 ore tiers
//                  (docs/m51-ore-tiers-spec.md): the veins ranked by
//                  remoteness, the farthest SteelSharePercent hold steel ore,
//                  the next IronSharePercent iron ore, the rest copper ore.
//                  Balance dials.
public sealed record VeinConfig(
    int OneIn = 3,
    ulong Seed = 0,
    long SurveyTicks = Time.Day,
    int SurveyRadius = 2,
    int IronSharePercent = 20,
    int SteelSharePercent = 10);
