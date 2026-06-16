# Equipment: Items as Resources, Buffs as Loadout

## Decision

Equipment (Sword, Bow, Shield) ships as three new **`Resource` enum
values** (`Sword = 5`, `Bow = 6`, `Shield = 7`). Items are **crafted
instantly** at a Barracks via `CraftEquipmentIntent` (consuming raw
resources from the Barracks' own holdings) and **equipped** onto a unit
via `EquipUnitIntent` at any owned `StorageStructure` holding the item.
Equipping consumes the item and adds a permanent `Buff` to `Unit.Buffs`
— the M7 scaffold becomes live.

Every unit holds at most **`BuffRules.MaxBuffsPerUnit = 2` buffs, of
distinct Kinds** — a loadout (sword + shield Soldier, bow + shield
Archer), not a stack.

| Item   | Buff               | Equippable by     | Craft cost          |
|--------|--------------------|-------------------|---------------------|
| Sword  | +3 power           | Soldier           | 5 Wood + 5 Ore      |
| Bow    | +4 power           | Archer            | 10 Wood             |
| Shield | +10 health         | Soldier, Archer   | 5 Wood + 5 Stone    |

All numbers are balance knobs in
`src/Sim.Core/Equipment/EquipmentCatalog.cs`; each weapon sits on a
different supply chain (Ore / Wood / Stone) so military pressure pulls
on every extractor type.

## Why

### Items as `Resource` values (not an item system)

Equipment today is fungible and stateless — one sword equals any other
sword; there is no durability, no quality tier, no per-instance state.
That is exactly the contract `Resource` already satisfies, and the
whole logistics layer comes along for free:

- `StorageStructure.Holdings` stores them (it's a
  `SortedDictionary<Resource,int>` — canonical snapshot order for
  free).
- `HaulIntent` / `HaulPickupEvent` move them, including the
  ground-pile fallback — looting a battlefield is the *existing* haul
  flow pointed at a pile.
- `Snapshot` and `StructDto.Holdings` need zero new code.

A bespoke item system (item entities with ids) buys nothing until
items carry per-instance state. If durability ever lands, that is the
moment to revisit — and the migration is localised to the equip/drop
seam, because nothing else ever inspects an item.

### Instant crafting (not Dock-style timed production)

The losing option: timed crafting with the Dock production pattern
(`ProductionArmed` / `LastProductionTick` / `NextProductionTickSeq`
anchors + a `RegenerateQueue` case + mid-craft recovery tests). It was
rejected because the pacing it buys already exists upstream — ore must
be mined on a production period and hauled to the Barracks; the
logistics chain *is* the time cost of a sword. Instant crafting means
this milestone adds **zero new scheduled events and zero new
persistence anchors**, collapsing its recovery-risk surface to
nothing.

Converting later is additive (add the anchor trio + regen case), not a
rework: `CraftEquipmentIntent`'s validation and the catalog's
`CraftCost` survive unchanged.

Crafting requires no unit present — there is no smith. Deferred: if a
"crafter labor" mechanic lands, it slots in as a precondition on the
intent (same shape as `RequiredBuilderCount` on construction).

### Equip at any owned storage (not Barracks-only)

Hauling already moves Sword/Bow/Shield into any storage for free, so
"craft at the Barracks, haul swords to a forward stockpile, equip at
the front" is real strategic gameplay that costs zero code.
Barracks-only equipping would add a special case the storage machinery
doesn't otherwise have, and would make every reinforcement walk home.

### Two buff slots, distinct Kinds (not one weapon, not stacking)

`BuffRules.MaxBuffsPerUnit = 2` with at most one buff per `Kind`.
Customization means *choosing two different buffs*; allowing two
swords (+6 power) is stat-stacking, not a loadout, and creates a
degenerate "always double the best item" equilibrium. The slot cap is
a generic buff rule (lives in `Combat/BuffRules.cs`, not the equipment
layer) so future buff sources — training drills, well-fed — share it
via `BuffRules.CanAccept`.

Deferred question, recorded: if passive sources like well-fed land
later, they may move to a separate non-slot category so eating doesn't
compete with a shield. Decide when the first passive source exists.

### Modifiers bake into the `Buff` at equip time

`EquipUnitIntent` copies the catalog's `PowerModifier` /
`HealthModifier` into the `Buff` instance, which is what the snapshot
carries. Retuning `EquipmentCatalog` therefore never mutates
already-equipped units — a save from last month replays identically
under new balance numbers. The catalog is data for *future* equips
only.

### HealthModifier semantics (Shield is the first user)

Exactly as `Buff.cs` documented in M7: applied to **current Health at
apply time** (`Health += HealthModifier`), reversed at strip time
(`Health -= HealthModifier`, clamped to a minimum of 1 so stripping
can't kill). There is no "max health" stat to track — Health is the
only number, and the casualty rule (lowest-Health-first) reads it
directly, so a shield-bearer naturally dies later.

The min-1 clamp admits a convoluted minor heal (retrain a nearly-dead
shielded unit to strip at clamp, retrain back, re-equip). Accepted:
the path costs two trainings plus re-hauling the shield, and the
alternative — letting a strip kill the unit — is hostile to the
player.

### Death drops equipment

`CombatRules.OnUnitDeath` already drops cargo to
`world.GroundResources` (the M7 raiding mechanic: kill the caravan,
loot the goods). Equipment follows the same rule via
`Equipment.DropEquipmentToGround`: each equipment-kind buff converts
back to one item on the death tile. Matter is conserved; killing
equipped soldiers is lootable; the loot loop (kill → ground pile →
haul → re-equip) closes with zero new machinery.

### Buff expiry: the seam, not the sweep

`CombatRules.EffectivePower(unit, now)` now takes sim time and filters
buffs with `ExpiresAt <= now` (a buff expiring *at* `now` is already
inactive). `now` is always sim event time — never wall clock — so the
lazy filter is deterministic and observation-independent by
construction.

There is **no expiry sweep**: equipment is permanent
(`ExpiresAt: null`), so no expired buff can exist yet. When timed
buffs land (drills, well-fed), expired entries get pruned at the
deterministic mutation sites that already touch the unit (the granting
event, or combat-round start) — never inside `EffectivePower`, which
must stay a pure read (see `docs/architecture.md`, pure-read wall).

## Acceptance tests

- `EquipUnitTests.EquipSword_OnSoldier_AddsBuff_ConsumesItem` — the
  core consume-and-buff contract, all values catalog-derived.
- `EquipUnitTests.Equip_SecondShield_Rejected_DuplicateKind` /
  `Equip_ThirdBuff_Rejected_SlotCap` — the loadout rules.
- `CraftEquipmentTests.CraftSword_InsufficientOre_Rejected_NothingMutated`
  — fail-clean validation.
- `EquipmentCombatTests.LootedSword_HauledFromBattlefield_ReEquipsAnotherSoldier`
  — the loot loop end-to-end on existing logistics.
- `MilitaryDeterminismTests.MidFight_EquippedForces_SnapshotRoundTrip_Identical`
  — the M7 closure gate re-proven in a buffed world.

## Update 2026-06-16 — the first non-combat buff (the cart)

The deferred "passive/non-combat source" question is now live: the **cart**
(`docs/cart.md`) is a `Buff` that trades move speed for carry capacity. It adds
two modifier dimensions to `Buff` (`CargoModifier`, `MoveCostPercent`) and rolls
them up live (cargo in `Unit.CargoCapacity`, move-cost in
`MoveIntent.ScheduleNextHop`). Per the "two slots, distinct Kinds" note below, we
shipped it **sharing the 2 slots** (carts → Haulers, weapons → Soldiers/Archers,
so they rarely compete); a separate gear-slot category is the clean split if play
demands it. The craft/equip/drop loop and the slot rules were reused unchanged.

## Future expansion

- **Timed buffs** (drills, well-fed): `ExpiresAt` is already
  serialized and already filtered; a granting event plus pruning at
  mutation sites is the entire cost.
- **Armor / more equipment**: new `Resource` value + one
  `EquipmentCatalog` row. Append-only.
- **Timed crafting**: Dock anchor pattern, additive.
- **Durability / quality**: the trigger for revisiting
  items-as-resources; localised to the equip/drop seam.
- **Unequip intent**: not built (drop happens via death/retrain). If
  players need voluntary swaps, an `UnequipIntent` is the strip helper
  plus a deposit-into-storage; the helper already exists.

## Out of scope

- Ranged-from-adjacent for Archers (see `docs/combat-model.md`,
  `GatherForcesNearTile` seam).
- Win conditions / castle capture (deferred until multiplayer, per
  audit remediation).

## References

- `docs/combat-model.md` — the M7 seams this milestone fills.
- `docs/cargo-capacity.md` — the catalog-pattern precedent.
- `src/Sim.Core/Combat/Buff.cs` — the M7 scaffold contract this
  implements.
