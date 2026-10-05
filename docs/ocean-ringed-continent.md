# Ocean-Ringed Continent (worldgen)

## The decision

The generated world is an **island continent**: one landmass in the middle of
the map, every map edge guaranteed ocean, all edge water one connected sea.
This is achieved by **shaping the elevation field** (`ContinentShaper`) — a
domain-warped radial falloff mask multiplied into the raw Perlin noise before
biome classification — not by post-processing biomes. Starts are picked
**interior-first** (`StartPicker` scans center-outward).

## Why

"Sail from any edge to any coast" needs the sea to be one connected component
that touches the whole border, and the coastline should read as geography, not
as a circle stamped on noise.

**Why shape elevation rather than the alternatives:**

- **Chosen: multiply the elevation field by a falloff mask.** The elevation
  field is the single source of truth: `BiomeClassifier` derives Water from
  `elevation < WaterMax`, and the client rebuilds its heightmap and waterline
  from the *same* quantized field (`ViewProjector`, `GET /map/elevation`).
  Shape the field once and sim water, rendered sea, and boat pathing agree by
  construction. `MapGenerator` and `WorldFactory` both source elevation from
  `ContinentShaper.BuildElevation` — one code path, two call sites, no drift.
- **Lost: post-classification biome carving** (stamp Water tiles around the
  border after classification). Cheap, but the client heightmap would still
  show land above the waterline where the sim says sea — the two views desync,
  and every future consumer of elevation inherits the mismatch.
- **Lost: a separate Ocean biome/tile kind.** Append-only enum churn, wire
  changes, double bookkeeping in every biome switch (movement, degradation,
  canals), for a distinction ("ocean vs lake") the flood-fill can compute when
  gameplay actually needs it.

**The mask** (all knobs on `GenerationConfig`, tuned via a visual preview
harness 2026-07-06):

1. Square-bump radial distance `d = 1-(1-nx²)(1-ny²)` — 0 at center, exactly
   1 on the border; unlike Euclidean it lets land reach map corners.
2. Domain warp `d += CoastWarpAmplitude·(warp-0.5)` from a third noise field
   (`CoastSeedOffset`) — continent-scale bays/peninsulas; the anti-circle.
   The warp field is **min-max normalized** first: octave-summed Perlin mapped
   by `(raw+1)/2` is strongly mid-heavy and otherwise mutes the warp to a
   fraction of its amplitude (measured: near-circular coasts at any amplitude).
3. Smoothstep falloff from `CoastInner` to `CoastOuter`, times a hard
   `EdgeOceanTiles` edge ramp → the border ring is provably water, hence the
   sea is connected (not statistically — every seed).
4. `ContinentDome`: gentle interior uplift (fades out by `CoastInner`).
   Without it, masking erased every Mountain tile on unlucky seeds (seed 1151,
   the server default, had zero) and `StartPicker` found no start. Also reads
   as real geography: coastal lowlands, interior ranges.

**Accepted trade-offs:**

- Land area drops to ~50-65% of the map (legacy 128×128 was ~97% land). The
  same `--ai N` count now shares a smaller continent; `FindAiStart` already
  warns-and-skips when factions don't fit.
- `WaterMax` no longer means "proportion of water" (its original comment);
  it is purely **sea level** on the shaped field.
- Fractal noise can pinch a bay shut → an unreachable lagoon (a lake). The
  connectivity guarantee covers the *edge-connected sea* (measured 95-100% of
  all water); lakes are allowed and flavorful, not a bug.
- Bandit pressure concentrated on the smaller landmass hit coastal starts
  hard, which exposed that `StartPicker`'s old top-left `(y,x)` scan
  systematically parked the castle on the north coast of ocean worlds. Fixed
  by scanning center-outward (the codebase's standard `(dist, y, x)` ring
  order) — the AI-vs-bandits balance labs pass again on the new geography
  without touching any bandit knob.

## Acceptance tests (WorldGenTests)

- `OceanBorder_EveryEdgeTile_IsWater` — every border tile, several seeds.
- `Ocean_IsOneConnectedSea` — flood fill from a corner reaches every border
  tile and ≥85% of all water.
- `OceanBorder_Off_IsTheLegacyRawNoiseWorld` — the escape hatch is
  bit-identical to the pre-ocean generator.
- Existing proportion/start/determinism properties retuned and green.

## Future expansion

- **Ocean vs lake as sim data**: the flood-fill already distinguishes them;
  if gameplay needs it (harbors only on ocean coast, "landlocked" AI logic),
  compute once at genesis and store — no generator rework.
- **Archipelagos / multiple continents**: replace the single radial distance
  with min-distance to N warped blob centers; the mask pipeline, edge ramp,
  and all guarantees carry over unchanged.
- **Fjords / erosion-carved coasts**: swap or augment the warp field with
  ridged noise, or run the existing client-side droplet erosion server-side —
  the shaper is the one place such a pass would slot in.
- **Naval gameplay** (ports, sea trade, coastal raids by boat) builds on the
  connectivity guarantee; nothing here constrains it.
- Deliberately deferred: guaranteed-fair multi-player coastal access (each
  faction gets a usable harbor site) — lands with the multiplayer fairness
  milestone, not worldgen.

## Update 2026-10-05 — interior-first is for one kingdom only

`StartPicker`'s centre-out scan now places only a lone kingdom. With two or more, every
castle comes from `SeatPlacer`, which shares the mainland out evenly and keeps the castles
off the coast by the same token (each sits in the middle of its share). See
`docs/fair-start-placement.md`.
