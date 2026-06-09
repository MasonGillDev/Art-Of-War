# Cargo Capacity: Derived from Role

## Decision

`Unit.CargoCapacity` is a **derived** property — a computed getter that
reads from `UnitCargoCatalog.CapacityFor(unit.Role)`. There is no
stored backing field, no per-unit value to keep in sync, and no
serialised byte for it in the snapshot.

| Role                                                       | Capacity |
|------------------------------------------------------------|---------:|
| Hauler                                                     |       25 |
| Boat                                                       |      100 |
| None / Builder / Farmer / Miner / Lumberjack / Quarryman / Scout |    5 |

Tunable in one place: `src/Sim.Core/Logistics/UnitCargoCatalog.cs`.

## Why

The gameplay reason haulers exist is that they carry more than other
citizens. When `CargoCapacity` was a per-unit `init`-only field, the
training flow created a silent bug:

- A Builder spawned at genesis took whatever `UnitSpawn.CargoCapacity`
  said (default 1).
- `TrainUnitIntent` flipped `Unit.Role = Hauler` but the capacity
  field was init-only and never updated.
- The "trained" Hauler still had the old cap — same as the Builder
  they used to be. The training intent did nothing visible.

Deriving capacity from role at read-time makes the role flip atomic
with the cargo buff. No second mutation site, no risk of role / cap
drift after capture, training, or any future role-mutating event.

### Why a catalog (not a switch inlined into `Unit`)

`UnitCombatCatalog` already established the per-role-stat pattern
(`BaseHealth`, `BasePower`). A second `UnitCargoCatalog` mirrors its
shape so future contributors can pattern-match: "per-role number →
one row per role in a static table, one `Spec(role)` lookup."

The catalog also gives the constants names (`HaulerCapacity`,
`BoatCapacity`, `DefaultCapacity`) that tests can reference directly
rather than hard-coding magic numbers. When the values move during
balance tuning, the tests follow automatically.

### Why drop the field entirely (snapshot format bump)

Two options were considered:

1. **Keep the field, sync it on every role change.** Smaller test
   diff, no format bump. But it requires every future role-mutating
   site (training, capture-on-death, any new mechanic) to remember
   the cargo update — exactly the kind of drift the original bug
   came from. Adds permanent maintenance overhead.
2. **Make it derived; drop the field.** Bigger one-time test diff
   (every `new Unit(...) { CargoCapacity = 5 }` needs removing), one
   snapshot-format bump (8 → 9). But the field literally cannot drift
   because there's nothing to drift — the getter always reads from
   the role.

The bug the user just hit was option 1's failure mode. Option 2 makes
that class of bug structurally impossible. The migration cost is
one-shot; the property is permanent.

### Why per-role uniform (not per-spawn override)

`UnitSpawn` used to carry `CargoCapacity` as a constructor parameter,
allowing test fixtures and genesis specs to set arbitrary values.
Removed because every role's capacity is now decided by the catalog;
a per-spawn override would just be a second source of truth for the
same gameplay knob. If a future scenario needs "this particular
Hauler can carry 50 because they wear a magic backpack," that's a
Buff in `Unit.Buffs` (the M7 catalog-modifier pattern), not a per-
unit field.

### Why DefaultCapacity = 5 (not 1)

Civilians can lug a small load when asked. 1 was an arbitrary legacy
default from when only Haulers ever picked up cargo. 5 keeps the
"haulers are the bulk carriers" gradient (5× difference) while
letting a Builder shuttle a single trip's worth of stone without
needing to detour for a Hauler. Tuning knob.

## What changed

### Source

- **New** `src/Sim.Core/Logistics/UnitCargoCatalog.cs` —
  `CapacityFor(role)` + named constants.
- **Modified** `src/Sim.Core/World/Unit.cs` — `CargoCapacity` is a
  computed getter (`=> UnitCargoCatalog.CapacityFor(_role)`).
- **Modified** `src/Sim.Core/Persistence/Snapshot.cs` — `FormatVersion`
  bumped 8 → 9. The CargoCapacity int is no longer written or read;
  restore derives it from the role byte.
- **Modified** `src/Sim.Core/World/Genesis.cs` — `UnitSpawn` no longer
  takes `CargoCapacity`.
- **Modified** `src/Sim.Core/Boats/BoatProductionTickEvent.cs` —
  drops the `CargoCapacity = BoatConstants.DefaultCargoCapacity`
  assignment (boats now derive 100 via `Role == Boat`).
- **Modified** `src/Sim.Core/Boats/BoatConstants.cs` — drops the
  `DefaultCargoCapacity` constant (moved into `UnitCargoCatalog`).
- **Modified** `src/Sim.Core/Logistics/HaulIntent.cs` — drops the
  now-unreachable `CargoCapacity <= 0` reject (every role has positive
  capacity in the catalog).

### Tests

- **New** `TrainUnitTests.TrainUnit_ToHauler_ImmediatelyGrantsCargoBuff`
  — pins the user-visible promise: training a Hauler flips the
  capacity from `DefaultCapacity` to `HaulerCapacity` atomically.
- **Updated** all per-unit `CargoCapacity = X` initialisers in tests
  removed (init-only field is gone; capacity follows role).
- **Updated** `HaulIntentTests.HaulIntent_HappyPath_CastleToStockpile`
  + `MoveOnBusyTests.MoveOnHauling_KeepsCargo_AndStaleEventsFence` +
  `MoveOnBusyTests.MoveThenNewHaul_OldEventsFence_NewHaulCompletes`
  expected cargo amounts adjusted to the new HaulerCapacity (25).

## Future expansion

- **Buff-based cargo modifiers.** A "trader caravan" upgrade or a
  "wounded — drop half cargo" status reads as a Buff that multiplies
  the catalog value. Same `Unit.Buffs` pattern that combat uses for
  armour/training/morale, applied to cargo as a separate rollup
  function. No structural change to `UnitCargoCatalog`.
- **Per-role capacity tuning.** Adjusting the three constants in
  `UnitCargoCatalog` is the entire tuning surface; tests use the
  named constants so they follow.
- **New roles.** Append a row to `UnitCargoCatalog.CapacityFor`'s
  switch. If a future role needs a brand-new value, add a named
  constant and a row. The catalog is append-only, same discipline
  as `UnitCombatCatalog`.

## Out of scope

- **Per-resource capacity weights.** Stone is heavier than wood; a
  Hauler maybe carries 25 wood OR 15 stone. This is a separate
  gameplay axis (resource density). Not modelled today; if it lands,
  it lives in a `Resource.Density` table that the pickup math reads.
- **Hauler buff perks.** Once Buffs exist for non-combat effects,
  per-role-tier upgrades ("Caravan Master: +50% capacity") become
  natural. Out of scope until the M7 buff system grows non-combat
  edges.

## References

- `src/Sim.Core/Combat/UnitCombatCatalog.cs` — the precedent pattern.
- `docs/intent-validation.md` — the "Resolve re-validates everything"
  contract; the cargo-derive interaction with retrains is one
  application of that rule.
- `src/Sim.Core/Population/TrainUnitIntent.cs` — the role-mutation
  site whose buff was being silently dropped.
