using Sim.Core;

namespace Sim.Server.Bandits;

// M16 — every pacing knob for the bandit driver in one place (the user
// tunes these constantly; see docs/m16-bandits-spec.md). These are
// SERVER-side policy: how often the brain thinks, how many parties the
// world sustains, how big they are. The sim-side LAW (spawn darkness,
// MinSpawnDistance, MaxPartySize clamp, combat stats, cargo capacity)
// lives in Sim.Core catalogs/constants and is validated at resolve time
// no matter what this config says.
public sealed record BanditConfig
{
    public bool Enabled { get; init; } = true;

    // How often the driver re-evaluates the world. Strategic-band-lite:
    // one think per game-hour keeps the intent log lean while still
    // reacting within a fraction of any march.
    public long ThinkPeriodTicks { get; init; } = Time.Hour;

    // Prosperity scaler: one live party per this many player structures
    // (sprawl attracts wolves), capped at MaxLiveParties. At the default
    // 8, a starter base (castle + a few camps) draws zero or one party;
    // a sprawling empire draws the cap.
    public int StructuresPerParty { get; init; } = 8;
    public int MaxLiveParties { get; init; } = 4;

    public int PartySizeMin { get; init; } = 2;
    public int PartySizeMax { get; init; } = 4;

    // Every Nth spawned party is an AMBUSHER: it sits idle in the fog
    // until something walks into its sight — the "accidentally ran into
    // them" encounter. The rest spawn as raiders and wander hunting.
    public int AmbusherEvery { get; init; } = 3;

    // Random-site sampling budget per think when a spawn is due. The
    // driver pre-filters with the same pure reads the intent validates
    // with, so misses are cheap.
    public int SpawnAttemptsPerThink { get; init; } = 40;

    // How far a hunting party wanders per leg when nothing is in sight.
    public int WanderRadius { get; init; } = 15;

    // Driver RNG seed. The driver does NOT need to be deterministic for
    // the sim's determinism contract (its DECISIONS land in the durable
    // intent log; replay re-reads the log, not the brain) — seeding it
    // just makes server runs and driver tests reproducible.
    public ulong Seed { get; init; } = 0xBA4D17;
}
