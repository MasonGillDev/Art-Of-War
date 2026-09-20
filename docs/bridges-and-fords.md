# Bridges and Fords — paying off the river toll

**Status:** decided 2026-09-17, reviewed and amended the same day; not yet
built. Proposed milestone **M33**.
**Depends on:** rivers (`docs/rivers.md`, the `RiverEdge` mask and
`River.CrossingCostFor`), canals (`docs/canals.md`, the build-that-mutates-
the-grid shape), M2 roads as terrain memory, M3 `Sight.Reveal` as the one
write path for per-player memory, M26's plan/ground-truth fog split.

## The decision

A **bridge** is a constructed edge attribute. The player places it on one
river edge; a construction site on the near bank is hauled and built by the
ordinary site machinery; completion sets a *bridged* bit on that edge of both
bank tiles and leaves **no structure**. A bridged edge costs nothing to cross.

Two rules the user locked:

1. **Bridges cannot be destroyed.** No health, no raze, no demolish. Once
   built, a bridge is terrain.
2. **Bridges are hidden from other players until discovered.** A player knows
   a bridge only after *seeing* either bank tile while the bridge stands.
   Known bridges are per-player terrain memory, written by `Sight.Reveal`,
   and they persist through re-fog like roads do.

A **ford** is the same bit placed by world generation: a shallow edge with a
reduced crossing cost, discovered the same way. Fords are deferred to a later
slice; the mechanism is shared and is recorded here so the bridge build leaves
the seam.

## Why

### What it fixes

Today a river is a pure tax. Every foot hop across an edge that carries one
pays a flat `RiverConstants.CrossingCost` (120 game-minutes) on top of the
destination tile's terrain. Roads never touch that surcharge, by design:
fording is a fixed chore, not a function of the far bank's road. So a busy
crossing becomes road, ford, road, and the two-hour toll stays forever.
Boats ignore rivers entirely. There is no way to pay the toll off, which is
exactly the hole the rivers doc left for "bridges and fords later".

### Why an edge attribute, not a structure

The wall discussion (`docs/walls-and-gates.md`; the edges-versus-tiles
addendum is still to be written there) settled the rule of thumb: **edges
are for inert things, tiles are for things that carry health, ownership,
construction, combat and rubble.** Rivers earned edges
because they carry none of those. A bridge that cannot be destroyed carries
none of them either once built, so it belongs beside the river bit, not in
the structure table.

A bridge as a structure on a bank tile lost on three counts: it consumes a
kilometre tile of farmable land for a span of stone, it reopens the "why is a
wall a whole tile" complaint we just declined to fix in the sim, and it
drags in health and siege rules for an object the user has said cannot be
attacked.

### Why the canal build shape

A canal is placed as a job on tiles, hauled and built, and its completion
mutates the grid (`Biome.Water`) with no resulting structure. A bridge is
the same idea on an edge. Reusing it means: one construction site on the
near bank (site health, haul destination, builder assignment, the M30
walk-then-build goal all unchanged), one new branch in `BuildCompleteEvent`,
zero new anchors. The canal's "extend from water" ordering constraint does
not apply, so the site is a single tile, never a path.

### Why indestructible (accepted trade-off)

A destroyable bridge needs health, a target for `FortSiege`, a rubble state
for the edge, and an answer to "who stands where when attacking a span". Each
is the edge-flavoured twin of a tile system, which is the exact cost the wall
decision refused. The cost of *not* having it: a rival can use your bridge
against you. That is a real strategic consequence and a good one. Bridges are
a gift to whoever controls the banks, which pushes the war game toward
holding the crossing with gates and manned towers (`docs/manned-towers.md`)
rather than blowing it up. Burning bridges is recorded under future
expansion as a separate decision on the same bit, should the war layer ask.

### Why hidden until discovered, and why the walls fog split

Terrain topology is public on the wire and rivers ride it. A bridge is a
player's work. Shipping it in the public terrain block would leak
construction through fog, so bridges follow **roads**, not rivers: the
ground truth lives in the grid, each player's knowledge lives in per-player
memory, and the wire projects only what the player knows.

Planning and execution split exactly as walls do (`Fortification.BlocksPlan`
vs `BlocksMover`): the A* planner prices a crossing from the *planner's
remembered bridges*, the hop scheduler prices it from ground truth. A unit
marching across an unknown bridge pays nothing on the ground and the player
learns about it by watching the hop go faster, which is the "learned by
observation" pattern the AI already lives by. A player who remembers a bridge
is never wrong, because bridges never go away.

One deliberate leak follows from that and is accepted: `ViewProjector.FillHop`
prices every *visible* unit's current hop from ground truth so the client can
animate it, so a rival seen crossing a bridge you do not know about shows a
toll-free hop. That is observation, not the wire handing over the map: the
unit is in your sight and it visibly moves faster. Closing it would mean
pricing the hop from the viewer's memory instead (a per-view read, cheap);
recorded here so the choice is known, not rediscovered.

Rejected alternatives:

- **Explored-tile visibility** (a bridge is known once either bank was ever
  explored). Lost: a player who scouted a valley last season would "know"
  about a bridge built after they left. Roads carry that leak today and the
  roads doc defers fixing it; bridges are rarer, more strategic, and cheap to
  do properly from the start.
- **Public, like rivers.** Lost: the user's call, and it would turn every
  bridge into a free map ping of "someone is building here".

## Locked rules

- **Placement** is `PlaceBridgeIntent(tile, edge)`. Validation: the tile is
  in bounds and land; `edge` is one of N/E/S/W and the tile's river mask has
  that bit; the far bank is in bounds and land; the edge is not already
  bridged **and no in-flight bridge site already targets it from either
  bank** (a scan of construction sites' `BridgeEdge`, mirroring
  `CanalReservation.IsReserved`; without it two players, or one player
  twice, fund the same span and the second completion is a wasted haul);
  the near-bank tile has no structure, no extraction claim and no canal
  reservation (it hosts the site). The far bank is *not* reserved: a
  structure there is fine, a bridge lands on the edge, not the tile. A bank
  tile that already holds a structure (a castle on the river) can never
  host the site, so that crossing is placed from the other bank; the client
  placement mode should say so rather than reject silently.
- **One site, near bank.** `ConstructionSite` gains a `BridgeEdge` field
  (`RiverEdge.None` for every other site). Cost and duration are catalogue
  rows for `StructureKind.Bridge`, single-tile, never path-scaled. Site
  health is the ordinary 25: an unfinished bridge is scaffolding and can be
  razed like any site, which is the only moment a bridge is ever attackable.
- **Completion** sets the bridged bit on `tile` for `edge` and on the far
  bank for `River.Opposite(edge)`, removes the site, and leaves no structure.
  Two invariants, validated at genesis and re-validated on Snapshot restore:
  the symmetry rule rivers already have (a bridged bit on my North is a
  bridged bit on my northern neighbour's South), and **bridged implies
  river** on the same edge of the same tile. A hand-built mask with a
  bridge over dry land, or over one bank only, throws.
- **Cost.** `River.CrossingCostFor` returns 0 when the edge is bridged. Road
  condition on each bank still applies through the ordinary terrain term, so
  a bridged road crossing is priced like any road hop. Carts pay nothing
  extra. Boats are unchanged (a river is still not water).
- **Fog.** `GameWorld.RememberedBridges[playerId]` is a per-tile mask, the
  `RememberedBiome` shape. `Sight.Reveal` writes every revealed tile's
  current bridged mask into it **and the mirrored bit onto the far bank**:
  the bit for one edge lives on two tiles, and "seen either bank" has to
  mean the planner finds it from whichever bank it starts on. A memory that
  held only the revealed tile's byte would price a crossing at full toll
  from the bank the player never looked at. Memory is therefore
  self-symmetric, exactly like the grid, and `IsBridged(memory, from, to)`
  is one lookup on `from`. It is never cleared. Bridged bits on an edge
  neither bank of which the player has seen since the bridge completed are
  absent from their memory. `MovementCost.PlanCost` reads the *planner's*
  memory; the hop scheduler and `ExecutionCost` read the grid.
- **The owner learns on completion.** `BuildCompleteEvent` writes the new
  bits into the owner's memory directly (the builder is standing on the
  bank, but a reveal is not guaranteed that tick, so the write is explicit).
- **Wire.** `ViewDto` gains a per-player `Bridges` list of `(x, y, mask)`
  projected from `RememberedBridges`, fog-gated like `Roads`. The public
  `WorldDto.River` mask is untouched. The AI brain reads the same field,
  so bridge knowledge is fair by construction.
- **Bridges cannot be removed** by any intent or event. Canal flooding
  across a bridged edge is rejected at `PlaceCanalIntent` (you cannot dig a
  canal under a bridge; deferred, see below).

## Mechanics (seams)

| Piece | Where | Change |
|---|---|---|
| Edge bits | `RiverEdge` / `TileGrid._river` | four upper bits `BridgedNorth = 16` … `BridgedWest = 128`; same byte, no new grid bytes in the snapshot |
| Snapshot | `Snapshot.cs` | format version bump for the new per-player `RememberedBridges` block (the bits themselves ride the existing river byte); restore re-validates both invariants |
| Reads | `src/Sim.Core/Rivers/River.cs` | `IsBridged(grid, from, to)`; `CrossingCostFor` returns 0 when bridged; a memory-aware overload for planning |
| Placement | `src/Sim.Core/Rivers/PlaceBridgeIntent.cs` (new) | validation above; creates the site |
| Site | `ConstructionSite` | `BridgeEdge` field beside `CanalPath` |
| Completion | `BuildCompleteEvent` | new branch beside the canal flood: set bits both sides, write owner memory, no structure |
| Catalogue | `StructureCatalog` | `StructureKind.Bridge` row: build cost, duration, one builder, not placeable via `PlaceSiteIntent` (single entry point, like Wall and Canal) |
| Planning | `MovementCost.PlanCost` / `Planner` | crossing term reads the planner's `RememberedBridges`; execution unchanged |
| Memory | `GameWorld.RememberedBridges`, `Sight.Reveal` | per-player mask written on reveal; serialized |
| Wire | `ViewProjector`, `WireDtos.cs`, `WireV2.cs` | `Bridges` list beside `Roads` in BOTH view builders: the v1 `Project` the AI brains read and the v2 view the client reads; fairness depends on the first, the picture on the second; client draws the span and the site |
| Fords (later) | worldgen `RiverCarver` + a `FordCost` constant | same bits, placed at genesis, `CrossingCostFor` returns the reduced value |

Determinism: no new anchors, no new scheduled events. Placement is an
intent, completion rides the existing site event, memory is written at the
existing inverted pure-read wall. Recovery is free.

## Knobs (tests derive from these)

- `StructureCatalog[Bridge].BuildCost`: start at 30 Wood, 20 Stone.
- `StructureCatalog[Bridge].BuildDurationTicks`: start at 3 game-days.
- `RiverConstants.FordCost` (later): start at a third of `CrossingCost`.

## Acceptance tests

- **Toll paid off.** A foot unit crossing an unbridged edge pays
  `CrossingCost`; after the bridge completes the same hop pays 0; a hop on a
  non-river edge of the same tile is unchanged before and after.
- **Both banks.** The bit is set on both tiles with the symmetry invariant;
  a hand-built asymmetric mask throws on restore, and so does a bridged bit
  on an edge with no river.
- **One span per edge.** A second `PlaceBridgeIntent` on an edge with a site
  in flight, from either bank, is rejected.
- **Either bank suffices.** Player B reveals only the far bank: B's memory
  carries the bit on both tiles and B's planner, starting from the near
  bank, prices the crossing at 0.
- **Placement fences.** Reject: no river on the edge, far bank is water or
  off-map, edge already bridged, near-bank tile occupied or claimed. Accept:
  a structure on the far bank.
- **Scaffolding is mortal, the bridge is not.** Razing the site leaves no
  bridge; no intent or event clears a bridged bit after completion.
- **Hidden until seen.** Player B never sees the bridge tile: B's view
  carries no bridge and B's planner prices the crossing at full toll; B's
  unit walks it anyway and the hop costs 0 on the ground. B then gains
  vision of one bank: the bridge appears in B's view, B's planner prices it
  at 0, and it stays in B's view after re-fog.
- **Owner knows at once.** The owner's view carries the bridge the tick it
  completes with no reveal required.
- **Roads compose.** Road on both banks plus bridge prices as two road hops.
  Road on both banks with no bridge still pays the toll (the rivers rule).
- **Carts and boats.** A cart across a bridge pays only its multiplier on
  the terrain term; a boat's cost is unchanged.
- **AI fairness.** The Homesteader's route through a valley changes only
  after its view carries the bridge; a reflection pin that the brain reads
  `Bridges` from the view and never from the grid.
- **Headline.** Twin-run hash equality across place → haul → complete → cross
  → snapshot → restore → cross again, driver live and then replayed.

## What gets built now vs later

**Now (M33), server first:** bridged bits, placement intent with the
in-flight reservation, catalogue row, site field, completion branch, planner
memory split, `RememberedBridges` with its two-bank reveal write, wire field
in both view builders, tests above. **Then the client**, as its own step, in
the canal precedent: a placement mode that picks a river *edge* (a new input
affordance; tiles are what the client picks today), the canal's "one rule
differs" preview pattern, the span and the site drawn.

**Later, each on the seam this leaves:**

- **Fords** at genesis: the reduced-cost bit, placed by the river carver
  where the river is young and the banks are low. The exploration payoff.
- **Burning bridges**: if the war layer wants it, an owner-only demolish
  verb or a siege target on the site-less edge. A new decision, recorded as
  an addendum here, not a silent edit.
- **AI bridge building**: a rung in the irrigation mould (find the crossing
  the kingdom's haulers pay most for, place a bridge). Brains benefit from
  bridges with no rung today because the planner prices them.
- **Canals and bridges**: a canal crossing a bridged edge, or a bridge over a
  canal tile. Rejected at placement for now.
- **Wide rivers**: rivers are one edge wide; a two-tile span is out of scope
  until rivers are.

## References

- `docs/rivers.md` — the edge model and why rivers are not tiles.
- `docs/canals.md` — the build-that-mutates-the-grid shape.
- `docs/walls-and-gates.md` — the plan/ground-truth fog split; the
  edges-versus-tiles rule of thumb.
- `docs/movement-cost.md` — the per-hop cost seam.
- `docs/manned-towers.md` — holding a crossing instead of destroying it.
- `docs/architecture.md` §2 — pure-read walls, the one write path for
  per-player memory.
