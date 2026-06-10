# Military Training: Barracks-Routed Roles

## Decision

Two new combat roles — **Soldier** (`UnitRole.Soldier = 9`) and
**Archer** (`UnitRole.Archer = 10`) — trained by the existing
`TrainUnitIntent` at a new **Barracks** structure
(`StructureKind.Barracks = 12`, a buildable `StorageStructure`).
Which structure trains which role is a static catalog:
`RoleTrainerCatalog.TrainerFor(role)` — civilian roles → School,
military roles → Barracks, Boat → none (dock-produced, never trained).

| Role    | BaseHealth | BasePower | Shape                          |
|---------|-----------:|----------:|--------------------------------|
| Soldier |         30 |         3 | tank — dies last under lowest-Health-first |
| Archer  |         15 |         5 | glass cannon                   |
| (civilians) |     10 |         1 | unchanged M7 baseline          |

Numbers are balance knobs in `UnitCombatCatalog`. Archer is a **stat
row only** this milestone — ranged-from-adjacent stays the documented
`GatherForcesNearTile` seam in `docs/combat-model.md`.

## Why

### One intent, routed by catalog (not a parallel TrainSoldierIntent)

`TrainUnitIntent` already carries the full precondition stack
(ownership, idle, age gate, not grouped/embarked/breeding) and a
frozen durable wire name in `IntentJson`. A parallel military intent
would duplicate every gate and add a second wire name forever. The
only thing that differs per role is *where* you train it, so that one
fact became data: `RoleTrainerCatalog.TrainerFor(UnitRole) →
StructureKind?`. The switch is exhaustive — an unknown role throws
rather than silently falling back (see `docs/architecture.md` on
silent fallbacks).

A Barracks rather than the School because military training is a
distinct strategic investment: a building the player must afford
(100 Wood + 20 Stone), place, and defend. It is a `StorageStructure`
(capacity 200) because it doubles as the crafting site — raw
materials are hauled in, weapons are crafted into the same holdings
(see `docs/equipment-model.md`).

### Health delta on role change (not full heal, not carry-over)

Per-unit Health plus per-role BaseHealth forces a rule for retraining.
Options considered:

1. **Set Health to the new role's BaseHealth.** A wounded unit heals
   by retraining — free infirmary, clearly exploitable.
2. **Carry Health unchanged.** A Farmer (10) trained to Soldier (30)
   arrives at one-third health — training *wounds* the recruit, which
   reads as a bug.
3. **Delta: `Health += Base(new) − Base(old)`, clamp min 1.** Chosen.
   Absolute wounds persist (a Soldier at 25/30 retrained to Farmer is
   at 5/10), full-health units stay full, retraining can't kill (the
   clamp), and the math is one integer expression at the single
   role-mutation site.

The delta reads the catalog live, so retuning BaseHealth mid-save
changes future retrain deltas — deterministic, just worth knowing when
tuning.

### Retraining strips equipment

Before the role flips, `Equipment.DropEquipmentToGround` removes all
equipment buffs and drops the items on the trainer tile (recoverable
by haul, matter conserved). Without this, a Soldier retrained to
Farmer keeps a sword buff their role could never have equipped. The
losing alternative — rejecting training while equipped — was ruled out
because there is no UnequipIntent, so equipped units would be
permanently role-locked.

### Archer as a stat row (ranged deferred)

This milestone proves roles + equipment on the unchanged M7 round
event. Ranged-from-adjacent touches the combat trigger, the engagement
pin, and the force-gather seam — real scope that deserves its own
milestone. Until then Soldier and Archer differ as tank vs glass
cannon under the lowest-Health-first casualty rule, which is already a
meaningful composition choice.

## Acceptance tests

- `MilitaryTrainingTests.TrainSoldier_OnOwnBarracks_FlipsRole` and
  `TrainSoldier_OnSchool_Rejected` / `TrainBuilder_OnBarracks_Rejected`
  — the routing fence, both directions.
- `MilitaryTrainingTests.TrainToSoldier_AdjustsHealthByBaseDelta` —
  the delta rule, catalog-derived.
- `MilitaryTrainingTests.RetrainWoundedSoldier_ToFarmer_KeepsAbsoluteDamage_ClampsAtOne`.
- `MilitaryTrainingTests.Retrain_StripsEquipment_DropsItemsToTile`.

## Future expansion

- **More military roles** (Knight, Pikeman…): append a `UnitRole`
  value, a `UnitCombatCatalog` row, and a `RoleTrainerCatalog` row.
- **Ranged-from-adjacent**: the `GatherForcesNearTile(tile, radius)`
  seam in `docs/combat-model.md`; Archer passes radius 1.
- **Training costs / duration**: today training is instant and free
  (the School precedent). If military training should cost resources
  or time, that lands as preconditions in `TrainUnitIntent` gated on
  the trainer kind — the routing catalog already identifies military
  roles.
- **Tiered trainers**: `TrainerFor` returning a set (e.g., Knight
  needs Castle) is a one-line shape change.

## References

- `docs/combat-model.md` — per-unit Health, lowest-Health-first, and
  the seams this fills.
- `docs/equipment-model.md` — the equipment layer that gives these
  roles their loadouts.
- `src/Sim.Core/Population/TrainUnitIntent.cs` — the single
  role-mutation site.
