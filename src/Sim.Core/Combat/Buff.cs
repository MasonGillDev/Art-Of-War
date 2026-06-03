namespace Sim.Core.Combat;

// M7 — scaffolding for armor / training / equipment / temporary
// effects. EMPTY today (no buff instances are created in M7) but the
// rollup reads through it from day one, so adding a buff later doesn't
// touch the round event or the catalog.
//
// Kind            — stable string id; future code branches by this.
// PowerModifier   — added to UnitCombatCatalog.Spec(role).BasePower in
//                   CombatRules.EffectivePower.
// HealthModifier  — added to UnitCombatCatalog.Spec(role).BaseHealth at
//                   buff-application time (a future "+5 max HP from
//                   armor" buff would bump the unit's Health on apply).
// ExpiresAt       — sim tick when the buff lapses; null = permanent.
//                   Today there's no expiry-tick sweep; future buff
//                   processing reads this.
public sealed record Buff(
    string Kind,
    int PowerModifier,
    int HealthModifier,
    long? ExpiresAt);
