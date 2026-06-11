# M15 Spec — Extraction Claims (working-tile claims for extractors)

> Milestone spec (workflow §7 step 1). This is the *what*; the planner
> breaks it into phases at step 2. Decision docs for the choices below
> get written/updated during implementation per CLAUDE.md.

## What we're adding

Degrading extractors (LumberCamp, Farm) stop working an invisible
radius and start working an explicit, player-chosen set of **claimed
tiles**. At placement, the player selects which X in-biome tiles within
range the extractor will consume. Claims become the single source of
truth for three mechanics that are currently separate or missing:

1. **Degradation footprint** — fertility loss applies to the claimed
   tiles, not a blind Chebyshev box around the building.
2. **Exclusion / spatial scarcity** — a tile can be claimed by at most
   ONE extractor, regardless of kind or owner. You cannot stack camps;
   a forest patch supports as many camps as it has claimable tiles.
3. **Production taper** (final phase) — output scales with how many
   claimed tiles are still in the required biome band, so a camp slows
   as its cut exhausts instead of binary-cliffing.

This is the revision the code predicted: `Structure.cs` marks the
Extractor a "PHASE-A PLACEHOLDER" whose working-radius model and
snapshot format "WILL change."

## The gap (why now)

- **Stacking is optimal today.** Degradation is MAX-not-sum, but
  production stacks fully — packing five LumberCamps into one forest
  patch quintuples output while every shared tile pays the land cost
  once. The most exploitative layout is the best layout.
- **Edge placement is a free lunch.** Production ignores the radius
  entirely and dormancy checks only the extractor's own tile, so a
  camp on a biome edge gets full output while wasting its collateral
  degradation on worthless neighboring grassland.
- **No spatial scarcity → no exploration pressure.** Nothing forces
  expansion outward until land *temporally* exhausts. With claims +
  march-pace movement (M-time retune), scarcity pushes the player out
  and distance makes "out" cost something — roads, forward stockpiles,
  and escorts become the game.
- **Binary output cliff.** A camp produces at 100% until its own tile
  flips band, then stops. No feedback that the land is dying.

## Locked decisions

These were made deliberately (2026-06-11 design discussion); reverse
only with a written addendum.

1. **Claims block ALL owners.** An enemy's claim physically holds the
   land — "conquer them to take it." This is resource competition by
   design, not griefing to be prevented.
2. **Claims are exclusive across ALL extractor kinds**, not just
   same-kind. A tile is either being farmed or logged, never both.
   (One claim map; also collapses the MAX-over-claimants machinery —
   each tile has at most one claimant.)
3. **Player-selected tiles with auto-suggest.** The client proposes
   the best X tiles (most in-biome, nearest, deterministic); one click
   accepts, or the player paints adjustments. Choice when it matters,
   zero friction when it doesn't. **Server-side**: if `ClaimTiles` is
   omitted from the intent, the server auto-selects deterministically
   (canonical nearest-valid order) — keeps Sim.Host scenarios, tests,
   and dumb clients working with no painting UI.
4. **Per-kind claim parameters live on `StructureSpec`** (catalog
   knobs; tests derive, never hard-code): `ClaimCount` and
   `ClaimRange` (Chebyshev, from the extractor tile).
5. **Claims only for degrading extractors this milestone.** LumberCamp
   and Farm. Quarry/Mine stay tile-only (they're off the fertility
   ladder; their scarcity pass comes with a future ladder extension).
6. **Demolish is deferred but expected.** Until it ships, an exhausted
   camp's claim stays locked — including for its owner. Accepted
   interim cost; DemolishIntent is the release valve and is already on
   the roadmap.

## Proposed mechanics (planner refines; flag disagreements at step 2)

### Placement

- `PlaceSiteIntent` gains an optional `ClaimTiles` list (same shape as
  the `DockSlip` extension: only read for claiming kinds).
- Validation (resolution-time, fail-clean): site tile valid per
  existing rules; exactly `ClaimCount` tiles; each within `ClaimRange`
  of the site; each in the kind's `RequiredBiome` (derived `BiomeAt`,
  not worldgen); each unclaimed by ANY existing extractor or pending
  site. The extractor's own tile is the building, not a working tile —
  it is implicitly reserved (no one else may claim it) but does not
  count toward `ClaimCount` and does not degrade.
- **Claims reserve at SITE placement** and transfer to the finished
  extractor at `BuildCompleteEvent` — otherwise two sites could claim
  the same tiles while building. `ConstructionSite` carries the
  pending claim.
- Same-tick contention for the last claimable tiles resolves by
  submission order (`(At, Seq)`) — pin with a fairness test.

### Degradation (replaces the radius model for claiming kinds)

- A producing (TickArmed) extractor degrades exactly its claimed
  tiles at `DegradeAmount` per `DegradePeriod`. `OnProductionTransition`
  catches up the claim set instead of the radius box. The catch-up
  scope stays bounded (claim size, not map size).
- Recovery rule unchanged: a claimed tile recovers while no producing
  claimant exists (dormant camp = land rests), desert latch unchanged.
- `DegradeRadius` survives only as the default `ClaimRange`; the
  radius-scan (`MaxInRangeProducingDegradeAmount`) reduces to a
  claimed-by lookup. Keep the MAX shape only if a transition window
  can still produce overlap; otherwise delete it with a doc note.

### Dormancy + taper

- **Dormancy**: an extractor goes dormant when NO claimed tile remains
  in its `RequiredBiome` band (replaces the own-tile check for
  claiming kinds). "The cut is exhausted."
- **Taper (final phase)**: per-tick output = worker rate ×
  inBandClaims / ClaimCount, integer math (rate numerator/denominator
  pattern already used for role bonuses). Production visibly slows as
  the land dies — the feedback loop the binary cliff can't give.

### Persistence & wire

- `Extractor` + `ConstructionSite` gain claim-tile lists →
  **`Snapshot.FormatVersion` bump** (the format change the Phase-A
  comment promised). Old snapshots refuse cleanly per the version
  gate; snapshot-on-deploy discipline applies.
- Claims serialize in canonical order (sorted (y,x)); determinism
  audit gets the new mutation points (claim set writers: PlaceSite,
  BuildComplete, restore).
- `StructDto`: own structures expose their claim tiles (render the
  worked land); enemy structures expose claims only when the
  structure is visible — fog rules unchanged, reject toasts explain
  blocked placements.

### Client (separate pass, same milestone)

- BuildMode: after choosing the site tile for a claiming kind, show
  the auto-suggested claim (highlight tiles); click tiles to toggle;
  confirm places the site with the painted claim. Dock's 2-click flow
  is the precedent for multi-step placement.
- Render own claims as a subtle tile tint while the extractor is
  selected (reuse the destination-marker pooling pattern).

## Explicitly deferred

- **DemolishIntent** (claim release + building removal) — next
  milestone; the user is tracking it.
- **Quarry/Mine claims** — with the Mountain/Hills ladder extension.
- **Structure combat / sieges** — note loudly: until structures can be
  destroyed or captured, an enemy claim is a PERMANENT blocker. This
  is acceptable now because it raises the stakes of M7's field combat
  (kill the workers, starve the camp dormant) and because sieges are
  already future work in `combat-model.md`.
- **Worker-intensity degradation scaling** — unchanged from M9's
  deferral.
- **Claim editing after placement** — re-claiming means demolish +
  rebuild until a ReassignClaimIntent earns its keep.

## Headline tests

- `ClaimsDeterminismTests.MidBuild_PendingClaim_SnapshotRoundTrip_Identical`
  — snapshot between site placement and completion; restored run
  resolves identically (the closure gate over the new state).
- `ClaimExclusionTests.SecondCamp_CannotOverlapClaim_AnyKind_AnyOwner`
  — the scarcity contract, including the enemy-claim-blocks case.
- `ClaimExclusionTests.SameTick_LastTiles_FirstSubmittedWins_BothOrders`
  — fairness.
- `ClaimDegradationTests.Camp_ExhaustsItsClaim_GoesDormant_TotalProductionBounded`
  — the M9 headline restated over claims: finite extraction, and tiles
  OUTSIDE the claim untouched (the edge-exploit fix made testable).
- `ClaimTaperTests.Output_ScalesWith_InBandClaimCount` — config-derived
  taper math.
- Twin-run hash over the full pipeline; 100×-view purity over claim
  reads. All expectations derive from `StructureSpec` /
  `BiomeDegradationConfig` — never hard-coded.

## References

- `docs/biome-degradation.md` (+ 2026-06-10/11 addenda) — the fertility
  machinery claims re-target; the carry-drop invariant constrains any
  new catch-up scope.
- `docs/extraction-model.md` — the structure-gated extraction model.
- `docs/time-and-scale.md` (2026-06-11 addendum) — the band framework;
  claims + march-pace movement are two halves of the same
  exploration-pressure design.
- `src/Sim.Core/World/Structure.cs` — the Phase-A placeholder comment
  this milestone retires.
