namespace Sim.Core.Royalty;

// M31 — world-level dynasty configuration. Set at genesis, immutable for the
// world's lifetime, serialized in the snapshot. Parallel to CombatConfig and
// PopulationConfig (docs/king-and-dynasty.md, docs/m31-king-dynasty-spec.md).
//
// AuraRadius      — tiles, measured as an integer EUCLIDEAN disc
//                   (dx*dx + dy*dy <= r*r), the same shape Sight.Reveal uses,
//                   so "radius" means one thing across the codebase. The
//                   king's buff is positional on purpose: a global buff makes
//                   him a lockbox item nobody ever moves, a radius makes him a
//                   piece on the board.
// AuraPowerBonus  — flat power added to each of the king's own units inside
//                   the disc. THE temptation knob: with-king should win often
//                   but not always. Tempting always, mandatory never.
// MajorityAge     — age-years below which a crowned heir is a child monarch
//                   and projects NOTHING. A known, visible window of weakness
//                   enemies can plan around — telegraphed stakes, on brand.
//
// Denominated in AGE-YEARS (like every PopulationConfig gate), so the
// demographic clock rescales minority along with everything else.
public readonly record struct RoyaltyConfig(
    int AuraRadius,
    int AuraPowerBonus,
    int MajorityAge)
{
    // Defaults are a STARTING POINT for the balance lab, not a tuned answer.
    // The temptation-zone sweep (docs/m31-king-dynasty-spec.md, Phase D)
    // asserts the shape of the win-rate curve, never a specific number here,
    // so retuning these must never turn a test red.
    //
    // MajorityAge tracks PopulationConfig.MinTrainAge: the age a citizen can
    // be given a job is the age a monarch can hold the crown's power. One
    // coming-of-age in the fiction, not two.
    public RoyaltyConfig() : this(
        AuraRadius: 3,
        AuraPowerBonus: 1,
        MajorityAge: 6)
    { }
}
