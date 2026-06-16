# Architecture & Standards

> Authoritative reference for how this game is built. Patterns that are mandatory, things that are forbidden, and the roadmap of where we're going. When a future contributor (or future-you) asks "how do we do X here?", this doc is the answer — and if it isn't, this doc gets updated.

The vision lives in `persistent-rts-design.md`. *This* doc is about engineering practice: the patterns that make the design work, and the boundaries we don't cross because we'd lose determinism or persistence or testability.

---

## 1. The Core Contract

One promise the engine must keep, above everything else:

> **Determinism.** Given the same inputs, the same code produces the same outcomes. Forever. Across machines, runtime versions, and crashes.

Determinism is the testable form of every other property we want — replay, recovery, fairness, fog-doesn't-touch-the-sim, audit. Every milestone has a **headline test** that operationalizes determinism for that milestone's surface. If the headline test fails, the milestone isn't done, no matter how much code shipped.

| Milestone | Headline test |
|---|---|
| M0 — engine | `Snapshot.Hash(run1) == Snapshot.Hash(run2)` for identical scenarios |
| M1 — logistics | Same-tick fairness pinned (who wins buffer contention) |
| M2 — roads | Lazy decay observation-independent (`catchUp(once at T) == catchUp(many along the way)`) |
| M3 — fog | `Snapshot.Hash(simWithViewsOff) == Snapshot.Hash(simWithViewSpam)` |
| M4 — persistence | `Snapshot.Hash(uninterruptedRun) == Snapshot.Hash(snapshotAndRecoverRun)` |

These are not unit tests. They are *contracts*. They live in named test classes and are referenced from the milestone status docs.

---

## 2. Foundational Patterns

These patterns recur. Learn them; reuse them; do not re-invent them.

### 2.1 Event-driven, never tick-driven

There is **one** global event queue (`Sim.Core.Engine.EventQueue`). Priority is `(At, Seq)`. Time advances by *processing the next event*, not by ticking the world.

**Allowed:**
- An event scheduling other events as part of its `Apply`.
- A self-rescheduling event (e.g. `ProductionTickEvent` rescheduling itself at `now + period`).

**Forbidden:**
- A loop that walks all tiles every N sim-seconds.
- A loop that walks all extractors every N sim-seconds.
- Any code path with the shape "iterate everything periodically."

The cost of `for each tile in the world` scales with the world. The cost of event processing scales with the number of decisions. A million-tile world with one walking unit should fire one event per arrival, not a million.

### 2.2 The pure-read wall

Some computations need to be read by clients, AI, pathfinders, views — **without** changing sim state. Examples: `Road.ConditionAt`, `Road.EffectiveCost`, `View.VisibleTiles`, `View.BuildPlayerView`.

**Rule:** these functions return a fresh value computed from current state and never write. If they could write, the timing of read calls would become part of the simulation's hash — and reads happen from the client / UI / pathfinder at unspecified moments. Hash determinism would die instantly.

**Test pattern:**
```csharp
var hashBefore = Snapshot.Hash(sim);
for (var i = 0; i < 100; i++)
    SomePureRead(world, ...);
Assert.Equal(hashBefore, Snapshot.Hash(sim));
```

Every pure-read API gets one of these.

### 2.3 The inverted pure-read wall (sim writes, view reads)

Some derived state IS stored — but it's written by deterministic events only and read by views only. Example: `GameWorld.Explored` (per-player explored memory).

**Rule:**
- The mutation has *exactly one* event-driven write site (e.g. `Sight.Reveal` is called only from `MoveArrivalEvent.Apply`, `BuildCompleteEvent.Apply`, `Genesis.Build`).
- Views read it but never write it.
- A view writing this would corrupt snapshotted state.

This pattern is documented per-feature in `docs/determinism-audit.md`. When you introduce a new piece of state of this shape, **add it to the audit**.

### 2.4 Self-rescheduling events with re-arm

Continuous-looking processes (production filling a buffer, road decay over time, a build progressing) don't tick globally. The owning entity schedules ONE future event; that event optionally schedules the next one.

Pattern in `ProductionTickEvent.Apply`:
- Has work to do? Apply it, schedule the next tick, mark `TickArmed = true`.
- Buffer full / no workers? Set `TickArmed = false` and **don't reschedule** — the entity goes dormant.
- Something later changes the world (a haul-pickup frees buffer space, a new worker is assigned)? That code calls `extractor.ArmIfDormant(sim)` which schedules a fresh tick.

**Rule:** never poll. Re-arm via the event that *changed the world* into a state where the dormant thing should resume. This is what keeps event volume bounded by activity, not by time.

### 2.5 Lazy catch-up math

Time-rate state (road decay; future: biome regen, hunger, weather, anything continuous) **is not stored as "the current value" plus a global decay loop.** Store **rate + last-touched-tick** and compute the current value on access.

Reference implementation: `Sim.Core.Roads.Road.CatchUpDecay`.

The math is integer-exact and **observation-independent** — touching a tile once at tick T must give the same condition as touching it many times along the way to T. The key is "advance the last-touched-tick by *completed boundaries only*, carry the remainder":

```
periods = (now - LastDecayTick) / DECAY_PERIOD     // integer floor
condition -= periods * DECAY_PER_PERIOD            // completed boundaries only
LastDecayTick += periods * DECAY_PERIOD            // carry the remainder
```

If `lastTick` were set to `now` and the remainder dropped, the result would depend on how often you observed. That's nondeterminism wearing arithmetic's clothes.

**Test pattern (observation-independence):**
```csharp
var w1 = MakeWorld(); CatchUp(w1, T);            // once
var w2 = MakeWorld();
for (var t in irregularSequenceUpTo(T)) CatchUp(w2, t);   // many
Assert.Equal(state(w1), state(w2));
```

**Forbidden:** condition-dependent decay rates. A rate that depends on the value being decayed creates a coupled-interval trap — you'd have to integrate over the elapsed time, which makes the math fragile and observation-sensitive. If you ever want "stone holds up longer than dirt," do it **band-stepped** (catch up to the next band at the current flat rate; switch rate at the boundary), never continuously coupled.

**Spatial extension (M9).** When the rate at a tile depends on **other entities at varying positions** (e.g. nearby extractors degrading fertility), the same lazy-catch-up shape works — with two extra disciplines:

1. **Catch-up only at rate-changing events** (e.g. extractor production start/stop). Between such events, the rate at any tile is invariant by construction. Reads then derive the rate from the current world state (which equals the rate that's held since the last catch-up) and apply it over `(now - lastUpdateTick)`. Pure: no mutation.
2. **Anchor `lastUpdateTick = now` at every transition catch-up** (drop the carry-remainder). The remainder is fine within a constant-rate segment; across rate transitions it would re-interpret partial old-rate elapsed-time as partial new-rate elapsed-time, which is wrong. So at every transition, finalise the old rate's effect to `now` and start the new rate's window cleanly. This also means the catch-up writes an entry even when the math is a no-op, which is what makes a "first time this tile sees any rate" tile correct.

Reference implementation: `Sim.Core.Biomes.BiomeDegradation` (M9). The aggregator across in-range producers is **MAX, never sum** — overlapping producers do not stack their rates (that would invite a "cluster to instakill" exploit and break the lazy/local property).

**Band-crossing step penalty (M9 follow-on).** When the value being decayed has discrete output bands (e.g. fertility → F/G/D biome), a smooth gradient at a band boundary collapses recovery into a 1-tick blip — defeating the gameplay pressure the dormancy was supposed to create. Solution: snap the value to the next band's baseline on the downward crossing (asymmetric — no bonus on upward). Implemented inside the catch-up math: detect crossings during the elapsed-time integration, apply rate up to the crossing, snap, continue with remaining periods. Bounded iteration (at most one snap per band traversed), observation-independent across crossings, pure-read-safe. See `docs/biome-degradation.md` §step-penalty.

### 2.6 Fencing tokens for stale events

When a state change can invalidate an already-queued event, you need a way for the event to detect "I'm stale" at fire-time and no-op cleanly.

We don't dequeue events. We let them fire, and they check.

Three live examples:
- `ConstructionSite.ScheduledCompletion` — the `BuildCompleteEvent` checks `site.ScheduledCompletion == this.At`. If the site got paused and resumed, the new completion is at a different tick; the old event fires, sees the mismatch, no-ops.
- `Unit.AssignmentEpoch` (`byte`, bumped on every activity change) — `HaulPickupEvent`/`HaulDepositEvent`/`MoveArrivalEvent` carry the epoch they were scheduled at and fence on mismatch.
- `Extractor.NextProductionTickSeq` and `ConstructionSite.BuildCompleteSeq` (M4) — preserved across recovery so same-tick fairness survives crashes.

**Rule:** when you add a state transition that invalidates a pending event, use one of these patterns. Never dequeue, never search the queue — let the event self-fence.

### 2.7 Sparse per-entity dynamic state

Most tiles don't have a road. Most tiles aren't explored by most players. Most structures aren't extractors. Storage that scales with grid size × players × kinds will eventually crush you.

**Rule:** dynamic state lives in sparse dictionaries keyed by the entity that has it.

- `GameWorld.Structures: Dictionary<TileCoord, Structure>` — sparse by tile.
- `GameWorld.Roads: Dictionary<TileCoord, RoadState>` — sparse by tile; tile removed when condition → 0.
- `GameWorld.Explored: Dictionary<int, HashSet<TileCoord>>` — sparse by player.

Pure reads on absent entries return the "default" (no road = plain biome cost; no entry in Explored = not seen). Don't pre-populate.

### 2.8 Anchors for in-flight state (M4)

Anything that schedules a future event must store an **anchor** on the entity that "owns" the schedule. The anchor is two fields:

- `long? NextXxxTick` — when the event fires.
- `long? NextXxxSeq`  — the `Seq` it was scheduled with.

`RegenerateQueue.From(sim)` iterates entities, reads the anchors, and recreates the event queue via `Simulation.ScheduleWithSeq(at, seq, e)`. This is how mid-flight snapshot restore works — the queue isn't stored, it's *regenerated*.

**Rule:** if you add a system whose state evolves via scheduled events (combat ticks, sieges, weather), add an anchor. Add a regeneration case in `RegenerateQueue.From`. **Never** put your event types into durable storage — durability is for stable schemas (entity state + intents), not for refactor-prone event internals.

---

## 3. Things You Must Do

A short list of non-negotiables.

1. **Integer math, never floats, for anything serialized.** Floats accumulate differently across machines/runtimes; replay diverges. If you need fractions, use rational arithmetic via `Numerator / Denominator` integers (see `StructureSpec.RoleBonusNumerator/Denominator`).

2. **Own the RNG.** Use `Sim.Core.Engine.Rng` (xorshift64). Never use `System.Random` — its algorithm has changed between .NET versions, which makes replays break across runtime upgrades.

3. **Validate at resolution time, not submission time.** Every `Intent.Resolve` re-checks its preconditions against current world state. Submission-time validation is advisory (a courtesy to clients). Resolution-time is correctness. See `docs/intent-validation.md`.

4. **Fail clean.** Intents/events that can't apply return `IntentOutcome.Reject(...)` and **mutate nothing**. No half-applied state. No exceptions for "valid in some sense but precondition not met."

5. **Append-only enums in serialized state.** `Resource`, `Biome`, `StructureKind`, `UnitRole`, `Activity`, `HaulPhase` — every enum that hits the snapshot. Existing values keep their byte forever; new values get the next available byte. Renumbering breaks every old snapshot.

6. **Canonical iteration order for snapshots.** Iterate by id (units), by `(y, x)` (structures), by enum ordinal (resources in a holdings map), by player id (per-player state). HashSets get sorted at serialize-time. Anything that touches a `Dictionary`'s natural iteration order is a bug.

7. **One mutation point per state.** Roads → only `Road.CreditTraffic` (called from `MoveArrivalEvent.Apply`). Explored → only `Sight.Reveal` (3 event-driven sites). Anchors → only the scheduling site that owns them. Audit them in `docs/determinism-audit.md`.

8. **Folder = namespace.** `src/Sim.Core/Roads/Road.cs` is in `namespace Sim.Core.Roads`. No exceptions. Type names that collide with namespace names get renamed (`GameWorld`, not `World`).

9. **Decision docs for non-trivial choices.** When a choice rules out a defensible alternative, write a doc in `docs/`. Cover: the decision, why, what was ruled out, how this expands. See `CLAUDE.md` and the existing docs as templates.

10. **Headline test before milestone "done."** Each milestone has a one-sentence test that proves the contract. The milestone isn't done until that test is green. (Per §1 table.)

---

## 4. Things You Must NOT Do

The bans. Each comes with a real consequence — these aren't style preferences.

1. **No floats in serialized state.** Cross-machine replay divergence. Banned.

2. **No `System.Random`.** Cross-runtime divergence. Use `Sim.Core.Engine.Rng`.

3. **No global iteration on a timer.** "Every N ticks, walk every X" doesn't scale and re-introduces the global-tick failure mode the event engine exists to avoid. If you need to apply something across many entities, drive it from the events that already touch them (lazy catch-up; per-entity self-rescheduling). The exception is `Snapshot.cs` iterating for serialization — that's not a sim event, it's I/O.

4. **No mutation in pure-read paths.** `Road.EffectiveCost`, `View.VisibleTiles`, `Pathfinding.FindPath` (with its cost delegate), `Snapshot.Hash` — never write. Test it with the 100×-no-mutation pattern. A read that writes corrupts the determinism contract irrecoverably.

5. **No write-back from views to sim state.** If a view computes a derived value, it returns it; it does NOT cache it onto a shared field. Caching observation onto sim state means observation timing becomes part of state.

6. **No event type stored in durable form.** Snapshots serialize *entity state*. Intents serialize their JSON payload. **Event class internals never appear in durable storage.** Events are derived from `state × code`; durable artifacts must outlive code changes.

7. **No path recomputation on restore.** A committed path was chosen against road conditions at command time. Recomputing on restore against current road conditions could pick a different route → state divergence. Store the committed `PathRemaining`. (M4 architecture decision.)

8. **No condition-dependent decay rates.** Coupled-interval trap. Use band-stepped rates if needed. (Pattern §2.5.)

9. **No silent fallbacks for "I don't know what to do."** Throw `InvalidOperationException` for can't-happen states with a clear message naming what was unexpected. The first principle of debugging persistent systems is "fail loudly at the cause, not silently at the symptom."

10. **No "iterate to find" when an index would do.** `ConstructionSite.BuildersPresent` and `BuildCompleteEvent.Apply` scan all units to find ones on a tile — this is O(units) per call. Today it's fine; at scale it becomes a per-tile index (`Dictionary<TileCoord, List<int>>`). When you write code with this shape, add a comment flagging the scaling concern (look at `BuildersPresent` in `Structure.cs` for the existing flag).

11. **No "trust the client" anywhere.** Every intent re-validates. Every fenced event re-checks its tokens. The wire is hostile, even if today the only "client" is our own test code.

12. **Never bypass `EventQueue` ordering.** The `(At, Seq)` priority is the source of truth for resolution order. There is no "fire this immediately" backdoor.

13. **Never use auto-properties for serialized state without thinking about restore.** `init`-only properties block post-construction mutation, which means snapshot restore can't fill them via object-initializer alone if the constructor enforces an invariant. Read `Unit.RestoreAssignmentEpoch` and `Rng.SetState` as the right pattern: an `internal void RestoreXxx(...)` accessible to `Snapshot.cs` only.

14. **Never write a `// TODO: handle later` for a determinism property.** Either the property holds or the milestone isn't done. TODOs are for ergonomics and polish, not contracts.

15. **Never edit a commit's published history (no `--force`, no `--amend` after push).** Use new commits to fix old ones. Locally before push, amends are fine.

---

## 5. Module Structure

### Folder layout

```
src/Sim.Core/
  Engine/        Simulation, EventQueue, ScheduledEvent, Rng, IntentOutcome
  Intents/       Intent (base), IntentEvent
  World/         TileCoord, TileGrid, Unit, GameWorld, Resource, Biome,
                 Structure, Player, HaulPlan, Activity, Genesis
  Movement/      Pathfinding, MoveIntent, MoveArrivalEvent
  Logistics/     PlaceSite/AssignBuilders/AssignWorkers/UnassignWorkers/
                 Haul intents; BuildComplete, ProductionTick, HaulPickup,
                 HaulDeposit events
  Roads/         RoadConstants, RoadState, Road
  Vision/        Sight, View
  Persistence/   Snapshot, RegenerateQueue
src/Sim.Host/    CLI entry point
tests/Sim.Tests/ xUnit tests, one file per feature
docs/            Decision docs, audit, this file
```

**Rules:**
- Folder = namespace.
- A "feature" gets a folder when it crosses one file. Single-file features can live in `World/`.
- Tests live alongside other tests in `tests/Sim.Tests/`, one file per feature/topic.
- The `Sim.Persistence` project (M4 Phase C) sits at `src/Sim.Persistence/` and holds SQL-dependent code. `Sim.Core` stays SQL-free.

### Decision docs (`docs/`)

The decision-doc convention is documented in `CLAUDE.md`. Recap:
- One file per decision.
- Cover: what was decided, why (including alternatives ruled out), how this expands.
- File the decision when a choice rules out an alternative that a future reader would otherwise reach for.

Existing decision docs:
- `persistence-model.md` — intents-as-truth + snapshot-as-anchor (needs M4 update — see `m4-status.md`).
- `code-layout.md` — feature folders + namespace convention.
- `extraction-model.md` — structure-gated extraction, everything-physical.
- `intent-validation.md` — resolution-time validation contract.
- `determinism-audit.md` — the living audit of "one mutation point" invariants.
- `world-generation.md` — frozen-output procedural pipeline; freeze rule; water passable-but-expensive.

---

## 6. Testing Standards

Every new feature ships with at least the tests below. If a contract isn't testable, the design is wrong.

### Twin-run

Run the same scenario twice; assert `Snapshot.Hash` equality. This is the smoke test that catches the broadest class of nondeterminism bugs cheaply.

### Pure-read wall enforcement

For any public read API on volatile state (road condition, visibility, etc.), test the 100×-no-mutation pattern. See `LiveVisibilityTests.VisibleTiles_IsPureRead_NoMutation` and `RoadCostTests.EffectiveCost_IsPureRead_NoMutation`.

### Observation independence (for lazy-catch-up state)

Compute once at T vs many times along the way to T. Assert identical result. See `RoadDecayTests.CatchUpDecay_IsObservationIndependent`.

### Same-tick fairness

When multiple events at the same `At` could affect each other, the order must be deterministic by `Seq` (submission order). Write a contention scenario, assert who wins, swap submission order, assert the winner swaps. See `SameTickFairnessTests`.

### Snapshot round-trip

`Snapshot.Hash(sim) == Snapshot.Hash(Snapshot.Restore(Snapshot.Serialize(sim)))`. For every new field added to `Unit` / `Structure` / `GameWorld`. See `SnapshotRoundTripTests`.

### Headline determinism test (the per-milestone contract)

See §1. The milestone isn't done without it.

---

## 7. The Milestone Workflow

Each milestone follows the same shape. New work goes through these phases.

### Step 1 — Hand off a spec doc

A milestone starts with a spec document. The spec covers: what we're adding, what the gap or feature is, what decisions are locked, what's deferred, what the headline test looks like.

Specs are written by the human; the engine work is broken into phases by the planner.

### Step 2 — Review, decisions, plan file

The planner reviews the spec, flags risks, asks focused questions about decisions the spec leaves open. The result is a plan file at `~/.claude/plans/<id>.md` with:

- **Context** — why this change exists.
- **Locked decisions** — every "right now we're doing X" choice.
- **Phased approach** — 4–7 phases, each with a clear done-criterion.
- **Files modified / created.**
- **Existing utilities reused** (don't reinvent).
- **Out of scope** — what's deferred.
- **Verification** — `dotnet build` / `dotnet test` / host smoke.

### Step 3 — Execute phase by phase

Each phase ends with `dotnet test` green. Each phase has at least one test that closes its contract. Don't move to the next phase with a failing test.

### Step 4 — Audit

Update `docs/determinism-audit.md` with the new mutation points and pure-read paths. Grep-verify that the audit's claims hold.

### Step 5 — Demo / smoke

The host (`dotnet run --project src/Sim.Host`) runs a scenario that exercises the new feature end-to-end and prints something a human can read.

### Step 6 — Commit per milestone (or per phase if the phase is large)

One commit per milestone is the default. Phase commits are appropriate when phases are independently shippable (M4 Phases A+B and C–F are separable; Phases inside M1 are not).

---

## 8. Roadmap

### Done

| Milestone | What it added | Status |
|---|---|---|
| M0 | Deterministic event engine, intents, snapshots, twin-run | ✅ |
| M1 | Logistics loop (build → staff → produce → haul → build) | ✅ |
| M2 | Emergent roads (traffic → condition → cost) + move-on-busy + epoch fencing | ✅ |
| M3 | Fog of war (owner + explored + visible + player view) + Tower | ✅ |
| **M4** | **Persistence & recovery (anchors + `RegenerateQueue` + SQLite intent/snapshot stores + `Recovery` + host `--data-dir`)** | **✅** |
| **M5** | **Group movement (Form / MoveGroup / Disband; solo-intent rejection on grouped units)** | **✅** |
| **M6** | **Multi-faction & diplomacy (configurable N factions; three-state symmetric relationships; unilateral-telegraphed war + bilateral peace/ally)** | **✅** |
| **M7** | **Combat (force-vs-force on a tile; multi-round linear-proportional; per-unit Health + Buffs seam; capture-on-death; mid-fight crash recovery)** | **✅** |
| **M8** | **Population (derived age from BornTick; seeded lifespan; House + breeding; stop-on-removal one rule; mid-gestation crash recovery)** | **✅** |
| **M9** | **Biome degradation (spatial lazy field: per-tile fertility, MAX over in-range producers, implicit Desert latch, biome-mismatch dormancy ends infinite single-tile extraction)** | **✅** |
| **M11 (Phase 1)** | **Procedural map generation (Perlin + Whittaker → frozen integer biomes; water passable-but-expensive)** | **✅** |
| **M13** | **Food consumption (castle sink: per-period drain by population; M9-style lazy catch-up; predicted-dry-out FamineCheckEvent; staggered StarvationDeathEvent — oldest first; recovery-clean)** | **✅** |
| **M12** | **Boats (per-unit Traversal enum; water-only BoatMovementCost; Dock = shipyard + embark/disembark seam with Slip + auto-production + stall/re-arm; embarked passengers off-tile; Embark/MoveIntent/Disembark; sink-drowns; recovery-clean)** | **✅** |
| **M14** | **Military (Barracks trains Soldier/Archer via `RoleTrainerCatalog`; Sword/Bow/Shield as `Resource` values; instant `CraftEquipmentIntent`; `EquipUnitIntent` → 2-slot distinct-kind Buff loadouts; `EffectivePower(now)` expiry seam; death/retrain drops items to the ground pile; zero new anchors → recovery-clean for free. Ranged-from-adjacent still deferred. See `docs/military-training.md` + `docs/equipment-model.md`)** | **✅** |
| **M15** | **Extraction claims (LumberCamp/Farm work explicit player-chosen claim tiles — degradation footprint = claim, one claimant per tile across all kinds/owners, full structural exclusion both directions, ceil production taper by in-band claims, claim-exhausted dormancy + decline-to-arm; claims reserve at site placement and transfer at completion; FormatVersion 10; deterministic auto-select when the intent omits tiles. Demolish/Quarry-Mine claims/claim-editing deferred. See `docs/extraction-claims.md`)** | **✅** |
| **M16** | **Bandits (reserved hostile-to-all faction id `-1`; spawn/despawn-in-darkness as SERVER-INTERNAL durable intents validated at resolve time; `UnitRole.Bandit` age-exempt; `LoadCargoIntent` as the general steal/recover atom — haul source-ownership gap pinned as raiding-by-design; `BanditDriver` in Sim.Server = the proto-automation layer: pure reads in, ordinary intents out, ephemeral brain, replay-from-intent-log headline proves determinism survives out-of-sim AI. Camps/structure-damage/notices deferred. See `docs/bandits.md` + `docs/m16-bandits-spec.md`)** | **✅** |
| **M17 (Phase 1)** | **AI players — the Homesteader (`--ai N` full fair factions, identical starts, neutral scout retired; brain consumes ONLY the projected ViewDto — fairness is the signature, reflection-pinned; strategic ladder + background logistics + per-think unit reservations; DecisionTrace ring buffer + `--ai-trace`; the BALANCE LAB: `Homesteader_Survives100GameDays_NoStarvationDeath` freezes "is the opening winnable?" as CI; replay-from-intent-log re-proven for a full economy run. Five arbitration lessons documented in `docs/ai-players.md`. Defender/Rival/training deferred)** | **✅** |
| **M18** | **Automation engine (standing orders: condition/action atoms + steps as durable Core data in `GameWorld.StandingOrders`, FormatVersion 12, zero new anchors; cursor block mutated only by server-internal durable `AdvanceOrderCursorIntent` with an ExpectedStep fence; evaluation in Sim.Server `AutomationDriver` — third instance of the M16 driver shape, fog-fair via per-owner `View.VisibleTiles`, canonical order-id arbitration, bounded-retry auto-disable with notice; SupplyLine/Route/StandingProduction are templates over the generic engine; headline = driverless replay hash match; host `--automation` smoke. Unlock structures, military atoms, pooled haulers deferred. See `docs/automation-layers.md` + `docs/m18-automation-engine-spec.md`)** | **✅** |
| **M21** | **Water restores land + canals (water proximity lifts the M9 desert latch on DEGRADED land only — `WaterProximity.IsNearWater` + one condition in `DeriveRate`, raw desert stays barren via the existing `storedDev<0` guard; canals = `PlaceCanalIntent` whole-path build → `BuildCompleteEvent` floods land to `Biome.Water` with NO resulting structure, cost/time scaled per tile, must extend from water, `CanalReservation` scan mirrors `Claims.ClaimantAt`; canal completion is a new rate-transition that catches up affected fertility under the OLD rate then mutates the grid — the §2.5 anchor discipline; composes with M12 boats for free — sail canals, dock slips on canal tiles, zero new boat code; `FormatVersion` 14→16 (`WaterRecoveryRadius` + `ConstructionSite.CanalPath`), zero new anchors → recovery-clean; host `--canal` smoke. Boosted/raw-desert irrigation + canal draining deferred. See `docs/canals.md`)** | **✅** |

### After M8 — the big systems

These are the milestones the design doc (`persistent-rts-design.md`) calls out and that the engine is now ready for. (M5 was originally slotted as Combat; group movement landed first because the abstraction underpins both caravans and combat formations.)

**M5 Phase 2 — Split / Merge / Dispatch.** Atomic group restructuring intents. Cleanest once Form is exercised in practice — the design questions ("what if a group is mid-move when split?") become concrete.

**Next big system — open.** ~~Mobs~~ landed as M16 (bandits — the "AI/intent-generator layer" exactly as predicted; bandit CAMPS are its designed follow-up). Remaining candidates: **trade** (barter posts on top of the now-meaningful demographic economy — caravans, exchanges, the M7 capture-on-death pipeline applied to raiding trade routes), **player automation** (standing orders submitting intents from outside — the M16 driver is its proven architectural template), or **things-in-the-fog** (ruins/caches/unique deposits — the exploration payoff flagged by the 2026-06-11 fun audit).

**M7 — Trade.** Async trade posts (§11): list / deposit / accept / withdraw via menu UI but with goods physically moving in the sim. Caravans (multi-unit haulers built on the Group primitive) become natural here. Trade composes with roads, raids (M6), and diplomacy.

**M8 — Roads phase-2.** Per-edge condition (if fortifications need route granularity), remembered roads at last-seen condition for explored-but-unseen tiles (fog × roads), condition-band-stepped decay if "stone holds longer" is gameplay-visible.

**M9 — Population & roles.** Citizens are currently spawned at genesis. M9 adds: training units into roles, population growth tied to Food, role specialization beyond the M1 set.

**M10 — Clients & networking.** The current `Sim.Host` is a one-shot smoke driver. M10 turns it into a real server: WebSocket / gRPC intent submission, per-player `BuildPlayerView` over the wire, push notifications (§12). This is what the M3 `BuildPlayerView` design exists to feed.

**M11 — World generation.** Phase 1 landed: Perlin + Whittaker pipeline, frozen integer-biome output, deterministic start picker, water made passable-but-expensive so generated maps never trap the player. See `docs/world-generation.md`. Phase 2+ (larger worlds, named locations, multi-player fair start placement, post-classification feature passes) is deferred until gameplay needs it.

(Order is suggested, not locked. M10 can come earlier if the user wants to drive the game from a real client. M11 can come whenever the world feels too small.)

### Long-term

Listed in the design doc (`persistent-rts-design.md`); each will get its own decision doc when planned:
- Fortifications (gates, walls), chokepoint control on per-edge roads.
- Hex vs square grid — open decision per §13.1 of the design doc; defer until the cost of square's diagonal artifacts shows up.
- Currency — explicitly deferred per §13.9 of the design doc; if added, must be a physical carriable good (no abstract balance).
- AI opponents.
- Ranked play / matchmaking.

---

## 9. When You're Stuck

In order of escalation:

1. **Re-read the relevant decision doc.** Most "how should I do X" questions are answered in `docs/`.
2. **Look at the existing precedent.** Roads → fog → M4 anchors are all the same pattern in three flavors. A new one of these almost certainly mirrors them.
3. **Read this doc's §2 patterns.** If the new feature doesn't match a pattern, ask why.
4. **Write a decision doc.** If the choice is novel, capture it before coding.
5. **Ask.** When the call is consequential and the spec is silent, ask the human before deciding.

---

## 10. Updating This Doc

This file is **a contract with future contributors.** Update it when:

- A new pattern joins §2 (a recurring technique is identified).
- A new "must not do" joins §4 (we learned the hard way).
- A new test pattern joins §6.
- A milestone lands or starts (update §8).
- A standards decision changes.

If a code review changes a pattern that's documented here without updating this doc, the doc is stale and the review missed something. The doc and the code are kept in sync by commit discipline.
