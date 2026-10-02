# M42 — subtile movement: status

**Decision:** `docs/subtile-movement.md` (reviewed 2026-09-29, see its update).
**Status:** Phases 1 to 4 built 2026-09-29; phases 5 and 6 planned. Nothing committed.

**Baseline:** full suite green at the end of phase 4: 1,555 + 58, 0 failed.

**Where this sits:**
- It comes before making grid combat the default (M43: the old M42 list in
  `docs/m41-status.md`, the AI and the balance pass).
- **Grid worlds only until then.** The default game moves and fights exactly as before.
- **Test discipline:** focused filters while iterating, the full suite at the end of each
  phase. Build with `-p:OutDir=<scratch>` if a server is running.

## Phases

| # | Phase | What | Status |
|---|---|---|---|
| 1 | Every unit has a subtile | `Unit.Subtile`; placed centre rows first on usable subtiles when created (genesis, births, spawns, landing); only hostile owners share one; pop-out when the ground changes and on peace; saved (v43). Hops that land before phase 2 place the unit the same way, so the invariant holds between phases | **built**, 17 tests |
| 2 | World moves walk in | shared tile-entry method extracted from `MoveArrivalEvent` (`TileEntry.Enter`); a hop lands on an entry-edge lane, then a unit that stops walks to the back at quarter-hop pace (`SubtileWalk`); lanes as the bottleneck (wait on the source tile, retry per step time); groups land in files; the cap counts non-hostile neutrals. **Moved to phase 4:** a hop onto an open board put off to the beat (it only makes sense once the board reads subtiles and the outside lane is gone) | **built**, 12 tests |
| 3 | Subtile routes | `SubtileRouteIntent`: a drawn chain of adjacent subtiles within the 3×3 block and the step limit; walked step by step at quarter-hop pace; river edges pay the ford; no road bonus; a blocked step waits and retries once per step time and never drops the route; validated against what the player can see | **built**, 19 + 1 tests |
| 4 | Battles on real positions | boards read units' subtiles (no seating, no shelter, no outside lanes); a hop onto an open board is put off to the next beat, and the unit stays on its source subtile until then; battle move and route orders become subtile routes walked one per turn; crossings use the real subtile; `BoardSlot.At` is the unit's subtile | **built**, 2 new tests and the M41 tests updated |
| 5 | Wire | each unit's subtile and route on the view | built |
| 6 | Client | units drawn at their subtile everywhere; zoomed in, right-drag traces a subtile route (gaps filled in a straight line) | built, not Play-run |

## Decisions carried in (2026-09-29 review)

- **Arrivals walk in.** No teleporting past a defending line; a fight starts where the
  front holds.
- **Only enemies share a subtile.** The cap counts everyone non-hostile on the tile. Peace
  mid-duel separates the pair.
- **Blocked steps keep the route open;** the player cancels and redraws.
- **Footprint changes pop units** to the nearest open subtile by walking distance, with the
  seven guards in the doc.
- **One shared tile-entry method** for hops and edge-crossing steps.
- **Created units** are placed directly (centre rows, usable subtiles); boats stand on a
  water subtile, passengers have none until they disembark.

## Open questions carried in

- **Groups:** a group lands in files (as many as there are free lanes at a time). A group
  route (drawing one route for the whole group) is future work.
- **Workers, residents and builders:** a worker stands on some subtile of its workplace's
  tile like anyone else. No special spots yet.
- **Road wear from subtile steps:** decided in phase 3: none (`TileEntry.Enter(creditRoad: false)`).
- **Route step limit:** decided in phase 3: 64 (`SubtileRoutes.MaxSteps`).

## Phase 1: what was built (2026-09-29)

- **`Unit.Subtile`** (`Subtile?`, written only by `Battlefields/Placement.cs`). Null in a Pooled
  world, while aboard a boat, and for a unit with no room on its tile.
- **`Placement.Seat`:** centre rows first, lowest lane first, open ground before walls, gates
  and towers, on **usable** subtiles (standable and reachable from some edge); a subtile
  nobody holds before one held only by enemies. Called from `GameWorld.AddUnit` (genesis,
  births, spawns, landings), `DisembarkIntent`, and, until phase 2, from `MoveArrivalEvent`
  and `GroupArrivalEvent` so the invariant holds between phases. Boarding clears it.
- **`Placement.Reseat`,** called from `GameWorld.AddStructure`: units that may no longer stand
  where they are are popped to the nearest open subtile by walking distance (breadth-first,
  N, E, S, W), lowest id first, never onto a held subtile; a full tile moves the unit to the
  nearest other tile with room (`TileCapacity.RoomNear` gained an `except` tile).
- **`Placement.SeparateNonHostile`,** called when a proposal is accepted into a non-hostile
  state: the lowest id in a shared subtile stays, the rest are popped.
- **`GameWorld.Restoring`:** `Snapshot.Restore` sets it, so nothing is placed or popped while
  a world is rebuilt. **Snapshot v43:** each unit's row gains its subtile.
- **Determinism audit:** `Unit.Subtile` has one writer (`Placement`); every call site is in
  the event, intent or world-build path, in id or event order, never from a read.

**Not yet:**
- The M41 board still keeps its own places (`BoardSlot.At`). Until phase 4 a unit on an open
  board has two positions, and only the world one is kept in step with structure changes.
- Nothing walks. A hop seats the unit instantly (phase 2 replaces that).
- The cap still counts one side (owner and allies); counting neutrals is phase 2.
- Boarding and disembarking are hooked but have no test of their own.

## Phase 2: what was built (2026-09-29)

- **`TileEntry.Enter`** (`Movement/TileEntry.cs`): the one tile-entry method. Position, subtile,
  `EnteredFrom`, dock slip, road credit, sight, camps, the scout log, a waiting boat. Solo hops
  and every group member use it (groups now also run the dock, camp and scout hooks they used
  to skip).
- **Landing:** `Placement.EntryLane` picks the lane of the entry edge nearest the subtile the
  unit stood on. A lane is a subtile it may step onto from outside and no non-hostile unit
  holds; a subtile held only by an enemy is a duel on arrival, taken only when no free lane is
  left. No lane: the arrival waits on its source tile and its `MoveArrivalEvent` is
  rescheduled a step time later (the route stays open).
- **Walking in:** `SubtileWalk` (`Battlefields/SubtileWalk.cs`). A unit that stops walks back to
  front: far row first, lower lane first, open ground before walls, only through subtiles it
  may enter and nobody holds. One `SubtileStepEvent` per step at a quarter of the tile's
  terrain cost, rounded up (no road bonus). Each step is chosen from the tile as it is; no way
  through means it stays. Nothing walks on an open board. The anchor is
  `Unit.SubtileWalkTick/Seq`, saved in v43 and rebuilt by `RegenerateQueue`.
- **Groups** land in files: as many members as there are free lanes, in id order; landed
  members start walking (which frees lanes); the event is rescheduled a step time later for
  the rest; `group.Position` moves only when the last member is in.
- **The cap** (`TileCapacity.SideCount`, and the planner's `VisibleSideCount`) counts every
  non-hostile unit on the tile: own, allied and neutral.

**Things to watch (real consequences of the rules, not bugs):**
- **Edge throughput.** A unit passing through holds its entry lane until its next hop lands,
  so an edge carries four units per hop time. Busy haul lines are the place to look.
- **A castle has one lane.** Only the gap is an entry, and created units are placed on open
  ground centre rows first, which reaches the gap after the courtyard. A unit standing idle
  in the gap blocks every arrival, friend or foe, until it moves. Worth a rule (created units
  keep off entry lanes on tiles with few of them, or an owner may always swap into the gap).
- **A group crosses a tile more slowly** in grid worlds: landing in files adds about two step
  times for a group of twelve.
- **A member of a half-landed group** has `Position` on the new tile while the group's is on
  the old one. A new group order given in that window plans from the old tile.
- **The M41 board still seats units its own way.** A hop onto an open board lands on a lane
  and the M41 code then places the unit by `BoardSlot`; the two agree again in phase 4.

## Phase 3: what was built (2026-09-29)

- **`WorldSubtile`** (`Battlefields/WorldSubtile.cs`): a subtile named on the whole map,
  `(tile × 4 + subtile)`, so a route crosses a tile edge with no special case.
- **`SubtileRouteIntent(unitId, route)`** (registered in `IntentJson`). An empty route cancels.
  Refused for another player's unit, a grouped unit, an embarked unit, a boat, a breeding
  parent, a unit on an open board (give it a battle order), and any route that fails
  `SubtileRoutes.Check`. It retasks like `MoveIntent` (job released, goal and haul plan
  countermanded, a hop in flight and a walk-in dropped).
- **`SubtileRoutes.Check`** (against what the player can see): non-empty, at most 64 steps, every
  step 4-adjacent (a gap is refused, never filled), within the 3×3 block around the start tile,
  on the map, no water without a bridge, no impassable ground, no known fortification, and
  every step allowed by the footprints (closed sides, who may stand where).
- **Walking:** one `SubtileRouteStepEvent` per step. A step costs a quarter of the hop onto the
  tile it enters, rounded up, plus the ford (`RiverConstants.CrossingCost`) across a river
  edge; no road bonus and no road wear. A step across a tile edge is `TileEntry.Enter` plus
  the combat trigger, so sight, `EnteredFrom`, camps and the scout log all happen.
- **Blocked steps** (a friend on the next subtile, no room on the next tile) wait and retry once
  per step time; the route stays open. **Impossible steps** (a wall that went up after the
  route was drawn, a closed side, water) stop the unit and end the route. A unit knocked off
  its route (popped, or peace) drops it.
- **A battle opening** on the unit's tile pauses the route (kept, no anchor); it resumes when the
  board closes (`Battlefields.Close`). A new world move replaces a route (`BeginMove`).
- **Saved** in v43: the steps left and the anchor; `RegenerateQueue` rebuilds the step event.

**Notes:**
- **Fog validation is nearly moot.** Every tile of a route's 3×3 block is inside its own unit's
  sight, so nothing in it can be hidden when the route is drawn. What matters is the walk
  checking the ground truth (a wall built after drawing). The check still uses the visible
  set, so it can never leak a hidden structure.
- A route step onto a tile with an **open board** lands and the M41 code seats the unit, as
  for a hop. Phase 4 replaces that.

## Phase 4: what was built (2026-09-29)

- **`Battlefields.cs` rewritten around real subtiles.** `Open` and `Admit` enrol everyone on the
  tile where they stand (`Enroll`); `RunTurn` reads `Unit.Subtile`; the resolver's moves write
  `Unit.Subtile`. Deleted: seating, `EntryLane`, the centre-first and edge-first fills, shelter,
  `BoardSlot.At/Sheltered/Waiting/OnBoard`.
- **A hop onto an open board is put off to the next beat** (`Battlefields.HoldsArrival`, in
  `MoveArrivalEvent` and `GroupArrivalEvent`): the event is rescheduled for the beat, the unit
  stays on its source subtile, and on the beat it lands on an entry lane (Phase 2) and joins the
  board. Its arrival event fires after the beat's turn resolves, so it acts from the next turn.
- **Leaving** is unchanged in spirit: on the beat, then a world hop. The unit keeps its own edge
  subtile until the hop lands, and lands on the entry lane nearest it.
- **Battle routes:** `Battlefields.AdoptRoute` turns a `SubtileRoute` into a `BattleOrder.Route`
  of the steps inside this tile (plus the subtile just outside if it goes on across an edge; the
  rest is dropped). It runs when a unit joins a board with a route, and when a
  `SubtileRouteIntent` reaches a unit on a board. `SetBattleOrderIntent` still gives Hold, Swap,
  Withdraw, board-local moves and routes, and Stop.
- **A unit with no subtile** (a tile over its cap) is not on the board: it can neither act nor be
  hit. The M41 shelter test became "the rest have no place".
- **Snapshot:** a battle slot no longer stores a subtile or a sheltered flag.
- **Wire:** unchanged (`Waiting` and `Sheltered` are always false), so the client contract holds.
- **Tests changed:** the M41 tests about seating, the outside lane and shelter now assert the new
  behaviour (5 edits); two Phase 3 route tests changed meaning (a battle no longer pauses a route,
  it takes it).

**Still open:**
- A subtile route's steps beyond the tile a battle opens on are dropped, not kept.
- **Collision with a landing.** An arrival lands as an event after the turn, not as a move inside it,
  so it never contests a subtile with a unit that is moving that same beat.
- Phases 5 and 6 (the wire and the client).

## Phase 5: what was built (2026-09-29)

- **`UnitDto` gains `SubX/SubY`** (0..3, `-1` when the unit has no subtile). PUBLIC for every
  visible unit, like the hop: where something stands is a fact you can see.
- **`UnitDto` gains `RouteX/RouteY`**, parallel arrays of world-subtile coordinates (tile x 4 +
  subtile), the steps still to walk. OWN UNITS ONLY (a drawn route is an order). It is the same
  list on a battlefield (the battle route), so the client draws one thing.
- Filled at both `ViewProjector` construction sites (`FillSubtile`, `FillRoute`). The
  `BattlefieldDto` contract is unchanged.
- **Intent side:** `SubtileRouteIntent` is already in `IntentJson` as "SubtileRouteIntent", with
  `UnitId` and `Route` (a list of `{X, Y}` world subtiles). An empty route cancels.
- **Tests:** `WireV2Tests` `Subtile_IsPublic_ButTheDrawnRouteIsOwnUnitsOnly` and
  `Subtile_IsMinusOneWhenAUnitHasNone`. 236 wire + battlefield tests pass.
- The in-flight walk-in (`SubtileWalk`) is not on the wire; the client sees the subtile change.
- The client mirrors: `ViewDto` (`subX/subY/routeX/routeY`), `session-and-wire.md`.

## Phase 6: what was built (2026-09-29, `Art Of War(prod)` only)

- **Units at their subtile:** `Client/Presentation/SubtileStand.cs`, called from `UnitMotion`
  after the battle board. Glide, hop lane guess, road bend kept as an offset. No push-off
  buildings for a unit with a subtile.
- **Tracing:** `Client/Input/SubtileRouteInput.cs`. Right-DRAG (6 px) while
  `CameraRig.ZoomedInToSubtiles` (450 units). A click still acts, on release; the press is taken
  so the world can't act on it (`PlayerController.RightClickAtHover`, `GestureActive`, added).
  Client fills gaps, undo by dragging back, cheap checks only (3x3 block, 64 steps).
- **Drawing:** `Client/Input/SubtileRouteOverlay.cs`: own units' routes and the draft; refused
  steps red.
- **Wire:** `SubtileRoutePayload`, `WireSubtile`, `IntentFactory.SubtileRoute`; wired in
  `GameBootstrap` (no editor step).
- **Checked:** `_AowTypeCheck.csproj` and `_AowWireBoundary.csproj` build clean. NOT Play-run.
- **Known rough edges:** the lane guess for a hop can differ from the sim's, so a unit may slide
  a little after landing; a right click now acts on release; two drape helpers (see overlay).

## Update 2026-09-29: superseded by M43

M43 (`docs/m43-status.md`) made subtile movement the only movement. Walking in, entry lanes, the files
landing, the hop put-off, the sidestep and the blocked-step wait built here are gone; placement, the
route rules, the pathing step rules and battle-route adoption carried over. See
`docs/subtile-movement.md` (the M43 updates).
