# Rivers: a crossing penalty on tile edges

## The decision

A river is a **per-tile edge mask** in `TileGrid` (`RiverEdge`, four bits: N/E/S/W)
saying "a river runs along this edge of this tile". A hop from tile A to its
4-neighbour B pays a flat `RiverConstants.CrossingCost` on top of terrain **if and
only if the edge they share carries a river**. Walking along the bank pays nothing.
Boats ignore rivers. Rivers are carved once by the generator from the shaped
elevation field, frozen into the grid, snapshotted, and shipped to the client in the
static genesis payload as one more full-map integer array.

To make that possible the pathfinder's cost delegate became an **edge cost**,
`(from, to) -> int`, and `MovementCost.PlanCost` takes the source tile as well as
the destination. That is the only structural change; everything else is additive.

## Why

### Cost had to become a property of the hop

Every movement cost in the sim was a property of the tile being *entered*:
`Pathfinding.FindPath` took `Func<TileCoord,int>` and paid `costFn(neighbour)`;
`PlanCost` knew only the destination. "You pay to cross the river, not to walk
beside it" is a statement about the pair of tiles, so no per-tile cost can express
it. `ExecutionCost` already took `(from, to)` — the hop scheduler, the group
scheduler and the client hop animation (`ViewProjector.FillHop`) all had both tiles
in hand — so only the planning side needed the extra argument. The Manhattan
heuristic stays admissible because a crossing only ever *adds* cost.

The three copy-pasted planning lambdas (`MoveIntent`, `MoveGroupIntent` ×2) became
one `MovementCost.Planner(...)` factory, so the signature change landed in one place
and the next per-hop modifier (bridges, fords) will too. The one-argument
`FindPath` overload survives as a convenience for callers that genuinely price
tiles (reachability checks, tests); it wraps the edge form.

### Edges, not tile centres

The alternative — a river through a tile's **centre** with a per-tile axis
(NS / EW / bends) — was the first picture and lost:

- A tile is a kilometre. A unit "on" a river tile is on neither bank. A crossing
  then spans **two hops** (enter the river tile from the west, leave it to the
  east). Charging on entry penalises a unit that steps onto the bank and walks
  along it; charging correctly means remembering which side it came in on — a new
  anchor on the unit and an A* over `(tile, entry side)` states. Charging half on
  every perpendicular entry and exit is stateless but charges the bank walk.
- The edge model needs no state at all: the river lies *between* tiles, a hop
  either crosses it or it doesn't, and the invariant is a single symmetry rule
  (bit N on A ⇔ bit S on the tile above A) that genesis validates and throws on.
- Confluences, sources and mouths fall out of the mask with no special cases, the
  same way `docs/roads-as-draped-ribbons.md` gets crossroads from arm counts.
- Bridges and fords later are an edge attribute or a paired-road check that
  waives the penalty. Nothing to redesign.

The mask still *belongs to the tile* — it is the tile's river data and its
direction — so the "modifier attached to a tile" framing holds.

### Additive constant, Foot only

The penalty is a flat number of game-minutes added to the terrain term in
`MovementCost.TerrainCostFor`, following the `terrain + crowding` shape
`ExecutionCost` already has. A multiplier was rejected: fording a river is a fixed
chore (find the shallows, wade, dry out), not a function of how good the far bank's
road is. Roads therefore do not reduce it — that is what a bridge will be for.

Only `Traversal.Foot` pays. A river is not a `Biome.Water` tile, so boats neither
sail it nor are blocked by it; navigable rivers are a different model (a chain of
tiles, not edges) and are explicitly out of scope.

Cart buffs multiply the finished hop cost as before, so a cart fords slower still.

### Fog needs nothing

Terrain cost has always been planner-visible everywhere (roads included), and
terrain topology is public on the wire by an earlier decision (`WireV2.cs`).
Rivers are terrain. `PlanCost` and `ExecutionCost` agree on the river term by
construction; AI brains path through intents and inherit it.

### Carved by the generator, frozen, then public

Rivers are derived server-side from the **shaped** elevation field, in
`MapGenerator.Build` after `ContinentShaper` and before the freeze, for exactly the
reason `docs/ocean-ringed-continent.md` shapes elevation instead of post-carving
biomes: sim water, drawn water and pathing must agree by construction. The client's
erosion bake is decorative and the server never runs it, so it cannot be the
source.

`RiverCarver` works on the **corner grid** — the same corner heights the client's
terrain mesh uses (mean of the surrounding tiles). It priority-floods from every
ocean corner so each corner has a monotone descent to the sea (raw noise is full of
pits), keeps each corner's flood parent, picks sources on high land corners spaced
apart, and follows the parent chain down to the sea, stopping at the first Water
tile, the map edge, or an edge that already carries a river (a confluence). Each
corner-to-corner step marks the boundary between two tiles on both of them.

Float math is fine here — the generator is off the replay path — and the result
is an integer mask the sim cannot tell from a hand-authored one.

## What gets built

- `Sim.Core.World.RiverEdge` (flags byte) + `TileGrid.RiverEdgesAt / SetRiverEdges`.
- `Sim.Core.Rivers.River.Crosses(grid, from, to)` (pure read) and
  `RiverConstants.CrossingCost`.
- `GenesisSpec.Rivers`; `Genesis.Build` validates symmetry.
- Snapshot **v30**: a river byte per tile after the biome grid; hashed.
- `Pathfinding.FindPath(grid, a, b, Func<TileCoord,TileCoord,int>)`;
  `MovementCost.PlanCost(world, from, to, ...)`; `MovementCost.Planner(...)`.
- `GenerationConfig.RiverCount / RiverSourceMinElevation / RiverSourceSpacing /
  RiverMaxLength`; `GeneratedMap.Rivers`; `RiverCarver`.
- `WorldDto.River` (v2 genesis, row-major, full map).
- Client: `RiverNetwork` + ribbon mesh along tile boundaries, draped like roads.

## Acceptance tests (`RiversTests`)

- A hop across a river edge costs terrain + `CrossingCost`; the hop along the bank
  costs plain terrain; the reverse crossing costs the same.
- A* takes a detour around a river when the detour is cheaper than the crossing,
  and crosses when it isn't.
- `Crosses` and `PlanCost` are pure reads (100× hash check).
- Boats: a river edge changes nothing for `Traversal.Water`.
- Genesis rejects an asymmetric mask.
- Snapshot round-trip preserves the mask; twin-run on a generated world with
  rivers hashes equal.
- Generated maps: the mask is symmetric everywhere; every river component reaches
  a corner touching Water; rivers exist across seeds; same config ⇒ same rivers.
- `WorldDto.River` mirrors the grid.

## Future expansion

- **River irrigation.** M21's water-recovery latch checks proximity to
  `Biome.Water`; treating a river edge as water in `WaterProximity.IsNearWater` makes
  riverbanks renewable farmland. One condition. This is the gameplay payoff and the
  next slice.
- **Canals from rivers.** `PlaceCanalIntent` requires adjacency to Water; letting a
  river edge count is a small extension.
- **Bridges / fords.** An edge attribute (ford = reduced cost) or "road on both
  tiles" (bridge = no cost) inside `River.CrossingCostFor`. The per-hop cost seam
  already exists.
- **River moisture.** Biome classification could read distance-to-river so valleys
  green. Deferred until the look asks for it.
- **Navigable rivers.** Out of scope; needs a tile-chain model and a third
  traversal table.
