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
// CargoModifier   — flat add to Unit.CargoCapacity (rolled up live in the
//                   getter, summed across buffs). A cart's +carry; a future
//                   "wounded: drop half" would go negative. (M-cart)
// MoveCostPercent — % ADDED to each hop's ground-truth move cost (the unit
//                   moves slower). Summed across buffs, applied in
//                   MoveIntent.ScheduleNextHop. 0 = no effect; +50 = 1.5x
//                   slower. A cart's tradeoff for the extra cargo. (M-cart)
//
// A buff is a bag of stat modifiers; only the relevant fields are non-zero
// per kind (sword → power; shield → health; cart → cargo + movecost).
public sealed record Buff(
    string Kind,
    int PowerModifier,
    int HealthModifier,
    long? ExpiresAt,
    int CargoModifier = 0,
    int MoveCostPercent = 0);
