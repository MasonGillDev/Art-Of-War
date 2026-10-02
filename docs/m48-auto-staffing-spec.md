# M48 Spec: Auto-staffing

> Milestone spec (workflow §7 step 1). This is the *what*. Phases are
> sketched at the end, and the plan file (step 2) pins them. The decision
> and its losing alternatives are in `docs/auto-staffing.md`. Numbered
> after M47 (groups at work and at war, `docs/m46-groups-spec.md`), which
> is already claimed. M46 (24527e3) and M47 (f81930f) are merged; M48
> builds on M46's held slots and takes the next free snapshot version.

## What we're adding

1. **A desired-worker count per building.** Set at placement (default:
   the worker cap), shown on the building card, editable in place. Zero
   means "never auto-staff".
2. **An implicit staffing order per tile.** Placing a site installs one
   order that lives and dies with the building. While a site stands it
   keeps the site at its required builder count; once the building stands
   it keeps the building at its desired worker count.
3. **A Build errand** beside Train and Staff in the driver: pull a dormant
   Builder, walk them to the site, assign them.
4. **An implicit Train quota per role**, target = the sum of desired
   workers over the player's buildings that prefer that role. Pulls only
   no-role adults, at the trainer building for the role.
5. **Lifecycle hygiene.** Every path that removes a structure clears its
   implicit order and releases its claims; the same hook also clears
   player-authored orders on the tile, which it does not do today.

## The gap (why now)

- A dead farmer stays dead. `Staff` exists and works
  (`SubstrateWorkforceTests.Staff_RefillsTheFieldWhenAWorkerDies`) but
  only for buildings the player has authored a Staff order for on the
  Machine page. Nobody does that for every farm.
- Builders are never sent by the sim. They arrive only through
  `AssignBuildersIntent` or the named builder on `BuildIntent`; if that
  builder is gone the goal dissolves with no substitute
  (`Construction.cs:77-93`).
- `WorkersBelow` reads 0 for anything that is not an `Extractor`
  (`PredicateEvaluator.cs:140`), so a site cannot be a Staff subject today
  and `SetOrderIntent` rejects it (`SetOrderIntent.cs:140-148`).
- Orders outlive their building. `world.Structures.Remove` has five sites
  (`Construction.Complete`, `SiegeDamage.RazeStructure`,
  `ClearRubbleIntent`, `PlaceSiteIntent` for a bridge on a canal,
  `CacheLooting`, `Idols`) and none clears orders whose subject is that
  tile. A Staff order on a razed farm reads 0 workers forever and keeps
  pulling.
- `Order` has no in-place edit; a change is Clear + Set with a new id,
  which releases the crew for one think (`docs/automation-substrate.md`,
  "Priority is now a primary verb"). A desired-count stepper cannot work
  that way.

## Locked decisions

1. Desired count lives on the order (`Order.Target`), nowhere else. The
   building card reads it through the order on the tile.
2. The implicit order is a real `Order` with `Implicit = true`. It never
   counts toward anything, is never listed on the Machine page's authoring
   list, and cannot be Cleared by the player; it is edited through its
   target and removed only with the building.
3. One implicit order per tile for the building's whole life. The driver
   picks the errand from what stands on the tile: `ConstructionSite` →
   Build with target `Spec.RequiredBuilderCount`; `Extractor` → Staff with
   target `Order.Target`; any other kind → the order is removed at
   completion (nothing to staff).
4. The staffing order only pulls **below** target. It never unassigns.
5. The Staff selector for implicit orders: `Selector.OfRole(preferredRole,
   tile, radius: 0)` (0 = no radius limit, `SelectorResolver.cs:73`),
   `RequireDormant = true`, canonical order (distance, y, x, id). Role is
   the spec's `PreferredRole`; a building with no preferred role uses
   `Selector.Anyone`.
6. The Build selector: `Selector.OfRole(UnitRole.Builder, tile, 0)`.
7. Staff never trains. The implicit Train quota is a separate order per
   (player, role), subject `RoleCount(role)`, source = the trainer building
   per `RoleTrainerCatalog`, target = Σ `Target` of the player's implicit
   orders whose standing structure prefers that role. Recomputed by the
   driver each think from the world (it is derived, so it is not stored;
   the order record holds only identity and status). If the player has no
   trainer for the role, the quota reports `Blocked: no School` and does
   nothing.
8. Priority for implicit orders: staffing orders rank by building kind in
   a fixed table (food first), then tile `(y, x)`; the implicit Train
   quotas rank after all staffing. Player-authored orders keep their own
   priority and sort among them by number. (Hauling no longer uses the
   priority ladder; the ladder still orders Breed, Train and Staff.)
9. Manual control wins, unchanged: a unit the player commands is not
   dormant, and the player can always assign or unassign by hand.
10. The AI keeps `ThinkContext.StaffExtractor`. Implicit orders also run
    for AI players (they place sites through the same intent), so the AI's
    own staffing becomes redundant but harmless; removing it is a later
    cleanup, gated on the Homesteader lab staying green.
11. The player-facing Staff authoring flow is retired from the client, and
    `SetOrderIntent` stops accepting an explicit Staff recipe. No compat
    shim: the game is in development with no saved games to replay
    (`docs/client-intent-migration.md`).

## Detailed rules

### Placement

- `PlaceSiteIntent` and `BuildIntent` gain `int? DesiredWorkers`. Null
  means the spec's `WorkerCap`. It is clamped to `[0, WorkerCap]`. For
  kinds with `WorkerCap == 0` the value is ignored.
- On Applied, the intent installs the implicit order in the same
  resolution (one mutation, replays with the intent). Id from
  `world.NextOrderId++`, as every order.
- God-build (`Construction.IsGodBuild`) completes the site in the same
  intent; the order is installed first and the completion step keeps or
  removes it per decision 3.

### Editing

- New intent `SetOrderTargetIntent(orderId, target)`: owner check, order
  exists, `Implicit` orders only clamp to the standing building's cap;
  replaces the record in `world.Orders` under the **same id** (records are
  init-only, so it is a copy with the new target). Claims and status are
  untouched; the crew is not released.
- Lowering the target below the current worker count does nothing to the
  workers already there (decision 4).

### The driver

- `RunErrand` gains `Errand.Build`: destination = the site tile; verb on
  arrival = `AssignBuildersIntent(tile, [id])`; done when
  `u.Activity == Activity.Building && u.Assignment == tile`.
- Trigger for an implicit order is derived, not stored:
  site → `BuildersPresent(world) + inflight < RequiredBuilderCount`;
  extractor → `Workers.Count + HeldBy.Count + inflight < Target` (M46's
  held slots count as filled; see risk 2). In-flight counts the
  order's own `InFlight` claims plus pending claims earlier in the pass,
  the same discipline as the role quotas.
- A site whose materials are not yet delivered still pulls its builders
  (they wait on the tile, which is today's behaviour for a hand-assigned
  builder). Open question for the plan: whether to gate the Build errand
  on `MaterialsMet()` so builders are not benched on a starved site. The
  recommendation is to gate it and report `Blocked: waiting for
  materials`, so a wood shortage shows on the card as what it is.
- The Build errand should use the goal-shaped assign (which walks the unit
  itself) rather than the driver's older move-then-assign dance; Staff can
  move to the same shape in the same change.
- Journal outcomes are unchanged: `Fired`, `InFlight`, `Waiting`,
  `NoCrew` ("no builder free" / "no matching worker free"), `Blocked`.

### Lifecycle

- A new Core helper `Orders.OnStructureRemoved(sim, tile)` (next to
  `GoalRules.OnStructureRemoved`): removes every order whose
  `SubjectTile == tile` (implicit or not) and releases its claims. Called
  from all `world.Structures.Remove` sites listed in "The gap". A siege
  that converts a bridge back to a canal counts as removal.
- `Construction.Complete`: if the finished kind has `WorkerCap == 0`, the
  implicit order is removed here; otherwise it stays and switches to Staff
  by decision 3. Completion also releases any Build claims whose builders
  are now free (they already went Idle in `Complete`).
- `UnassignWorkersIntent`'s ghost purge and the death path are unchanged.

### Wire and client

- `StructDto` gains `DesiredWorkers` (the implicit order's target, or -1
  when the kind has no slots) and `StaffingOrderId`, so the card can show
  the order's `LastOutcome` through the existing `OrderDto` projection.
- `OrderDto` gains `Implicit`. The Machine page hides implicit orders from
  the list and keeps them on the map overlay (a pad on the building).
- Placement UI: a stepper, default = cap, in the site placement flow.
  Building card: the same stepper plus the staffing state (Resting /
  Working / Short of hands / Blocked). The production client is a separate
  repo; cut from the wire.

## Determinism and persistence

- **FormatVersion: the next free number when Phase A lands.** Groups work
  is taking numbers in parallel (M46 v50, M47 v51, M49 group stance v52),
  so the plan file pins the actual number at the time. `Order.Implicit`
  (bool).
  Nothing else is new state: desired count is `Order.Target`, triggers and
  Train targets are derived by the driver.
- **No new anchors, no new events.** Everything is driver work plus
  ordinary intents; the replay headline covers it.
- **Mutation points** (added to `docs/determinism-audit.md`): implicit
  orders are installed only by `PlaceSiteIntent`/`BuildIntent`, retargeted
  only by `SetOrderTargetIntent`, removed only by
  `Orders.OnStructureRemoved` and `Construction.Complete`.
- **Pure reads:** the derived trigger and the derived Train target. Both
  get the 100× no-mutation pin.
- **No compat for old logs.** `PlaceSiteIntent`/`BuildIntent` gain the
  field outright; a log without it is from before this milestone and is
  not replayed (`docs/client-intent-migration.md`).

## Headline test

**A kingdom restaffs itself and replays.** Start a kingdom with a farm
(desired 3, staffed 3), a mine (desired 1 on a 3-slot mine, staffed 1), a
school, two dormant farmers, one dormant builder, two untrained adults,
and a lumber camp site awaiting one builder.

1. Kill one farmer on shift. Within one think a dormant farmer is walking;
   the farm is back at 3 with no player intent.
2. Kill the second dormant farmer and another on shift. The farm reads
   Short of hands; the implicit Farmer quota trains one adult; Staff then
   places them. The farm is back at 3.
3. The mine stays at 1 throughout.
4. The lumber camp site gets the dormant builder, completes, and then
   staffs itself to its cap from whoever is free; the Lumberjack quota
   trains the last adult.
5. Raze the farm. No order and no claim refers to its tile afterwards.

Must hold for all of it:
- Driver on vs. intent-log replay with the driver off: equal
  `Snapshot.Hash`.
- Snapshot and restore mid-walk (a recruited hand halfway to the farm),
  then finish: same hash as the uninterrupted run.

## Phases (sketch; the plan file pins them)

| Phase | Work | Done when |
|---|---|---|
| A | `Order.Implicit`, FormatVersion bump, `Orders.OnStructureRemoved` wired into all removal sites, orphan-order regression tests | Razing a farm with an authored Staff order leaves no order and no claim; round-trip |
| B | `DesiredWorkers` on Place/Build intents, implicit order install, `SetOrderTargetIntent`, derived Staff trigger, priority table | A placed mine with desired 1 ends with one worker; death refills with no intent; desired 0 sends nobody |
| C | Build errand, site trigger, materials gate | A site placed with no builder named gets one from the pool and completes |
| D | Implicit Train quota per role, derived target | The Short-of-hands → train → place chain, with neither order knowing the other |
| E | Wire (`StructDto`, `OrderDto.Implicit`), debug client stepper and card state; retire the Staff authoring flow; audit entries; headline test | Headline green; manual playthrough |

## Out of scope

- Reinforce for groups (blocked on M46's add-member intent).
- Conscription of working units.
- Progression gating of the default count.
- Moving the AI off `StaffExtractor`.
- Any change to hauling (queue and routes are untouched).

## Risks for the planner

1. **The removal sites are scattered.** Six `Structures.Remove` calls,
   three of which do not call `GoalRules.OnStructureRemoved` today. Audit
   every one; a missed site is an order that pulls workers forever.
2. **M46 held slots.** M46 Phase D moves a mustered worker from
   `Extractor.Workers` to `Extractor.HeldBy`; a held slot counts against
   the cap and produces nothing, and the M46 session is changing
   `PredicateEvaluator.WorkerCount` is `Workers + HeldBy` (landed in
   24527e3). The derived Staff trigger must read that same count so a held
   slot is never refilled. An explicit `AssignWorkersIntent` may still take
   a held slot: `AssignWorkersIntent.GiveAwayHeldSlots` evicts the newest
   hold and cancels that unit's `SavedTask` (the player's choice wins). The
   implicit order never does, because it only pulls below the summed
   count.
3. **The AI double-staffs.** Its own `StaffExtractor` and the implicit
   order can both pull for one slot; the cap check counts walkers so the
   second is marched and dissolved, but it is wasted motion. Pin with the
   Homesteader lab.
4. **Builders benched on starved sites** if the materials gate is skipped.
5. **Train over-count.** The derived quota counts all units of a role,
   including those working elsewhere or dormant far away; it may train one
   more than needed when role units idle out of reach. Acceptable, record
   it.
