# M43 — one movement on subtiles, and the default flip: status

**Decisions:** `docs/subtile-movement.md`, the update of 2026-09-29 "all movement on
subtiles". **Status:** planned 2026-09-29. Steps 0 to 4 built (one movement, roads on subtiles, the old movement and pooled combat retired); steps 5 to 8 open.

This milestone makes subtile movement the game's only movement and grid combat its only
combat. It absorbs the old default-flip list (`docs/m41-status.md`, "M42" there, renumbered
M43).

## Steps

| # | Step | What |
|---|---|---|
| 0 | Measure | Subtile A* on a default 128×128 map: the whole grid against the tile-route corridor. Time, nodes expanded, path quality. Decides the pathfinder's shape. |
| 1 | Subtile pathfinder (built) | One search over subtiles: footprints (blocked, walls, closed sides, water, decks), terrain per step (quarter hop), river fords, fog (structures you can't see plan as open), the per-side cap as the planner sees it; in the corridor if step 0 says so |
| 2 | One step event (built) | Every move walks subtile by subtile: tile orders end nearest the centre, and drawn routes as now. Moving friends pass through each other outside battles; a unit standing still holds its subtile |
| 3 | Roads on subtiles (built) | Wear and cost per subtile link; M34 arcs converted (straight, or a 90° bend inside the tile); decay as now; the client's road painter fed subtile segments |
| 4 | Retire the old (built) | World hops, entry lanes, walking in, group files, the hop put-off, `CrossingRule` and `Crossings` (unless kept as the corridor search), `CombatModel.Pooled`, the pooled combat rounds, the old flat 50-per-tile cap |
| 5 | Groups | A group moves as a body of subtiles, keeping its members' relative places |
| 6 | AI and drivers (built) | Tile orders unchanged in shape; reachability checks use the new pathfinder; raid recall respects the cap; the power estimates learn the board |
| 7 | Tests and balance | Migrate the movement, haul, road and AI timing tests; the day-X raid and AI war tuning; snapshot v44 (subtile paths, subtile roads) |
| 8 | Client (hop fields and `RoadWalk` removed; not Play-run) | Tile clicks and drawn routes as now; roads from subtile links; the old pooled combat marks removed |

## Open questions

- Performance at scale: events per march (about 4× today) and the length of saved paths.
  Step 0 covers the search; the rest is measured once step 2 walks.

## Step 0: the measurement (2026-09-29)

`tests/Sim.Tests/Benchmarks/SubtilePathBench.cs`, run with `AOW_BENCH=1` in Release.
- **Map:** the default 128×128 (512×512 subtiles), seed 7, with 3 AI players' structures.
- **Step cost:** a quarter of the tile's foot hop.
- **Rules applied:** footprints for a neutral mover, water off limits, river fords.
- **Left out:** units and roads.
- **Sample:** 60 pairs of each length; all reachable.

| Search | Medium march (10–30 tiles) | Long march (60+ tiles) |
|---|---|---|
| Whole subtile grid | median 1.8 ms, p90 4.8, max 10.2; 5,634 subtiles expanded | median 5.8 ms, p90 12.3, max 53.9; 48,637 expanded |
| Tile route, then subtiles in its corridor (±1 tile) | median 1.2 ms, p90 2.9, max 8.9; 1,055 expanded | median 1.8 ms, p90 2.8, max 11.2; 4,724 expanded |
| Corridor path's extra length | median 0%, worst 12.9% | median 0%, worst 6.5% |

The corridor always found a path when one existed.

**Decision for step 1: the corridor search**, falling back to the whole grid if the corridor
finds nothing.
- **Even the whole grid is affordable** at this size. The corridor is about 10× less work on
  long marches, and its cost barely grows with distance.
- **Roads will matter.** Once they are cheap subtile links, the A* heuristic must use the
  cheapest possible step (a worn road). That weakens it, and a whole-grid search would
  expand far more. The corridor is the guard.
- **Re-measure after step 3** (roads) and with units in the cost function.

## Step 1: the subtile pathfinder (2026-09-29)

**Built.**
- `Movement/SubtilePathfinder.cs`: `Find(world, mover, start, goal, visible, stats, useCorridor)`.
  1. The **shape-aware tile route**: the existing tile A* with `CrossingRule` (the crossing masks), so a
     wall line routes to a gate or round its end, a castle only by its gate, a canal only at a bridge.
  2. Subtile A* inside that route's tiles plus a one-tile margin (`CorridorMargin`).
  3. **Fallback** to the whole grid if the corridor finds nothing, so a path that exists is found.
     `MaxExpanded` (600,000) bounds a search for an unreachable goal.
- `Battlefields/SubtileStepRules.cs`: **the one answer to "may this mover take this step?"**, with a
  per-query layer cache. The pathfinder plans with it and the walk checks each step with it
  (`SubtileRoutes.Check` and `StepProblem` now delegate), so a plan and the walk can only differ over
  what the player couldn't see. Also the step cost (`StepCost`: a quarter of the foot hop, rounded up,
  plus the ford) and `TileHopCost` (the tile route's currency).
- `StepMover` (owner, traversal, archer) says who is walking. Boats plan over water only and ignore
  footprints (they sail under decks and past docks).
- **Fog:** with a `visible` set, a structure the mover doesn't own and can't see plans as open ground
  and fortifications use `BlocksPlan`; with `visible` null everything counts (ground truth). The walk's
  per-step check stops the unit at what it couldn't see.
- **Units cost nothing here.** Moving friends pass through each other, and who is standing where is
  the walk's business (step 2). The per-side cap is not in the planner (step 2 decides where it bites).
- **Bridge decks walk like grassland** (`SubtileWalk.FootTerrainCost`, as `MovementCost.TerrainCostFor`
  prices them). Before this a bridge tile was priced as water, so the tile route walked round it.
- A pure read: no writes (pinned by the 100× no-mutation test), and ties break by (f, subtile index),
  so the same query always gives the same path.

**Tests** (`SubtilePathfinderTests`, 12): open ground is Manhattan through the corridor; the corridor
costs what the whole grid costs; a wall line is walked round by an enemy; an enemy enters a castle only
by the gap; a wall subtile is no goal for an enemy; a canal is crossed only at a bridge; feet never plan
onto water, boats stay on it; a wall in the fog plans as open ground and the walk's rules stop the unit;
fords; purity and determinism.

**Re-measurement** (`SubtilePathBench`, Release, `AOW_BENCH=1`; the default 128×128, seed 7, 3 AI
players; 60 pairs per row; step cost as above; no units, no roads).
Second map: the same with 24 lines of a foreign owner's wall, 309 tiles, up to 30 long.

| Map | Length | Whole grid | Corridor | Corridor extra cost |
|---|---|---|---|---|
| Default | Medium (10–30) | median 3.0 ms, p90 16.0, max 36.0; 5,554 expanded | median 2.7 ms, p90 9.0, max 19.1; 914 expanded | median 0%, max 3.1% |
| Default | Long (60+) | median 12.8 ms, p90 24.6, max 31.4; 47,245 expanded | median 4.9 ms, p90 14.1, max 23.9; 4,683 expanded | median 0%, max 2.1% |
| Wall lines | Medium | median 1.0 ms, p90 2.6, max 6.5; 5,232 expanded | median 0.5 ms, p90 1.4, max 9.4; 909 expanded | median 0%, max 3.7% |
| Wall lines | Long | median 14.7 ms, p90 28.1, max 32.6; 52,218 expanded | median 4.6 ms, p90 6.4, max 8.2; 4,662 expanded | median 0%, max 1.9% |

- All pairs reachable; the corridor **never fell back and never found nothing**: with the shape-aware
  tile route it almost always holds the real path, as predicted. (The medium default row's first
  numbers include JIT warm-up.)
- The corridor is about 10× less work on long marches, and now with the shape-aware route and with
  walls in the way. Its extra cost is at most a few percent (the margin of one tile constrains a
  path that would have cut a corner outside it).
- **To re-measure after step 3 (roads):** cheap road steps lower the A* floor and weaken the
  heuristic; the benchmark will then add road links.

## Steps 2 and 4: the plan and the test migration (proposed 2026-09-29, for the user's approval)

Nothing below is built. It is what I'd do, and what needs a decision before I start tearing out.

### What steps 2 and 4 do, in the order I'd do them

The old movement can't be kept beside the new one. So step 2 builds the new walk, and **step 4 deletes
the old the same day step 2 goes green**, not weeks later. In between, pooled worlds would keep the
old hops, so I would do steps 2 and 4 as one working change with the suite green at the end of it.

1. **Flip the default first** (`CombatConfig` default `Grid`; `GenesisSpec`; `--combat` stays for a
   while as a dev switch, then goes). Nothing else changes yet, so the tests that only break because
   they were written for pooled worlds fail together and can be triaged in one pass.
2. **The walk.** `MoveIntent` (a tile order) becomes: pick the goal subtile (the free subtile nearest
   the tile's centre) → `SubtilePathfinder.Find` → the unit's walk (today's `Unit.SubtileRoute`, one
   list, one step event, `SubtileRouteStepEvent` renamed `WalkStepEvent`). A drawn route is the same
   walk from a different source (no planning, the 3×3 limit, gaps refused).
   - Tile entry inside the walk uses `TileEntry.Enter` (already shared): sight, camps, scout log,
     docks, boat goals, combat trigger. `PursuitRules.Step` runs on each tile entry, as it ran on each hop.
   - What ran on "final arrival" (`DispatchOnFinalArrival`: pursuit, scout mission, haul pickup and
     deposit, goals, group rendezvous, fort siege) runs when the walk ends. It moves out of
     `MoveArrivalEvent` into one place.
   - Blocked steps: a friend **standing** on the next subtile blocks it (sidestep, then wait, as M42);
     a friend **moving** doesn't (the user's rule). A walk that ends on a subtile a friend has since
     stood on is re-aimed at the nearest free one.
3. **Delete** (step 4 proper): `MoveArrivalEvent`, `GroupArrivalEvent`, `MoveIntent.ScheduleNextHop`,
   `Unit.PathRemaining` and `NextArrival*` (the wire's `HopTo*` becomes the subtile step), entry lanes
   (`Placement.EntryLane`), `SubtileWalk` (walking in), files landing, the hop put-off
   (`HoldsArrival` on arrivals), `CrossingRule` and `Crossings` where nothing but the tile route needs
   them (the tile route does: **they stay, as the corridor's shape mask**), the old flat 50-per-tile
   cap, `CombatModel.Pooled` and the pooled unit-vs-unit rounds.
4. **Keep the siege rounds.** The M41 spec says "the board decides who holds the tile, then today's
   siege rounds run": structure damage stays. So `CombatRoundEvent` shrinks to the structure-damage
   round instead of going. **This needs the user's word** (question 3 below).

### Decisions I need

1. **A standing friend blocks a moving one (it must walk round or wait); a moving friend never does.**
   That is how I read "only a unit standing still holds its subtile". Is a moving unit allowed to *pass
   through* a standing friend's subtile? (I say no: it would end sharing.)
2. **A walk that ends where a friend now stands:** re-aim at the nearest free subtile, tie N, E, S, W.
3. **Siege rounds:** keep them as the structure-damage round (above), so the castle, Rival conquest,
   bandit camps and dead kingdoms keep working, and only the unit-vs-unit pooled rounds retire?
4. **The per-side cap (`TileCapacity`):** today it counts every non-hostile unit on the tile and blocks
   an entry that would exceed the room. With pass-through a busy road tile could hold up walkers. Should
   the cap count only units **standing** on the tile (and moving units never wait on it)?

### The test migration

About **700 of the ~1,500 tests, in 70 files,** touch the old movement or pooled combat APIs
(`MoveIntent`/`MoveGroupIntent`/`PathRemaining`/`NextArrivalTick`/`CombatRoundEvent`/`CombatState`/
`CombatModel.Pooled`). They fall into five kinds; most need only a longer wait, not a rewrite.

**A config-derived march helper** (`tests/Sim.Tests/TestMarch.cs`) replaces `MovementCost.ExecutionCost ×
hops`: `TestMarch.Walk(sim, unit, tile)` runs until the walk is done (the sum of step costs along the
pathfinder's path, plus a margin), and `TestMarch.TicksFor(world, unit, tile)` gives that sum. Tests
say "N × the step time", never a hard-coded tick, as the working rules ask.

| Kind | What it is | Files (tests) | What happens |
|---|---|---|---|
| **1. Move as setup** | Orders a move, waits, checks something else (a haul, a build, fog, an intent's authority) | about 35 files (~330), e.g. `HaulQueueTests` 19, `GoalShapedIntentsTests` 27, `IntentAuthorizationTests` 6, `ExploredMemoryTests` 7, `PlayerViewTests` 7, the Persistence tests | Run on the new movement with `TestMarch`; expect edits only where a wait was too short. The suite tells us. |
| **2. Movement mechanics** | Asserts hop timing, crowding cost, paths, anchors | `MovementCrowdingTests` 17, `MoveOnBusyTests` 8, `SameTickFairnessTests` 8, `GroupMovementTests` 9, `GroupDisbandTests` 10, `GroupSnapshotTests` 2, `MidFlightSnapshotTests` 3, `RegenerateQueueTests` 1, `BreedingMoveLockTests` 5, `RiversTests` 20, `CrossingTests` 14, `TileCapacityTests` 5, `CanalsTests` 18, `BoatsTests` 46 (13 move) | Rewritten against the walk: same intentions, new numbers (steps, not hops; the walk's anchor, not `NextArrival*`). Crowding cost goes (units cost nothing in the planner); `MovementCrowdingTests` shrinks to the cap and pass-through rules. |
| **3. Roads** | Traffic wear, cost, arcs | `RoadTrafficTests` 7, `RoadEmergenceTests` 6, `RoadsOnEdgesTests` 10, `RoadPathfindingTests` 5 | Rewritten in **step 3** for subtile links (wear per step, a worn step cheaper, M34 arcs converted). Old arc tests deleted with the arcs. |
| **4. Pooled combat** | Unit-vs-unit rounds, pins, resume | `CombatResolutionTests` 8, `CombatDeathTests` 5, `CombatTriggerTests` 4, `CombatEngagementPinTests` 6, `CombatPinResumeTests` 6, `CombatDeterminismTests` 1, `MilitaryDeterminismTests` 3, `EquipmentCombatTests` 5, `PursuitTests` 9 (5 hop), `PatrolTests` 10 | The board tests (`BattlefieldWorldTests`, `TurnResolver`, `MoraleTests`) already cover the model that replaces them. Each pooled test is either deleted (the rule is gone), or restated on a board (the rule survives: death drops cargo, equipment power, engage). I'll list each one's fate for the user before deleting it. |
| **5. Sieges** | Structure damage and razing | `SiegeDamageTests` 5, `SiegeHeadlineTests` 2, `WallsAndGatesTests` 20 (12 pooled), `DemolishTests` 8 | Kept if question 3 is yes: they re-run on the new movement (units walk to the wall, the board closes, the siege round runs). |

Plus: the M42 tests (`SubtilePlacementTests` 17, `SubtileWalkInTests` 12, `SubtileRouteTests` 22,
`SubtileBoardTests` 2): placement and route rules stay, walking in, entry lanes and files tests are
deleted with their code; `SubtileRouteTests` become the walk's tests. And the AI and balance tests
(`AiPlayerTests` 12, `RivalTests` 23, the lab) wait for step 6 and 7: the day-X raid and war tuning,
with the power estimates learning the board.

**Order of work for the tests,** so the suite is green at the end of every step: flip the default and
let the failures list themselves; fix kind 1 in bulk (a helper and longer waits); do kind 2 file by
file with each file's fate shown; leave kinds 3 and 4 failing (skipped with a reason) until their step,
each skip listed here; the full suite (1,555 + 58 today) green with nothing skipped at the end of
step 7.

## Steps 2 to 4: what was built (2026-09-29)

The user's answers to the plan's questions (`docs/subtile-movement.md`, the update "M43 step 2"):
friends never block a walk (only stopping needs room); a walk that ends on a friend re-aims at the
nearest free subtile; siege rounds stay; the cap counts units standing, not passing.

**The walk.** `Movement/Walk.cs` + `Battlefields/SubtileRoutes.cs`.
- A tile order (`MoveIntent`, hauls, goals, chases, scouts, drivers) is `Walk.Begin`: pick the goal
  (`PickGoal`: the free subtile nearest the tile's centre, the centre four first), then
  `SubtilePathfinder.Find` (planned as the owner sees the world), then `Unit.SubtileRoute` and one
  queued `SubtileRouteStepEvent`. `Unit.PathFinalDest` is the tile it was ordered to. A drawn route is
  the same walk with no errand.
- **Stepping.** Ground-truth check each step (`SubtileStepRules`, `visible` null). A step across a tile
  edge is `TileEntry.Enter` plus the combat trigger. Each step wears its road link.
- **Ending.** `Walk.Finished` runs the errand (`DispatchOnArrival`: pursuit, scout, haul, goal, group)
  and looks for a siege. `Walk.Halt` (a wall it couldn't see): Idle, no errand. On the tile it was sent
  to when the ground isn't what the plan thought (a cache or idol footprint over the centre),
  `ReAimInTile` re-aims at what it can stand on, or counts it arrived.
- **Stopping on a friend.** Re-aim: a tile order takes the free subtile nearest the tile's centre, a
  drawn route the nearest to where it was drawn. Fill a tile and the rest spill to the tile beside.
- **Battles.** Entering a hostile tile opens a board; the walk becomes a battle route
  (`Battlefields.AdoptRoute`) and the destination tile is kept; leaving is a step across the edge and
  `Walk.Resume` plans afresh; `Close` resumes everyone. When a board opens, friends who were walking
  through each other on one subtile are set apart (`Placement.SeparateOn`).
- **Groups.** `MoveGroupIntent`: each member walks to a subtile of its own (`reserved`), the group is
  Moving until the last stops (`PendingArrivals`), and a member drawn into a fight halts the whole group.
  Step 5 makes them keep formation.
- **Castles.** A castle whose gate faced off the map can't be left; `AddStructure` turns it to the first
  edge with a tile beside it.

**Roads (step 3).** `Roads/SubtileLink.cs`; `Road.EffectiveCost/CreditTraffic/ConditionAt` on links;
`SubtileStepRules.StepCost(..., now)` = a quarter of the hop, less the link's wear, plus the ford;
`CheapestStep` lowers the A* floor when roads exist. `world.Roads` is keyed by link; the snapshot road
block keeps its shape (owner subtile x, y, axis). The wire's `RoadDto` is now a link (x, y in subtile
coordinates). Canal digging drops links touching the tile. Not re-measured with roads yet.

**Retired (step 4).** `MoveArrivalEvent`, `GroupArrivalEvent`, entry lanes, walking in, files landing,
the hop put-off, `Unit.PathRemaining` / `NextArrival*` / `SubtileWalk*` / `LeavingBoard`, the group path
anchors, the crowding price and the flat cap (`MovementConstants`), `CombatModel` /
`CombatConfig.Model` / `--combat`, the pooled unit-vs-unit rounds (`CombatRoundEvent` is the siege round
only), and `CombatTrigger.PinBelligerents`. Kept: `CrossingRule` / `Crossings` (the corridor's shape
mask), the tile-level `MovementCost` helpers (no crowding term), and `Pathfinding.FindPath` (the tile
route).

**Wire.** The hop fields are gone from `UnitDto`; the step (`SubStep*`) is the movement. `RoadDto` is a
link. Client: compiles; roads draw as the tile arcs the links cross until the subtile painter (step 8).

### Test migration: what happened to each group

| Group | Fate |
|---|---|
| Move as setup (~35 files) | Kept. Waits that stepped in coarse hours, or stopped at "position == tile", now wait for the walk to end (`TestMarch`, per-tick loops): `ChartTests`, `IdolTests`, `AiPlayerTests` (Train floor), `SameTickFairness` (mirrored subtiles, a symmetric camp), `HaulIntentTests`, `DeterminismTests` (derived from `TestMarch.TicksFor`). |
| Movement mechanics | `MovementCrowdingTests` **deleted** (crowding price and flat cap are gone); `SubtileWalkInTests` **deleted** (walking in is gone); `MovementRulesTests` **new** (tile order ends nearest the centre, pass-through, a crowd doesn't jam, a hostile force stops a walk, a neutral passes a fight, a hidden wall halts a walk, retasking, a walk takes the sum of its step costs, group orders, twin-run and mid-walk restore). `RiversTests`, `SubtileRouteTests`, `GroupMovementTests`, `GroupDisbandTests`, `MoveOnBusyTests`, `WireV2Tests` (SubStep instead of hop), `RegenerateQueueTests`, `GroupSnapshotTests`, `ClaimsLedgerTests` ported. `TileCapacityTests`: the planner-routes-round test deleted; the 17th unit now re-aims beside the tile. |
| Roads | `RoadCostTests` and `RoadDecayTests` ported to links; `RoadTrafficTests`, `RoadEmergenceTests`, `RoadPathfindingTests`, `RoadsOnEdgesTests` **replaced** by `RoadLinkTests`. |
| Pooled combat | **Deleted** (the rule is gone; boards cover it): 5 in `CombatResolutionTests`, 3 in `ArcherLineTests`, 3 in `EquipmentCombatTests`, 5 in `CombatEngagementPinTests` (blockade and pin: now `MovementRulesTests`), 1 in `CombatTriggerTests`; the dock re-arm test (a foot unit can't stand on water) and "feet still wade in the default game". **Restated on the death pipeline** (every death lands in `CombatRules.OnUnitDeath`): `CombatDeathTests`, `CombatCaptureTests`, `GraveTrackerTests`, the breeding death in `HouseBirthTests`. **Restated on boards**: `CombatPinResumeTests` (the un-pin is `Walk.Resume` after a board closes), `MilitaryDeterminismTests`, the mid-fight restore in `RecoveryTests`, `PursuitTests` and `PatrolTests` (the catch opens a board), `KingBalanceLabTests` (Advance doctrine), `BanditDriverTests` ambusher (a board opens). |
| Sieges | `SiegeDamageTests`, `WallsAndGatesTests`: timing derived from when the siege opens; "defender dies then siege" is now "the board closes, then the siege". `CombatPlayerViewTests` on sieges. |
| Scenarios and hosts | The raid scenario marches to the gate tile first (a castle is entered by its gate; a blind march at a hidden wall halts). `LandingHostTests`: bands come in by the gate. Canal tests: a dug tile carries a Canal structure. |

**Behaviour that changed, on purpose.** Feet never wade: an order aimed at open water ends on the nearest
reachable land or does nothing. A castle is entered only by its gate: a blind march at a castle stops at
its wall. A non-combatant on a board withdraws. Fights are turns on a board (a beat each), not hours of
rounds.

## Open after step 4

- Step 5 (groups keep formation), step 6 (AI: reachability by the pathfinder, power estimates that know
  the board, raid recall against the cap), step 7 (balance: the day-X raid, war tuning, road wear rates:
  a tile is now four steps of wear), step 8 (client: the subtile road painter, the dead hop fields, a
  Play test).
- Rename `SubtileRoute*` to `Walk*` (the field is the walk now) and `SubtileRouteStepEvent`.
- Road wear rates are per link and untuned.

## Result after steps 2 to 4 (2026-09-29)

Suites: `Sim.Tests` 1521/1521, `Sim.Persistence.Tests` 58/58 (snapshot v44).

Pathfinder re-measured with 10,800 worn road links added (production pathfinder, Release, 128x128 tiles =
512x512 subtiles, 60 routes per class): the cheap-step floor weakens A* on the whole grid (long routes:
median 25.8 ms, ~78k subtiles expanded, against 13.1 ms / 47k without roads), but the corridor path, which
is what a walk uses, stays cheap: medium 0.6 ms median (max 2.6), long 4.3 ms median (max 7.5), no
fallbacks to the whole grid, nothing unreachable.

One fixture lesson worth keeping: a test that re-issues a `MoveIntent` every think re-anchors the step,
so a step dearer than the think period (a ford) never lands. Re-issue only when the unit isn't already
walking to that tile (`AiPlayerTests.Train_RestoresBuilderFloor_AfterRoleLoss`).

## Step 6: AI and drivers (2026-09-29)

Built (decision in `docs/m25-rival-spec.md`, update 2026-09-29): campaign power priced by the castle's
attacker slots (`EnemyIntel.AssaultPower`), the full-rally spill rule, the raid recall capped by the
castle's room, `Walk.CanReach` used by the bandit driver's target choice. `Rival` and `AiPlayer` suites and
the new `AiBoardAwarenessTests` pass. Not done: reachability for the view-only brains (the wire carries no
path), so an order to an unreachable tile is re-issued each think; no tuning of war outcomes (step 7).

## Step 8 so far: client cleanup (2026-09-29)

In `Art Of War(prod)`: the hop fields left `UnitDto` (they defaulted to 0 when the server stopped sending them,
so every `hopToX >= 0` test would have read every unit as mid-hop: the fix mattered), `RoadWalk` is deleted, and
"walking" is `SubtileStand.Walking(u)` everywhere. `_AowTypeCheck` and `_AowWireBoundary` build clean.
Open: a Play test (nothing here has been seen running), the subtile road painter (roads still draw as the
tile arcs `KnownWorld.TileArcOf` derives from links), the duplicated ground-line drape helper, and
`SimClock.HopProgress` (now the step progress; rename).

## Step 8: the subtile road painter (2026-09-29)

Built in the prod client (decision and rules in its `docs/roads.md`, update 2026-09-29): `RoadNetwork` now
builds arms from the subtile links (runs between hubs, rounded 90 degree turns, per-link wear), so the field,
proximity, wear ladder and dresser rules are untouched and roads lie where units walked. `KnownWorld` folds
links to tiles for tile-level readers, the minimap plots link ends, the rebuild hash is over links. Type-checks
clean; not Play-run. Watch in Play: bends and inner-row roads, junction blobs, milestone spacing, the cost
of a rebuild on a big network.

## Fix combat: battlefield entry and the bandit brain (2026-09-30)

Built (decision and detail in `docs/fix-combat-m43.md`): the entry guard and edge wait, pops that keep the
tile, the bandit brain's board rules. Suites: `Sim.Tests` 1550, `Sim.Persistence.Tests` 58, all green.
Client: nothing needed beyond a Play check that waiting units stand across the edge and nothing visibly jumps.
