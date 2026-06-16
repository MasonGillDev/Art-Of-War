namespace Sim.Core.Caches;

// M23 — genesis loot-cache scatter parameters (docs/loot-caches.md). Lives on
// GenesisSpec (a genesis-time input), NOT on GameWorld: the scatter runs once
// in the Simulation spec-ctor with the sim's seeded Rng, and only the
// RESULTING Cache structures are serialized — restore loads them, it never
// re-scatters.
//
// Count defaults to 0 so existing scenarios spawn no caches and consume no Rng
// (their snapshot hashes are bit-identical); a scenario opts in by setting
// Count > 0. The amounts/chance are balance knobs, tuned like every other
// config in the game.
public readonly record struct CacheConfig(
    int Count = 0,
    // Per-cache primary resource stack, rolled uniformly in [Min, Max].
    int MinResourceAmount = 20,
    int MaxResourceAmount = 60,
    // Percent chance (0..100) a cache ALSO holds one gear item
    // (Sword / Bow / Shield) — exploration as an early path to arms.
    int GearChancePercent = 30);
