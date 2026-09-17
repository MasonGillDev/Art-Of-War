# Scatter placement: affinities against the land, not weights against a die

## The decision

Each scatter entry carries **affinities** — clump, valley, slope, each in [-1, 1] — and the
placer keeps a candidate spot in proportion to how well those signals endorse it. The
signals come from the terrain itself: a **flow-accumulation map** baked on the eroded
relief (valley floor vs. crest), and **slope** from the same grid. The biome decides the
composition; the land decides the placement. `BuildNatureScatterSet` states each biome's
intent in a readable vocabulary (`Where.Valleys(0.7f)`, `Where.Crests(0.6f).Steep(0.5f)`).

## Why

The previous placer had one rule — uniform jitter, optionally masked by a noise clump
field — and per-biome *weights*. Weights decide *what* appears, never *where*. The result
was a sprinkle: the same random distribution in every biome, differing only in mix. No
amount of weight tuning produces what a landscape actually has — pines in the folds,
boulders on the crests, bushes where the water is, grass on the flats — because those
are relationships between props and terrain, and the placer had no terrain to read.

Alternatives ruled out:

- **Hand-authored placement.** Not procedural; the map is generated.
- **Per-biome density maps painted from noise.** More noise fields with no relation to
  the land — the same problem with more knobs.
- **Rules in code per biome (`if (biome == Forest) …`).** Unreviewable and unreusable;
  intent buried in branches. The affinity model puts the intent in data, next to the
  prop it describes, in a form an artist can read and edit in the Inspector.

## The signals

- **Flow accumulation** (`TerrainSampler.ValleyAt`): every relief node drains to its
  steepest lower neighbour; the count of nodes draining through a node is its flow area.
  Log-scaled and saturating at ~90 tiles of catchment, so a main valley reads 1, a lone
  fold ~0.4, a crest ~0.1. Baked once with the relief, deterministic.
- **Slope** (`TerrainSampler.SlopeAt`): rise over run from the relief grid; "fully steep"
  at 0.5 (~27°, a real hillside at this vertical exaggeration).
- **Clump field**: the existing value noise, now a soft 0..1 mask rather than a yes/no.

Suitability is the product of one `Affinity(signal, bias)` term per signal; a bias of 0
passes everywhere, ±1 passes only where the signal agrees. One deterministic roll decides.

## Consequences

- **Density is candidates per tile**, not props per tile. Particular props end up
  sparser than the number suggests. Tune density last.
- **Cost is nothing.** Two extra bilinear samples per candidate at chunk build.
- **The flow map is the seed for rivers.** A river is where flow area crosses a threshold
  — something that co-operates with the terrain, not a tile or a biome. The raw
  accumulation is kept (`FlowAreaAt`) for that day; nothing about it is decided here.

## Future expansion

- **Distance to water** (chamfer over Water tiles): reeds at the shore, willows near it,
  dead trees far from any.
- **Distance to roads, fields and structures**: clear verges, hedgerows along field
  lines, no trees on a farm's claim.
- **Relative placement**: bushes gathering *around* boulders — a second-order rule the
  same rejection framework can host by sampling a shared "rock field".
- **Height bands**: the mountain tree line.
- **Aspect**: flowers on south faces.

## Acceptance

- In grassland, boulders sit visibly on rises and slopes, bushes in the folds, big trees
  on gentle ground; the open flats are grass.
- In forest, canopy is densest along drainage lines and thins toward ridges.
- Hills show pines in the folds and bare exposed flanks.
- The same seed places the same props on every client.
