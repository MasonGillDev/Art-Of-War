# High-Terrain Visibility (M22)

## Decision

The **highest terrain band is common knowledge.** `Biome.Mountain` tiles — the
scarcest resource band (Mountain alone gates Stone via the Quarry) — are revealed
to **every** player's view from the start of the game, regardless of fog. The
opening becomes a **race to the peaks** rather than a slow fog-gated discovery.

The reveal is **terrain only**: a mountain tile's biome appears in the player's
`RememberedTerrain`, but the **units, structures, and roads on it stay fogged**
until the player gets genuine vision there. You see *where* to race, not *who has
already arrived*.

It is implemented entirely in the **pure-read view projection**
(`View.BuildPlayerView`) — no sim state is written, no snapshot field is added,
no determinism contract is touched. `Biomes.IsCommonKnowledgeTerrain(Biome)` is
the single knob for which bands are always-known (`Mountain` today).

## Why

### Why the highest terrain, and why a race

Mountains are the rarest band the generator produces and the only source of
Stone. Under full fog, finding them is a function of luck and scouting tempo, and
two players can spend the early game blind to where the contested prize even is.
Making the peaks common knowledge converts that into a *positional race* — a
clear, legible strategic objective from turn one — which is exactly the kind of
"hours-long-travel-rewards-commitment" tension the design is built around.

### Why terrain-only, not full visibility

Revealing what's *on* the mountains (enemy quarries, armies, road networks) would
collapse the fog game entirely on the most strategically important tiles. The
race only works if you can see the destination but not the state of the contest:
you know there's a mountain at (40,12), you don't know whether a rival already
camped it. Scouting (and getting there) is still how you learn occupancy. So the
reveal adds the biome to `RememberedTerrain` and nothing else.

### Why `RememberedTerrain` only — not `Explored` (and the road-leak it avoids)

`PlayerView.Explored` has exactly one consumer: the server's road overlay filter
(`ViewProjector`, which includes a road tile only if it is in `Explored`). If
mountains were added to `Explored`, an enemy road on an un-scouted peak would
leak. Adding them to `RememberedTerrain` *only* reveals the terrain to both
surfaces that matter — the human client (renders the `Remembered` tile array) and
the AI brain (`ThinkContext` builds its terrain map from `view.Remembered` +
`view.Visible`) — while keeping `Explored` honest ("tiles I personally saw") and
the road overlay leak-free. The same channel feeds humans and AI, so the reveal
is fair by construction (the M17 fairness signature).

### Why pure-view, not a genesis sim-state reveal

The considered alternative was writing every mountain tile into every player's
`Explored` + `RememberedBiome` at genesis (reusing the existing reveal plumbing).
Ruled out: it bloats serialized per-player state with O(mountains × players)
entries, it's a sim-state/snapshot change on the most determinism-sensitive
system in the codebase (fog), and it conflates "common knowledge terrain" with
"I explored this." The pure-view approach needs no snapshot change, no
`FormatVersion` bump, and keeps the M3 headline (`ViewsOff_HashEquals_ViewsOn`)
trivially intact — `BuildPlayerView` still writes nothing to world state.

### Why eager, cached on `GameWorld`

The set of mountain tiles is **immutable** (canals only flood non-Mountain land;
nothing else mutates the grid), so `GameWorld.CommonKnowledgeTerrain` is computed
**once in the constructor** from the frozen grid and never changed. It is **not
serialized and not part of the sim hash** — a read-side memo of immutable worldgen
data, recomputed identically from the grid on restore. Eager (constructor) rather
than lazy so `BuildPlayerView` stays a strict pure read with no cache write on the
fog path. Cost is one bounded O(W·H) scan per world construction.

## Future expansion

- **Reveal Hills too.** Change `IsCommonKnowledgeTerrain` to
  `b is Biome.Mountain or Biome.Hills` to make the second-highest band common
  knowledge — a one-line widening of the race.
- **Per-game toggle.** If some modes want full fog, promote the predicate to a
  world-level config flag (parallel to the other config records). View-side only,
  so still no snapshot impact on the toggle itself unless it's made durable.
- **Elevation-driven reveal.** If terrain gains a continuous elevation field, the
  predicate could become "reveal tiles above elevation E" — "you can see the high
  ground from anywhere" generalised.
- **Landmark reveals.** The same RememberedTerrain-only mechanism could surface
  other always-known features (ruins, named locations) without touching fog state.

## Acceptance tests

`tests/Sim.Tests/HighTerrainVisibilityTests.cs`:

- An unexplored mountain appears in `RememberedTerrain` as `Mountain`, but NOT in
  `Visible` or `Explored` (terrain-only).
- Unexplored Forest/Hills stay fully fogged (no free reveal for lower bands).
- An enemy unit on an un-scouted mountain stays hidden — only the terrain shows.
- A mountain the player *can* see behaves normally (in `Visible`, live entities on
  it surface) — the reveal never suppresses live visibility.
- `GameWorld.CommonKnowledgeTerrain` equals exactly the Mountain tiles.
- `BuildPlayerView` is still a pure read near the reveal (100× no-mutation), and
  building views doesn't change the snapshot hash; the reveal survives round-trip.

## References

- `docs/architecture.md` §2.2 (pure-read wall — `BuildPlayerView` writes nothing),
  §2.3 (inverted pure-read wall — why this is view-only, not a `Sight.Reveal`
  write).
- `docs/determinism-audit.md` (M22 addendum — the reveal is pure-read, the cache
  is non-serialized derived data).
- `docs/world-generation.md` (Mountain is the high-elevation band the generator
  produces; terrain is frozen, so the mountain set is immutable).
- `Sim.Core/Vision/View.cs` (`BuildPlayerView`), `Sim.Core/World/Biome.cs`
  (`Biomes.IsCommonKnowledgeTerrain`), `Sim.Server/ViewProjector.cs` (the wire
  projection that carries `Remembered` to the client/AI).
