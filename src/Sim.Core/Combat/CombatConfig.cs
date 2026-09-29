namespace Sim.Core.Combat;

// M7 — world-level combat configuration. Lives on GameWorld, set at
// genesis, immutable for the world's lifetime, serialized in the
// snapshot.
//
// RoundIntervalTicks — ticks between combat rounds on a contested tile.
//                      Small enough to feel snappy; large enough that
//                      reinforcements can arrive between rounds and
//                      retreating units can walk out. Tunable.
//
// M41 — Model picks the combat model at genesis (docs/battlefield-grid.md,
// docs/m41-status.md): Pooled is the stat-pool rounds above (the default until
// M42 flips it); Grid opens a 4×4 battlefield on every contested tile, fought
// in simultaneous turns of RoundIntervalTicks each (one turn = one round).
// LineSupport is the Grid's morale per friend alongside (BattleConfig).
// Snapshotted v41.
public readonly record struct CombatConfig(
    long RoundIntervalTicks,
    CombatModel Model = CombatModel.Pooled,
    int LineSupport = 25)
{
    public CombatConfig() : this(RoundIntervalTicks: 1 * Time.Hour) { }

    public Sim.Core.Battlefields.BattleConfig Battle => new(LineSupport);
}

public enum CombatModel : byte
{
    Pooled = 0,
    Grid = 1,
}
