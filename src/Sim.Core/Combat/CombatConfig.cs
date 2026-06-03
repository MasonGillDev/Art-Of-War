namespace Sim.Core.Combat;

// M7 — world-level combat configuration. Lives on GameWorld, set at
// genesis, immutable for the world's lifetime, serialized in the
// snapshot.
//
// RoundIntervalTicks — ticks between combat rounds on a contested tile.
//                      Small enough to feel snappy; large enough that
//                      reinforcements can arrive between rounds and
//                      retreating units can walk out. Tunable.
public readonly record struct CombatConfig(long RoundIntervalTicks)
{
    public CombatConfig() : this(RoundIntervalTicks: 10) { }
}
