# M44 Status — Stone from Hills, Ore from Mountain Veins

Spec: `docs/m44-stone-and-ore-spec.md`. Decision: `docs/stone-and-ore-land.md`.
Audit: `docs/determinism-audit.md` § M44 addendum.

## Decisions taken at implementation (2026-10-01)

- **Vein density:** `VeinConfig.OneIn = 3`, plus one vein per vein-less
  mountain range. The user chose this over start-placement rules and
  raising mountains near starts. The spec's four-layer plan was dropped.
- **Open questions, resolved by default after the user said "get started":**
  1. Only Miners survey.
  2. A found vein counts the moment the dig ends (no walk home).
  3. v44 saves are rejected (exact-version rule).
  4. The Quarry claims 6 Hills tiles within range 2.

## Built

| Phase | What | Pinned by |
|---|---|---|
| 1 | `Biomes.Resource` swapped (Hills→Stone, Mountain→Ore). Quarry moved to Hills with a 6-tile claim and no wear. Mine moved to Mountain. FortifyRung finds its quarry spot with the hill pocket search. Stale claim-count comments fixed. | `StoneAndOreTests` (7) |
| 2 | `Mining/` folder: `VeinConfig`, `Veins.Seed` (draws no Rng), `GameWorld.Veins` / `KnownVeins` / `SurveyedBarren`. Snapshot v45. `WorldFactory` sets the vein seed and logs each start's distance to the nearest mountain. | `MiningSurveyTests.Genesis_*` |
| 3 | `SurveyIntent` / `SurveyRules` / `SurveyCompleteEvent` / `SurveyReportEvent`. `Unit.Survey` and its anchor. Cancel on retask and on a halted walk. Combat resume. Queue regeneration. Snapshot rows. `IntentJson`. | `MiningSurveyTests.Survey_*`, `Snapshot_MidDig_*` |
| 4 | `StructureSpec.RequiresVein` (Mine) and the `PlaceSiteIntent` gate. Seeing a mine teaches its vein (`Sight.AfterReveal`, `OnMineRaised`). | `Mine_OnlyOnAKnownVein`, `SeeingAMine_TeachesItsVein_ForGood` |
| 5 | AI: ForgeRung goes known vein → mine, else survey, else `OreStarved` and TrainRung schools one Miner. Miners are kept out of routine Farmer retraining. | `ArmTests.Forge_SurveysForAVein_ThenPlacesTheMineOnIt` |
| 6 | Wire: `ViewDto.Veins` / `BarrenX` / `BarrenY`, `UnitDto.Survey*`, `BuildOptionDto.RequiresVein`, survey errand text, survey notices. Quarries send no `ClaimFertility`. Prod client: wire mirrors, `IntentFactory.Survey`, `KnownWorld.VeinAt` / `IsBarren`, the wheel's Survey verb, Mine placement `Fit.NoVein`, map marks. | `View_CarriesOnlyTheViewersOwnVeins_AndTheMineNeedsOne` |

## Verification

- **Server tests:** full suite green, 1595 of 1595 (2026-10-01). The two raid lab tests that were red at baseline pass now.
- **Client:** `_AowWireBoundary` and `_AowTypeCheck` build with 0 errors.
  **Not run in Play.**
- **Host smoke:** `Sim.Server` on 252×252 starts, generates 16 factions,
  and logs each start's nearest mountain. On the default seed that ranges
  from 7 to 38 tiles.

## Open / next

- **Play-test the client:**
  - The Survey verb on a Miner's wheel.
  - The rust vein squares and barren hatch on the minimap.
  - The Mine ghost refusing off a known vein.
  - The survey notices.
- **A 3D vein glint prop** (TileMarks) and a world-view marker for barren
  slopes. Today veins show only on the map.
- **Start fairness:** some starts are 30+ tiles from any mountain. Deferred
  by the user's choice; options are in `stone-and-ore-land.md`.
- **Balance:** stone and ore rates are unchanged. Run the AI lab (ArmTests
  200-day A/B) to see how much the survey step delays the armoury.
