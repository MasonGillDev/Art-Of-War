# M4 — Persistence & Recovery (status: paused after Phase B)

## Where we are

**Phases A+B complete; C–F remain.** The architectural contract that the rest of M4 hangs from is proven in tests. Picking this back up is mechanical from here — Phases C–F are durable backing + host wiring, not new architecture.

## The headline contract — proven

```
Snapshot.Hash(uninterruptedRun) == Snapshot.Hash(midFlightSnapshotAndRestore)
```

`tests/Sim.Tests/MidFlightSnapshotTests.cs::MidFlightSnapshot_RestoreRun_MatchesUninterrupted` exercises this with a scenario containing a long walk, an in-flight haul (post-pickup, walking back), and resumes both legs after restore.

The in-flight snapshot gap that's been on the open-debts ledger since the haul milestone is now closed in code.

## What's done (Phases A + B)

### Phase A — Self-describing in-flight processes

Every in-flight process now carries its **next-event anchor** on the entity state, so a queue can be reconstructed from state alone.

- **`Unit.PathRemaining`, `PathFinalDest`, `NextArrivalTick`, `NextArrivalSeq`** — movement is now state-anchored. `MoveIntent.Resolve` computes the full path once and stores it; `MoveArrivalEvent.Apply` pops the head per hop. No path recomputation on restore (recomputing against current road conditions could yield a different path → divergence).
- **`Unit.HaulPlan`** — orchestration anchor for haul flows. `HaulPhase.ToSource` / `HaulPhase.ToDest` tells `MoveArrivalEvent`'s final-arrival dispatch whether to fire `HaulPickupEvent` or `HaulDepositEvent`. **`MoveArrivalEvent.OnFinalArrival` is dropped** — we derive the follow-up from state instead of carrying an event pointer.
- **`Extractor.NextProductionTickSeq`** — alongside the existing `TickArmed` + `LastProductionTick`.
- **`ConstructionSite.BuildCompleteSeq`** — alongside the existing `ScheduledCompletion`.
- **`Simulation.Schedule` now returns `long`** — the `Seq` assigned. Live callers (extractors, sites, movement, haul events) stash this on the anchor.
- **`Simulation.ScheduleWithSeq(at, seq, e)`** — regen-only entry point. Does NOT bump `_nextSeq`. Called only by `RegenerateQueue.From`.
- **`Persistence/RegenerateQueue.cs`** — iterates entities in canonical order, schedules events with their stored Seqs.

### Phase B — Snapshot completeness + version header

- **4-byte format version** added after the existing magic. `FormatVersion = 1`. `Restore` refuses mismatched versions with a clear error pointing the operator at snapshot-on-deploy.
- **All new anchor fields serialized.** `WritePathRemaining`, `WriteNullableTileCoord`, `WriteHaulPlan` helpers in `Snapshot.cs`.
- **`Snapshot.Restore` calls `RegenerateQueue.From`** as its last step before returning. Restored sims arrive with their queues reconstructed.

### Test coverage

- `tests/Sim.Tests/RegenerateQueueTests.cs::LiveQueueEqualsRegenerated_AtMidFlightTick` — the A5 crux: live queue and restored-and-regenerated queue are identical (same events, same `At`, same `Seq`, same payload).
- `tests/Sim.Tests/MidFlightSnapshotTests.cs` — the headline gap-closing test, the "completed scenario still round-trips" regression test, and the version-mismatch refusal test.

**174 tests passing** (162 M3 baseline + 12 net new for M4 A+B).

## What's next (Phases C–F)

The architecture is in place. C–F adds the durable backing layer.

### Phase C — Durable intent log (SQLite)

- New project `src/Sim.Persistence/` (keeps SQL out of `Sim.Core`).
- `Microsoft.Data.Sqlite` reference.
- `IIntentStore` / `ISnapshotStore` interfaces; `SqliteIntentStore`, `SqliteSnapshotStore`.
- `intents(tick, seq, player_id, type, payload TEXT, PRIMARY KEY (tick, seq))` in WAL mode. JSON payload for auditability.
- `IntentJson.cs` — `System.Text.Json` with a small type-name → type registry.
- **Log-then-apply** discipline: durably commit the intent BEFORE applying it; ack the player only after commit. (Crash after log → recovery replays; crash before log → player wasn't ack'd, never sent.)
- Tests: append+load round-trip; ordered by `(tick, seq)`; partial-write recovery via SQLite WAL.

### Phase D — Snapshot store + Recovery

- `snapshots(tick, version, blob, created_at)`. Retain last 3.
- `Recovery.Recover(intents, snapshots, seed) → Simulation`:
  1. Load latest snapshot blob → `Snapshot.Restore` (already calls `RegenerateQueue.From`).
  2. Load intents with `tick > snapshot.tick`.
  3. Re-submit each via `Simulation.SubmitIntent`.
  4. `sim.Run(until: targetTick)`.
- **Headline tests**:
  - `RecoveryTests.CrashRecoveryMatchesUninterrupted` — periodic snapshots + simulated crash + recover = same hash as uninterrupted.
  - `RecoveryTests.PreSnapshotIntentsCanBeDeleted_StillRecovers` — the insulation proof. Deleting pre-snapshot intents from the log doesn't break live recovery, proving recovery anchors on the snapshot.

### Phase E — Crash-safety + deploy + host wiring

- Configurable snapshot cadence (default: every 5000 ticks OR every 100 intents).
- Torn-write safety test via SQLite WAL inspection.
- `Sim.Host` gains a `--data-dir <path>` flag:
  - On start: `Recover(...)` if the dir exists; else `Genesis.Build(...)` + seed.
  - Wraps `SubmitIntent` with `SubmitIntentDurable`.
  - Periodic snapshot; SIGTERM handler.
- `docs/persistence-model.md` updated: reframe recovery as "snapshot + RegenerateQueue + intent-tail" (the earlier "snapshot + intent-tail" framing was incomplete); document snapshot-on-deploy; document version-mismatch policy.

### Phase F — Determinism audit + persistence demo

- `docs/determinism-audit.md` M4 addendum:
  - Durable artifacts = intents (JSON, schema-stable) + pure-state snapshots (with version header). **No event types appear in any durable format.**
  - `Simulation.ScheduleWithSeq` has exactly one production caller (`RegenerateQueue.From`).
  - Recovery anchors on the latest snapshot; live recovery has no genesis dependence (D4 enforces).
- The demo:
  ```
  dotnet run --project src/Sim.Host -- --data-dir /tmp/aow-demo
  # ... mid-run ...
  kill <pid>
  dotnet run --project src/Sim.Host -- --data-dir /tmp/aow-demo
  # "Recovered at tick T; resumed with N in-flight events"
  ```

## How to pick this up later

The infrastructure changes are small and bounded:

1. **Add a `Sim.Persistence` project** referencing `Microsoft.Data.Sqlite` + `Sim.Core`. Wire into the solution.
2. **`IntentJson` registry** needs one entry per intent type. Today: `MoveIntent`, `PlaceSiteIntent`, `AssignBuildersIntent`, `AssignWorkersIntent`, `UnassignWorkersIntent`, `HaulIntent`. Each gets a stable type-name string.
3. **`SqliteIntentStore` + `SqliteSnapshotStore`** — straightforward WAL-mode tables + transactions.
4. **`Recovery.Recover`** — the orchestrator. `Snapshot.Restore` already does the queue regeneration; Recover just sequences snapshot+tail+run.
5. **`Sim.Host` gains `--data-dir`** — branch on dir-exists, plumb through `SubmitIntentDurable`, register a SIGTERM snapshot.

The new tests (~12) follow the same patterns as `MidFlightSnapshotTests` — scenario, snapshot, restore through the persistence layer, hash equality.

## Extension points (for future work)

This is what M4's architecture buys forward:

### Adding a new in-flight process

Any new feature that schedules a future event needs an **anchor** on the entity holding that future event, with exactly two fields:

- `long? NextXxxTick` — when the event fires.
- `long? NextXxxSeq`  — the `Seq` it was scheduled with.

Then add a regeneration line to `RegenerateQueue.From`:
```csharp
case YourThing y:
    if (y.NextXxxTick is { } at && y.NextXxxSeq is { } seq)
        sim.ScheduleWithSeq(at, seq, new YourEvent(y.At));
    break;
```

That's the entire contract. The pattern repeats for combat (next attack tick), trade (next trade-post check), siege (next siege-tick), anything that's in-flight.

### Adding a new intent type

`IntentJson` registry gets a new entry. The intent must be JSON-round-trippable via `System.Text.Json`. PlayerId on the base `Intent` covers ownership; specific intents add their own payload.

### Adding a new structure with periodic events

Add the two anchor fields; ensure the scheduling site (where `sim.Schedule(...)` is called) stashes the returned `Seq` onto the anchor; clear the anchor when the event runs to its terminal state. Pattern matches `Extractor.NextProductionTickSeq` and `ConstructionSite.BuildCompleteSeq` exactly.

## Files of note

**Written / modified in A+B** (uncommitted at pause):

- `src/Sim.Core/World/Unit.cs` — anchor fields, `HaulPlan` ref
- `src/Sim.Core/World/HaulPlan.cs` — new
- `src/Sim.Core/World/Structure.cs` — `NextProductionTickSeq`, `BuildCompleteSeq`, capture them on schedule
- `src/Sim.Core/Engine/Simulation.cs` — `Schedule` returns `long`; `ScheduleWithSeq` internal; `QueuedEventsSnapshot` test inspector
- `src/Sim.Core/Engine/EventQueue.cs` — `SnapshotInOrder` test inspector
- `src/Sim.Core/Movement/MoveIntent.cs` — `BeginMove`, `ScheduleNextHop`; full path stored on Unit
- `src/Sim.Core/Movement/MoveArrivalEvent.cs` — `OnFinalArrival` dropped; final-arrival dispatch via `HaulPlan`
- `src/Sim.Core/Logistics/HaulIntent.cs`, `HaulPickupEvent.cs`, `HaulDepositEvent.cs` — set/switch/clear `HaulPlan`; call `BeginMove`
- `src/Sim.Core/Logistics/ProductionTickEvent.cs` — capture Seq on reschedule; clear on dormant
- `src/Sim.Core/Persistence/Snapshot.cs` — version header; new anchor field serialization; `Restore` calls `RegenerateQueue.From`
- `src/Sim.Core/Persistence/RegenerateQueue.cs` — new
- `tests/Sim.Tests/RegenerateQueueTests.cs` — new (A5 crux)
- `tests/Sim.Tests/MidFlightSnapshotTests.cs` — new (B4 headline gap-closing)
