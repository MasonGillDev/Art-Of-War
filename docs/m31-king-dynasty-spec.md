# M31 — King & Dynasty (line, succession, and the king's aura)

**Design doc:** `docs/king-and-dynasty.md` (the *what* and the *why*).
This is the *how*: what gets built in M31, in what order, and what each
phase must prove.

**Scope decision (user, 2026-08-20):** line + succession + **the king's
aura**. The heir-apparent's own buff, royal doctrine postures, new-dynasty
founding, and the chronicler's coronation prose are **out of M31** — the
seams below are shaped so each lands later without disturbing what's built.

**Prerequisites:** the in-flight v2-wire slice lands first, and **M30
(goal-shaped intents, `docs/goal-shaped-intents.md`) lands before this** — the
dynasty must be born goal-shaped, and royal conception is specified there as
the one goal that may never become a standing order. M31 also touches
`CombatRules.EffectivePower`, which `ViewProjector` calls, so it wants a clean
tree.

---

## What the design doc assumed away

Two findings from reading the code; both change the shape of the build.

### 1. Parentage is not stored — the heir is not derivable today

`BirthEvent.Apply` (src/Sim.Core/Population/BirthEvent.cs:44) spawns the
child with `Role`, `OwnerId`, `BornTick` and then **discards**
`occ.ParentAId` / `occ.ParentBId` when it clears `house.Occupation`. No unit
carries a link to its parents. "Eldest living child of the current king" is
therefore not a pure read over current state — it is not computable at all.

**Decision: store both parents on the unit** (`Unit.ParentAId`,
`Unit.ParentBId`, both `int?`, null for genesis units and for anything
spawned outside `BirthEvent` — bandits, boats). Both, not just one: the sim
has no gender, so there is no "father" to privilege, and a full parent pair
is the reusable bone for future kinship, inheritance-of-anything, and
chronicler lineage prose. Cost is two nullable ints per unit in the
snapshot.

The design doc's "no stored heir field" claim survives intact — the *heir*
stays derived. What was wrong was the implicit assumption that the inputs to
that derivation already existed.

### 2. Royalty needs no flag at all

Given stored parentage plus one stored `Player.KingUnitId`, royalty is a
**pure derived predicate**:

```
IsRoyal(u) = u.Id == KingUnitId(u.OwnerId)
          || u.ParentAId == KingUnitId(u.OwnerId)
          || u.ParentBId == KingUnitId(u.OwnerId)
```

This is not a shortcut — it *is* the narrow-line rule, for free. On
succession, `KingUnitId` moves to the new king, and every sibling branch
lapses to commoner in the same instant with no sweep, no per-unit mutation,
and nothing to get out of sync. A stored `IsRoyal` flag would need exactly
that sweep, and would be a second source of truth that could drift from the
line state.

**Consequence for open question #3 (design doc §10):** the *strict* narrow
line (king's own children only) is free; the *gentle* variant (+ the
previous king's surviving children) costs one more stored field,
`Player.PreviousKingUnitId`, and one more clause in the predicate. **M31
builds strict.** The gentle variant stays a one-field addendum if play says
the line feels too thin.

### 3. The existing buff seam is the wrong shape for an aura

`Unit.Buffs` is a **stored** loadout: 2 slots, distinct kinds only
(`BuffRules.MaxBuffsPerUnit`, src/Sim.Core/Combat/BuffRules.cs:12). Granting
the king's aura as a stored `Buff` would (a) consume an equipment slot that
belongs to sword-and-shield, and (b) require grant/revoke churn on every hop
by every unit near the king — a mutation storm for something that is a pure
function of positions.

**Decision: the aura is a pure read**, computed at rollup time inside
`CombatRules`, never stored, never granted. This is the pure-read wall the
architecture doc already mandates for derived quantities. It costs a
signature change on `EffectivePower` (below), which is the real price of
this milestone.

---

## State added

| Where | Field | Notes |
|---|---|---|
| `Unit` | `int? ParentAId`, `int? ParentBId` | `init`-only. Set by `BirthEvent`; null for genesis/bandit/boat units. |
| `Player` | `int? KingUnitId` | Single mutation point: `Royalty.OnRoyalRemoved`. Null = interregnum/extinct. |
| `FactionStartSpec` | `int? KingUnitId` | Which `UnitSpawn.Id` is crowned at genesis. Validated against the spawn list. |
| `RoyaltyConfig` (new, on `GenesisSpec`) | `AuraRadius`, `AuraPowerBonus`, `MajorityAge` | Balance knobs, defaulted; scenarios override. Follows the `CombatConfig`/`PopulationConfig` precedent. |

**Snapshot `FormatVersion` 26 → 27** (src/Sim.Core/Persistence/Snapshot.cs:124):
two nullable ints per unit, one nullable int per player, and the
`RoyaltyConfig` row.

Everything else is derived: royalty, the heir-apparent, minority status,
interregnum, extinction, and the aura itself.

---

## Phase A — the line (state + pure reads)

**Build**

- `Unit.ParentAId` / `ParentBId`; `BirthEvent` records `occ.ParentAId` /
  `occ.ParentBId` on the child.
- `Player.KingUnitId`; `FactionStartSpec.KingUnitId` + `Genesis` validation
  (must name a spawn in this faction's list; the bandit faction may never
  have one).
- New `src/Sim.Core/Royalty/Royalty.cs` — a **pure-read wall**, mirroring
  `Population`'s discipline (`AgeYears`/`CanBreed` are reads; the single
  mutator lives at one named site):
  - `bool IsRoyal(GameWorld, Unit)`
  - `Unit? King(GameWorld, int ownerId)` — null when interregnum/extinct
  - `Unit? HeirApparent(GameWorld, int ownerId)` — eldest living child of
    the current king; **eldest = lowest `BornTick`, ties broken by lowest
    `Id`**, matching the canonical ordering `StarvationDeathEvent` already
    uses (src/Sim.Core/Food/StarvationDeathEvent.cs:106)
  - `bool IsMinor(GameWorld, Unit, long now)` — `AgeYears < MajorityAge`
- `Snapshot` v27 read/write.

**Prove**

- Heir derivation is **pure and order-independent**: the same world built by
  inserting children in different orders yields the same heir.
- Heir skips the dead: kill the eldest, next eldest is heir with no
  intervening call.
- Narrow line across three generations: on succession, the previous king's
  other children stop being royal; the new king's children start.
- Snapshot v27 round-trips parentage, `KingUnitId`, and config; a v26
  snapshot is rejected with the existing version message.
- Genesis validation: a `KingUnitId` naming a foreign or absent spawn
  throws; the bandit faction cannot be given one.

**Explicitly not in Phase A:** no combat change, no succession. The world
merely *knows* who is king and who would inherit.

---

## Phase B — succession, minority, extinction

**Build**

- `Royalty.OnRoyalRemoved(Simulation, Unit)` — the **single mutation point**
  for `Player.KingUnitId` — called from `Population.OnUnitRemoved`
  (src/Sim.Core/Population/Population.cs:116), which every death path
  already converges on: `DeathByAgeEvent`, `StarvationDeathEvent`, and
  combat death. No new death plumbing; that convergence is why the design
  doc's "thin sim cost" holds for this phase.
- Behaviour: if the removed unit is not the king, no-op (a dead royal child
  simply stops being a candidate — the heir is derived, so there is nothing
  to update). If it *is* the king, `KingUnitId = HeirApparent(...)?.Id`,
  which is null on extinction.

**Ordering hazard to pin with a test:** succession must be resolved with a
clear rule about whether the dying king is still in `world.Units`, and the
heir derivation must never see the dead king as a living parent. Fix the
order explicitly (derive from the king's children, reassign, then let the
caller complete the removal) and assert it. A test that kills king and heir
in the same tick pins the sequence.

**Prove**

- Succession on old-age death, on starvation death, and on combat death —
  all three produce the same crowning.
- **Twin-run determinism** across king deaths of both kinds (the standard
  twin-run harness), including a run where succession and a birth land on
  the same tick.
- Minority: an heir crowned under `MajorityAge` is king and `IsMinor`; the
  double-crisis case (child king with no children ⇒ no heir at all) falls
  out with no extra rule and is asserted as such.
- Extinction: last royal dies ⇒ `KingUnitId` null, realm persists, all other
  systems (food, combat, automation) unaffected.
- One Stop Rule interaction: king dies mid-gestation ⇒ the existing
  stop-on-removal path clears `House.Occupation` and no birth fires. This is
  existing behaviour; the test guards against M31 regressing it.
- Snapshot round-trip **through** an interregnum and through extinction.

---

## Phase C — the king's aura

The milestone's only invasive change.

**Build**

- `CombatRules.EffectivePower(Unit, long now)` gains world context. Two
  options; **take the overload, not the mutation**:
  - `EffectivePower(GameWorld world, Unit u, long now)` becomes the
    aura-aware rollup; the existing two-arg form stays as a
    catalog-plus-buffs rollup for the pure stat tests (`CombatStatsTests`,
    `EquipUnitTests` — 8 call sites that legitimately mean "this unit's own
    power").
  - Production call sites that must include the aura, all four:
    `CombatRoundEvent.cs:84`, `FortSiege.cs:85`, `ViewProjector.cs:545`,
    `ViewProjector.cs:586`.
- Aura rule: a unit gains `AuraPowerBonus` if a **living, non-minor king of
  its own owner** is within `AuraRadius`, measured as an integer **Euclidean
  disc** (`dx*dx + dy*dy <= r*r`) — matching `Sight.Reveal`'s shape
  (src/Sim.Core/Vision/Sight.cs:88) so "radius" means one thing across the
  codebase. Integer math only; no floats anywhere near the sim.
- Cost is O(1) per unit: `Player.KingUnitId` is a direct lookup, not a scan.
  This is precisely why `KingUnitId` is stored rather than derived — a
  derived king would make the per-unit aura check O(N) and the combat rollup
  O(N²).
- An embarked king (`IsEmbarked`) projects nothing — he is off the tile grid
  and invisible to combat, and the aura must not leak through the hull.
- A **minor** king projects nothing (Phase B's minority window, made
  mechanical here). Interregnum projects nothing because there is no king.

**Prove**

- Aura on/off by distance: a unit at `r` gains it, at `r+1` does not; the
  disc shape is asserted diagonally, not just on the axes.
- Owner-scoped: an enemy king in range buffs nobody of yours.
- Minor king, embarked king, and interregnum each project zero — three
  separate tests, since three different rules produce the same zero.
- `ForcePower` and the siege rollup both reflect the aura (the two rollups
  that decide fights).
- Determinism: twin-run of a battle fought inside the aura.
- Wire: `ViewDto` unit `Power` includes the aura for own units, so the
  client shows what the sim will actually compute.

---

## Phase D — balance lab: the temptation zone

`AuraRadius` and `AuraPowerBonus` are knobs, and per the biome-degradation
lesson (`docs/biome-degradation.md`) **lab tests must be config-derived,
never hard-coded to a tuned value.**

- **Temptation-zone sweep:** identical armies, king present vs. absent,
  across the knob range. Assert the *shape* — with-king wins strictly more
  often than without, and strictly less often than always. "Tempting always,
  mandatory never" is an inequality on win rates, not a magic number, and
  that inequality is what the test pins.
- **Opening safety:** a genesis king behind his castle cannot be reached by
  the bandit spawn-grace window (`73b7ca1`) before defences can function.
- **Dynastic bad-luck floor:** twin-run cohorts across lifespan seeds — a
  short-rolled first reign must produce a rocky opening, never an
  unrecoverable one (assert the realm is still economically alive at N
  years, not that it is comfortable).

---

## Out of scope (and the seam each leaves)

| Deferred | Seam it lands on |
|---|---|
| Heir-apparent's own buff | `Royalty.HeirApparent` exists after Phase A; the buff is one more clause in the Phase C rollup. |
| Royal doctrine posture ("king stays behind walls") | A standing order in the automation substrate — deliberately deferred while that substrate is mid-pivot (`docs/automation-as-core-game.md`). |
| New-dynasty founding | `KingUnitId` is nullable and extinction is already a stable state; founding is an intent that sets it. |
| Chronicler prose | Succession is a single mutation point; a narration hook attaches there when the layer exists. |
| Gentle narrow-line variant | One field (`Player.PreviousKingUnitId`) + one clause in `IsRoyal`. |
| Heir visibility through fog (design doc Q6) | A projector decision; `ViewProjector` filters royal markers once the question is settled. |

---

## Risk register

- **`EffectivePower` signature change** is the blast radius of this
  milestone: 4 production call sites, 8 test call sites. Keeping the two-arg
  overload for own-power keeps the test churn honest — a test whose meaning
  changes should change, one whose meaning doesn't shouldn't.
- **Snapshot v27** must land with the milestone, not after; every scenario
  and saved world regenerates.
- **Genesis kings for AI factions.** Every non-bandit faction needs a king
  from tick 0 — AI players and rivals included — or the aura silently
  favours whoever has one. Genesis validation should make a kingless player
  faction an explicit choice, not an accident.
