# Boats

## Decision

Boats are **Units** that traverse water cheaply and cannot enter land. Passengers
ride as an **embarked list** stored on the boat (off-tile while aboard), not as
group members. The **Dock** is a new `Structure` that is both the **shipyard**
(production-job that emits a boat onto a pre-chosen adjacent water tile) and the
**only** embark/disembark seam. Disembark is allowed at docks owned by the
player or any **ally**. Three new explicit intents — `EmbarkIntent`,
`MoveBoatIntent`, `DisembarkIntent` — drive water travel; there is **no
multi-modal autopath**. When a boat dies, its passengers **drown** with it.

A new `Unit.Traversal` enum (`Foot | Water`) selects the per-biome cost table
during pathfinding. `Foot` uses the existing `Biomes.MoveCost`. `Water` uses a
new water-only table: Water is cheap, every land biome is `Biomes.Impassable`.
Boats are the only units with `Traversal = Water` at launch.

## Why

### Why "boat is a Unit with embarked passengers" — not "boat is a Group member"

User's first sketch was: boat is a Unit, throw it into a Group with passengers,
let M5 group movement do the rest. This breaks on the M5 contract that
**group speed = pace of the slowest member**. A group sitting on a water tile
contains both a fast boat (water cost ≈ 6) and slow citizens (water cost 250);
the group pays 250 per hop, defeating the entire purpose of building the boat.

Fixing that inside Group semantics would mean "ignore non-boat members' water
cost when a boat is present" — a hidden coupling between two systems that
otherwise know nothing about each other. The embarked-list model is the clean
answer: passengers aren't on a tile at all while aboard, so they contribute
zero to movement cost by construction. The same invariant also protects them
from combat resolution on land tiles the boat passes over (architecturally
impossible: water and land never touch in the same arrival event because the
boat can't enter land).

### Why dock-only embark/disembark — not free shore landing

Boarding anywhere along a coast collapses two design properties at once:

- **Presence as investment** (the design's core principle). If you can land
  any army on any shoreline, the coast carries no investment-value — building
  the dock buys you nothing the open beach doesn't already give you.
- **Sea invasion as a real strategic choice.** When landings are
  infrastructure-gated, an attacker has to either ally with someone on the
  target coast, or capture an enemy dock the hard way (overland) before
  ferrying reinforcements. That's exactly the "strategic asymmetry hours-long
  travel rewards" the game is for.

The cost is steep: an isolated coast with no allies is **un-invadable by sea**
in the MVP. This is acknowledged and accepted. The two known escape hatches
(beach landing as a long forced disembark; capture-an-enemy-dock from adjacent
water) are deferred to a later milestone if play surfaces a need.

### Why explicit intents — not auto-planned multi-modal routes

Today, `Pathfinding.FindPath` runs against **one** cost function. Auto-planning
"walk → embark → sail → disembark → walk" requires either a multi-stage planner
that stitches per-leg sub-paths together, or a unified land+water graph with
virtual zero-cost edges at every dock. Both are real engineering work and both
introduce a new abstraction (mode transitions) into a system that currently has
none.

Three explicit intents (`Embark`, `MoveBoat`, `Disembark`) sidestep all of it
and align with the **intents-as-truth** principle (§3 of `architecture.md`): the
meaningful seams — *where do I board, where do I land* — are exactly the
strategic decisions players should be making by hand. Auto-routing through docks
would route around the choice. If demand for fewer clicks arrives, a planner
stitch can be added later without changing any of the underlying intents.

### Why passengers off-tile, not "passengers also on the water tile"

Putting passengers on the same water tile as the boat would mean:

- Combat resolution on that tile would see them and try to engage (they'd
  contribute force; they'd be killed individually); the boat's force would no
  longer be the single source of truth for "what hits the enemy."
- They'd contribute to crowding cost (`docs/movement-cost.md`) on the water
  tile despite not moving themselves — wrong.
- Solo intents (move, haul) would still be valid on them, breaking the
  invariant that an embarked unit cannot act independently.

Off-tile (removed from `TileGrid` while `EmbarkedOn != null`) makes all three
problems vanish by construction. Embarked units are unreachable until they
disembark, which is exactly the semantic we want.

### Why drown — not capture-on-death

M7's capture-on-death applies because dead-on-a-land-tile bodies and goods
are physical things a surviving force can pick up and haul. When a boat sinks,
the wreck is on a **water tile**, and the surviving force is by definition
*not on that tile* (the boat sank fighting another boat or a coastal force).
There's no clean "winner" to inherit the passengers. The simplest, most
consistent thing is for them to die with the carrier. No new wreck mechanic, no
new "passengers in the water" temporary state, no need to teach combat about
boat passengers as separate force contributors.

Cargo carried on the boat **also** drowns (`CargoResource`, `CargoAmount`
zeroed). Consistent with passengers.

### Why a per-unit `Traversal` enum — not a tile-property override

`docs/movement-cost.md` deliberately models speed as a **tile property** and
explicitly defers per-unit speed (out-of-scope §). Boats violate that on
purpose: the same Water tile is *cheap* for a boat and *near-impassable* for a
citizen on foot. That's not a "speed nerf," it's a **different movement domain**,
and tile-property-only can't express it.

The enum keeps the new state minimal — one byte per unit, append-only enum, no
double-bookkeeping. It is **not** a generic cost table per unit. Two modes
today (`Foot`, `Water`); a hypothetical `Flying` would be the third.

### Why production-job at the dock — not a build-site on the water tile

A `ConstructionSite` on a water tile *would* work mechanically (water is
passable-but-expensive, builders can reach it), but it reads wrong (citizens
walking onto open water to hammer a hull together) and complicates the
existing `PlaceSite` invariant that the build site sits on the destination tile.

The dock is already a Structure that owns workers and produces things. Adding
"this Structure's production output is a Boat Unit on the adjacent slip tile"
mirrors how extractors already turn workers + time into output. No new
mechanism. The slip tile (a water tile adjacent to the dock, chosen at
dock-build time) is the spawn point. If the slip is occupied at production
completion, the production stalls and re-arms when the slip clears (same
`ArmIfDormant` pattern as extractors).

### Why faster than any biome on foot

The design contract: a boat is **expensive and slow to build** but **pays off
permanently**. The investment-payoff loop only works if the operational gain is
clearly visible — a boat that's only marginally faster than walking gets built
once for novelty and abandoned. Boat-on-water at cost **6** sits below
Grassland at cost **10** — the cheapest foot biome — so any water crossing
beats any overland route of equivalent tile length, *before* roads enter the
picture.

Roads remain in their own investment lane: a maxed stone road on grassland is
faster per tile than a boat. Both are end-state investments in their own
domains; they compose rather than compete. A coastal-and-roaded empire is
faster than either alone.

The number `6` is a starting point, not a contract. The exact value lives in
`MovementConstants` and is tunable. The invariant the design depends on is
`boatWaterCost < min(landBiomeFootCost)`.

## What gets built

### New

- `src/Sim.Core/World/Traversal.cs` — `public enum Traversal : byte { Foot = 0, Water = 1 }`. Append-only.
- `src/Sim.Core/Movement/BoatMovementCost.cs` — water-only cost table:
  `Water → 6`, every other biome → `Biomes.Impassable`. One `CostFor(Biome)`
  function used by `Pathfinding.FindPath` when the moving unit's `Traversal == Water`.
- `src/Sim.Core/World/StructureKind.cs` — new value `Dock` (next byte; existing
  values keep their bytes per architecture §3 rule 5).
- `src/Sim.Core/World/Dock.cs` (or in `StructureCatalog.cs`) — Dock spec
  (cost, build time, workers, **`Slip: TileCoord`** persistent field set at
  build time; one boat may occupy the slip at a time).
- `src/Sim.Core/World/UnitRole.cs` — new value `Boat`.
- `src/Sim.Core/World/Boat.cs` (or extension fields on `Unit.cs`) — new
  durable fields:
  - `Traversal Traversal` (defaults to `Foot`; `Boat` role sets `Water`).
  - `int PassengerCap` (config; e.g. 4 for the launch hull).
  - `List<int> Passengers` (unit ids; canonical iteration order = ascending).
  - `int? EmbarkedOn` (on every Unit; null for non-passengers).
- `src/Sim.Core/Boats/EmbarkIntent.cs` — instant intent. Preconditions: all
  unit ids belong to caller, all on the dock tile, boat on adjacent water tile,
  capacity not exceeded, none of them already embarked, none of them in a
  forming/moving group. Effect: append to `Passengers`, set `EmbarkedOn`,
  remove from tile-index. Bump each passenger's `AssignmentEpoch` (any
  in-flight solo events fence-out).
- `src/Sim.Core/Boats/MoveBoatIntent.cs` — same shape as `MoveIntent` but
  uses `BoatMovementCost` for planning and execution. Reuses
  `MoveArrivalEvent` (which already supports per-unit cost via the unit's
  `Traversal`).
- `src/Sim.Core/Boats/DisembarkIntent.cs` — instant intent. Preconditions:
  boat on water tile 4-adjacent to a `Dock` owned by `PlayerId` or any ally
  of `PlayerId` (per M6 `Diplomacy.AreAllied`). Effect: place each passenger
  on the dock tile, clear `EmbarkedOn`, clear `Passengers`. Bump epochs.
- `tests/Sim.Tests/BoatsTests.cs` — see Acceptance tests below.

### Modified

- `src/Sim.Core/Movement/Pathfinding.cs` — A* takes either `Biomes.MoveCost`
  or `BoatMovementCost.CostFor` depending on the moving unit's `Traversal`.
  One added parameter; no behavior change for existing `Foot` units.
- `src/Sim.Core/Movement/MoveArrivalEvent.cs` — `ExecutionCost` queries the
  correct cost table by `Traversal`. (Crowding additive applies to water tiles
  too — boats pile up at busy docks just like land units.)
- `src/Sim.Core/World/Unit.cs` — `Traversal`, `PassengerCap`, `Passengers`,
  `EmbarkedOn` fields. `Passengers` snapshotted as a sorted list. `EmbarkedOn`
  snapshotted as nullable int.
- `src/Sim.Core/Combat/CombatRules.cs` — when a unit with non-empty
  `Passengers` dies, iterate the list and remove each passenger from the world
  (same removal path the dying unit takes). Cargo on the boat is zeroed by
  the normal death path. No new code path; one extra step in the existing
  `Kill` routine.
- `src/Sim.Core/Vision/Sight.cs` — embarked units contribute no vision
  (they're off-tile; `EmbarkedOn != null` filters them out of the vision-source
  enumeration). Boats contribute vision normally.
- `src/Sim.Core/Persistence/Snapshot.cs` — serialize new fields. Format version
  bumps by one. `RegenerateQueue.From` regenerates `MoveBoatIntent` arrivals
  from the boat's `NextMoveTick / NextMoveSeq` anchors (same shape as Unit
  move anchors).
- `src/Sim.Core/Logistics/AssignWorkersIntent.cs` & friends — reject
  assignment to embarked units (any solo intent on a unit with
  `EmbarkedOn != null` returns `IntentOutcome.Reject`).

### Persistence

Snapshot format version bumps. `Passengers` list, `EmbarkedOn`, `Traversal`,
`Dock.Slip` are all new persistent fields per architecture §2.8. No new
event types reach durable storage (events are reconstructed by
`RegenerateQueue.From` from the boat's existing move anchors).

## Composition with existing systems

- **M5 Group movement** — embarked units are not in groups; a unit cannot be
  both embarked and grouped. The existing solo-intent-rejection on grouped units
  is mirrored for embarked units. A whole *group* embarking is allowed (issue
  `EmbarkIntent` with the group's member ids); the group is disbanded by the
  embark, members ride as passengers, and disembark drops them as ungrouped
  units. Re-forming after disembark is the player's call.
- **M6 Diplomacy** — disembark validity reads `Diplomacy.AreAllied`. Enemy
  docks are not disembark targets in the MVP.
- **M7 Combat** — boats are Units with Health; force-vs-force resolution on
  water tiles works without modification. The only addition is the
  passengers-drown step in the existing `Kill` routine.
- **Roads** — roads do not appear on water tiles (no `RoadState` is ever
  written for a `Biome.Water` tile). Roads and boats compose by being two
  parallel investment loops, not by interacting on the same tile.
- **Fog** — boat vision works. Embarked passengers contribute no vision —
  diegetically correct (you can't scout from inside the hull).
- **Biome degradation (M9)** — water tiles are off the fertility ladder
  already (`BiomeDegradationConfig.WaterBaseline = 0`). Boats and degradation
  don't interact.
- **Population (M8)** — embarked units age and starve normally; their
  `BornTick` and death conditions don't depend on tile presence.

## Future expansion

- **Canals** — turn a land tile into a Water tile via a build job. New pattern
  (terrain mutation post-worldgen, today forbidden by
  `docs/world-generation.md`). Composes with the boat system without
  changing it: a canal is just more water to sail through.
- **Beach landing** — `BeachLandIntent`: a long, telegraphed forced
  disembark onto any land tile adjacent to the boat's water tile. Visible to
  defenders on fog as soon as it starts; the multi-hour duration is the
  defender's window to muster. Enables unilateral sea invasion without
  allies. Defer until play surfaces a need.
- **Sea combat from adjacent docks/water** — ranged-from-adjacent (§9.4 of
  the design doc) extended so a boat can engage a coastal dock or another
  boat one tile away. Pattern already exists for land; same self-rescheduling
  rounds.
- **Larger hulls** — `PassengerCap` is per-unit-kind; introducing a "war
  galley" or "transport" with different caps and cargo capacities is a
  catalog change, not a code change.
- **Boat capture** — currently a boat dies and passengers drown. If "board
  and seize" becomes desired, that's a new combat outcome (capture instead
  of kill). Out of scope here.
- **Auto-planned multi-modal routes** — the planner stitch. Either a per-leg
  sub-path stitcher or a unified land+water graph with virtual zero-cost
  dock edges. The three explicit intents continue to exist underneath; the
  auto-planner is purely a convenience layer.

## Out of scope (intentionally deferred)

- **Beach landing.** See above. The MVP shape — invasion requires allies or
  overland capture — is the design as written.
- **Capture-instead-of-drown on sink.** Diegetically defensible but not
  worth the new combat-outcome plumbing in the MVP.
- **Cargo + passengers in different proportions.** Cargo reuses the existing
  `CargoCapacity / CargoResource / CargoAmount` fields. A boat with
  passengers can still hold cargo; the cargo cap is independent of the
  passenger cap. Mixed-good Holdings on a single boat is a separate
  refactor that would also touch caravans — not here.
- **Embarked passengers contribute force in sea combat.** No. The boat's
  force is its own. A future "marines fire from the deck" mechanic is the
  ranged-from-adjacent extension above.
- **Embarked units consume food / age differently.** They age and consume
  normally — M8 mechanics don't depend on tile presence.

## Acceptance tests

- `BoatsTests.Boat_OnWater_PathfindsAtBoatCost` — A* through water uses the
  cost-6 table, not the cost-250 foot table.
- `BoatsTests.Boat_CannotPath_OntoLand` — A* rejects any land tile for a
  `Traversal.Water` unit; `MoveBoatIntent` to a land tile returns
  `Reject(no path)`.
- `BoatsTests.Boat_FasterThanAnyFootBiome` — pinned invariant:
  `BoatMovementCost.Water < min(Biomes.MoveCost(b) for b in landBiomes)`.
- `BoatsTests.Embark_AtDock_MovesPassengersIntoBoat` — passengers leave
  the tile-index, `EmbarkedOn` is set, `Passengers` contains their ids.
- `BoatsTests.Embark_BoatNotAdjacentToDock_Rejected` — boat one water tile
  too far; intent fails.
- `BoatsTests.Embark_ExceedsCap_Rejected` — passenger count + new > cap.
- `BoatsTests.Embark_AlreadyEmbarked_Rejected` — `EmbarkedOn != null` blocks.
- `BoatsTests.Embark_UnitInGroup_Rejected` — group membership blocks embark
  (mirrors M5 solo-rejection symmetry).
- `BoatsTests.EmbarkedUnit_RejectsSoloIntents` — moves, hauls, work
  assignments all fail while `EmbarkedOn` is set.
- `BoatsTests.EmbarkedUnit_ContributesNoVision` — `Sight.Reveal` does not
  include the passenger's would-be position; the boat's vision is the only
  thing that reveals.
- `BoatsTests.Disembark_AtOwnDock_PlacesPassengersOnDockTile` — passengers
  appear on the dock tile, `EmbarkedOn` cleared.
- `BoatsTests.Disembark_AtAlliedDock_Allowed` — M6 ally state honored.
- `BoatsTests.Disembark_AtEnemyDock_Rejected` — enemy-owned dock blocks.
- `BoatsTests.Disembark_NotAdjacentToAnyDock_Rejected` — open water blocks.
- `BoatsTests.BoatDies_PassengersDrown_AndCargoZeroed` — combat kill of a
  loaded boat removes all passengers and zeroes cargo.
- `BoatsTests.Dock_ProductionJob_SpawnsBoatOnSlip` — completed production
  emits a boat on the dock's slip tile.
- `BoatsTests.Dock_ProductionJob_SlipOccupied_StallsAndRearmsOnClear` —
  same dormancy/re-arm pattern as extractors. **Headline test for boat
  production.**
- `BoatsTests.Boats_TwinRun_HashesMatch` — twin-run determinism end-to-end
  through embark, sail, disembark.
- `BoatsTests.Boats_SnapshotRoundTrip_PreservesPassengersAndEmbarkedOn` —
  snapshot/restore preserves all new fields including the passenger list's
  iteration order.
- `BoatsTests.Boats_RecoveryAfterSink_BeforeReplay_IsIdentical` — M4
  headline: snapshot mid-sail, kill, replay → same final state.
- `BoatsTests.BoatMove_IsObservationIndependent_OnWaterCost` — pure-read
  100×-no-mutation on water-cost queries.

## Headline determinism test

> **`BoatsTests.Boats_TwinRun_HashesMatch`** — two identical scenarios
> (build dock → produce boat → embark group → sail across a lake →
> disembark at allied dock → embarked units re-issue moves) produce
> `Snapshot.Hash` equality.

This is the milestone's M-level contract per architecture §1.

## References

- `docs/architecture.md` §2.1 (event-driven), §2.6 (fencing tokens for
  embark/disembark / move events on passengers), §2.8 (anchors).
- `docs/movement-cost.md` (the existing tile-property speed model; this
  doc deliberately extends it with a per-unit `Traversal` dimension).
- `docs/persistence-model.md` (anchor pattern for `MoveBoatIntent`
  arrivals).
- `docs/diplomacy-model.md` (ally check for disembark).
- `docs/extraction-model.md` (production-job pattern reused for the
  dock-as-shipyard).
- `persistent-rts-design.md` §7 (caravans / logistics; the embarked-list
  pattern is the spiritual sibling of the cart/escort group), §10
  (diplomacy gates the disembark check).
