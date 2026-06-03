# M7 — Combat (COMPLETE — 2026-06-03)

## Where we are

**M7 done end-to-end.** Force-vs-force combat triggers on arrival when
`Diplomacy.AreHostile(a, b)` (M6) holds for any pair of owners on the
tile. Resolution is multi-round, linear-proportional, simultaneous, and
zero-variance. Per-unit Health (`BaseHealth = 10` for every civilian
role today) makes attrition gradual and gives a single seam for future
training, armor, and equipment via `Unit.Buffs`. Casualty distribution
is lowest-Health-first, tiebreak lowest-Id — deterministic and
observable. Capture-on-death drops cargo to `world.GroundResources`;
`HaulPickupEvent` falls back to ground piles. An in-progress combat
survives snapshot + crash recovery via the same M4 anchor pattern as
build/production/war (per-tile anchor on `world.CombatStates`).

The next big system is **spawning** — the paired birth-rate decision to
combat's death-rate. Separate milestone, separate deliberation.

## The headline contracts — proven

```
1. AreHostile(a, b) co-location on arrival ⇒ combat starts
2. Bigger force wins predictably; smaller side takes more damage per round
3. Reinforcement and retreat fall out of per-round re-gathering
4. Snapshot.Hash(uninterruptedBattle) == Snapshot.Hash(midFightSnapshotAndRestore)
5. Snapshot.Hash(uninterruptedBattle) == Snapshot.Hash(crashAndRecoverBattle)  ← THE CLOSURE GATE
6. Cargo on a dying laden unit drops to the tile; other haulers can pick it up
```

- `CombatResolutionTests.BiggerForceWinsPredictably`
- `CombatResolutionTests.Reinforcement_MidFight_ChangesOutcome`
- `CombatResolutionTests.Retreat_MidFight_StopsParticipation`
- `CombatResolutionTests.MidFight_SnapshotRoundTrip_Identical` ← M4 regen for combat
- `RecoveryTests.MidFightCombat_RecoveryResolvesIdentically` ← the crash-recovery gate
- `CombatCaptureTests.LadenUnitDies_CargoDropsToTile`
- `CombatCaptureTests.HaulFromGround_PicksUpLooseCargo`

## What landed

**Phase A — Combat unit state + power rollup.**
- `Unit.Health` (mutable instance state) + `Unit.Buffs: List<Buff>`
  (scaffolding for armor/training/equipment).
- `UnitCombatCatalog` mirrors `StructureCatalog`: role → spec
  (`BaseHealth = 10`, `BasePower = 1` for every civilian role).
- `CombatRules.EffectivePower(unit)` reads catalog + sums buff
  modifiers — the rollup is buff-aware from day one.
- `GameWorld.AddUnit` auto-fills Health from catalog when the unit is
  inserted with default Health 0 (existing test/Genesis call sites are
  unchanged; restored damaged units bypass auto-init).
- Snapshot `FormatVersion` bumped to 4; per-unit Health + Buffs
  serialized.

**Phase B — Trigger.**
- `CombatTrigger.MaybeBeginCombatOnTile` hooks into
  `MoveArrivalEvent.Apply` and `GroupArrivalEvent.Apply` after position
  and sight updates. Fence: already-contested tile is a no-op
  (reinforcement falls into the next round's re-gather).

**Phase C — Resolution.**
- `CombatRoundEvent` self-reschedules per-tile. Each round re-gathers
  forces, computes per-owner damage from start-of-round power, applies
  lowest-Health-first casualties, and ends when no hostile pair
  remains.
- Per-tile anchor on `world.CombatStates`
  (`{ Tile, NextRoundTick, NextRoundSeq, RoundNumber }`).
- `RegenerateQueue.From` reconstructs `CombatRoundEvent` from the
  anchor on snapshot restore — same M4 discipline.

**Phase D — Death consequences.**
- `CombatRules.OnUnitDeath` drops cargo (Phase E hook), removes from
  group (attrition-disband if empty), clears in-flight obligations,
  removes from `world.Units`.
- Pending `MoveArrival` / `HaulPickup` / `HaulDeposit` events fence
  cleanly via `TryGetValue` — the M2 landmine is closed.

**Phase E — Capture economy.**
- `world.GroundResources: Dictionary<TileCoord,
  SortedDictionary<Resource, int>>` — sparse, tile-keyed; canonical
  serialization (y, x, then Resource enum order).
- `HaulPickupEvent` falls back to ground piles when no structure
  source exists.
- `HaulIntent.Resolve` accepts source = structure OR ground pile.

**Phase F — View, host, server, docs.**
- `UnitView` extended with `Health`.
- `PlayerView.OngoingCombats: IReadOnlyList<CombatView>` — only
  surfaces combats on tiles in the viewer's `Visible` set (fog
  applies).
- `docs/combat-model.md` decision doc capturing force-vs-force,
  linear-proportional resolution, lowest-Health-first casualties,
  multi-round-with-reinforcement, capture-on-death, the per-unit-Health
  seam, and rejected alternatives.
- `docs/architecture.md` §8 — M7 row added.

## Test counts

- Sim.Tests: **277 passing** (+32 new M7 tests).
- Sim.Persistence.Tests: **27 passing** (+1 mid-fight crash recovery).
- Total: **304 / 304 green**.

## Carried debts updated

- **Mid-haul cargo on unit death** — CLOSED by Phase E
  (capture-on-death drops cargo to the tile).
- **Spawning / population** — opens. Paired birth-rate to combat's
  death-rate. Next milestone.
- **Emergent ford** (water earning road condition) — still deferred.
- **Sieges / structures as combatants** — next combat extension.
- **Targeted `AttackIntent` + mobs** — depend on the AI/intent-generation
  layer.
- **Ranged-from-adjacent** — seam in (`GatherForcesOnTile`); zero
  round-event change when it lands.
- **Equipment + buffs (instances)** — seam in (`Unit.Buffs`); empty list
  today.
