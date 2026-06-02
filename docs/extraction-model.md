# Extraction Model

## Decision

Resources are extracted via **structures staffed by workers**, not by units
standing on biomes. A player builds an extractor (Lumber Camp on Forest, Quarry
on Mountain, Mine on Hills, Farm on Grassland), assigns workers to it, and the
structure produces over time into a local **output buffer**. Haulers move
resources from buffers to where they're needed.

The **Castle** is the initial structure: a Stockpile-shaped container holding
the player's starting resources, with a capacity cap. Lose-condition, vision-pin,
and spawn-point responsibilities accrete later under the same type.

Builds and assignments are **physical** — to build a structure on tile T, the
required materials and the required number of builder units must be physically
present on T at the moment construction starts. No castle-as-magic-source.

## Why structure-gated extraction (not direct foraging)

Two models were considered:

- **A (chosen):** structure-gated — build an extractor to harvest from a biome.
- **B:** direct foraging — a unit standing on a biome harvests into its own cargo.

A was chosen because:

- **Build becomes a gateway, not a capstone.** You build to extract, extract to
  build more. A tighter economic loop in one milestone.
- **Workers are a scarce allocatable resource.** A staffed worker is *occupied*
  — the same body can extract OR fight OR haul OR build, never all at once.
  That tension is core to the design's spirit (§5 of the design doc).
- **Biome geography drives the economy.** Extractors are biome-locked; no one
  territory holds every biome — which is what makes barter trade *necessary*
  later (design doc §11).
- **Worker roles extend the existing pattern** — Farmer↔Farm, Miner↔Mine
  multipliers parallel builder-builds-faster and scout-sees-farther.

B's only advantage is fewer state types in M1. Trading that for a working
economic loop and the physical-occupancy property is worth it.

## Why everything-physical for builds

Two patterns were considered:

- **A (chosen):** materials and builders must be on the build site at the
  moment construction starts. Castle holdings do not satisfy a remote build.
- **B:** materials in a "global" stockpile (castle) can be spent on a remote
  build.

A was chosen because B punches a hole in the "everything physical" rule (design
doc §7.1) the whole game depends on. Once you allow magic teleporting materials
anywhere, you lose:

- Supply lines as physical objects
- Caravans as raid targets
- Roads as the consequence of repeated traffic
- Trade as a release valve for biome scarcity

The cost A accepts: more setup in test scenarios (hauls must move materials
before the build can start). That's a cost worth paying once; it's the cost of
the game working at all.

## Build mechanic

To start construction on tile T:

1. The required count of **builder units** must be physically present on T.
2. The required quantities of **materials** must be physically present on T
   (deposited there or carried by units who deposit on arrival).
3. The biome on T must be compatible with the structure type.

When all three are met, construction begins and the required materials are
consumed (at start, not at completion).

**Builds pause when conditions fail mid-build.** If the builder count drops
below the requirement (a builder is killed, or the player issues a Move that
pulls one off the tile), the build pauses without losing progress. It resumes
when sufficient builders are again present on the tile. Builders shouldn't
wander off mid-build in normal play — but the rule covers the cases that
matter: combat, and the player explicitly retasking.

This is the same **dormant-when-unsatisfiable / re-arm-when-satisfiable**
pattern that drives extractor back-pressure (below). One general property of
any continuously-running process gated by a condition.

## Production mechanic

A staffed extractor produces at a rate determined by
`baseRate × workers × roleBonus`, drawing from the biome's regenerating stock
on the tile and depositing into a local **output buffer** capped at `bufferMax`.

**Buffer back-pressure.** When the buffer hits cap, the production tick stops
re-scheduling. A haul pickup from the buffer re-arms it. Production is
event-driven; nothing polls.

**Over-extraction throttling.** Each production tick catches up biome regen
first, then extracts what's available. If extraction outruns regen, stock
depletes and production drops to the regen rate. Emergent, no special code.

## Castle as initial structure

`Castle : Structure` with:
- `Holdings: SortedDictionary<Resource, int>` — current resources, like Stockpile.
- `Capacity: int` — max total resources it can hold.

Its own type rather than a Stockpile instance because it accretes other
responsibilities later (lose condition, citizen spawn point, vision pin).
Naming it now means those have a home that's already there.

## What gets built (M1)

- `Resource` enum extended: `Wood, Stone, Ore, Food`.
- `Biome` enum: `Forest, Mountain, Hills, Grassland, Water`. Movement cost
  derived from biome via lookup.
- `StructureKind` extended: `Castle, Stockpile, ConstructionSite, LumberCamp,
  Quarry, Mine, Farm`.
- `StructureCatalog` data table: `kind → (required biome, output resource,
  base rate per worker, worker cap, build cost, build duration ticks, buffer
  cap, required builder count)`.
- `Unit.Activity` enum: `Idle | Moving | Working | Building | Hauling`. Read at
  resolution time for the "is this unit available" check.
- Construction state on tiles with active builds; pause/resume mechanism.

## What's deferred

- **Direct foraging.** Explicitly out for the foreseeable future. All extraction
  is structure-gated.
- **Multiple resources per biome.** One per biome.
- **Tech tiers / refining.** A Mine outputs Ore, period.
- **Carts, horses, escorts, composite caravan groups.** Hauling is a single unit
  for now.
- **Population growth / starvation.** Units don't consume Food yet.

## Reference

Realizes §5 (Citizens & Roles), §7 (Logistics), and parts of §3 (World &
Terrain) of the design doc. The "everything physical" rule that drives the
"no magic source" decision is §7.1. The validation contract that makes the
pause/resume mechanic correct lives in [intent-validation.md](intent-validation.md).
