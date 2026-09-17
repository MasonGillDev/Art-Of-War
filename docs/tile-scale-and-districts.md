# Tile scale, and structures as districts

## The decision

**A tile stays one square kilometre.** A structure is not rendered as a building —
it is rendered as a **district**: the building *and everything around it that makes
it that building*. A House is a hamlet of huts, a Farm is a farmstead plus its
fields, a Castle is a keep plus its walls, yards and outbuildings.

This keeps the sim's "one structure per tile" believable at 1 km per tile, and it
leaves the sim, the march pace, the time bands and the narrator's distances exactly
as they are.

## Why, and what lost

**The problem.** The board's camera shows individual houses, fences and carts. The
sim's tile is a kilometre. Drawing one house per kilometre looks like a diorama of a
lonely building in an empty field, which is what the client does today — and the
placeholder district table papers over it by drawing buildings at 60–90 m across.

**The alternative, rejected: shrink the fiction to ~200 m per tile.** It would make
single buildings believable, but a tile's size is load-bearing in the sim:
- march pace is "1 tile = 1 km" (`docs/` time bands), so movement and the day-length
  model would need re-deriving;
- the narrator and scout reports speak in distances tied to that scale;
- the map is 252 tiles across — at 200 m, the whole world becomes 50 km, which is a
  county, not a kingdom.

The owner wants the world to feel huge. Districts buy that without touching the sim.

**What districts cost.** Art must be authored as *kits of small pieces* — huts,
roofs, fences, field plots, carts — rather than as hero buildings. And LOD becomes
mandatory rather than an optimisation: a 10 m hut is sub-pixel at strategic zoom.

## The scale contract

One unit is 10 m. A tile is 100 units. These follow:

| Thing | Real size | Units | Notes |
|---|---|---|---|
| Peasant hut | 8–10 m | **0.8–1.0** | The basic building block of every district |
| Hall / longhouse | 20–30 m | 2–3 | School, Lodge, Barracks blocks |
| Castle keep | 40–60 m | 4–6 | Plus walls spanning 20–30 units |
| Tower | 15 m across, 25 m tall | 1.5 × 2.5 | |
| Field plot | 100–200 m | 10–20 | Several per claimed tile |
| Person | 1.8 m | **0.18** | At rest, before any zoom inflation |
| Cart | 4 m | 0.4 | |
| District footprint | 300–700 m | **30–70** | Most of a tile, not all of it |

**Every current number in `StructureDistricts` and `PresentationScale` is ~10x too
big and must be re-derived from this table.** Today a hut is 6–9 units (60–90 m) and
a figure is 1.6 units (16 m).

## What each district is made of

Composition is already in `StructureDistricts`; what changes is that pieces become
small and numerous, and that **the sim's own numbers drive the count**:

| Kind | District | Driven by |
|---|---|---|
| House | A hamlet: 15–40 huts, yards, a lane, fences | Residents living there |
| Farm | Farmstead, barn, and **fields drawn on its claimed tiles** | `claimX/claimY` on the wire (Farm claims 15 tiles) |
| LumberCamp | Cutting yard, log piles, and clearings in its claimed forest | Claims (8 tiles) |
| Quarry / Mine | Pit head, spoil heaps, sheds, cart track | Workers |
| Castle | Keep, curtain wall, gatehouse, bailey buildings | Level, garrison |
| Barracks | Walled compound, drill yard, ranked tents | Garrison size |
| Dock | Quay, warehouses, jetties, moored boats | Boats present |
| Rubble | The same district, broken: roofless shells, scorched plots | What it was |

Claims becoming visible is a real gameplay win: a Farm's fields *are* the land it has
claimed, so "why can't I build here" answers itself on screen.

## Consequences

1. **LOD is required, in three bands** (the zoom bands already exist):
   - *street* — real geometry, individual huts and people;
   - *overview* — district silhouettes, simplified, people as small crowds;
   - *map* — a marker per structure, no geometry.
2. **Instancing by piece, not by building.** A hamlet of 30 huts across 200 houses is
   6,000 instances; they must batch by mesh and quantized tone, which
   `DistrictBlock.ToneSteps` already sets up.
3. **`PresentationScale` changes meaning.** `NearInflation` becomes true scale (a
   person is 0.18 units), and inflation applies only in the upper zoom bands, where
   legibility beats literal size.
4. **Units are cohorts of people** — a crowd of small figures, not one giant.
5. **Roads and walls are kilometre-scale line features** and stay per-tile: a wall
   segment is a rampart with towers along it, not a garden fence.
6. **The art kit is modular**: huts, roofs, fences, plots, carts, piles, tents. A
   dozen reusable pieces build every district in the game.

## Next: districts read sim state

The rescale landed with districts still composed from `(kind, tileX, tileY)` alone, so
a House draws 18–32 huts whether two people or twenty live in it, and a Farm's strips
sit around the farmstead rather than on the land it actually works.

Both want the same change: **the recipe needs the structure row, not just its kind and
position.** `StructDto` already carries what is needed, and the wire already ships it.

- **Counts from state.** Huts from residents, tents from garrison, crates from
  holdings, spoil heaps from how long a mine has run.
- **Fields on claims.** A Farm claims 15 tiles and a LumberCamp 8; `claimX/claimY` are
  already on the wire and already drawn as outlines by `SelectionRenderer`. Drawing the
  strips ON those tiles makes the claim visible as worked land, and answers "why can't
  I build here" by looking rather than by reading a rejection.
- **One constraint:** `EntityPicker` builds its pick volumes from the same recipe, so
  whatever the renderer sees, the picker must see too — pass the row to both or neither.

## Future expansion

- **District growth over time.** A House that has held ten residents for fifty days
  can gain outbuildings; a Castle can gain a curtain wall at a level. All read from
  sim state, all presentation-only.
- **Regional character.** The same kit, different wood/stone/roof palettes by biome or
  culture.
- **Battles inside a district.** Street fighting in a hamlet becomes possible to stage
  because the hamlet has streets.

## Acceptance

- At street zoom, a person stands beside a hut at believable relative size, and the
  hamlet fills a recognisable part of a kilometre.
- At overview zoom, each structure kind is identifiable by district silhouette alone.
- At map zoom, markers only, and the frame rate is unchanged from today.
- A Farm's fields visibly cover the tiles it has claimed.
- A screenshot at the board's camera angle reads as a settled landscape rather than
  isolated objects on empty ground.

## Update 2026-09-16: honest sizes are the base, a dial fills the tile

The scale contract above stands as the **authoring** scale — every recipe is still
composed at 1 unit = 10 m with a 9 m hut — but the honest hamlet covered a third of
its tile and read as a crumb from any camera that also saw the hill behind it. Rather
than fatten each recipe by hand (which would quietly break the relative proportions the
contract exists to protect), a single asset, `EntityScaleSettings`, multiplies the whole
layer:

- **`DistrictScale`** (default 2.5) scales every district block's offset and size
  together, applied once inside `StructureDistricts.For`. Renderer and picker both
  come through that call, so the pick volume is the drawn building by construction —
  the constraint in "Next: districts read sim state" is honoured without a second code
  path. Walls and gates are exempt: they already span their tile.
- **`FigureScale`** (default 2.5) multiplies the zoom inflation curve inside
  `PresentationScale.SetZoom`, whose `Near`/`Far` ends moved onto the same asset. Every
  reader of `Inflation` — figures, cohort spread, hull, crown, selection ring, torch
  height — grows together, so a crowd stays a crowd.

The lost alternative was per-kind scale factors in the recipes. They would have let a
hamlet grow more than a tower, but every future recipe would have had to know about
them, and the "one dial, one place" rule from the look-tuning decision was worth more
than that freedom. If a kind ever genuinely needs its own factor, add it to the recipe,
not to the dial.

Consequence for the first acceptance point: "believable relative size" at street zoom
is now a setting, not a constant. `DistrictScale 1, FigureScale 1` is the honest world;
the defaults trade that for a tile that reads as settled.
