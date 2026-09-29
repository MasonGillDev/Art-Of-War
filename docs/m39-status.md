# M39 status: bandit camps

**Decision doc:** `docs/bandit-camps.md`. Started 2026-09-24.
**Rules:** no commits unless asked; each phase ends with its tests green and the full
suite no worse than the baseline; numbers are config knobs and tests derive from them.

## Also landed on 2026-09-24, before the camps

- **Archers fight behind the line** (`docs/combat-model.md`, update 2026-09-24):
  `UnitCombatSpec.Ranged`, and the round's damage order is (rank, health, id).
  `ArcherLineTests` 5.
- **The Lodge unlocks at "A good home"** (`docs/progression.md`, update 2026-09-24): the
  `Unlock` effect, `Progression.IsLocked`, `ViewDto.LockedKinds`, the client's build
  lists and toast. `LodgeScoutTests` +4.
- **The milestone ledger:** `docs/progression-milestones.md`.
- Suite after both: 1306 + 54.

## Phases

| Phase | Scope | Status |
|---|---|---|
| A | Sim: the `BanditCamp` structure, `CampConfig`, `Camps` (raise, daily tick: recruit + muster, prune, raze credit), camp omen + `CampRumour` effect, rows 5–6, `CampRazed` stat, chart hint Smoke, snapshot v39, bandit-pressure fix | done 2026-09-24 |
| B | Driver: camp-bound bandits re-derived from the sim every think (never kept in a party); raiders steal what they see, carry loot home and unload into the hoard, march on the target's seat when empty; survivors of a razed camp become ordinary raiders | done 2026-09-24 |
| C | Wire + prod client: camp omen (bearing, search circle, days to the first raid; never the tile), camp health public in sight (a siege); client vocabulary, HUD line + toasts, a red search circle and red chart diamond, the bubble's "how to burn it" row, the **BanditCamp tile recipe** (campfire, awnings, chest + crates, barrels, logs, torches, axes) | done 2026-09-24, `_AowTypeCheck` + `_AowWireBoundary` clean, **not run in Play**. Editor step owed: re-run **Window > Aow > Set Up Tile Recipes** |

Tests: `CampTests` 11 (placement, recruiting, mustering, a full raid played by the driver,
razing with its credit and captives, the pressure fix, the wire, snapshot mid-raid).

## Notes from the build

- **The cap is the camp's whole strength** (garrison plus riders still out). The first cut
  counted only the garrison, so the camp recruited while a raid was out and swelled past
  6 when it came home (caught by the raid test).
- **"The raid has left" is durable** (`BanditCamp.RaidDeparted`, set when a raider
  steps off the camp in `MoveArrivalEvent`). Without it, a raider home empty-handed looks
  exactly like one mustered minutes ago, and the driver would send it straight back out.
- **Traced end to end:** 3 raiders ride 20 tiles to an unguarded castle, take 15 each,
  walk home, unload (hoard 50 → 95), stand down at the next daily tick, and ride again a
  week later (→ 140).
- **Bandit pressure** now counts only real factions' structures (owner ≥ 0).

## Baseline

- Before M39 (with the archer and Lodge changes): 1306 + 54.
- After phases A–B: 1316 + 54.
- After phase C (final, 2026-09-24): 1317 + 54, 0 failed.
