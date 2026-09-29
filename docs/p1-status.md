# P1 status — declare what is already sent

**Package:** P1 from `docs/prod-client-gap-discovery.md` §6. Started 2026-09-20.
**Scope frozen 2026-09-20:** remaining work is T6, T10, T9 and the Tile Marks editor step. T3 and T8 are deferred out; nothing else gets added to P1.
**Rule:** no server changes, with one exception (T10, an additive genesis constant). Everything in P1 is already on the v2 wire; the work is
client-side declaration, state, HUD and world rendering. The old debug client is untouched.

## Ownership (one seam per agent)

| Agent | Owns | P1 tasks |
|---|---|---|
| Client contract | `Assets/Scripts/Wire`, `Net`, `Client/KnownWorld.cs`, `Client/Session.cs` | T1, T2 |
| World renderer | `Client/Presentation`, `Client/World`, `Client/Dressing` | T3, T4, T5 |
| UI/UX | `Client/Hud`, `Client/WorldUi`, `Client/Input`, `Client/Automation` | T6, T7, T8, T8b |
| Verification | type-checks, live run | T9 |
| Wire projector | `src/Sim.Server/Wire`, `ViewProjector.cs`, `tests/Sim.Tests/WireV2Tests.cs` | T10 |

Field names on the client DTOs are camelCase and must match the server's
`src/Sim.Server/Wire/WireDtos.cs` exactly (JsonUtility, no naming policy).

## Tasks

| # | Task | Owner | Status |
|---|---|---|---|
| T1 | Declare `scoutReports` (`ScoutReportDto`, `ScoutClaimDto`) and `graves` (`GraveDto`) on `Wire/ViewDto.cs`; mirror server shapes at `WireDtos.cs:118-124, 182-206`. Remove the "deliberately not declared" note. | contract | done 2026-09-20 |
| T2 | Expand `combats`, `graves`, `piles`, `scoutReports` into `KnownWorld` (per-tile lookup for combats/graves/piles; report list keyed by id). Keep `KnownWorld` the single read source. | contract | done 2026-09-20 |
| T3 | Render combats on tile: a licensed marker driven only by `CombatDto` (round number may drive intensity). No invented casualties. | renderer | **deferred** 2026-09-20 to the combat client milestone (user call). The combats coming-soon row stays. |
| T4 | Render piles: a small prop per tile from `PileDto.holdings` (candidate: `SM_Gen_Prop_Crate_*`, `rpgpp_lt_sack_*`). Visible-only, so it disappears with fog, which is correct. Same pass: swap the raw `view.piles` scan at `Presentation/SelectionRenderer.cs:281` to `KnownWorld.Piles` / `PileAt` (single-read-source rule). | renderer | done 2026-09-20 |
| T5 | Render graves: `SM_Prop_Grave_03` (unused today) at grave tiles; retire when the DTO row goes. | renderer | done 2026-09-20 |
| T6 | Coming-soon rows for the three silent gaps until T3–T5 land: combats, graves, walls/gates (`Hud/Dock.cs` beside the existing rows). Remove each row when its renderer ships. | UI | done 2026-09-20 — on the Build page, under the builder toggle; Graves row removed the same day since T5 shipped |
| T7 | Surface received-but-unused fields in existing panels: `buildEtaTicks` ("done in N days"), `foodPeriodTicks` (rate unit on the realm strip), `claimFertility` (grade on claim outline tooltip or card), `ProposalDto.expiryTick`, `OrderDto.currentStep` on the Machine page, `BuildOptionDto.buildDurationTicks` in the build menu, `minTrainAge` in `OrderIssuer.CanTrain` and the train tooltip, `gestationTicks` on the breeding panel. | UI | in progress — world UI first (see docs/world-ui.md, update 2026-09-20): `buildDurationTicks` (build ring caption), `minTrainAge` (`OrderIssuer.TrainObstacle`, wheel + HUD), `gestationTicks` (family caption) done 2026-09-20; `buildEtaTicks` (site bar caption) and `foodPeriodTicks` (castle bubble = realm) done 2026-09-20; `claimFertility` done 2026-09-20 (`WorldUi/SoilGrade.cs` grades; the claim-outline tint in `SelectionRenderer.DrawSelection` was a 6-line edit in the renderer seam, flagged to that owner); `expiryTick` deferred to the admin screen; `currentStep` skipped pending the automation redesign |
| T8 | Scout reports page: replace the coming-soon row at `Hud/Dock.cs:482` with the report list from T2. Observation log only; no prose narration yet (that is P9). | UI | **deferred** 2026-09-20 to P9 (user call). The scout reports coming-soon row stays; the KnownWorld API and vocabulary from T2 are ready when it resumes. |
| T8b | Fold `Input/TileIndex.cs`'s private `_pileByTile` / `PileAt` into `KnownWorld.PileAt` and forward callers; swap the raw `view.piles` scan at `WorldUi/HaulMode.cs:159` to `KnownWorld.Piles` / `PileAt`. Single-read-source rule. | UI | done 2026-09-20 (verified: no pile lookup left in `TileIndex`; `KnownWorld` is the only raw `view.piles` reader) |
| T9 | After each merge: `_AowTypeCheck.csproj` and `_AowWireBoundary.csproj` build clean; run the server with `--refining` and one death with loot, confirm each new field (graves, piles, fertility rules) is non-empty on `GET /v2/view/1`. | verification | first pass 2026-09-20, see Verified; `scoutReports` non-empty needs a Lodge (60 wood; default castle has 20), covered by the `--scouting` host demo instead |
| T10 | Put the fertility thresholds on genesis so `claimFertility` can be graded: add a `FertilityRulesDto { ForestThreshold, DesertThreshold }` block to `WorldDto` (beside `Population`/`Royalty`/`LightCycle`, filled in `ViewProjector.BuildWorldDto` from `BiomeDegradationConfig`, defaults 7500/2500 at `BiomeDegradationConfig.cs:85-86`). Additive on v2; one `WireV2Tests` case asserting the values match the config. Client contract agent then mirrors it on `Wire/WorldDto.cs`. Unblocks the fertility gradient (renderer seam, left over from T7). First P11-style genesis constant; the rest stay in P11. | projector | done 2026-09-20 — `FertilityRulesDto` as `WorldDto.Fertility` (`WireV2.cs`), filled from `GameWorld.BiomeDegradationConfig` via a third optional `BuildWorldDto` parameter (defaults = config defaults, so existing callers ship 7500/2500); `WireV2Tests.FertilityRules_AreTheWorldsOwnConfig` green (25/25 in the class). **Contract seam:** mirror `fertility { forestThreshold, desertThreshold }` on `Wire/WorldDto.cs`. |

## Verified

- 2026-09-20 23:05 (coordinator close-out check):
  - `WireV2Tests`: 25/25 pass incl. `FertilityRules_AreTheWorldsOwnConfig`. T10 done in code
    on both sides.
  - Live server on :8080 (PID 205956, tick 87945) is a **stale binary**: `GET /v2/world` has no
    `fertility` block. T10 is unverified live until that server is restarted from a fresh build.
  - **Tile Marks editor step is NOT complete.** `Settings/Dressing/TileMarks.asset` exists
    (19:00) but `MainScene.unity` was last saved 2026-09-18 13:14 and contains neither the
    `TileMarks` component nor the asset guid. Either the menu ran without MainScene open, or
    the scene was not saved. At runtime `GameBootstrap` adds the component but `Marks` is null,
    so the layer logs "no TileMarkSet assigned" and draws nothing. Redo: open MainScene, run
    Window > Aow > Set Up Tile Marks, then save the scene (Ctrl+S).
  - T6 rows present at `Hud/Dock.cs:354-356`; the **Graves** row contradicts T5 (graves render)
    and should be removed by the UI agent.

- 2026-09-20 19:15 (verification, T9 first pass): every "done" row above is in code.
  - `dotnet test` Sim.Tests: 1120 passed, 0 failed (3 m 42 s). `WireV2Tests` 25/25 including
    the new T10 case.
  - `_AowWireBoundary.csproj` 0 errors / 0 warnings; `_AowTypeCheck.csproj` 0 errors, the 3
    pre-existing MSB3245 warnings.
  - Live server (`Sim.Server --port 8099`, default world): `GET /v2/view/1` carries `combats`,
    `graves`, `piles`, `scoutReports` as arrays (all empty in a fresh world); `foodPeriodTicks`
    = 360 beside `foodPerPeriod`; every structure carries `buildEtaTicks`; `GET /v2/world`
    `population.minTrainAge` = 6, `gestationTicks` = 3240; all 18 `buildable` entries carry
    `buildDurationTicks`. `claimFertility` is filled only for completed extractors, so a fresh
    world's farm SITES show `[]` (projector line ~908) — expected.
  - `DispatchScoutIntent` posted for own scout 27 was accepted then rejected at resolution
    ("no intelligence Lodge built") — correct. A non-empty `scoutReports` on the live view
    needs a Lodge (60 wood, castle stock 20), so the report pipeline was exercised by
    `Sim.Host --scouting` instead: claims + prose produced, twin run identical, log
    round-trips. Wire fill-in of the report list still unproven live; re-run once a Lodge
    exists in a play-through.
  - T10 live: the server binary I ran predates the T10 edit, so `world.fertility` was absent
    on the wire in this pass; the unit test covers it. Re-check on the next live run.
  - Doc drift fixed this pass: T8b row said "todo" while the handoff said done.
  - Still not run in Play: TileMarks (editor step owed). `Hud/Dock.cs:481` still shows the
    "Scout reports" coming-soon row with the now-false text "the view does not carry them yet"
    (T8 open). No combats / walls-gates coming-soon rows exist yet (T6 open).

- 2026-09-20 18:58 (coordinator): after all three seams' edits, `_AowTypeCheck.csproj` and
  `_AowWireBoundary.csproj` both build with 0 errors. T8b confirmed done by grep. T6, T8, T9,
  T10 not started.

- 2026-09-20 (contract): T1 + T2 landed. `_AowWireBoundary.csproj` and `_AowTypeCheck.csproj`
  both build with 0 errors (the three MSB3245 reference warnings on the type-check are
  pre-existing). Not yet live-verified against a running server (T9).

- 2026-09-20 (renderer): T4 + T5 landed as `Client/Presentation/TileMarks.cs` + `TileMarkSet.cs`
  and `Editor/SetupTileMarks.cs`; `GameBootstrap` adds the component at connect and rebuilds it
  on tick; `SelectionRenderer.DrawHaulCandidates` now reads `KnownWorld.Piles`. Both
  `_AowTypeCheck.csproj` and `_AowWireBoundary.csproj` build with 0 errors. Not run in Play.
  Engine doc: `Art Of War(prod)/docs/tile-marks.md`.

## Handoffs from the renderer seam

- **Editor step owed (user):** run `Window > Aow > Set Up Tile Marks` with MainScene open, then
  save the scene. It creates `Settings/Dressing/TileMarks.asset`, wires `SM_Prop_Grave_03`,
  `SM_Gen_Prop_Sack_01`, `SM_Gen_Prop_Crate_01`, and adds/assigns the `TileMarks` component on
  the GameBootstrap object. Without it the layer logs one warning and draws nothing.
- **UI seam:** T6 now means: add the **combats** row (T3 deferred) and the **walls/gates** row (P2).
  No graves row (they render). The scout reports row stays untouched (T8 deferred).
- **Grave semantics, confirmed from `GraveTracker`:** a grave row exists exactly while the loot
  a witnessed death dropped is still on its tile. The client draws a headstone per row and
  cross-checks nothing. Pile rows are visible-only, so a remembered tile shows the headstone
  without sacks — intended.
- **Still raw in `SelectionRenderer.DrawHaulCandidates`:** `view.structures` is scanned
  directly (two lines above the old piles scan). Not a P1 row; `KnownWorld` has no structure
  list API yet, so it stays until one exists.

- 2026-09-20 (contract): T10 client mirror landed — `fertility { forestThreshold, desertThreshold }`
  on `Wire/WorldDto.cs`, null-tolerant for a server that predates the block. Both type-check
  projects 0 errors. Not live-verified (T9).

## Handoffs from the contract seam

- **Fertility grade (UI seam, T7 leftover):** read `KnownWorld.Genesis.fertility` and grade
  `StructDto.claimFertility` against the two thresholds. Guard for a null block.

- **KnownWorld read API (T2):** `CombatAt/GraveAt/PileAt(x, y)` per-tile, `Combats/Graves/Piles`
  lists, `ScoutReports` (oldest first by id) and `ScoutReport(id)`. Renderers (T3–T5) and the
  reports page (T8) read these; nothing should touch `View.combats` etc. directly.
- **Vocabulary for T8:** `Wire/ViewDto.cs` now carries `ReportStatus`, `ClaimKind` and
  `ClaimCertainty` constant classes with `Name()` helpers, mirroring the server enums.
- **Duplicate to fold (UI seam):** done 2026-09-20. `TileIndex` indexes units and structures
  only; `HaulPlanner`, `OrderIssuer` and the world-UI bubble read `KnownWorld.PileAt`, and
  `HaulMode` lists `KnownWorld.Piles`.
- **Direct `view.piles` reader:** migrated 2026-09-20 (renderer seam).
- **Shared duration text (UI seam):** `Client/Hud/Days.cs` — `Days.Of / Ago / In` — is the one
  formatter for "how long"; the realm strip, the Machine page status and the war countdown use
  it. T7's ETA/expiry/duration rows should too.

## Deferred out of P1

Wall/gate rendering (P2), health and buffs on figures (P3), farm plots and district
plots (P4), anything that needs a new wire field.
