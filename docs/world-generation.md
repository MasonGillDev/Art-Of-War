# World Generation

## The decision

Worlds are produced by a one-shot procedural pipeline (`Sim.Core.WorldGen.MapGenerator`) that uses floating-point noise to classify each tile into an integer `Biome`, then **freezes** the result into a `Biome[,]` grid before the simulation ever sees it. The sim consumes the frozen grid through the same `GenesisSpec.Biomes` override that hand-authored scenarios already use. Replay never re-executes the generator.

Today's pipeline: two `SharpNoise.Modules.Perlin` fields (elevation + moisture) → `BiomeClassifier` (Whittaker-style thresholds) → `StartPicker` (first Grassland tile with Forest + Hills + Mountain inside a Chebyshev radius). Defaults: 64×64, 4 octaves, frequency 0.04, start radius 15.

## Why

### Why a *frozen* pipeline rather than a sim-time generator

The engine's headline contract is `Snapshot.Hash(run1) == Snapshot.Hash(run2)`. Floats accumulate differently across CPUs and runtime versions — that's why `docs/architecture.md` §3 bans them from anything serialized. A generator that ran on every replay would put float math on the replay path; one machine's "Forest" would become another machine's "Grassland" and the replay would diverge silently. Freezing the output to an integer grid before sim start moves the float math entirely off the replay path: the sim only ever sees the same integer biomes the snapshot would.

**The seed is provenance, not state.** `GeneratedMap.Seed` exists so a human can re-derive a map for debugging. It is not an input to any sim event. Replay anchors on the snapshot's biome grid; the seed could be deleted without affecting determinism.

### Why Perlin + Whittaker thresholds

Two-field (elevation + moisture) Whittaker classification is the smallest pipeline that produces recognisable, coherent regions ("here is a forest, here is a mountain range") rather than per-tile speckle. SharpNoise's `Perlin` module handles octave summing internally, so the generator code stays under 200 LOC. It is **not** the final word on map shape — it is the minimum viable thing that produces playable test maps.

Alternatives ruled out:
- **Voronoi / plate-tectonic simulation** — better continent shapes but more code, more parameters to tune, and the gameplay payoff is invisible at 64×64. Defer to a later worldgen phase if/when world sizes grow.
- **Hand-authored tile sets / Wave Function Collapse** — strong for puzzle-like maps; wrong for an emergent RTS where the design wants gradients (mountain *ranges*, not mountain *tiles*).
- **Sim-time generation** — would have closed off cross-machine replay (see above).

### Why `Biome.Water` became passable-but-expensive (cost 250) instead of `Impassable`

`Biome.Water` was `Impassable` originally because hand-authored scenarios put water only where the designer wanted it impassable. Noise-generated maps produce arbitrary lakes that can split the playable area, trap the Castle, or make `Pathfinding.FindPath` return null for tiles the player can see but not reach.

Three options were considered:
1. **Keep `Impassable`, reject any seed whose start is in a disconnected pocket.** Rejects the symptom, not the cause: the player can still see "ungoverned land" they can never path to. Bad UX.
2. **Post-process the generator to flood-fill and convert isolated water back to grassland.** Works, but adds a whole second pass with its own thresholds to tune; the cure is heavier than the disease.
3. **Make water cheap-to-classify-as-water but expensive-to-walk.** One-line change to `Biomes.MoveCost`. Every tile remains reachable (the `WorldGenTests.EveryTile_IsReachableFromStart` flood test pins this), and "you *can* cross the lake, you just won't want to" is exactly the trade-off real RTSes use.

We took option 3. The cost (250) is a "sane large integer, not `Impassable`" — high enough that A* will route around water whenever land exists, but bounded so summing it across a long crossing can't overflow. Real water mechanics (boats, swim, deep water actually impassable, naval movement) are deferred to a later design pass and will replace this single integer with something structured.

### Why start picking is a deterministic scan, not weighted random

`StartPicker` walks `(y, x)` in order and returns the first Grassland tile with Forest + Hills + Mountain inside `radius`. This is:
- **Trivially deterministic** — no RNG needed, the function is `(grid, radius) → TileCoord?`.
- **Auditable** — when a player asks "why did the Castle land here," the answer is "first qualifying tile in scan order," not "the weighted random pick landed there."
- **Cheap** — bounded by grid size, terminates on first match.

A weighted random pick would have been nicer for multi-Castle placement, but multi-player start placement is out of scope here (it lands with combat, when fairness across starts actually matters).

### Why Desert is a low-elevation moisture carve-out, not its own elevation band

Desert is gated by **moisture, not elevation**: in the low-elevation band that would otherwise be Grassland/Forest, very dry tiles (`moisture < DesertMoistureMax`, default 0.20) become Desert. The classifier check sits after the water/mountain/hills elevation gates so deserts only appear on low ground — high ground stays Hills or Mountain regardless of how dry it is. This matches the real-world intuition (deserts are flat-and-dry, not "any dry tile"), keeps Hills/Mountain proportions stable, and costs one extra threshold rather than a re-shuffle of the elevation bands.

`MoveCost(Desert) = 40` — slower than Hills (35), faster than Mountain (45). Desert is non-extractive: no `StructureCatalog` entry has `RequiredBiome = Desert`, so no extractor can be placed there. That's the entire "no resources in the desert" rule — it falls out of the existing biome-gated build check, no new machinery needed.

The proportion knob is `DesertMoistureMax`. Lowering it pushes desert towards rarity; raising it past `MoistureSplit` (0.50) would invert the moisture meaning and produce nonsense, so the value is intentionally bounded well below that. The `Desert_AppearsAcrossSeeds` test guards against a future change that silently kills the branch.

## Future expansion

The pipeline is structured so each stage can be swapped without touching the sim contract.

- **Bigger worlds (M11 Phase 2).** Width/Height are config; the generator runs in O(W·H) at constant memory per pass. A 512×512 world is fine; a 4096×4096 world will want chunked noise generation and a smarter start picker but does not require touching the sim.
- **More biomes.** `Biome` is an append-only enum (architecture §3 rule 5). Adding `Swamp`, `Tundra`, etc. follows the same recipe used for Desert: append the enum value, add a `Biomes.MoveCost` + `Biomes.Resource` entry (both switches throw on unknown — they're exhaustive on purpose), add a `BiomeClassifier` branch, add a host glyph for the smoke output. Old snapshots keep working; new generation can produce them.
- **Named locations / fixed landmarks.** A post-classification "feature pass" can stamp specific biomes or future structure markers onto the grid before it is frozen. As long as the pass runs before `GeneratedMap` is constructed, the freeze rule still holds.
- **Multi-player start placement.** `StartPicker` returns a single tile today. The natural shape for N players is a separate `StartPicker.PickMany(grid, n, fairness)` that returns N coordinates with a min-Chebyshev-distance constraint and equal local resource access. Lands with combat (M6).
- **Real water mechanics.** When boats / swimming / coastlines become gameplay-visible, the integer `MoveCost` for `Biome.Water` is replaced by a structured cost (per-unit-role, per-adjacent-structure). The generator does not change; it still emits `Biome.Water` tiles.
- **Replayable seeds in the player-facing UI.** Already supported — the seed is on `GeneratedMap` and round-trips through `GenesisSpec.Biomes`. A "share map seed" button only needs UI plumbing.

## Out of scope (intentionally deferred)

- **Multi-player fair start placement.** With combat (M6).
- **Rivers, coastlines, beaches as distinct biomes.** With real water mechanics.
- **Resource node placement at generation time.** Resources are currently extracted by structures the player builds on the appropriate biome; node-level placement would change the extraction model (`docs/extraction-model.md`).
- **Hex grid.** Open decision per `persistent-rts-design.md` §13.1; generation cost of switching is bounded (one classifier + one neighbour rule).

## Acceptance tests

`tests/Sim.Tests/WorldGenTests.cs` pins the contract:

- `SameConfig_ProducesIdenticalGrid` — generator determinism (D1).
- `EveryTile_IsAValidBiome` — no `None`/uninitialised cells leak through.
- `Start_IsGrassland_WithResourceBiomesNearby` — picker promise upheld.
- `EveryTile_IsReachableFromStart` — flood from start covers every tile (the water-passable contract).
- `BiomeProportions_AreSane` — guards against degenerate maps.
- `DifferentSeeds_ProduceDifferentMaps` — guards against an accidentally seed-blind pipeline.
- `TwinRun_OnGeneratedGenesis_HashesMatch` — proves no generator float leaked onto the replay path.
- `Snapshot_RoundTrips_OnGeneratedWorld` — proves the snapshot path is biome-grid-only, generator-independent.

The last two are the freeze-rule contract: if generator state ever became sim state, those tests would diverge.

## Update 2026-06-16 — canals are the sanctioned post-worldgen terrain mutation (M21)

The freeze rule above says the biome grid is frozen before the sim starts and
the generator never runs on the replay path. **Canals (M21) are the deliberate,
sanctioned exception** to "terrain is immutable post-worldgen": the canal
`BuildCompleteEvent` branch calls `TileGrid.SetBiome(p, Biome.Water)` for each
finished-canal path tile (`docs/canals.md`).

This does **not** weaken the freeze-rule contract, because the mutation is sim
state, not generator state:

- The mutation happens inside a deterministic, event-driven `BuildCompleteEvent`
  — same `(At, Seq)` ordering as everything else, no floats, no generator.
- The grid is already snapshotted faithfully as one biome byte per tile
  (`WriteGrid`/`ReadGrid`), so a canal-flooded tile round-trips and replays as a
  plain `Biome.Water` tile. The generator is never consulted to reproduce it.
- `Snapshot_RoundTrips_OnGeneratedWorld` and `TwinRun_OnGeneratedGenesis` stay
  green for the same reason they always did — the snapshot path is biome-grid-
  only and generator-independent, whether a Water tile came from worldgen or a
  canal.

So the rule sharpens rather than breaks: **the grid is frozen against the
*generator*, not against the *sim*.** Deterministic event-driven terrain
mutation (canals today; canal draining or other terraforming later) is allowed
precisely because it is sim state captured by the snapshot, off the float/replay
path. A worldgen *feature pass* that stamped biomes would still have to run
before the freeze; a sim-time terrain change must run as an event and be
snapshotted — which is exactly what canals do.
