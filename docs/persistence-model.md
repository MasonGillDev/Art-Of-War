# Persistence Model

## Decision

The durable source of truth for the simulation is the **intent log** — the
append-only record of player commands. Periodic **pure-state snapshots**
are the recovery anchor: on restart, recovery loads the most recent
snapshot, **regenerates the in-flight event queue** from per-entity anchors
inside the snapshot (`RegenerateQueue.From`), then **replays only the
intent tail** written after the snapshot. Resolved events (the consequences
of intents) are kept in memory for debugging and tests but are **not**
durably persisted.

> **Recovery = snapshot + RegenerateQueue + intent-tail.** The earlier
> framing of "snapshot + intent-tail alone" was incomplete — in-flight
> events were scheduled by intents issued BEFORE the snapshot, so the
> tail alone can't reconstruct them. The snapshot's per-entity anchors
> (`Unit.NextArrivalTick/Seq`, `Extractor.NextProductionTickSeq`,
> `ConstructionSite.BuildCompleteSeq`, plus the M5 `Group` anchors) carry
> exactly enough to re-schedule the pending events with their original
> Seqs. `RegenerateQueue.From` does the reconstruction.

## The in-flight correctness gap — **CLOSED** (M4, 2026-06-03)

The gap described below was the headline correctness item carried since the
haul milestone. It is now resolved in code:

- **M4 Phases A+B** added per-entity anchors and `RegenerateQueue.From`,
  so `Snapshot.Restore` reconstructs the full in-flight event queue from
  pure state. The mid-flight snapshot round-trip test
  (`MidFlightSnapshotTests.MidFlightSnapshot_RestoreRun_MatchesUninterrupted`)
  pins this.
- **M4 Phases C–F** added the durable SQLite-backed intent log + snapshot
  store + `Recovery.Recover` orchestrator + host `--data-dir` mode. The
  closure-gate test
  (`RecoveryTests.CrashRecoveryMatchesUninterrupted`) confirms that a
  scenario with concurrent in-flight processes (mid-walk, mid-haul,
  mid-production, mid-group-formation) recovers from a SIM-LEVEL crash
  via snapshot store + intent-tail replay and reaches the *identical*
  hash an uninterrupted run produces.
- **Insulation proven**: deleting every pre-snapshot intent from the log
  before recovery still works
  (`RecoveryTests.PreSnapshotIntentsCanBeDeleted_StillRecovers`) — live
  recovery anchors on the snapshot, not on genesis.

The historical framing of the gap is kept below for context.

## The in-flight correctness gap (historical framing)

A snapshot alone preserves *static* world state: tile biomes, structure
holdings, unit positions and activities, RNG state, the sim clock. What it
does **not** preserve is the event queue — the pending arrivals, construction
completions, production ticks, and haul pickup/deposit events that drive
every change in progress.

For a persistent async RTS that is the wrong subset of state to capture.
Almost every meaningful moment in the live game has work in flight: a
caravan halfway across a forest, a build site three ticks from completion,
an extractor between production ticks, a hauler walking back from a pickup.
**The default condition of the world is "things partway through happening."**
A save format that only restores frozen states preserves the world as it
almost never actually is.

The failure mode is silent and easy to miss. Tests pass because tests
assert on frozen states. The bug bites the first time the real server
crashes and restarts while *anything* is walking or building — which, in
practice, is every restart. Every in-flight action gets silently dropped:
the caravan never arrives, the build never completes, the haul never
deposits. The world looks intact but its motion is gone.

**Intent-tail replay is the fix.** The snapshot captures static state; the
intent tail captures the player commands that *generated* the in-flight
events. Recovery re-submits the tail to the restored sim, which
deterministically reschedules the same events at the same ticks. The queue
is reconstructed from inputs, not stored. This is precisely why the durable
boundary lives at intent submission, not event resolution — and why the
persistence milestone is intent-tail-replay rather than snapshot-only.

Until intent-tail replay lands, `Snapshot.Serialize` / `Snapshot.Restore`
in code is correctness-limited to *frozen* worlds. Every current snapshot
round-trip test exercises a state the live game is rarely in. The
reassurance from those tests is genuine but narrow; the breadth needs
intent-tail replay to land.

This is not a missing feature. It is a correctness gap in the durability
promise. The whole reason this doc commits to intent-log-as-truth (and
why the persistence milestone is non-trivial) is that snapshot-only
recovery would silently corrupt every persistent-RTS restart.

## Why this over a fully durable resolved-event log

Two forms of "log everything" were considered:

- **A (chosen):** persist intents; re-derive events on replay.
- **B:** persist resolved events as outcome records; replay applies recorded
  mutations and never re-runs sim logic.

B is genuinely patch-proof — a balance change to forest cost can never silently
rewrite a past battle, because the past is stored as facts, not derived under
current code. A has to manage that risk explicitly (see "guards", below).

A was chosen because the write-volume asymmetry is severe in this game:

- Intents are **rare** — a player issues a handful per session (move, build,
  list trade, declare war).
- Resolved events are **many** — one caravan walk is one intent and tens of
  arrival events, plus future road-traffic updates, vision recomputes, and
  combat rounds.

The durable write boundary lives on the **low-volume side** under A and on the
**high-volume side** under B. B's storage cost and fsync discipline would be
paid against the wrong side of that asymmetry.

The trade A accepts: recovery re-derives state by re-running intents through
*current* sim logic, so after a balance patch a naive replay from genesis would
silently rewrite history. Snapshots plus the two guards below close that gap.

## How recovery works

1. Load the most recent snapshot. A snapshot contains `sim_tick`, `rng_state`,
   `next_seq`, and the full canonical world state (grid, units, etc.).
2. Load the intent tail — all intents whose `sim_tick > snapshot.sim_tick`.
3. Resubmit the tail to a fresh `Simulation` initialized from the snapshot.
4. Run the sim forward to current wall-clock.

The window during which a sim-logic change can rewrite history is bounded by
snapshot frequency.

## The two guards that make A safe

1. **Every intent is stamped with `code_version`** when written. Recovery
   refuses to start if any tail intent's `code_version` differs from the
   running binary's. This converts the silent-rewrite class of bug into a
   loud, fail-loud condition.
2. **Snapshot-on-deploy is a release step, not a checklist item.** The deploy
   pipeline runs `quiesce → snapshot → verify → release` and refuses to ship
   if any step fails. In the normal case the version-skew window is zero — the
   new code only ever runs against intents stamped with its own version.

When guard 1 fires (e.g. a deploy crashed between quiesce and snapshot), the
operator's path is: produce a snapshot under the old binary, then deploy the
new one. The recovery path will not paper over the gap.

## Acceptance test for the persistence milestone

Three tests, all required. The first one is the gating one — it's the
direct test of the in-flight correctness gap.

- **In-flight crash-recovery hash test.** Kill the host mid-scenario *while
  in-flight work exists*: a unit walking a path, a build with active
  progress, an extractor between production ticks, a hauler mid-trip. Each
  of these cases on its own, plus a composite case with all of them at
  once. Restart from snapshot + intent tail. The post-recovery snapshot
  hash must match what the live sim's hash was at the same `sim_tick`, and
  the previously in-flight events must resolve at their original ticks.
  *This is the test that proves intent-tail replay closes the gap.*
- **Frozen crash-recovery hash test.** Same, but snapshot at a moment when
  the event queue is empty. Should pass even without the intent tail —
  catches regressions where snapshot canonicalization breaks.
- **Version-skew refusal test.** Flip the `code_version` stamp on a tail
  intent. Recovery must refuse to start.

"It restarts" is not the bar. The bar is "it restarts with provably
identical state, including everything that was in motion."

## What gets built (persistence milestone)

- `IIntentStore` — append-only intent storage. SQLite, single writer, WAL
  mode. **Durability boundary:** the intent row (with `code_version`) is
  written and fsynced *before* the server acknowledges the player's command.
- `ISnapshotStore` — full canonical world serialization plus
  `(sim_tick, rng_state, next_seq, code_version)`. Written atomically
  (temp file + rename to start; pluggable to object storage later).
- `Recovery` — loads latest snapshot, loads intent tail, validates
  `code_version`, and **re-submits the tail to deterministically
  reconstruct in-flight events**. This is the core mechanism that makes
  snapshot+restore correct for moving worlds, not just frozen ones (see
  "in-flight correctness gap" above) — it is the load-bearing line item
  of this milestone, not a thin wrapper around snapshot I/O.
- `CodeVersion` — constant baked into the binary at build time.
- Deploy pipeline `quiesce → snapshot → verify → release` step.

## What does not get built now

- Durable persistence of the resolved-event log. It stays in-memory (debug
  aid, test assertions).
- Snapshot replication / object-storage backing.
- Migration tooling for cross-version intent replay. The version-skew guard
  refuses cross-version replay rather than attempting it.

These are reversible additions later. The decision to *not* persist resolved
events is the only one that is structural — adding it later means a second
durable write path and a reconciliation story, but does not require rewriting
anything already shipped.

## How this expands

The architecture leaves room to grow in three directions without disturbing
what's built:

- **Snapshot tempo.** Initially a single periodic interval (configurable). At
  scale the world will likely be partitioned and snapshotted per region;
  per-region hashes (or a merkle root over regions) replace the single SHA-256.
  The recovery path keeps its shape.
- **Resolved-event persistence as an opt-in audit channel.** If we later want
  true patch-proof history (ranked-play disputes, post-mortems, research
  replays of completed games), a second write path can persist resolved events
  to cold storage. That is B-as-an-overlay, not B-as-the-truth.
- **Multi-process / sharded sim.** Each shard owns its own intent store and
  snapshot cadence; the global-queue invariant (design doc §2.3) becomes a
  per-shard invariant with explicit cross-shard event protocols. Intent-log-as-
  truth survives sharding because intents are addressed to entities owned by
  exactly one shard.

## Operator playbook

### Snapshot-on-deploy

The two-guard discipline (snapshot-on-deploy as a release step + version
header refusal) closes the patch-rewrites-history window:

```
1. quiesce:    stop accepting new intents
2. snapshot:   take a snapshot under the OLD code (drains in-flight events
               into anchors; produces a blob whose contents the old binary
               understands)
3. deploy:     replace the binary with the NEW build
4. restore:    new binary's Recover() reads the snapshot; its
               FormatVersion check passes if the new build is forward-
               compatible, refuses if not (see below)
5. resume:     intent intake unfrozen; world continues
```

Skip the snapshot step at your peril: if you deploy without quiescing,
in-flight intents written under the old binary's logic re-derive under
the new code's logic — silent history rewrite.

### Version-mismatch behavior

`Snapshot.FormatVersion` is bumped any time the on-disk layout changes
incompatibly. `Snapshot.Restore` (and therefore `Recovery.Recover`) reads
the version header after the magic; mismatched version throws
`InvalidDataException` with a message pointing at this section.

Today there is no automatic forward migration — the message tells the
operator to run snapshot-on-deploy under the producing code's version,
which materializes the world as static state that the new binary can
re-serialize fresh. Multi-version migration tooling is deferred until the
operational pain justifies the matrix.

## Reference

This is the persistence realization of "Determinism + replay from day one"
(design doc §2.4) and "One global queue, processed independently of who's
online" (§2.3). The simulation itself remains the omniscient authority;
persistence is what lets that authority survive process restarts and deploys
without rewriting the world.
