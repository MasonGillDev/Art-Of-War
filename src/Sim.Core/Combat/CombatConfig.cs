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
// M43 — there is one combat model, the battlefield grid (docs/battlefield-grid.md): a
// 4×4 board of subtiles on every contested tile, fought in simultaneous turns of
// RoundIntervalTicks each (one turn = one round). The pooled stat-pool rounds are gone
// except the SIEGE round, which drains a structure's HP when attackers are alone with it.
// LineSupport is the board's morale per friend alongside (BattleConfig).
public readonly record struct CombatConfig(
    long RoundIntervalTicks,
    int LineSupport = 25)
{
    public CombatConfig() : this(RoundIntervalTicks: 1 * Time.Hour) { }

    public Sim.Core.Battlefields.BattleConfig Battle => new(LineSupport);
}
