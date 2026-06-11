# Intent Authorization

## Decision

Every intent that targets a unit, structure, or group verifies the
issuing `Intent.PlayerId` against the target's `OwnerId` at `Resolve`
time. A mismatch is a hard reject for single-target intents and a
silent per-id skip for multi-id intents (same pattern as the other
per-id eligibility checks).

| Intent                  | Check                                                | Failure mode |
|-------------------------|------------------------------------------------------|--------------|
| `MoveIntent`            | `unit.OwnerId == PlayerId`                           | Reject       |
| `HaulIntent`            | `hauler.OwnerId == PlayerId`                         | Reject       |
| `AssignWorkersIntent`   | `extractor.OwnerId == PlayerId` + per-unit owner     | Reject / skip|
| `UnassignWorkersIntent` | `extractor.OwnerId == PlayerId`                      | Reject       |
| `AssignBuildersIntent`  | `site.OwnerId == PlayerId` + per-unit owner          | Reject / skip|
| `MoveGroupIntent`       | `group.OwnerId == PlayerId` *(already enforced)*     | Reject       |
| `FormGroupIntent`       | per-unit owner *(already enforced)*                  | Reject       |
| `DisbandGroupIntent`    | `group.OwnerId == PlayerId` *(already enforced)*     | Reject       |
| `EmbarkIntent`          | boat + passengers owned *(already enforced)*         | Reject       |
| `DisembarkIntent`       | `boat.OwnerId == PlayerId` *(already enforced)*      | Reject       |
| `TrainUnitIntent`       | unit + school owned *(already enforced)*             | Reject       |
| `BeginBreedingIntent`   | house + both parents owned *(already enforced)*      | Reject       |

## Why

Without this check, an authenticated player could submit a `MoveIntent`
naming any other player's unit and the sim would obediently retask it.
The same hole let one player silently sabotage another by submitting
`UnassignWorkersIntent` on an enemy lumber camp or `AssignBuildersIntent`
that diverted enemy builders onto a foreign site. The `Group`-shaped
intents already enforced ownership; the single-unit and structure-
targeting intents had not yet been brought up to that bar.

This was called out as a deferred work item in the original
`Intents.Intent` class doc-comment:

> "(eventually) used by validation when intents become player-scoped
>  (e.g. AssignWorkers must target a structure owned by the issuing
>  player)"

— and stayed that way until the cross-player intent surface (combat
M7, diplomacy M8) made the gap exploitable in practice.

### Why hard reject for single-target, silent skip for multi-id

Single-target intents (`MoveIntent`, `HaulIntent`) carry one subject.
A non-owned subject is unambiguous — the request was either
maliciously crafted or the result of a stale client state, and the
right answer is "no, with a reason on the resolved log."

Multi-id intents (`AssignWorkers/Builders`) already use per-id skip
for any reason (unit doesn't exist, isn't on the tile, isn't Idle,
is grouped, etc. — see `docs/intent-validation.md`). Non-ownership
joins that list. The intent itself still applies for the valid ids;
only the non-owned ids are silently filtered. This keeps a single
batched assignment from being entirely rejected when one id in the
list is a typo or a captured-since-submission unit.

The **structure-side** check on multi-id intents is hard reject —
because if the structure isn't yours, there is no scenario where the
intent should partially apply. An attacker shouldn't be able to even
*attempt* assignments at an enemy extractor.

### Why this composes with the existing "fail cleanly" contract

`docs/intent-validation.md` requires every `Resolve` to be authoritative
and to leave the world byte-identical on rejection. The ownership check
is one more precondition in that list. No world mutation happens before
the check; the rejection reason is recorded on the resolved log; replay
still works because the same check applies under the same code on
restore. No new state, no snapshot bump.

## Out of scope (intentionally deferred)

- **`HaulIntent` source/destination ownership.** Today the hauler-side
  ownership check stops the obvious sabotage path (retasking another
  player's hauler). Whether a player should also be barred from hauling
  *to* or *from* another player's storage is a separate gameplay
  question (alliance hauling? trade-post deposits? raid-and-loot from
  unguarded enemy stockpiles?). The intent log + resolution-time
  re-validation already catches the "structure was captured between
  submission and resolution" case; broader access rules wait for the
  trade / alliance milestone.
- **Diplomacy intents** (`DeclareWarIntent`, `ProposeRelationshipIntent`,
  `RespondToProposalIntent`). These carry their own subject field
  (`DeclarerId`, `InitiatorId`, etc.) and don't currently check it
  against `PlayerId` — same shape of bug, different surface. Out of
  scope for this fix; tracked as a follow-up.
- **Diplomatic ally exceptions.** A future "allies can re-task each
  other's units" rule would extend the ownership check with an
  `IsOwnOrAllied` predicate. The existing `IsOwnOrAllied` helper used
  by `EmbarkIntent` is the precedent. Not needed today.

## Acceptance tests

`tests/Sim.Tests/IntentAuthorizationTests.cs`:

- `MoveIntent_OnOtherPlayersUnit_Rejected`
- `HaulIntent_OnOtherPlayersHauler_Rejected`
- `AssignWorkers_OnOtherPlayersExtractor_Rejected`
- `AssignWorkers_OtherPlayersUnitInIdList_SkippedSilently` — the
  per-id-skip contract.
- `UnassignWorkers_OnOtherPlayersExtractor_Rejected`
- `AssignBuilders_OnOtherPlayersSite_Rejected`

## Update 2026-06-11 — M16: server-internal intents + the raiding economy pinned

**New authorization class: SERVER-INTERNAL intents.**
`SpawnBanditPartyIntent` / `DespawnBanditPartyIntent` (and the bandit
`PlayerId = BanditConstants.OwnerId` itself) are rejected at the WIRE
(`GameHost.SubmitEnvelopeJson`) regardless of payload — only the
in-process `BanditDriver` may submit them, below the HTTP gate. They
remain ordinary durable intents past that gate (registered in
`IntentJson`, replayed by recovery). Defense in depth: both intents ALSO
reject `PlayerId != BanditConstants.OwnerId` at resolve time, so even a
bypassed wire guard can't let a player conjure or vanish bandits.

**The haul source-ownership deferral is now a pinned DESIGN DECISION,
not an open question.** M16's `LoadCargoIntent` deliberately copies
`HaulIntent`'s stance: unit ownership is checked, source ownership is
not. Loading from a hostile structure's buffer **is the raiding
economy** — bandits robbing your lumber camp, you looting theirs back,
players raiding each other's unguarded frontiers. Whether you can stand
on the tile alive is combat's problem, not authorization's. The eventual
trade/alliance milestone may add rules for *non-hostile* access
etiquette; hostile access stays open by design.

## References

- `docs/intent-validation.md` — the "Resolve re-validates everything,
  fail cleanly" contract that ownership joins.
- `src/Sim.Core/Intents/Intent.cs` — the `PlayerId` field this check
  consumes.
- `src/Sim.Core/Groups/MoveGroupIntent.cs` — the precedent pattern
  (`group.OwnerId != PlayerId` reject) that the single-unit intents
  now match.
