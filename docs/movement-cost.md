# Movement Cost & Crowding

## Decision

Per-tile **crowding cost** is added on top of terrain (biome + road) cost
for every movement hop. Two cost functions with different fog-of-war
semantics:

- **`MovementCost.PlanCost`** — called by A* during `MoveIntent.Resolve` /
  `MoveGroupIntent.Resolve`. Player-perspective: counts the planning
  player's own units always; counts non-own units only on tiles currently
  visible to that player. Tiles in fog appear empty of strangers. Tiles
  visibly at the hard cap return `Biomes.Impassable` so A* routes strictly
  around them.

- **`MovementCost.ExecutionCost`** — called when scheduling each hop's
  arrival. Ground truth: counts every unit physically present on the
  source and destination tiles. The cost is
  `terrain(to) + max(banded(sourceCount), banded(destCount))`.

The crowding additive is **banded** (flat integer per tier, never a
continuous function of count):

| Units on tile | Banded cost added |
|---|---|
| 1–3   | 0  (normal play; no penalty) |
| 4–7   | 10 (small group, noticeable) |
| 8–15  | 25 (medium army, sluggish) |
| 16+   | 50 (massive pileup, very slow) |

A **hard cap** at `MovementConstants.MaxUnitsPerTile = 50` is enforced at
arrival time. `MoveArrivalEvent` rejects (unit yields as Idle on previous
tile, path cleared, `AssignmentEpoch` bumped). `GroupArrivalEvent` rejects
the entire group hop (group goes Idle at its source, `MovementEpoch`
bumped). The hard cap is a panic switch against unbounded stacking, not a
gameplay knob — most play never bumps it.

## Why

### Why two cost functions (plan vs execute)

Planning happens at intent-submission time over a delegate `costFn: tile
-> int`. The unit isn't physically traversing the path yet; the cost
function is being asked "how expensive would this tile be?" That question
has two valid answers:

- **What the player believes** — used to compute the path. Player-
  perspective costs route the unit around dangers the player can see, and
  *not* around dangers in fog. This produces the "cost of ignorance"
  gameplay: setting a path into the unknown carries real risk.

- **What actually happens** — used when scheduling the per-hop arrival
  tick. Ground truth, ignores fog. The unit pays the real cost of where
  it actually steps, even if the plan was based on incomplete information.

Single-cost models break one or the other contract. If the planner uses
ground truth, A* automatically routes around fog'd enemies → omniscient
pathfinding → fog of war loses its bite. If the executor uses player-
perspective, the same unit pays different costs at different times for
the same hop → nondeterminism (and a unit walking into an enemy army
shouldn't move at solo-cost speed). The split keeps both clean.

### Why fog-aware planning specifically

This was the load-bearing call. A player who can't see an enemy army
shouldn't have their unit *automatically* route around it. The
interesting gameplay — scout the map, identify threats, plan around them
— collapses if A* always picks the safe-but-longer route by default.

So the planning cost only counts what the planner can see. Specifically:

- A tile in `View.VisibleTiles(world, playerId)` → count every unit on it.
- A tile NOT in that set → count only same-owner units (the player knows
  their own positions always, regardless of fog).

This is computed once per `FindPath` call (one `View.VisibleTiles` call,
cached in a `HashSet<TileCoord>`), then queried per-tile inside A*.
Determinism: visibility is a pure-read function of world state; same
inputs → same path.

### Why source AND destination crowding (max, not sum)

Source crowding is what makes large groups slow: a group always sits on
its own member crowd. A 10-member group has a source-crowd count of 10
(plus any incumbents), pays the 8-15 band penalty, hop after hop. This
is the "armies are slow" gameplay.

Destination crowding is what makes solo-unit caravans elongate at
bottlenecks: as units pile onto a target tile, each subsequent unit pays
more to enter. Caravans naturally stretch out.

`max(source, destination)` rather than `sum`:
- Sum would double-count the cost of a crowded mid-march tile (you'd pay
  big leaving it AND arriving — and on the next hop, again).
- Sum can also overshoot Impassable in pathological cases.
- Max keeps the additive bounded and matches the intuition: the
  more-crowded side is what's actually slowing the move.

### Why banded (not linear)

Architecture §2.5: integer-exact, observation-independent, banded math
beats continuous coupling. A linear `N × constant` cost would:

- Require choosing a constant that's noticeable at N=10 without being
  meaningless at N=2 — narrow tuning window.
- Make every unit added to a tile incrementally change every cost query
  for paths through it — chatty and hard to reason about.

Bands give clean breakpoints. Players can think in terms of "stay under
4 to be fast, under 8 to be ok, under 16 to be playable." Tuning happens
by adjusting band edges and additive values, not by sliding a single
constant.

### Why atomic group arrival is preserved

M5 spent real effort making `GroupArrivalEvent.Apply` a single atomic
event that updates every member's position together. Breaking that to
let groups visually elongate would mean:

- Each member fires its own `MoveArrivalEvent`.
- Group coherence (combat triggers, rendezvous logic, snapshot anchors)
  all need rewiring for per-member arrivals.
- Half a milestone of refactor.

The crowding model gets "groups are slow" via source crowding without
touching group atomicity. The group teleports to each next tile as one
event, but each hop's *cost* scales with member count. Trade-off:
visually they stay clumped instead of stretching, but mechanically they
slow down exactly as the gameplay needs.

If visible stretching ever becomes gameplay-important, that's a
standalone milestone — not a movement-cost decision.

### Why count all units (regardless of owner) in execution cost

Physical crowding is physical — when 10 units are on a tile, the 11th
struggles to push through whether they're friendly, allied, or enemy.
Owner-aware execution costs would mean enemies pass through your
formations for free, which doesn't match the spatial intuition and
removes one of the most interesting consequences (enemy chokepoints
slow you down even on terrain you control).

The owner-aware *plan* cost is the right place for the asymmetry: there,
the question is "what does the player know," which is exactly
fog-perspective. There, owner does matter (own units known unconditionally;
others fog-gated).

### Why hard cap at 50

It's a panic-switch number — high enough that realistic play never bumps
it, low enough that bugs (e.g. an infinite spawn loop) can't crater
performance by piling 10,000 units on one tile. Tunable in
`MovementConstants.MaxUnitsPerTile` if play surfaces a need.

The hard cap matters less than it would in a tight-cap-driven game
(units pile on tiles in the dozens by design here, not the hundreds).
Mostly it's a guard rail.

### Why path planning sees Impassable on cap-saturated visible tiles

Once a tile is at cap, no unit can enter — so the planner avoiding it is
strictly correct. Returning `Biomes.Impassable` from `PlanCost` lets A*
route strictly around it instead of treating it as a 60-cost gamble that
the cap will lift by arrival time.

(Fog'd cap-saturated tiles still look empty to the planner. The unit
will plan a path through and be rejected at arrival — the rejection is
the player learning "ah, that tile was full." Ignorance has
consequences.)

## What gets built

### New
- `src/Sim.Core/Movement/MovementConstants.cs` — `MaxUnitsPerTile`,
  `BandedCrowdingCost(int) -> int`.
- `src/Sim.Core/Movement/MovementCost.cs` — `CountUnitsOnTile`,
  `CountVisibleUnitsOnTile`, `PlanCost`, `ExecutionCost`.
- `tests/Sim.Tests/MovementCrowdingTests.cs` — curve, pure-read,
  fog-aware planning (incl. the load-bearing
  `Path_DoesNotAvoidFoggedCrowd_RoutesStraightThrough`), hard cap
  rejection, group source crowding, twin-run determinism.

### Modified
- `src/Sim.Core/Movement/MoveIntent.cs` — `BeginMove` uses
  `MovementCost.PlanCost` (fog-aware) for path planning;
  `ScheduleNextHop` uses `MovementCost.ExecutionCost` (ground truth) for
  hop duration.
- `src/Sim.Core/Movement/MoveArrivalEvent.cs` — hard cap check at top of
  `Apply`; on overflow the unit yields as Idle and clears its path.
- `src/Sim.Core/Groups/MoveGroupIntent.cs` — same plan/execute split.
  Old `GroupCost` helper removed; both paths go through `MovementCost`.
- `src/Sim.Core/Groups/GroupArrivalEvent.cs` — hard cap check at top of
  `Apply`; on overflow the group goes Idle at its source.

### No format-version bump
No new persistent state. Crowding is derived (unit positions are already
snapshotted). Snapshot.FormatVersion stays at 6.

## Future expansion

- **Per-tile unit index** — if `CountUnitsOnTile` becomes a hot path at
  scale, add a `Dictionary<TileCoord, List<int>>` index maintained by the
  position-write sites. Same precedent as
  `ConstructionSite.BuildersPresent` (architecture §4 rule 10). The index
  is a pure mechanical change; the API stays the same.
- **Per-unit-kind crowding curves** — heavy units count as more in the
  banding; carts/horses count as less. Adds a `crowdingWeight` int to
  `Unit`. Curve becomes `BandedCrowdingCost(sumOfWeights)`.
- **Per-biome cap modifiers** — Mountain tiles hold fewer units (footing),
  Castle tiles hold more (defensive). A function `CapFor(tile, world)`
  rather than a single constant.
- **Capacity-aware combat formation** — when combat triggers on a tile,
  any units beyond cap could "spill" to neighbours. Out of scope until
  M7 needs it.
- **Visible group elongation** — break atomic group arrival, members fire
  individual MoveArrivalEvents. Half a milestone in M5/M7 land. Not
  needed for the crowding-slows-movement goal.

## Out of scope (intentionally deferred)

- **Per-unit speed nerf** — modeled here as a tile property, not a unit
  one. Adding a unit-level slowness would require new state and
  double-bookkeeping; the tile-property model already captures the
  intended semantics.
- **Capacity-aware pathfinding cost during execution** — execution cost
  is ground truth at hop time; we don't re-plan as fog reveals new
  congestion mid-march. Same shape as the M2 "committed path" pattern;
  the player retasks if they want to react.
- **Hard-cap mid-march yield-and-wait** — on rejection the unit goes
  Idle, not "wait for opening." Adding a wait-and-retry would require
  a new "tile capacity changed" event source, which we don't have. The
  player re-issues.

## Acceptance tests

- `MovementCrowdingTests.BandedCrowdingCost_HitsExpectedTiers` — the
  curve is pinned.
- `MovementCrowdingTests.CrowdingCountAndCost_Are_PureReads_NoMutation`
  — 100× no-mutation hash test.
- `MovementCrowdingTests.ExecutionCost_BothCrowded_TakesMaxNotSum`
  — the max-not-sum contract.
- `MovementCrowdingTests.PlanCost_EnemyUnitsInFog_AreInvisible` —
  the fog-of-war contract for planning.
- `MovementCrowdingTests.Path_AvoidsVisibleCrowd_RoutesAround`
- `MovementCrowdingTests.Path_DoesNotAvoidFoggedCrowd_RoutesStraightThrough`
  — **THE LOAD-BEARING TEST. The planner does NOT route around fog'd
  enemies. Cost of ignorance.**
- `MovementCrowdingTests.MoveArrival_RejectedAtHardCap_UnitGoesIdleOnPreviousTile`
- `MovementCrowdingTests.Group_HopCost_ScalesWithMemberCount_ViaSourceCrowding`
  — large groups slow per hop.
- `MovementCrowdingTests.Group_AtomicArrival_AllMembersOnSameTile_AfterHop`
  — the M5 atomic-arrival invariant holds after a crowded hop.
- `MovementCrowdingTests.Group_HopBlockedByCap_GroupGoesIdleAtSource`
  — group cap rejection.
- `MovementCrowdingTests.Crowding_TwinRun_HashesMatch` — twin-run
  determinism end-to-end through the new code.

## References

- `docs/architecture.md` §2.2 (pure-read wall), §2.5 (banded math vs
  coupled-interval trap), §4 rule 10 (index when O(N) becomes a hot
  path).
- `docs/persistence-model.md` (committed paths over re-planning).
- M2 road decay (`Sim.Core.Roads.Road`) is the precedent for "store
  rate × last-touched-tick, compute on access" pure-read derived state.
- M3 fog of war (`Sim.Core.Vision.View.VisibleTiles`) provides the
  player-perspective primitive PlanCost queries.

## Update 2026-06-11 — march pace (×3) and the 1-tile-=-1-km anchor

Biome move costs tripled (Grassland 10→30, Forest 30→90, Hills 25→75,
Mountain 45→135, Desert 40→120, Water-on-foot 250→750; boats stay 6).

**The anchor:** 1 tile ≈ 1 km. The old costs were a 6 km/h *stroll*
sustained forever — units crossed the 256-tile map in 1.8 game-days, so
distance was strategically free next to everything else on the
days-to-a-season band (docks 10 d, war delay 30 d, land exhaustion
~104 d). The new costs price a SUSTAINED march (~2 km/h on open ground —
rest, camps, and terrain included): crossing the map is a 5.3-day
expedition, a maxed road (−66%) restores the old pace (roads ARE the
speed upgrade), and a boat is 5× open-ground pace (the Dock's promise).

**Crowding bands scaled in lockstep** (10/25/50 → 30/75/150, now the
named constants Small/Medium/LargeBand). The bands are pegged at
+1/+2.5/+5 grassland-tiles-worth: a 16+ pileup must cost more than a
short detour around it, or the route-around-visible-crowds behaviour
silently dies — which is exactly how the proportionality break surfaced
(Path_AvoidsVisibleCrowd_RoutesAround failed when terrain tripled and
the bands didn't).

**Tests** now derive every movement expectation from
`Biomes.MoveCost` / `RoadConstants` / the band constants — the next
retune of any of these numbers is a one-file change.
