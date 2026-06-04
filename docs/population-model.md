# Population Model

## Decision

The game has **no steady state by design**: combat (M7) and old-age
death drain the population; breeding adds to it; the gap between them
is the strategic metabolism. M8 is the simulation layer that makes this
work without ever ticking a per-unit clock.

**Age is derived from a stored `BornTick`**, never ticked. `Unit.BornTick`
is immutable, set at unit-creation time. Age is
`(now - BornTick) / TicksPerYear` — a pure read, the same shape as fog
visibility and road condition. There is **no `Unit.Age` field** (a
reflection-based test pins this) and **no global age sweep**.

Two gates read from this derivation:

- **Training (age ≥ 15)**: a unit cannot be assigned a role-required
  task (`AssignBuildersIntent`, `AssignWorkersIntent`) until 15. Children
  can still move, haul, and do unskilled work.
- **Fertility window (18 ≤ age ≤ 40)**: a unit can be a breeding parent
  only inside this window, **checked once at breeding start**. Aging past
  40 mid-gestation does NOT stop breeding.

**Old-age death**: lifespan is rolled ONCE per unit, at creation,
uniformly within `[LifespanMinYears, LifespanMaxYears]` × `TicksPerYear`,
from the sim's seeded `Rng`. Materialized to `Unit.DeathTick`; a
`DeathByAgeEvent` is scheduled at that tick; `Unit.DeathSeq` stores the
anchor for M4 regen. **The roll happens at exactly two unit-creation
sites**: inside the spec-aware `Simulation(GenesisSpec, ulong)` ctor
(genesis units, canonical iteration order) and inside `BirthEvent.Apply`
(children, inside the deterministic event stream). **No post-hoc
sweep exists** — a separate wiring step would consume RNG outside the
sim's owned construction or event stream, and would silently drift if a
code path forgot to invoke it.

**Breeding** rides the extractor *pattern* in a new structure:

- `StructureKind.House` (new) — a `StorageStructure` (holds food) with
  an optional `BreedingOccupation` that names two parents and stores
  the M4 anchor `(BirthTick, BirthSeq)`.
- `BeginBreedingIntent(house, parentA, parentB)` validates ownership +
  presence + idleness + fertility window + food balance + neither
  parent already breeding. On success: deduct
  `BirthFoodCost` from the house, occupy both parents
  (`Activity.Working`), schedule a `BirthEvent` at
  `now + GestationTicks`.
- `BirthEvent.Apply` fences on the anchor, spawns a role-less child at
  the house tile with `BornTick = sim.Now` (and its own seeded lifespan
  roll), frees both parents, clears the occupation.

**Stop-on-removal is ONE rule** (`Population.OnUnitRemoved`) called
from both death paths (`CombatRules.OnUnitDeath` and
`DeathByAgeEvent.Apply`). It scans Houses for an occupation that names
the removed unit as a parent; if found, clears the occupation (fencing
the queued `BirthEvent` via anchor mismatch) and frees the survivor.
**Combat code and Aging code never name Breeding**; the removal-side
helper is the one place the cross-feature coupling lives. Food is
**not** refunded — the gestation period consumed it, and raids should
bite.

`PopulationConfig` (world-level, immutable post-genesis, snapshot-
serialized) carries the tunable knobs: `TicksPerYear, MinTrainAge,
MinFertileAge, MaxFertileAge, GestationTicks, BirthFoodCost,
LifespanMinYears, LifespanMaxYears`. Parallel to `DiplomacyConfig`
(M6) and `CombatConfig` (M7).

## Why

### Derived age matches the precedent

Fog visibility and road condition are both pure reads from stored state.
A `Unit.Age` field that ticks would mean a global per-unit pass every
tick — wasteful for an idle-heavy async sim, and a place where a bug
would silently desync. Pure derivation is cheaper *and* impossible to
desync: there's no state to diverge.

### One rule for stop-on-removal — zero combat ↔ breeding coupling

If combat had to know about breeding, every new way a unit can die
(starvation, drowning, sieges) would add a coupling. Centralizing
on "a parent was removed" means combat says "a unit died" and aging
says "a unit died" and breeding watches that one signal. Each new
death pathway gets cross-feature interaction *for free*. The
combat-killing-parent test and the aging-killing-parent test both
pass through the same `OnUnitRemoved` helper.

### Attack-but-survive doesn't stop breeding

A failed raid that wounds the parents but doesn't kill them doesn't
also disrupt the birth — that would be combat reaching into breeding,
the exact coupling we're avoiding. The model is "removal stops
breeding"; non-removal events are invisible.

### Aging past 40 mid-gestation continues

The fertility window is checked once at start because:

1. It matches the spec's intent: the *decision to breed* must be made
   inside the window.
2. Mid-gestation re-checks would mean another global sweep or a
   per-house tick.
3. The strategic texture is the same: starting late means racing the
   clock to begin, not to finish.

### Variable lifespan avoids cohort cliffs

If everyone born at the same tick had the same death tick, a panic-
bred cohort would form a generation that becomes fertile together,
goes post-fertile together, and dies in a synchronized cliff. Variable
lifespan smears the cliff into a slope — boom/bust echoes for
generations, *emergently*, with no extra code.

### Children act-but-don't-train

Role-less children can move, haul, and do unskilled work — they're
useful as logistics labor immediately. But they can't be trained into a
specialist role until 15 ticks-per-year. This makes a population center
a *future army* the raider can destroy: killing a household's kids
denies the player not just current value but 15 years of incoming
specialists. The 15-year investment window has teeth.

### Lifespan roll happens inside the sim, never as a post-hoc sweep

Every other RNG consumer in the sim rolls *inside a resolving event*,
in deterministic order. Lifespan would have been the exception if we
had a `ScheduleAllLifespans(sim)` helper called after `new Simulation(...)`
— a footgun: forget it on a new code path, and units silently never die.
Instead, the lifespan roll is folded into the new
`Simulation(GenesisSpec, ulong)` ctor (one canonical iteration in
faction-id-then-unit-id order, inside the sim's owned construction) and
into `BirthEvent.Apply` (inside the event stream). Two sites, both
grep-checkable, both impossible to forget.

## Rejected alternatives

- **Ticking `Unit.Age` field**. Global per-unit sweep every tick;
  silent desync risk; no payoff over derivation.
- **Deterministic lifespan (everyone dies at age N)**. Synchronized
  cohort cliffs would dominate emergent demography. Variable is
  cheap (one roll per unit) and dramatically better-feeling.
- **Breeding cooldown**. Extra knob with no payoff over the gestation
  throttle. Two parameters where one suffices.
- **Food refund on parent death**. Raids should bite — losing a
  family's food investment is the on-theme consequence.
- **Inherited roles / genetics**. Roles are always trained. Adds
  complexity (parent-role tracking through births) without design
  payoff.
- **Children can't move**. Forces a babysitting mechanic. Children-
  as-unskilled-labor is more interesting *and* simpler.
- **`Population.ScheduleAllLifespans(sim)` as a post-hoc wiring
  helper**. A determinism landmine — consumes RNG outside the sim's
  owned construction or event stream, drifts silently when a caller
  forgets it. Folded into the spec-aware Sim ctor instead.

## Future expansion

- **Age-affects-capability**. Combat power / work output declining with
  age plugs into `Unit.Buffs` (the M7 seam) — a buff with
  `PowerModifier < 0` and a derived age threshold. Zero changes to
  breeding, death, or the casualty rule.
- **Automation** (auto-rebreed, "keep my population stable"
  policies). Server-side convenience on top of `BeginBreedingIntent`.
- **Per-unit food consumption + starvation**. New gating event;
  reuses `OnUnitRemoved`.
- **Multi-child broods / twins**. Replace single `BirthEvent` spawn
  with N child spawns; one configuration knob.
- **Fertility-by-age curves**. The current binary window becomes a
  probability function over age. Same gate point in
  `BeginBreedingIntent`.
- **Migration, housing limits, population caps**. Defer until
  unbounded growth actually bites in play.
- **Mobs / wildlife population**. Reuses this birth/age shape +
  capture-on-death (M7) when the AI/intent-generator layer lands.

## Acceptance tests

- `PopulationAgeTests.AgeYears_DerivesFromBornTick_AcrossTicks` —
  derived age changes with `now`; state doesn't.
- `PopulationAgeTests.Unit_HasNoMutableAgeField` — reflection-pinned
  invariant.
- `PopulationDeathTests.UnitDies_AtExactDeathTick` — scheduled death
  fires deterministically.
- `PopulationDeathTests.LifespansVary_NoSyncedCliff` — variable
  lifespan.
- **`PopulationDeathTests.MidLife_SnapshotRoundTrip_DeathFiresAtSameTick`** —
  M4 regen for aging.
- `PopulationTrainingTests.AssignBuilders_RejectsChild` — the 15-year
  gate.
- `PopulationTrainingTests.Child_CanMoveAndHaul` — children can act.
- `HouseBreedingTests.BeginBreeding_ValidPair_StartsGestation` — the
  start path.
- `HouseBirthTests.Combat_KillsParent_MidGestation_StopsBreeding` —
  combat → stop-on-removal.
- `HouseBirthTests.OldAge_KillsParent_MidGestation_StopsBreeding` —
  aging → stop-on-removal.
- `HouseBirthTests.AttackedButSurvived_BreedingContinues` — failed
  raid doesn't stop breeding.
- `HouseBirthTests.ParentAgesPast40_MidGestation_BreedingContinues` —
  window-at-start semantics.
- **`HouseBirthTests.MidGestation_SnapshotRoundTrip_BirthFiresAtSameTick`** —
  the M8 closure gate.
- **`RecoveryTests.MidGestation_RecoveryProducesChild`** — durable
  store crash recovery for an in-flight birth.

## Reference

M8 closes the strategic loop: build (M1) → train (M8) → breed (M8) →
fight (M7) → raid (M7) → die (M7/M8) → rebuild. Diplomacy (M6) gates
hostility; combat resolves it; population restocks it. The next big
system is open — likely mobs (a new faction whose intents come from
AI, reusing this birth/age shape for wildlife) or trade (barter posts
on top of the now-meaningful demographic economy).
