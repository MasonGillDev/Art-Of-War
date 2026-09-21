# M35 status: environmental fertility

**Decision doc:** `docs/environmental-fertility.md`. Started 2026-09-20.
**Rule:** knobs default to strength 0 through Phase D, so every existing suite
and lab curve stays byte-identical until the Phase F sweep. No commits unless
asked.

## Addendum verification (2026-09-20)

| Addendum claim | Code says |
|---|---|
| Fertility stores sparse deviation from a generated baseline | True. `Fertility` = (Deviation, LastUpdateTick); baseline from `BiomeDegradation.BaselineFertility(biome, config)` |
| Baseline is derived on demand, not baked | True. Snapshot writes config + sparse dict only. No migration. |
| Water-distance derivation exists | Partial. `WaterProximity.IsNearWater` is boolean, box-bounded, and blind to rivers (edge masks, not Water tiles). |
| Rivers already count as water sources | False. Listed as the "next slice" in `docs/rivers.md`, unbuilt. |
| Forest-depth derivation | New. Worldgen moisture is not persisted; depth must come from the grid. |
| Production reads fertility | No. `ProductionTickEvent` tapers on the in-band claim COUNT (binary per tile). `extraction-claims.md` already names per-tile yield weighting as the formula swap. |
| Lazy catch-up can take a baseline change | Yes via the existing canal hook (`BuildCompleteEvent.cs`, `OnWaterProximityChanged`), widened to the fertility radii. |
| Depth-fertility feedback loop needs a lab sweep | Cannot exist: depth reads the worldgen grid (static except canals), because the lazy field only permits event-driven baseline changes. |
| Baseline is "just the floor" | No. Also band identity (`Band()` thresholds), step-penalty snap targets, deviation clamp, AI rotation thresholds, client soil grade. Hence the in-band clamp. |

## Phases

| Phase | Scope | Status |
|---|---|---|
| 0 | Decision doc, this tracker, addenda on biome-degradation / canals / rivers / extraction-claims, determinism-audit entry | done 2026-09-20 |
| A | Pure derivations: `WaterProximity.DistanceToWater` (river-aware), `ForestDepth`, `EnvironmentalFertility.Offset`, knobs on `BiomeDegradationConfig` (FormatVersion 33), strength-0 identity tests | done 2026-09-20 (`EnvironmentalFertilityTests`, 15 green) |
| B | Tile-aware baseline through `FertilityAt` / `CatchUp` / `DeriveRate` / snap targets; river-aware `IsNearWater` | done 2026-09-20 (147 green across the fertility-adjacent classes) |
| C | Canal hook: affected-set union, old-rate catch-up, instant lift; observation-independence test across a canal | done 2026-09-20 (`EnvironmentalFertilityCanalTests`, 4 green; radius union gated on strength) |
| D | Taper formula swap in `ProductionTickEvent`; `ClaimTaperTests` extension | done 2026-09-20 (`FertilityTaperTests`, 4 green; `Claims.InBandClaimFertilitySum`) |
| E | Wire (`ClaimBaseline`, per-tile baseline carrier), `AiConfig` relative rest/resume, `ThinkContext` best-of-nearest siting, prod client `SoilGrade` relative | done 2026-09-20 (`EnvironmentalFertilityWireTests` 6 green; prod client `_AowTypeCheck` + `_AowWireBoundary` build clean; `AiConfig.ResumeSoilWornWithin`) |
| F | Lab: strength-0 identity on the four lab tests, gradient sweep, deep-camp burn-budget check; defaults are the user's call | in progress 2026-09-20: `EnvironmentalFertilityLabTests.GradientSweep_LabReport` (flat / mild / strong, 100 days) |

## Baseline (before any code change)

- Full suite 2026-09-20 before any code change: 1124 passed, 0 failed, 3m26s.
- Full suite after Phases A–E (lab sweep included): see Verified.

## Verified

- 2026-09-20 presentation follow-up (user): band-relative soil grade + graded claim preview while placing; `FertilityRulesDto` gains the band baselines; 34 wire tests green, prod client type-checks.
- 2026-09-20 after Phase E: full suite green: 1152 passed, 0 failed, 3m26s (1124 baseline + 28 new, incl. the 3-rung sweep); prod client `_AowTypeCheck` + `_AowWireBoundary` build clean; not run in Play.

## Phase F — gradient sweep (2026-09-20, `GradientSweep_LabReport`, 100 days, seed 7, 96x96, two Homesteaders)

Faction 0 (human slot, map centre) landed on DRY grassland; faction 1 sits by a river. Same seed, same brains, only the knobs differ. All three rungs pass the survival pins (no starvation death, no famine at d100, pop > 14). No degraded desert appeared in 100 days at any rung (rotation holds).

| Rung | water / dry / depth | f0 d100 pop, food, farms, site baseline | f1 d100 pop, food, farms, site baseline |
|---|---|---|---|
| flat | 0 / 0 / 0 | 35, 2737, 5, 5000 | 44, 3260, 5, 5000 |
| mild | +1000 / -250 / +300 | 36, 3382, 6, 5219 | 41, 2187, 4, 5783 |
| strong | +2500 / -500 / +600 | 37, 3922, 7, 5926 | 43, 4755, 4, 6937 |

Reading: the brain sites by the gradient (f1's farms average 5750–6937 vs the 5000 band); dry-land f0 still grows and, by d60+, finds better pockets (baseline climbs as it spreads). Strong is not lethal on this seed. Defaults remain at strength 0; **the rung is the user's call.**

## 2026-09-20 — the gradient is a launch switch, not a default

Setting the "mild" rung as the config default failed 11 tests: nine exact-output pins on hand-built default-config worlds (Production, Canals, ClaimsHelper, HousingBuff) and, more importantly, BOTH 160-day labs — the riverside faction ends in famine (BalanceLab debt 78; CropRotation faction 1). The 100-day sweep did not see it. Reading: the live-fertility taper (decision 2) plus the dry-edge penalty is a real food-curve change and needs its own balance pass (candidates: drop DryEdgePenalty to 0, lower ResumeSoilWornWithin, or weight the taper by baseline rather than live fertility). Until then the config default stays the identity and the played gradient is `--fertility mild|strong` (`ServerOptions.FertilityGradient` → `WorldFactory.FertilityFor`). Open item.
