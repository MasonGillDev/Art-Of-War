# M44 Spec — Stone from Hills, Ore from Mountain Veins

> Milestone spec (workflow §7 step 1). This is the *what*; the planner
> breaks it into phases at step 2. Decision docs for the choices below
> get written/updated during implementation per CLAUDE.md.

## What we're adding

Stone and ore become **slow but plentiful**, in contrast to farmland and
wood, which are **scarce** (they wear out). They get there by different
routes:

1. **Stone comes from Hills.** A Quarry sits on a Hills tile and claims
   nearby Hills tiles, the same way a Farm claims Grassland. That land is
   held exclusively, but it **never wears out**. Your stone income is
   limited by how much hill land you hold and how long the work takes,
   not by the land running dry.
2. **Ore comes from Mountains, and only at veins.** Ore veins are hidden
   on Mountain tiles at worldgen. A Miner has to **survey** a stretch of
   mountain, which takes time, to find one. A Mine can only be placed on
   a vein your faction has found. A vein **never runs out**.
3. **Only the finder knows where a vein is.** Once a Mine stands on the
   vein, anyone who can see the Mine knows where it is.

## The gap (why now)

- **The mapping is backwards.** `Biomes.Resource` (`World/Biome.cs:118-128`)
  says Hills→Ore and Mountain→Stone. The catalog follows it: Quarry
  needs Mountain and Mine needs Hills (`World/StructureCatalog.cs:57-96`).
  The design wants the reverse.
- **Quarry and Mine have no land mechanic at all.** Both have
  `ClaimCount = 0` and `DegradeAmount = 0`, so they are own-tile only
  and can be stacked. Any free tile of the right biome works equally
  well. `StructureSpec.cs:60-78` and `Structure.cs:174-179` flag this as
  deferred ("own-tile-only until their ladder extension").
- **Mines need no exploration.** Mountains are common knowledge
  (`docs/high-terrain-visibility.md`), so today a mine site is visible
  and valid from tick 0. Ore never pushes anyone outward.

## Locked decisions

Made 2026-10-01 with the user. Reversing any of these needs a written
addendum.

1. **Ore comes from Mountains and stone from Hills.** `Biomes.Resource`
   flips: Hills→Stone, Mountain→Ore. Quarry gets `RequiredBiome = Hills`
   and Mine gets `RequiredBiome = Mountain`.
2. **Quarries claim Hills tiles, and those tiles never wear out.** The
   Quarry gets `ClaimCount > 0` and `ClaimRange > 0`, but keeps
   `DegradeAmount = 0`. The M15 claim machinery already covers this:
   exclusion across all owners and kinds, `AutoSelect`, validation, the
   client's predicted claim, and release on demolish.
   - Hills **stay off the fertility ladder**. There is no new ladder and
     no Hills band.
   - Because `FertilityAt` returns the fixed Hills baseline for
     off-ladder tiles, the M15 taper always works out to 1. The quarry
     runs at full rate for as long as it holds its claim.
3. **Prospecting is an area sweep.**
   - The player sends a Miner to a Mountain tile.
   - When he arrives, he surveys for a fixed time.
   - When the survey finishes, he reports the **nearest** vein within
     the survey radius, or reports nothing.
4. **Only the finder knows about a vein.** A found vein goes into that
   faction's known set and nowhere else. No one else learns of it until
   a Mine is built on it.
5. **Veins are permanent.** They have no reserve and no depletion. A
   Mine on a vein produces at its catalog rate forever, and finding the
   vein is the whole cost.
6. **Veins are seeded at worldgen and are deterministic.** They come
   from `(seed, x, y)` plus a guarantee pass. No RNG is consumed at play
   time. The vein set is persisted rather than re-derived, so a snapshot
   never depends on worldgen code staying the same.
7. **About a third of mountain tiles hold ore.** The user chose this
   on 2026-10-01: one simple density rule, with no start-placement rule
   and no terrain reshaping. A small safety net makes sure every mountain
   range has at least one vein. See *Ore veins*.

## Detailed rules

### Quarry (stone)

| Field | Today | M44 |
|---|---|---|
| RequiredBiome | Mountain | **Hills** |
| OutputResource | Stone | Stone |
| ClaimCount / ClaimRange | 0 / 0 | **6 / 2** (tunable) |
| DegradeAmount | 0 | 0 (no wear) |
| Rate | 1 per worker every 12 h, ×2 for a Quarryman, cap 3 | unchanged (tunable) |

- Claim tiles must be Hills, free, not claimed by anyone, not
  canal-reserved, and within range. These are the existing `Claims.ValidateOne`
  rules with the biome swapped. A claim is all or nothing: there are no
  partial claims.
- The quarry never goes dormant because of its claim. `InBandClaimCount`
  stays equal to `ClaimCount`, since Hills never drift.
- Hand-built quarries pick up the lazy auto-claim through
  `Extractor.ArmIfDormant`, just as Farms do.
- **Why claim at all if the land doesn't wear?** Claims are where the
  "takes up land like a farm" cost comes from. Hills become territory
  you hold and fight over, and you can't pack five quarries into one
  hillside.

### Ore veins (worldgen)

- New world state: `GameWorld.Veins`, a sorted set of Mountain tiles, in
  canonical (y,x) order.
- **Density.** A Mountain tile has a vein if
  `hash(veinSeed, x, y) mod VeinOneIn == 0`. **`VeinOneIn` defaults to
  3**, so about a third of mountain tiles have a vein (user, 2026-10-01).
- **Safety net.** Every connected mountain range (8-neighbour flood fill)
  gets at least one vein. A range the hash left empty gets one on its
  lowest-hash tile. Even a lone peak has ore.
- `veinSeed` comes from `GenesisSpec.VeinSeed`, which `WorldFactory`
  sets to the map seed. Seeding consumes **no** sim `Rng`, so every
  other random stream stays byte-identical.
- Seeding runs once, in `Genesis.Build`. The result is persisted and
  never re-derived.
- The knobs `VeinOneIn` and `VeinSeed` live in a `VeinConfig` on
  `GenesisSpec`. Tests read them from config and never hard-code them.

**Measured terrain (2026-10-01, 10 seeds at 252×252 with 16 factions):**
- Mountain is about 5% of land on average. On seed 11 it is 0.3%,
  just 99 tiles.
- 135 of 157 starts have fewer than 12 mountain tiles within 10, and
  some starts are 30–85 tiles from any mountain.
- Hills are about 22% of land. 47 starts have no 6-tile hill pocket
  within 10.

The user chose density over reshaping the terrain or moving starts, so
distance to the mountains varies by start. See *Out of scope*.

### Survey (prospecting)

New intent: `SurveyIntent(unitId, targetTile)`.

**Validation** happens at submit and again at resolve
(`docs/intent-validation.md`):
- The unit is owned and alive.
- The unit's role is `UnitRole.Miner`.
- The target is a Mountain tile and is in bounds.
- The unit is not in a group, aboard a boat, or locked in battle.

**Lifecycle.** This is a goal-shaped intent, following the M30 pattern
in `Intents/GoalRules.cs` and modeled on `Boats/EmbarkGoal.cs`:
1. The unit walks to `targetTile` using normal M43 movement.
2. On arrival, schedule a `SurveyCompleteEvent` at
   `arrival + SurveyDuration`. **`SurveyDuration` defaults to 1 day and
   is tunable.** The event carries a fencing token (§2.6).
3. The survey is cancelled, and the stale event is a no-op, if the unit:
   - moves,
   - is given any other order,
   - dies,
   - enters battle, or
   - joins a group.

   The in-flight survey is an anchor (§2.8). It is persisted, so a
   snapshot taken mid-survey resumes correctly.
4. When `SurveyCompleteEvent` fires:
   - **Search area:** Mountain tiles within Chebyshev **`SurveyRadius`
     (2, tunable)** of the surveyor's tile.
   - **Found:** take the **nearest** vein the faction does not already
     know about, ordered by (distance, y, x). Add it to the faction's
     `KnownVeins`. Mountain tiles strictly closer than that vein go into
     `SurveyedBarren` (proven empty).
   - **Not found:** every Mountain tile in the radius goes into
     `SurveyedBarren`.
   - Emit a `SurveyReportEvent` for the alert log ("Vein found at…" /
     "Nothing in these slopes").
   - The unit goes back to idle on the spot. It does not have to walk
     home to deliver the news.

New per-faction state, held as sparse sorted sets:
- `KnownVeins`: veins this faction has found. It includes veins that are
  already mined, so the knowledge isn't lost if the mine is destroyed.
- `SurveyedBarren`: tiles proven to have no vein. The client uses it so
  players don't search the same slopes twice.

**Expected effort at the defaults** (radius 2, 1 in 3):
- A full 5×5 sweep over solid mountain covers about 24 tiles, so a sweep
  practically always finds a vein.
- A sweep on a ragged mountain edge covers about 6 tiles, so about 91%
  find one.
- Prospecting is a time cost, not a gamble.

### Mine (ore)

| Field | Today | M44 |
|---|---|---|
| RequiredBiome | Hills | **Mountain** |
| Placement | any free Hills tile | **a vein tile in the placer's `KnownVeins`** |
| Claims | none | none (the vein *is* the land) |
| Rate | 1 per worker every day, ×2 for a Miner, cap 3 | unchanged (tunable) |

- `StructureSpec` gets a new flag, `RequiresVein`. `PlaceSiteIntent.Resolve`
  gains one rule, checked after the biome check: "tile ∈
  `KnownVeins[owner]`". Otherwise it returns a new rejection,
  `NoKnownVein`.
- One mine per vein. This falls out of the existing "no structure
  already on the tile" rule.
- Two factions can know the same vein. The first to place a site wins,
  which is ordinary intent ordering. A stake does not reserve anything;
  only a placed site does.
- If a mine is razed, the vein stays. The owner still knows it, and the
  enemy who saw the mine now knows it too (see *Visibility*).

### Visibility

- Mountains stay common knowledge. Veins do not. `KnownVeins` is never
  sent to another faction.
- Once a Mine or Mine site exists, it is an ordinary structure. Anyone
  whose fog reveals it sees it. When a faction first **sees** a Mine,
  that vein is added to the faction's `KnownVeins`. This makes "once
  mined, anyone who can see it knows" a permanent fact, so it doesn't
  disappear when the mine does.
- Scout charts (M38) may later carry veins ("Glint" hint). That is
  deferred.

## Persistence

- `Snapshot.FormatVersion` goes from **44 to 45**.
- New section `Veins`: the sorted vein tile list.
- New per-player data: the sorted `KnownVeins` and `SurveyedBarren` lists.
- New unit anchor: the active survey, holding `(targetTile,
  completeTick, fence)`. It sits on the unit, or in the M30 goal record
  if one fits.
- `StructureSpec.RequiresVein` comes from the catalog and is not
  snapshotted.
- **Older saves:** follow `docs/persistence-model.md` practice for
  format bumps. If v44 saves have to load, the migration seeds `Veins`
  from the stored seed. Pre-existing Mines on Hills and Quarries on
  Mountain then become invalid; see open question 3.

## Wire (v2)

- **Genesis:** `BuildOptionDto` gains `RequiresVein`. Quarry's
  `RequiredBiome`/`ClaimCount`/`ClaimRange` change, and the client
  already reads those fields. `FertilityRulesDto` stays as it is.
- **Live view (own faction only):**
  - `Veins`: known veins, each with a `mined` flag.
  - `Barren`: `SurveyedBarren` tiles. This is a set that only grows. A
    delta or epoch scheme is the planner's call.
  - Unit activity `Surveying`, with the completion tick, so the client
    can draw a progress ring.
- **Intent endpoint:** `Survey { unitId, x, y }`.
- **Alerts:** "Vein found" and "Survey came up empty".

## AI

The AI reads only its view, and the new view fields are enough.

- **FortifyRung:** quarry placement moves from
  `NearestFreeTile(Mountain)` to the claim-aware pocket search that
  Farms use: `NearestPocketTile` with Hills and the quarry's
  `ClaimCount`.
- **IrrigateRung:** no change. It only staffs an existing quarry.
- **ForgeRung:** the mine step becomes:
  1. If the AI knows an unmined vein, place a Mine there.
  2. Otherwise, if it has an idle Miner, send him to survey the nearest
     Mountain tile whose radius isn't fully barren. Score candidates by
     unsurveyed mountain tiles in radius, minus distance.
  3. Otherwise, train a Miner. This is the existing School path.
- **Arbitration:** a surveying Miner is busy and shows up as a claim in
  the arbitration ledger, so other rungs don't take him.

## Client (prod: `Art Of War(prod)`)

- **Survey order.** With a Miner selected, add a "Survey" verb to the
  hotbar or context panel, and right-click on Mountain while in that mode.
  The preview draws the 5×5 sweep and dims tiles already proven barren.
- **Overlays:**
  - A glint marker on known unmined veins.
  - A subtle hatch on barren tiles.
  - A progress ring on a surveying unit.
- **Placement:** Mine placement is only valid on known veins. Ghost
  tiles turn red elsewhere, with the tooltip "No known vein: send a
  Miner to survey."
  - Quarry placement reuses `BuildPlacement.PredictClaim` with Hills.
    Hide the soil/land bar for kinds whose `DegradeAmount` is 0 and show
    "6 hill tiles" instead, because the bar would always read full.
- **HUD:** wire-only, so no fields beyond what is listed above.
- **Editor steps owed:** probably a vein marker prop. Spoil-heap art
  already exists for both kinds.

## Docs to update during implementation

- **New decision doc `docs/stone-and-ore-land.md`:** why stone gets
  claims but no wear, why ore uses hidden veins plus a survey instead of
  a Mountain ladder, and the alternatives that lost (Hills ladder,
  finite veins, a daily luck roll, tile-by-tile search, public veins).
- **Addenda (`## Update 2026-10-xx`):**
  - `extraction-claims.md`: Quarry now claims; Mine does not.
  - `biome-degradation.md`: Hills and Mountain stay off the ladder for
    good.
  - `high-terrain-visibility.md`: veins are hidden even though
    Mountains are common knowledge.
  - `world-generation.md`: vein seeding and the guarantee.
  - `ai-players.md`: Forge, Fortify, and survey.
  - `refining-structures.md`: the Smelter's ore now comes from Mountain.
  - `secrets-and-progression-proposal.md`: stone and ore numbers.
- `determinism-audit.md`: new mutation points (survey completion, vein
  discovery on sight) and the pure reads (`KnownVeins`, `Veins`).

## Refactors recommended alongside

- **Stale claim counts.** These should be fixed in the Phase 1 commit:
  - `extraction-claims.md:29` says LumberCamp 6 / Farm 4, but the code
    has 8 / 15.
  - `StructureCatalog.cs:50,127` and `Claims.cs:66` repeat the old
    counts.
- **Naming clash.** Land `Claims` (`World/Claims.cs`) and the automation
  `Claim` (`Automation/Claim.cs`) are unrelated. Renaming land claims to
  `LandClaims` / `ClaimTiles → LandTiles` would make them easier to
  tell apart in AI code, which uses both. This touches many files, so
  it should be done in its own commit.
- **Unused baselines.** The Hills and Mountain baselines in
  `BiomeDegradationConfig` are stored but never read. M44 confirms
  they will stay unused, so either delete them or comment them as
  reserved. Deleting needs a snapshot field change.

## Out of scope / deferred

- Vein quality or richness tiers, and veins that make different ores.
- Charts carrying vein intel from scouts (M38 `Glint`).
- Selling or trading vein locations through diplomacy.
- Wear on Hills (a stone ladder). Reversing locked decision 2 needs an
  addendum.
- **Start fairness for mountains and hills.** Some starts are 30+ tiles
  from any mountain (see the measurements). Options for later: a home
  massif stamped near ore-poor starts, starts that prefer mountains, or
  a lower `MountainMin`. That last one would also need the client's
  `WorldGeometry.MountainBand` to match.
- Rebalancing stone and ore rates. The defaults are unchanged here and a
  balance pass comes later.

## Headline test

On a twin-run of a fixed seed, an AI faction with no known veins:
1. trains or uses a Miner,
2. surveys,
3. finds a vein,
4. places a Mine on it,
5. smelts Iron from the ore.

It also stands up a Hills quarry with a 6-tile claim. Both runs must hash
the same, and a snapshot taken mid-survey must round-trip to the same
hash.

**Supporting tests:**
- **Vein density (worldgen, a seed sweep at 252×252, the production
  size).** World-wide, the share of Mountain tiles with a vein is within
  a tolerance of `1 / VeinOneIn`. Every connected mountain range has at
  least one vein. Veins sit only on Mountain tiles.
- **Seeding is isolated.** With `VeinOneIn` changed, every non-vein
  part of the snapshot hashes the same, because no sim `Rng` is drawn.
- Placing a Mine on an unknown vein, or on Mountain with no vein, is
  rejected with `NoKnownVein`.
- A survey is cancelled by a move order, death, or battle, and the stale
  event is a no-op.
- Two factions survey the same slopes and each learns the vein
  independently. Neither one's view leaks the other's `KnownVeins`.
- A quarry's claim never degrades, its output stays at full rate over a
  simulated year, and it blocks other extractors.
- Seeing an enemy Mine adds that vein to your `KnownVeins` permanently.

## Proposed phases (for the planner)

1. **Swap and quarry claims.** Flip `Biomes.Resource`, the Quarry/Mine
   biomes, and the quarry claim fields. Fix the stale comments. Update
   the AI FortifyRung pocket search.
2. **Veins in worldgen and persistence.** Add `GameWorld.Veins`, density
   seeding plus the per-range safety net, snapshot v45, and the density
   test.
3. **Survey.** Intent, goal, anchor event, fencing, per-faction sets,
   and alerts.
4. **Mine gating and visibility.** `RequiresVein`, `NoKnownVein`, and
   learning a vein by seeing its mine.
5. **AI.** The ForgeRung survey and mine flow, and the arbitration
   ledger.
6. **Wire and client.** DTOs, intent endpoint, survey order mode,
   overlays, and placement validation. Then the audit, host smoke, and
   decision doc.

## Open questions (resolve at step 2)

1. **Must the Miner be trained?** Only `UnitRole.Miner` can survey,
   which means a School first. The alternative is to let anyone survey
   and give Miners a faster `SurveyDuration`.
2. **Should finding a vein be instant?** The spec says knowledge arrives
   when the survey completes. A "must walk home to tell it" rule, like
   M38 scouts, would add risk on hostile slopes but slow the early game.
3. **Old saves.** Drop v44 saves, or migrate them and demolish the
   now-invalid Quarries on Mountain and Mines on Hills, refunding their
   cost?
4. **Quarry claim size.** 6 tiles is a guess. Hills patches near starts
   need measuring so 6 always fits. `StartPicker` may need to require a
   claimable hills pocket, not just one Hills tile.
5. ~~Can `StartPicker` still place everyone?~~ Dropped on 2026-10-01.
   The user chose plain density (1 in 3) over any start-placement rule.
