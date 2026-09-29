# M38 status: scouting secrets (Lodge scouts, charts, idols)

**Decision doc:** `docs/scouting-secrets.md`. Built 2026-09-23.
**Rules:** no commits unless asked; each phase ends with its tests green and the full
suite no worse than the baseline (M37 end: 1272 + 53); numbers are config knobs and tests
derive from them.

## User decisions (2026-09-23)

- The Lodge can be built at any time for now; when it should arrive is a later
  progression call.
- Only a scout **on a mission** charts; the marker is the **exact tile**; findings reach
  the chart only if the scout **returns alive**.
- First secrets: caches, rumoured caches, idols.
- The AI keeps its starting scouts.
- Idols are **single use, then crumble**; the circle lands on **unexplored land**;
  **20 idols per map**.
- A charted secret the player sees is gone is **struck through**.
- Art: idols use `SM_Gen_Prop_Statue_05`, caches `SM_Gen_Prop_Chest_01`, both through the
  tile dressing system.

## Phases

| Phase | Scope | Status |
|---|---|---|
| A | Scouts from the Lodge: `RoleTrainerCatalog` Scout → Lodge; a human seat (the `progression` switch) starts with two untrained citizens in place of its scouts (same headcount); AI factions and AI-driven labs keep theirs; the AI's train rung asks for a scout only if it owns a Lodge, and trains it there | done (`LodgeScoutTests` 3; two old School-scout tests retargeted to Miner) |
| B | Secrets + charts: `SecretHint` (Glint, StoneFigure) by structure kind; `Charts.Deliver` walks the mission log at `Returned` (known as of the last leg that saw it, gone if a later leg swept its tile empty); `Charts.OnSight` from `Sight.AfterReveal` (refresh or strike), `Charts.OnSecretGone` when a cache is emptied or an idol crumbles in the owner's sight; `View.Sees`; `TileOrder`; the rumour circle's offset drawn from the sim RNG; snapshot v38 | done (`ChartTests` 7) |
| C | Idols: `StructureKind.Idol` (22), `Idol` + `IdolKind` (Lesser 2 days r6, Greater 5 days r10; every 4th is Greater), `IdolConfig` (genesis-set, snapshotted), `IdolScatter` after the caches (fog-only, 12 from castles, 10 apart), `ActivateIdolIntent` (standing, or walk there as `GoalKind.ActivateIdol`), `VisionGrant` in `View.VisibleTiles`, `View.Sees`, `BanditRules.IsSeenByAnyPlayer`, expiry event, circle on unexplored land by sim RNG, the idol crumbles; `--idols N` (host 20, bare options 0) | done (`IdolTests` 8, IntentJson client payloads pinned) |
| D1 | Wire: `ViewDto.Chart` (tile, hint, state, seen/gone ticks) and `ViewDto.VisionGrants` (centre, radius, ticks left), owner-only; idols are ordinary structures in sight; no secret kind or contents in the chart | done (`SecretsWireTests` 3) |
| D2 | Prod client: `SecretsDto.cs`; `ScoutPlanner` (patrol template: waypoints, return rule toggle, Enter/Backspace/right-click/Esc) + wheel verbs + numbered waypoint chips + pill; "Wake the idol" verb (walks there); idol bubble row; chart diamonds on the map (stone figure taller, struck = faded + crossed) and idol circles (pale blue ring); HUD toasts (scout home with N finds, a find gone, an idol woken); Scout trains at the Lodge in the client's trainer map; `KindIdol`, `GoalActivateIdol`; **Cache and Idol tile recipes** added to Set Up Tile Recipes | built, `_AowTypeCheck` + `_AowWireBoundary` clean, **not run in Play**. Editor step owed: re-run **Window > Aow > Set Up Tile Recipes** |

## Baseline

- Before M38: 1272 + 53.
- After Phase A: 1275 + 53. After B: 1282 + 53. After C + D1: 1293 + 54 (final run below).

## Notes from the build

- **Record-struct defaults trap.** `new IdolConfig()` on a record struct whose primary
  constructor has optional parameters is all zeros, not the defaults. IdolConfig now has
  an explicit parameterless constructor (the RoyaltyConfig pattern). `CacheConfig` has
  the same shape; harmless today because its only `new()` use is the Count-0 case.
- **The chart is born only at `Returned`.** A scout killed before it gets home delivers
  nothing, with no special case.
- **Seen through an idol is seen, not charted.** Idol sight refreshes or strikes existing
  chart entries like any sight, but never adds one.
- The human seat's scout slots are **untrained citizens**, not removed, so the opening's
  food balance is unchanged.

## Open

- When the Lodge becomes available (a progression Unlock row later).
- A report panel for the M20 prose; the chart and toasts carry the facts meanwhile.
- The fog-edge chart view: markers are on the map texture only; the world UI shows
  nothing for a charted tile out of sight.

## Fixes after the first playtest (2026-09-23)

- **Host crash when a scout came home having seen a cache or idol.** The M20 report
  compiler named the owner of every sighted structure; the sentinel owner -2 indexed the
  house-name table with a negative number (`Lexicon.FactionName`). Sentinels are now "no
  one's", and secrets are described as faintly as the chart does ("something glinting",
  "figure of stone"). Regressions: `ScoutLexiconTests`,
  `ChartTests.TheReport_OnAMissionThatSawACache_Compiles`.
- **Caches and idols showed as red marks on the map.** The map painted every visible
  structure in a relation colour, and sentinels read as foes. The map now never paints a
  secret (`SimVocabulary.IsSecret`); the only mark is a scout's chart diamond. The
  client's per-tile owner grid skips them too, so the ground under a cache is no longer
  tinted as someone's land.
- Suite after the fixes: 1297 + 54.
