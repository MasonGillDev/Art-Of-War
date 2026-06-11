# Extraction Claims: Tiles as the Unit of Land Use

## Decision

Degrading extractors (LumberCamp, Farm) own an explicit list of
**claimed tiles** — `ClaimCount` tiles of their `RequiredBiome` within
`ClaimRange` (Chebyshev) of the building, chosen by the player at
placement (or auto-selected deterministically when the intent omits
them). The claim is the single source of truth for three mechanics:

1. **Degradation footprint** — fertility loss applies to exactly the
   claimed tiles while the extractor produces. The building's own tile
   never degrades (it's the building, not a field).
2. **Exclusion** — a tile is claimed by at most ONE structure, of any
   kind, owned by anyone; and **no structure or construction site may
   be placed on a claimed tile, the owner's included** (full structural
   exclusion, both directions). Claims are physical territory.
3. **Production taper** — per-tick output scales with how many claimed
   tiles are still in the required biome band:
   `extract = min(ceil(workerRate × inBand / ClaimCount), FreeBuffer)`.

Dormancy: a claiming extractor goes dormant when **zero** claimed tiles
remain in band ("the cut is exhausted"). Non-claiming extractors
(Quarry, Mine — `ClaimCount = 0`) keep the legacy own-tile model
untouched.

| Knob (StructureSpec) | LumberCamp | Farm |
|---|---|---|
| ClaimCount | 6 | 4 |
| ClaimRange (Chebyshev) | 2 | 2 |

Claims reserve at **site placement** (carried on the
`ConstructionSite`, copied to the finished `Extractor` at
`BuildCompleteEvent`) so two in-flight sites can't promise the same
land. Snapshot `FormatVersion` 9 → 10 (the format change the old
"PHASE-A PLACEHOLDER" comment on `Extractor` always predicted).

## Why

### Why claims at all (what the radius model couldn't fix)

- **Stacking was optimal.** Degradation is MAX-not-sum but production
  stacks, so packing camps into one forest patch multiplied output
  while shared tiles paid the land cost once. The most exploitative
  layout was the best layout. With one-claimant-per-tile, a patch
  supports exactly as many camps as it has claimable tiles.
- **Edge placement was a free lunch.** Production ignored the radius
  and dormancy watched only the own tile, so a camp on a biome edge
  got full output while dumping its collateral damage on worthless
  neighbors. Claims must BE the required biome, and they are what
  degrades.
- **Binary cliff.** Output was 100% until the own tile flipped, then
  zero. Taper gives the feedback loop: the land visibly tires.
- **No spatial scarcity.** Combined with march-pace movement
  (docs/time-and-scale.md), claims force expansion outward and make
  outward cost something — exploration pressure by design.

### Why exclusive across ALL kinds (not just same-kind)

A tile is either farmed or logged, never both. One claim map, simpler
validation, and `ClaimantDegradeAmount` has at most one claimant per
tile — the MAX fold over claimants is kept anyway (order-independent
even if the invariant were ever violated; no silent first-match,
per architecture §4 rule 9).

### Why full structural exclusion both directions (user decision)

Claim tiles must be structure-free, and placement of ANY structure on
ANY claimed tile rejects — including your own stockpile on your own
wheat field. One rule, physical reading, real base-layout pressure.
The losing alternatives: owner-exempt placement needs a claim-shrink
rule nothing else wants, and claims-only-block-claims lets a rival
tower up in your fields.

### Why CEIL in the taper (deviation from the spec's floor wording)

`floor(rate × inBand / Count)` can hit 0 while claimed land is still
alive (rate 1, inBand 1, count 6) — and a producing-but-zero extractor
self-reschedules forever doing nothing, the exact shape of the
zero-power combat loop bug. With ceil, output ≥ 1 while any claimed
tile lives, and the dormancy guard (inBand == 0) is the only stop.
`inBand` is clamped to `ClaimCount` so a serialized claim list larger
than a later-retuned `ClaimCount` can't amplify production.

### Why `ArmIfDormant` declines to arm at zero in-band claims

Arming an exhausted camp just to have the next tick reject would burn
a transition pair per attempt — and every transition catch-up drops
the sub-period recovery carry on the claim tiles (the 2026-06-10
biome-degradation invariant, recovery direction). Declining to arm
preserves the carry and gives `AssignWorkersIntent` an honest reject
instead of a silent insta-dormancy.

### Why lazy arm-time auto-claim (and why NOT in AddStructure)

Hand-constructed extractors (test fixtures, dev tooling) have no
intent-time claim. `ArmIfDormant` fills an empty claim list via the
same deterministic `Claims.AutoSelect` before its pre-start catch-up.
This is safe because `ArmIfDormant` only runs inside event/intent
resolution against a COMPLETE world — never during snapshot restore.
`GameWorld.AddStructure` was rejected as the fill site: it runs during
`Snapshot.ReadStructures` while the world is partially rebuilt in
canonical order, so an auto-claim there could see different neighbor
claims than the live run did — hash divergence. (Restored extractors
carry their claims in the snapshot and skip the fill entirely.)

Ordering inside `ArmIfDormant` is sound: claims are assigned before
the pre-start catch-up, which still excludes this extractor because
`ClaimantDegradeAmount` gates on `TickArmed`, not claim presence.

### Semantic change vs M9: claimant-only recovery suppression

Under M9, ANY producing extractor in range suppressed a tile's
recovery. Under claims, only the tile's claimant does. A neighboring
producing camp no longer pauses recovery on land it didn't claim —
which is the point: the footprint is the claim.

### Auto-select rule (deterministic)

Candidates within `ClaimRange`, ordered by (Chebyshev distance, y, x);
take the first `ClaimCount` valid (in bounds, derived `BiomeAt` ==
`RequiredBiome`, unclaimed, structure-free, ≠ site tile); reject the
placement if fewer exist ("insufficient claimable land" — no partial
claims, the count IS the knob). The stored list is re-sorted to
canonical (y, x) so painted and auto-selected claims of the same set
serialize identically.

## Acceptance tests

- `ClaimExclusionTests.SecondCamp_CannotOverlapClaim_AnyKind_AnyOwner`
  + `Structure_CannotBePlaced_OnClaimedTile` — the territory contract.
- `ClaimExclusionTests.SameTick_LastTiles_FirstSubmittedWins_BothOrders`
  — (At, Seq) fairness over land contention.
- `ClaimsDeterminismTests.MidBuild_PendingClaim_SnapshotRoundTrip_Identical`
  — the M4 closure gate over the new state.
- `ClaimDegradationTests.Camp_ExhaustsItsClaim_GoesDormant_TotalProductionBounded`
  + `TilesOutsideClaim_Untouched` — finite extraction, edge exploit dead.
- `ClaimTaperTests.Output_ScalesWith_InBandClaimCount` — staggered
  claim fertility, config-derived.

## Future expansion

- **DemolishIntent** — the claim release valve (deferred, tracked).
  Until it ships, an exhausted camp locks its claim — including for
  its owner — and an enemy camp is a permanent blocker (raises the
  stakes of field combat: kill the workers, starve it dormant).
- **Quarry/Mine claims** — with the Mountain/Hills ladder extension.
- **Claim editing** — a ReassignClaimIntent if demolish+rebuild proves
  too clumsy.
- **Per-tile yield weighting** — taper already reads per-tile band;
  weighting by per-tile fertility is a formula swap.

## References

- `docs/m15-extraction-claims-spec.md` — the milestone spec.
- `docs/biome-degradation.md` (+ addendum) — the fertility machinery
  claims re-target; the carry-drop invariant that shaped decline-to-arm.
- `docs/extraction-model.md` — the structure-gated extraction model.
- `docs/combat-engagement-pin.md` / `docs/intent-validation.md` — the
  resolution-time validation discipline all claim checks follow.
