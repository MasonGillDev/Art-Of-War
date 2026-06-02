# Intent Validation

## Decision

Every intent re-validates **all** of its preconditions at **resolution time**
against the current world state. Submission-time validation is courtesy for the
UI; resolution-time validation is correctness.

If preconditions fail at resolution, the intent **fails cleanly** — it mutates
nothing, the world remains valid, and a rejection reason is recorded on the
resolved-event log for eventual surfacing to the player.

## Why submission-time validation can't be authoritative

In an async, event-driven sim, an intent submitted at sim-tick T may resolve at
T+0 (immediate) or T+N (a future-scheduled component of a composite intent).
Between submission and resolution, the world changes:

- A builder assigned to a build may be killed by an ambush no client saw coming.
- Materials counted as "present" at submission may have been hauled away or
  raided.
- A target tile may have a different structure on it by the time the intent
  fires.
- A unit's position at submission is stale the moment the response leaves the
  server.

The client-side check that powers the UI ("are these units available? are
resources there?") is reasoning about a world that **no longer exists** by the
time the intent matters. If we let that check substitute for authoritative
validation, the sim enters states the rules forbid, and replay/snapshot
guarantees collapse.

## The contract every intent obeys

Every `Intent.Resolve(sim)` and every `ScheduledEvent.Apply(sim)` derived from
an intent must:

1. **Re-verify every precondition** against the current world state — not
   against any value remembered from submission. The unit may have moved, died,
   or been reassigned. The tile may be different. Resources may be gone.
2. **Mutate nothing if any precondition fails.** Either fully apply or fully
   abort; never partial.
3. **Fail cleanly.** Failure is a valid outcome. The world is unchanged, no
   exception is thrown, and a rejection reason is recorded.
4. **Treat intent payloads as requests, not facts.** `BuildIntent(tile=T,
   kind=Farm, units=[U1,U2])` means "if it's still legal, build a Farm at T
   using these unit IDs." The sim looks up U1's and U2's *current* state at
   resolution; it never trusts positions or activities recorded in the payload.

## Examples

- **`MoveIntent(unit, dest)`** — verify the unit exists; verify dest is
  in-bounds; pathfind from the unit's *current* position. If the unit was
  killed since submission, abort cleanly.
- **`AssignWorkerIntent(unit, structureTile)`** — at the on-arrival sub-event:
  verify the unit still exists, is on the structure's tile, is currently `Idle`,
  and the structure still exists and isn't at worker cap. Any "no" → no
  assignment, no error.
- **`BuildIntent(tile, kind, requiredBuilderIds)`** — verify biome compatibility,
  no existing structure on the tile, kind is valid. Decompose into the moves
  and hauls needed; the final "actually start building" step re-verifies
  *right then* that the required count of `IsBuilder` units are present, the
  required materials are present, and no other build is in progress. If any
  check fails, the construction state on the tile is not created.

## "Fail cleanly" semantics

An intent that cannot apply must:

- Leave the world byte-identical to before resolution.
- Not throw — preconditions failing is a normal outcome, not a bug.
- Tag the resolved event with `IntentOutcome.Rejected(reason)` for the
  notification system to surface (§12 of the design doc; stubbed for now,
  delivered when clients exist).

The asymmetric consequence: a player whose intent fails should eventually know
*what* failed and *why* (e.g. "build aborted: only 1 of 2 required builders
present"), but the sim's job is to **fail forward** — the world is still valid,
the queue still drains, no operator intervention is needed.

## Why this is load-bearing for replay

The intent log is the durable source of truth (see
[persistence-model.md](persistence-model.md)). Replay re-runs intents through
current sim logic. If submission-time validation in some old client version
accepted intents the sim would reject today, those intents are in the log.
Recovery has to handle this gracefully — and the *only* way it can is if every
`Resolve` unconditionally re-validates. There is no "I trust the client
checked this" path. Ever.

This is also what makes the system robust against balance patches: an intent
submitted under old rules but resolved under new rules will fail cleanly if it
no longer satisfies the new preconditions (e.g. a structure now costs more
materials than were hauled to the site).

## Mechanism (M1)

- `IntentOutcome` value on every resolved event: `Applied` or
  `Rejected(reasonCode)`.
- A small `Validation` helper module exposing common checks
  (`UnitExists`, `UnitOn(tile)`, `UnitIdle`, `StructureExists`, `TileEmpty`,
  `MaterialsPresent`, …) so per-intent validation reads like a list of
  preconditions rather than a tangle of inline checks.
- Notification surface stubbed but not delivered (no clients to notify in M1).
  Rejection reasons are recorded; surfacing comes when clients are wired up.

## How this expands

- **Multiplayer.** Essential the moment two players' intents can interfere.
  Player A picking up the last unit of stock invalidates Player B's pending
  haul. The sim resolves them in submission order; B's haul re-validates and
  finds nothing to take. No special "conflict" code path is needed — it falls
  out of resolution-time validation.
- **Patches.** Runtime safety net that makes the intents-as-truth persistence
  model survive balance changes — see
  [persistence-model.md](persistence-model.md).
- **Notifications.** When clients are wired up, rejection reasons surface as
  messages. No new sim code needed — the rejection metadata already exists on
  the resolved-event log.

## Reference

This is the resolution side of the "Determinism + replay from day one"
principle (design doc §2.4). Together with the persistence model
(intents-as-truth + snapshot-as-recovery-anchor) and the
[extraction model](extraction-model.md) (the physical-presence rules being
enforced), it forms the contract that lets the world survive both async timing
and code evolution.
