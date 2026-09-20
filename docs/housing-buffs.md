# Housing Buffs — a fed household works and fights harder

**Status:** decided and built 2026-09-19.
**Depends on:** M19 homes and per-house food, the M31 pure-read power seam
(`CombatRules.EffectivePower(world, unit, now)`), M1 production ticks.

## The decision

A unit is **settled** when its home is a House of its own faction whose
pantry is not in debt. A settled unit gets a flat power bonus and, when
assigned to an extractor, a flat extra output per production period. Two
knobs in `HousingConstants`: `SettledPowerBonus` (1) and
`SettledWorkBonusPerWorker` (1).

Three rules the user locked:

1. **Castle-homed units get nothing.** The castle is uncapped shelter of
   last resort; sleeping in the yard is not a home.
2. **A starving household gets nothing, and no penalty.** Famine already
   kills; it does not weaken first. A resident of a house in debt is exactly
   an unhoused unit until the pantry is fed, then settled again at once.
3. **Settled is a pure read**, never a stored buff. It is a fact about the
   unit's home and that home's stock, both of which are world state.

## Why

### What it fixes

Before this, a home was pure accounting: the only thing a house did for a
resident was feed them, and the only thing the player felt was the famine
when it did not. Overflow children homed at the castle were an invisible
drain. Now the first house is a felt reward, an unfed house is a felt loss
of output before it is a loss of life, and a house placed by the fields
makes those fields better. Housing becomes a decision with a payoff rather
than a chore with a death clock.

### Why additive, not a multiplier

Per-worker extractor rates are 1 (mismatched role) or 2 (matching role) per
period. Any fraction floors to nothing on those numbers. A flat +1 per
settled worker is legible ("a housed worker makes one more"), integer-exact,
and large enough to matter: +50% on a matched worker, +100% on a mismatched
one. That size is deliberate; housing is meant to be the biggest thing a
player can do for a field short of staffing it with the right trade.

### Why no penalty (the losing alternative)

A "starving residents work slower" rule was proposed and rejected by the
user. It would have turned the three-day grace into a visible degradation,
but it stacks a second punishment on a household the player is already
about to lose, and it makes the recovery curve steeper exactly when the
player is trying to fix it. The famine machinery is the penalty. Feeding
the house is the whole answer, and the buff returning the instant the
pantry is fed is the reward for doing it.

### Why a pure read beside the king's aura

`Unit.Buffs` is the two-slot equipment loadout; a housing buff as a stored
`Buff` would eat a sword slot and need grant/revoke churn on every food
delivery and every re-home. The king's aura already solved this for a
position-derived bonus by computing it at read time in the world-aware
`EffectivePower`. Settled is the same shape for a home-derived bonus.
Zero snapshot change, zero anchors, replay-identical by construction.

### Trade-offs accepted

- **Balance curves move.** Every AI colony with houses now produces more,
  so the M17/M19 labs shift upward. This is the intended direction; the
  labs pin survival floors, not exact yields.
- **The Rival's overmatch estimate does not see settledness** of enemy
  units (power is private on the wire). Its own units' power does include
  it. A housed defender is therefore slightly stronger than the attacker
  expects, which favours the defender at the margin, consistent with the
  async doctrine.
- **No distance rule yet.** A resident posted far from their house is still
  settled. Making the buff depend on working within reach of home would make
  house placement matter more; deferred until the basic loop is felt in play.

## Mechanics

| Piece | Where |
|---|---|
| Settled read + knobs | `src/Sim.Core/Population/Housing.cs` |
| Power | `CombatRules.EffectivePower(world, u, now)` adds `Housing.SettledPowerBonus` beside the aura |
| Work | `ProductionTickEvent` adds `Housing.SettledWorkBonus` per worker before the claim taper |
| Wire | `UnitDto.Settled`, own units only, both projection paths |

## Acceptance tests (`HousingBuffTests`)

- Settled at a fed own house: base power plus the bonus. Castle-homed: base.
  Starving resident: base, never less. Fed again: settled at once.
- Owner-scoped: a home pointing at a house that changed hands never settles.
- A settled matched-role worker produces the catalog rate plus the bonus per
  period; a castle-homed one produces the catalog rate.
- A starving worker produces the catalog rate, not less.

## Future expansion

- **Distance to home**: settled only while working within `HomeAssignRadius`
  of the house. A placement rule, not new machinery.
- **Fertility**: a settled pair conceives cheaper or faster. Closes the loop
  between housing and growth; wants the M8 breeding economics re-read first.
- **House tiers**: a bigger or stocked house with a larger bonus is a
  catalogue row plus a second constant.
- **Morning report**: "N of your people are unhoused" is a one-line pure
  read over `Housing.IsSettled`.

## References

- `docs/m19-per-house-food-spec.md` — homes, the three auto-assignment
  triggers, and the 2026-09-19 "starving house has no free beds" addendum.
- `docs/king-and-dynasty.md` — the pure-read aura precedent.
- `docs/automation-as-core-game.md` — law 5, bread is electricity.
