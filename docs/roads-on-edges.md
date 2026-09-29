# Roads on edges — a road is the lane between two tiles

**Status:** decided 2026-09-18; **sim and production client built the same day** (uncommitted). Milestone **M34**. See the update at the bottom for what the client did with the crossing.
**Depends on:** M2 roads as terrain memory (`Road.cs`, lazy decay), rivers
(`docs/rivers.md`, the `RiverEdge` mask and the `(from, to)` cost delegate),
the ribbon renderer (`docs/roads-as-draped-ribbons.md`, corner-node arms),
bridges (`docs/bridges-and-fords.md`, decided, unbuilt), walls
(`docs/walls-and-gates.md`, tiles on purpose).

## The decision

A road is a **per-edge entity**. The unit of road condition is no longer a
tile but the **arc between two 4-adjacent tiles**: the lane a unit walks when
it hops from A to B. `GameWorld.Roads` becomes a sparse map keyed by
`TileEdge` (a canonical unordered adjacent pair), each hop credits exactly one
arc, and the Foot cost of a hop is the destination's terrain reduced by the
condition of the arc crossed. Units keep walking tile centre to tile centre.
Rivers keep their mask. Nothing about the walk graph changes; only what a
road *is* changes.

Two rules the user locked:

1. **Roads are edges, and only edges.** No through-tile lanes, no diagonal
   lanes. The eight-lane menu from the markup (four edges, two through, two
   diagonals, each with its own cost) is rejected; see *Why*.
2. **Rivers and roads share one edge vocabulary.** A river edge and a road
   arc are addressed the same way (`TileEdge`), so a road along a bank and a
   road across a ford are both ordinary arcs, and the bridge bit lands on the
   same edge the crossing road uses.

## Why

### Two kinds of edge, and they are dual to each other

The tiles-versus-edges conversation started with walls and roads. Both live
"between tiles", but they are opposite things:

- A **barrier** sits *on* the boundary between A and B and is **crossed** by
  the hop A→B. Rivers are barriers. A bridge is a barrier's exemption.
- A **connection** is walked *along*. A road is a connection.

On a square grid these are geometrically dual. The boundary segment between
A and B is crossed by the hop A→B, and the *same* segment, seen as a line, is
walked by a hop between the two tiles on either side of it, parallel to the
boundary. The renderer already exploits this: with the node at the tile's
south-west corner, the arm for the hop A→East(A) is drawn along A's south
boundary, and the arm for A→North(A) along A's west boundary. Every boundary
segment on the map carries exactly one hop's lane. So "roads on edges" and
"roads on hop arcs" are the same statement, and the client has been drawing
the second while the sim stored the first's tile.

That is the inconsistency this doc closes. Today a road tile spreads its
reduction to all four hops into it, so one worn lane becomes a lattice of
four; the renderer draws arms to every road neighbour, so a single lane looks
like a grid of streets around each tile; and a house on a road tile is on the
road's data even though the ribbon runs beside it.

### Why not the eight-lane menu (the markup)

The picture had four edge lanes, two lanes through the centre and two
diagonals per tile, each with its own cost, structures killing the lanes that
pass through them. It does not survive contact with A*:

- A* returns the strict minimum. For any destination off the axis, a
  diagonal at 1.41 beats two edges at 2.0, so the optimal path is the octile
  staircase. Roads here are worn by traffic, so open country fills with
  diagonal desire lines through tile centres, each dying the moment someone
  builds on the tile. Price the diagonal above the edge pair and nobody ever
  takes it. There is no weight that yields a mix; variety comes from
  obstacles, not costs.
- A worn edge road at a third of terrain beats a bare diagonal, but it never
  gets worn in because everyone takes the diagonal first.
- It needs a finer walk graph (centre + four corners + four midpoints per
  tile). Every consumer of tile paths changes shape: movement anchors,
  crowding, combat-per-tile, vision, snapshot, wire. The blocking idea was the
  good half of the markup and it needs none of that (see *Structures*).

### Why not corner walkers (the honest dual grid)

The other way to make roads edges is to move the walkers onto tile corners:
corners are nodes, tile boundaries are arcs, tiles are the cells between four
corners. Roads are then arcs by construction and structures block through-
traffic for free. It lost because:

- **Unit position is a tile everywhere.** Crowding, combat, extraction
  claims, vision radius, siege adjacency, the hop scheduler and the entire
  client are keyed on `TileCoord`. A corner coordinate is a rewrite of the
  sim, not a road feature.
- **Rivers would have to move.** A river on corner arcs is never crossed by a
  corner hop; arcs only meet at nodes, so "crossing" would need entry-side
  state at each river corner. That is the two-hop-with-memory model
  `docs/rivers.md` rejected, relocated. Rivers would have to go to centre
  arcs (through tiles), which the river doc, the carver, the relief bake and
  the bridge doc all built against.

Keeping walkers on centres and pricing the arc is the model that leaves
rivers, bridges, walls and every tile system untouched.

### Storage: bits go on both tiles, payloads go on the canonical arc

The repo has one edge convention already: the river mask is stored on both
tiles with a symmetry invariant, and reads look at `from`. That is right for
a bit that is set once. A road arc carries a mutable payload (condition plus a
decay anchor), and storing it twice means mutating it twice. So:

- **Bits** (river, bridged, later fords or fences) live in per-tile masks on
  both sides, symmetric, validated at genesis and restore. Unchanged.
- **Payloads** (road condition) live once, keyed by the canonical `TileEdge`:
  the pair `(A, B)` with `A` the west or north tile. There is exactly one
  owner and no invariant to validate.

Both are addressed through `TileEdge.Between(from, to)`, which is the
refactor this milestone pays for: `River.EdgeBetween` and `River.Opposite`
become members of the shared type, and `River.Crosses` takes either form.
Half-edge ownership (each tile owns its east and south arcs) is the same
thing as the canonical pair, stated per tile; the dictionary form keeps the
sparse shape `Roads` already has.

### Cost and credit stay the same numbers

`Road.EffectiveCost(world, from, to, now)` returns `terrain(to)` reduced by the
condition of `TileEdge.Between(from, to)`, with the proportional formula from
`docs/road-cost-reduction.md` unchanged. Terrain is still the destination's:
a road from grassland onto a mountain pays reduced mountain one way and
reduced grassland the other, as today. `CreditTraffic(world, from, to, now)`
credits the arc crossed. A route of N hops credited N tiles before and credits
N arcs now, so `BASE_GAIN`, `GAIN_FLOOR`, decay and the "about twenty
traversals to a first road" feel need no retune.

The river surcharge is additive and roads still never reduce it. A worn arc
across a river edge is a fording place that people keep using, which is
exactly the signal for where the bridge should go.

### Structures: no arc runs through a tile, so nothing needs killing

The markup's rule was "a structure on a tile zeroes any road that passes
through it, and pathing never goes through a structure". With edge roads the
first half is moot: arcs run between tiles, never across one, so there is no
through-road to zero. An arc *into* a structure tile is that structure's
approach and it keeps its condition; the renderer already ends such an arm at
the yard. The second half, structure tiles as destination-only for Foot
pathing, is **deferred** and recorded under *Future expansion* with a
recommendation against doing it now: a tile is a kilometre, farms are
structures, and a dense settlement becomes a maze. The visual reason for the
rule (roads through houses) is gone with this decision, and the mechanic is a
one-line follow-on in `PlanCost` and the mover's ground-truth check if the
war layer ever wants it.

### Roads and rivers on the same boundary

Because every boundary carries one hop's lane, a road arc and a river edge
can share a segment. That is not a conflict, it is a **bank road**: the arc
`(A, East(A))` drawn along A's south boundary, where the river between A and
South(A) runs. Both walkers are on A's side, so the client offsets the road
into A by the river's half-width. Pure presentation.

The **crossing** arc `(A, South(A))` is drawn along South(A)'s west boundary,
so it meets the horizontal river at A's south-west corner, while the bridge
(M33) lives on the edge and the site machinery marks its midpoint. The client
resolves this with an S-bend: a crossing arc's ribbon is routed through the
midpoint of the river segment it crosses, using the same `ArmControls`
mechanism that already bends arms around rises. Units follow the ribbon
through `RoadWalk`, so feet and span agree by construction. The bridge keeps
its sim identity on the edge, and its geometry is drawn where the road
crosses. The alternative, drawing the span at the corner, was rejected
because river vertices are tile corners and the carver turns there.

### Fog: an arc is known when either bank is explored

Roads are terrain memory gated by `Explored` (design 8.6). An arc has two
tiles; it ships when **either** is explored. Same rule the bridge doc chose
for "seen either bank", for the same reason: the planner prices the hop from
whichever tile it starts on. The known leak (an explored-once tile shows the
road's live condition) is unchanged and still deferred to the roads doc.

## Locked rules

- **Key.** `TileEdge` is a readonly struct `{ TileCoord A; TileCoord B }`,
  constructed only by `TileEdge.Between(from, to)`, which canonicalises (A is
  the west or north tile) and returns `null`/`None` for non-adjacent pairs.
  `Side(tile)` returns the `RiverEdge` direction of the edge as seen from
  `tile`. It lives in `Sim.Core.World` beside `RiverEdge`.
- **Storage.** `GameWorld.Roads : Dictionary<TileEdge, RoadState>`. Sparse,
  absent means condition 0, removed when decay hits 0. `RoadState` unchanged.
- **Reads.** `Road.ConditionAt(world, edge, now)` and
  `Road.EffectiveCost(world, from, to, now)` are the only public reads. The
  tile-only `EffectiveCost` overload is **deleted**; a tile-only reachability
  check (`FormGroupIntent`) prices plain `Grid.TerrainCost`.
- **Write.** `Road.CreditTraffic(world, from, to, now)` from
  `MoveArrivalEvent`, which already holds both tiles. Still the one mutation
  point. Decay-then-gain order and the carry are unchanged.
- **Water.** No arc touching a Water tile ever holds a road. `CompleteCanal`
  removes all arcs incident to each flooded tile. Boats are unaffected.
- **Structures.** No change. Structure tiles are enterable; arcs into them
  keep condition.
- **Snapshot v32.** The roads block writes `(A.X, A.Y, axis, condition,
  lastDecayTick)` sorted by `A.Y, A.X, axis` with `axis` 0 = East, 1 = South,
  filtered by effective condition as today. No migration: v31 snapshots are
  rejected by the existing version check, which is how every prior bump
  behaved.
- **Wire.** `RoadDto` gains `Axis` (0 East, 1 South); `X, Y` are the owner
  tile. Emitted in **all three** view builders in `ViewProjector` (the v1
  `Project` the AI brains read, the v2 view, and the reveal view). Gated by
  either-endpoint explored. `WireVersion` stays 2; the client and server are
  deployed together and the DTO grows a field.
- **Client.** `KnownWorld.RoadConditionAt(tile)` becomes
  `RoadConditionAt(tile, axis)` or `RoadConditionAt(TileEdge)`. `RoadNetwork`
  builds one ribbon per arc from the arc set directly instead of inferring
  arms from road-tile adjacency; the corner-node connection rule, the
  perpendicular arrival and the meander are unchanged. `RoadWalk`, the
  minimap and full-map painters, `TileDresser` and `TerrainColorizer` read
  arcs. `RiverNetwork` and `RoadNetwork` address boundary segments through the
  same `TileEdge` so the bank offset and the crossing S-bend are computed
  from one lookup.
- **AI.** Brains path through intents and inherit the cost. Nothing reads
  `Roads` directly today; the v1 view field changes shape and the brain's
  view reader follows.

## Mechanics (seams)

| Piece | Where | Change |
|---|---|---|
| Edge key | `src/Sim.Core/World/TileEdge.cs` (new) | canonical adjacent pair; `Between`, `Side`, `Opposite` moved here from `River` |
| Rivers | `src/Sim.Core/Rivers/River.cs` | `Crosses` and `CrossingCostFor` accept a `TileEdge`; behaviour identical |
| Road state | `GameWorld.Roads`, `RoadState` | key type changes; payload unchanged |
| Road math | `src/Sim.Core/Roads/Road.cs` | `(from, to)` reads and credit; tile overload deleted |
| Cost | `MovementCost.TerrainCostFor` | passes `(from, to)` to `Road.EffectiveCost`; already has both |
| Credit | `MoveArrivalEvent` | `CreditTraffic(world, leftTile, To, now)` |
| Reachability | `FormGroupIntent` | plain terrain cost |
| Canal | `BuildCompleteEvent.CompleteCanal` | remove the four incident arcs |
| Snapshot | `Snapshot.cs` | `FormatVersion = 32`; roads block keyed by arc |
| Wire | `WireDtos.RoadDto`, `ViewProjector` ×3 | `Axis` field; either-endpoint explored gate |
| Host smoke | `Sim.Host/Program.cs` | route summary lists arcs |
| Client | `KnownWorld`, `RoadNetwork`, `RoadMeshBuilder`, `RoadWalk`, `MapPainter`, `TileDresser`, `TerrainColorizer`, `ContextPanel` | arc-keyed; bank offset; crossing S-bend |
| Bridges (M33, later) | `River.CrossingCostFor`, client span placement | unchanged in the sim; span drawn where the crossing arc meets the river |

Determinism: no new anchors, no new scheduled events, no new write sites.
`ConditionAt` stays a pure read with the same lazy catch-up. Recovery is
free.

## Knobs

None new. `RoadConstants` is untouched by design; the credit count per route
is identical to today.

## Acceptance tests (`RoadsOnEdgesTests`, plus edits to `RoadTests`)

- **One lane, not four.** After traffic runs A→B along a straight route, the
  hop A→B is reduced and the hops from A to its other three neighbours are
  plain terrain. Today's tile model fails this.
- **Symmetric arc.** Condition of `(A, B)` read from B→A equals A→B;
  `TileEdge.Between(a, b) == TileEdge.Between(b, a)`; non-adjacent pairs
  yield `None`.
- **Same numbers.** A route of N hops walked K times produces the same
  condition on each arc that the tile model produced on each tile, for the
  same `RoadConstants`. Decay carry and same-tick stacking tests port
  unchanged with the key swapped.
- **Terrain is the destination's.** Grassland→mountain on a maxed arc costs
  reduced mountain; the reverse costs reduced grassland.
- **River untouched.** A maxed arc across a river edge costs reduced terrain
  plus the full `CrossingCost`; along the bank, reduced terrain only.
- **Canal clears arcs.** Flooding a tile removes every arc incident to it and
  none beside it.
- **Purity.** `ConditionAt` and `EffectiveCost` 100× hash check.
- **Snapshot v32** round-trips the arc set; a v31 stream is rejected with the
  existing message.
- **Wire gate.** An arc with exactly one explored endpoint ships; with none it
  does not; the v1 and v2 builders agree.
- **Twin-run** on a generated world with traffic hashes equal.

## Future expansion

- **Structures as destination-only** for Foot pathing: `PlanCost` returns
  `Impassable` for a structure tile that is not the goal, mirrored in the
  mover's ground-truth check. One line each. Deferred; recommended against
  until the war layer asks, for the maze reason above.
- **Built roads.** A `PlaceRoadIntent(tile, axis)` that seeds condition
  through a site, the bridge's build shape on a dry arc. The key already
  exists; this is a catalogue row and a `BuildCompleteEvent` branch.
- **Fords** (M33) and **bank roads** compose: a ford is a river bit, a bank
  road is an arc, the same boundary can hold both.
- **Barrier edges.** Fences, hedgerows and field boundaries are river-shaped:
  a symmetric bit on both tiles with a crossing surcharge, read from `from`.
  They would follow the bits-on-both-tiles convention above and plug into
  `TerrainCostFor` as a second additive term. Walls stay tiles; see the
  addendum in `docs/walls-and-gates.md`.
- **Vertices** (corner state: towers, gate posts, junction pieces) are not
  needed by anything designed and are not reserved. If they arrive they get a
  third convention, owned by the tile whose south-west corner they are.
- **Road-aware AI rungs** (a builder that roads the haul belt) read the same
  arc set the brain's view already ships.

## Update 2026-09-18 — built; the crossing S-bend became a node nudge

Sim side shipped as specified (`TileEdge`, arc-keyed `Roads`, snapshot v32,
`RoadDto.Axis`, 1114 tests green). Client shipped: `KnownWorld` holds two
arc slots per tile (`RoadArcAt`, `RoadBetween`, and a per-tile `RoadConditionAt`
that is the best incident arc for the legibility layer and context card);
`RoadNetwork` draws exactly one span per known arc, two half-arms meeting at
the midpoint of their end nodes; `RoadWalk` curves a hop only when *its own
arc* carries a road and neither tile has a structure, and stands people on
the node of any road-touched open tile; the chart, the tile dresser's road
facing and the terrain renderer's road hash read arcs.

**The S-bend was dropped in favour of a node nudge.** Working it through on
the client showed the crossing arc does not meet the river at the segment
midpoint or at a corner *beside* it: it meets it at the end **node** itself,
which is a river vertex shared by up to four arms. Routing one arm through
the midpoint would displace a hub that other arms depend on. The rule that
falls out cleanly instead: `RoadNetwork.NodeOf` pushes a node that sits in a
channel (`TerrainSampler.RiverDistanceAt` under 1.6 half-widths) diagonally
into its own tile, in steps, until it is dry; every arm from it follows,
the two halves of an arc meet at the midpoint of the *nudged* nodes so they
still join exactly, and `ChooseBend` adds a large penalty to any bend
candidate in the water. A bank road therefore runs beside the channel and a
crossing arc fords at an angle from dry bank to dry bank. Where the bridge
span is drawn is now M33's call, made against that geometry rather than a
midpoint rule that no longer describes where the road is.

Known and left alone: the client maps condition to width with a divisor of
100 while the sim's maximum is 1000, so any arc past a tenth of maximum
draws at full width. Pre-existing, cosmetic, and a one-constant change once
someone decides what a "good" road should look like.
