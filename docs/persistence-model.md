# Persistence Model

## Decision

The durable source of truth for the simulation is the **intent log** — the
append-only record of player commands. Periodic **snapshots** are the recovery
anchor: on restart, recovery loads the most recent snapshot and replays only
the intent tail written after it. Resolved events (the consequences of intents)
are kept in memory for debugging and tests but are **not** durably persisted.

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

Two tests, both required.

- **Crash-recovery hash test.** Kill the host mid-scenario. Restart from the
  latest snapshot. The post-recovery snapshot hash must match what the live
  sim's hash was at the same `sim_tick`.
- **Version-skew refusal test.** Flip the `code_version` stamp on a tail
  intent. Recovery must refuse to start.

"It restarts" is not the bar. The bar is "it restarts with provably identical
state."

## What gets built (persistence milestone)

- `IIntentStore` — append-only intent storage. SQLite, single writer, WAL
  mode. **Durability boundary:** the intent row (with `code_version`) is
  written and fsynced *before* the server acknowledges the player's command.
- `ISnapshotStore` — full canonical world serialization plus
  `(sim_tick, rng_state, next_seq, code_version)`. Written atomically
  (temp file + rename to start; pluggable to object storage later).
- `Recovery` — loads latest snapshot, loads intent tail, validates
  `code_version`, replays.
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

## Reference

This is the persistence realization of "Determinism + replay from day one"
(design doc §2.4) and "One global queue, processed independently of who's
online" (§2.3). The simulation itself remains the omniscient authority;
persistence is what lets that authority survive process restarts and deploys
without rewriting the world.
