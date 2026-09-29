namespace Sim.Core.Battlefields;

// M41 — the battlefield's balance knobs (docs/battlefield-grid.md §5). Set at
// genesis, immutable for the world's lifetime. The turn length is not here:
// a turn is one combat round, CombatConfig.RoundIntervalTicks.
//
//   LineSupport — morale per friend alongside (orthogonal, same board).
//                 Morale is 100 + LineSupport × friends; damage dealt is
//                 base × morale ÷ 100, rounded down.
//
// The explicit parameterless constructor is load-bearing: `new BattleConfig()`
// on a record struct would otherwise zero every knob (the IdolConfig trap).
public readonly record struct BattleConfig(int LineSupport)
{
    // Morale's steady value. A constant, not a knob: morale is a percentage.
    public const int SteadyMorale = 100;

    public BattleConfig() : this(LineSupport: 25) { }
}
