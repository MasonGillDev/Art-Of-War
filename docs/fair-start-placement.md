# Fair start placement: the land shared out evenly

**Status:** built 2026-10-05. Code: `src/Sim.Core/WorldGen/SeatPlacer.cs`, called from
`WorldFactory.BuildSpec`. This is the "multi-player fair start placement" that M11 Phase 2
(`docs/world-generation.md`, `docs/architecture.md`) deferred.

## The decision

When a world holds more than one kingdom, every castle (the human seat's included) is placed
by **sharing the mainland out evenly**:
1. Seats start on a ring around the mainland's centre, half way to the coast, turned by the
   map seed.
2. **Lloyd relaxation** (40 rounds: each land tile goes to its nearest seat, each seat moves
   to the centre of its tiles) settles them into shares of about equal size.
3. Each seat snaps to the nearest tile a castle can start on **inside its own share**. Only a
   share with no viable tile looks anywhere, so a poor share doesn't pull its castle onto a
   neighbour's land.
4. If the continent is too small for every seat's share (the small test maps), seats are
   **packed** instead: each next castle goes on the viable tile farthest from every castle so
   far, and whichever layout fits more kingdoms wins.

A single-kingdom world keeps the generator's centre start (`StartPicker`, `map.Start`)
unchanged.

A castle may start on a tile that is:
- grassland on the mainland (the land connected to the generator's start);
- in a meadow, with 40+ grassland within 6 tiles (M17's rule);
- with forest within 10 tiles. Without it, the 96-tile fertility lab put a castle on a
  desert's edge with no wood for 14 tiles, and that kingdom starved. The old AI search had no
  wood rule. The old human start needed forest within 28+ tiles, which almost always held.
- 24+ tiles from every castle already placed.

## Why

### The problem it fixed (measured)

The old placement put the human at the map's centre. It then searched for each AI on a ring
up to 64 tiles out, scanning rows from the top (`FindAiStart`). Every AI therefore took the
first good tile on the **north** side of its ring.

A probe measured each kingdom's share of the land, giving each land tile to the castle
nearest it by foot travel time (real movement costs, rivers included). Results on the default
252×252 map, 5 seeds:

| | Old placement | New placement |
|---|---|---|
| 6 kingdoms: land per kingdom, min–max | 1,033–13,972 | 3,908–8,894 |
| 6 kingdoms: far land per kingdom (outer 30% of the map by walking time) | **0**–5,661 | 412–4,094 |
| 16 kingdoms: kingdoms that fit | 15 on 2 of 5 seeds | 16 on every seed |
| 16 kingdoms: land per kingdom | 270–7,750 | 1,206–3,749 |

Under the old placement, every map piled its far land in the south. Northern kingdoms had the
coast at their backs and none of their own. That was unfair today, and it blocks the planned
wilderness bands (danger and loot rising with distance from all kingdoms). A band means
nothing if one seat has all of it.

### Options weighed

- **Fix the scan order only.** This loses: the human still sits in the middle, surrounded,
  with the least far land of anyone.
- **Fixed ring** (seats at equal angles, part way to the coast). This was measured at 50% and
  60% of the way. It is better than the old placement, but it ignores the continent's shape:
  a seat aimed at a narrow peninsula gets a sliver of land. Worst-case land per kingdom was
  2,081–3,148 against Lloyd's 3,540–5,393.
- **Lloyd relaxation by straight-line distance.** Chosen: the most even of the options, and
  it fits all 16 kingdoms on every seed tried.
- **Lloyd relaxation by walking time** (Dijkstra over movement costs each round). This was
  measured and was **no better**: worst-case far land 374–1,213 against 345–1,161. It costs
  12 Dijkstra passes per world built, so it lost.

### What it means for the map

Each kingdom sits in the middle of its own share. The far land lies at the **edges between
kingdoms** and along the coast, not in one corner. Deep land is borderland: a frontier with
neighbours, not a private backyard.

The spread is not perfectly even: the far land per kingdom still varies about 3× on some
seeds. Terrain (lakes, mountain chains, peninsulas) makes perfect equality impossible on a
random continent. This is a large step from "0 for some".

### Determinism

The placement uses floating-point maths at generation time only, under the same contract as
`NoiseField` and `ContinentShaper`:
- it runs once;
- its result (castle tiles) is frozen into the `GenesisSpec`;
- nothing on the replay path reads it.

Seat 0's position depends on the seed's ring turn, so the human seat isn't always in the same
corner.

## Future expansion

- **Wilderness bands:** a frozen travel-time-from-all-castles field cut into Settled /
  Frontier / Wild / Deep. Built on these positions, a band now means about the same thing for
  every seat.
- **More human seats:** seat order is just the order of the list. Assigning several humans to
  seats (spread apart, or grouped) is a choice about which indexes to give them.
- **Balancing pass:** if seeds turn up with a seat far poorer than the rest, a local search
  that nudges seats to even out far land is the next step. It was not needed to fix the
  measured problem.
- **Coastal access** (`docs/ocean-ringed-continent.md`, deferred): a harbour-site rule can be
  added to `Viable` without touching the relaxation.

### Tests the new layout moved

Several scenario tests had been written against the old castle spots (a faction-1 castle on
the north coast of the 96-tile seed-7 map). Each now names the layout it needs instead:
- `FortifyTests`: the threat-arc test reads the castle's real position; the rubble test's
  "generous" site cap went from 12 to 40 (an inland ring needs more wall tiles); the
  stone-short test uses seed 3, where hills are in sight.
- `IrrigateTests` and `ScavengeTests`' lab use seed 3.
- The crop-rotation lab runs on 128 tiles: on 96, an even split starts the two kingdoms about
  24 tiles apart.
- The rival personality test runs on 128 tiles, which fits its five kingdoms.

The AI labs are sensitive to where castles sit. A future placement change will move them
again.

## Acceptance

- `SeatPlacerTests`:
  - every kingdom fits (6 and 16 on the default map);
  - no kingdom holds less than half the average share of the mainland;
  - every castle passes `Viable`;
  - one kingdom keeps the centre start.
- `AiPlayerTests` still pins every castle pairwise separated by 24+ tiles, with the same
  starting loadout.
- The probe numbers above came from a throwaway tool and are not checked in.
