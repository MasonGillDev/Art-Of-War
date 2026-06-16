# The Cart: a non-combat buff (carry capacity vs. speed)

## Decision

A **cart** is a new equipment item (`Resource.Cart = 8`) that, when equipped,
adds carry capacity at the cost of move speed. It is modeled as a **`Buff`** —
the same craft → haul → equip → drop-on-death loop the weapons use — not a new
entity. It is the **first non-combat buff**, and to support it the buff system
gains two new modifier dimensions:

- `Buff.CargoModifier` — added to `Unit.CargoCapacity` (rolled up live, summed
  across buffs).
- `Buff.MoveCostPercent` — added to each hop's move cost (the unit moves slower),
  summed across buffs, applied in `MoveIntent.ScheduleNextHop`.

Launch numbers (balance knobs in `EquipmentCatalog`): a cart gives **+25 carry**
(doubles a Hauler's 25) for **+50% move time per hop**, crafted from **20 Wood +
10 Stone**, equippable by **Haulers**. It **shares the existing 2 buff slots**
(no separate gear slot).

## Why

### Why a Buff, not a new entity

A cart as described is a **pure, fungible stat modifier** that dies/drops with its
carrier — exactly the buff/equipment contract. Riding `Buff` means it reuses the
*entire* existing loop for free: crafted as a `Resource` at a Barracks
(`CraftEquipmentIntent`), hauled to any storage, equipped (`EquipUnitIntent`,
role-gated), and dropped back as a `Cart` item on death/retrain
(`Equipment.DropEquipmentToGround`, which already handles *any* equipment buff
generically). **Zero new entities, zero new events, zero new persistence anchors
→ recovery-clean.** The whole feature is two modifier fields + two rollups + a
catalog row + an equip bake.

### Can a buff affect two stats at once — yes, that's its shape

`Buff` was already a multi-modifier record (`PowerModifier` + `HealthModifier`);
a sword sets power, a shield sets health, nothing stops one buff from setting
several. The cart just adds two *new* modifier dimensions to the bag. The buff is
a bag of stat modifiers; only the relevant fields are non-zero per kind.

The catch the cart exposes: the two stats it wants weren't buff-driven before.
`CargoCapacity` was a pure role-derived getter (it ignored buffs), and per-unit
move speed **did not exist at all** (movement is a tile-cost property). So the
cart isn't "just another `EquipmentCatalog` row" the way a sword is — it required
teaching `CargoCapacity` to roll up buffs (anticipated by `cargo-capacity.md`)
and introducing a per-unit move-cost dimension (genuinely new).

### Why the slowdown is applied to EXECUTION cost only (not A* planning)

Movement cost is split: `PlanCost` chooses the *route* (fog-aware A*),
`ExecutionCost` sets each hop's *real travel time*. A uniform per-unit multiplier
doesn't change which route A* prefers (all tiles scale equally), so applying it
to planning would be pointless and would drag the determinism-sensitive
pathfinder into the change for nothing. The slowdown *is* travel time, so it
lives in `ExecutionCost` only — the cart takes the **same route, just slower**.
This keeps the change off the A* path and confined to one line in
`ScheduleNextHop`.

The math is integer and determinism-safe: `hopCost * (100 + slow) / 100` with a
`long` intermediate (no overflow) and an `Impassable` guard (a blocked cost never
gets scaled). A unit with no move-cost buffs (`slow == 0`) is untouched — every
existing move test is bit-identical (pinned by `NoCart_Movement_Unchanged`).

Recovery is clean for free: the scaled cost is baked into the hop's
`NextArrivalTick` anchor at schedule time, so a mid-move cart survives
snapshot/restore (the arrival was already scaled; restore doesn't recompute it).
And equipping requires an **Idle** unit, so you can't equip mid-hop — every hop a
cart unit takes is already scaled.

### Why it shares the 2 slots (and when to split)

There is no distinct combat slot — `BuffRules` is a single 2-slot pool gated by
count + distinct `Kind`. The cart is the first *non-combat* buff, the exact case
`equipment-model.md` deferred ("if passive sources land later, they may move to a
separate non-slot category"). We ship it **sharing the pool**: carts go on
Haulers, weapons on Soldiers/Archers, so slot competition rarely bites. A separate
"gear slot" is a clean future addition (a slot-class on the catalog + a per-class
cap in `BuffRules.CanAccept`) — split only if play shows weapons and carts
fighting over slots.

### When a "similar" attachment should become its OWN mechanic (mounts, siege)

The cart fits the buff model because it is (a) fungible — no per-instance state,
(b) a pure stat modifier on its carrier, and (c) destroyed/dropped with the
carrier. **Promote an attachment to its own entity** (a `Unit`, the way the M12
boat is a unit with its own `Traversal`, health, cargo, and passengers) the moment
it needs any of:

1. **Per-instance state** — durability, its own HP, ammo/fuel.
2. **Independent existence/behavior** — it can detach and be parked/wander, it
   *acts* (a siege engine attacks), or it's captured/destroyed separately.
3. **Its own cargo or crew** — goods that survive the carrier's death, or N units
   crewing it.

Applied: **cart = buff** (this doc). **Siege weapon = its own entity** (HP +
attacks + crewed + captured as an object — none of that is a stat modifier; model
it boat-style). **Mount = depends** (a "+speed, dies with rider" abstraction →
buff; a separately-killable horse that can be unmounted → entity). Carts and
future stat-attachments ("trader caravan: +cargo", "wounded: drop half cargo",
"well-fed: faster") all ride the buff modifier bag; anything that crosses the
state/independence line goes the entity route and doesn't pollute the bag.

## What changed

- `Resource.Cart = 8` (append-only).
- `Buff` gains `CargoModifier` + `MoveCostPercent` (defaulted 0 — existing
  weapon buffs are unchanged). `Snapshot` serializes both;
  `FormatVersion` 17 → 18.
- `EquipmentSpec`/`EquipmentCatalog` gain the two modifiers + the `Cart` row;
  `EquipUnitIntent` bakes them into the granted `Buff`.
- `Unit.CargoCapacity` rolls up `CargoModifier` over the unit's buffs (clamped
  ≥ 0). `MoveIntent.ScheduleNextHop` scales the hop's `ExecutionCost` by the
  summed `MoveCostPercent`.

## Out of scope / deferred

- **Group movement with a carted member.** The slowdown is applied to solo
  `MoveIntent` hops only; group pace (slowest member) doesn't read cart
  multipliers yet. Carts are for solo haulers; add it to `MoveGroupIntent` if
  caravans want carts.
- **A separate gear slot** (see above) — ship shared, split if needed.
- **Client wiring** — the Unity client needs `Resource.Cart = 8` and a craft/equip
  affordance to use carts in-game (same follow-on shape as the cache wiring).

## Acceptance tests

`tests/Sim.Tests/CartTests.cs`: cart adds carry capacity; cart slows movement;
crafted at a Barracks; equipped on a Hauler (consumes the item) and rejected on a
non-Hauler; counts toward the shared 2-slot cap; drops as a `Cart` item on strip
and restores capacity; round-trips through the snapshot preserving both modifiers;
a carted move twin-runs to equal hashes; and an un-carted move is bit-unchanged.

## References

- `docs/equipment-model.md` — the buff/equipment loop this extends; flagged the
  "first non-combat buff" slot question.
- `docs/cargo-capacity.md` — explicitly anticipated buff-based cargo modifiers.
- `docs/movement-cost.md` — the tile-property speed model; this adds the first
  per-unit move-cost dimension, on the execution side only.
- `docs/boats.md` — the entity (vehicle-as-Unit) pattern siege/mounts would use
  if they cross the state/independence line.
