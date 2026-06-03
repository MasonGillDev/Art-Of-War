# M6 — Multi-Faction & Diplomacy (COMPLETE — 2026-06-03)

## Where we are

**M6 done end-to-end.** Configurable N factions at genesis, all mutually
known from tick 0; three-state symmetric per-pair relationships
(`Neutral`/`Enemy`/`Ally`) with a single world-level `Delay` constant;
unilateral-telegraphed war declaration; bilateral propose/accept for
peace and alliance; diplomatic state (relationships + pending wars)
surfaced as public knowledge in `PlayerView`; full M4 persistence
integration (pending wars survive snapshot + crash recovery via
`RegenerateQueue.From`).

Combat (M7) consults `Diplomacy.AreHostile(a, b)` and lands thin on top
of this gate.

## The headline contracts — proven

```
1. AreHostile(a, b) ⇔ relationship(a, b) == Enemy
2. DeclareWarIntent at T ⇒ relationship Enemy at T + Delay
3. Bilateral peace can cancel the in-flight hostile transition
4. Snapshot.Hash(uninterruptedRun) == Snapshot.Hash(crashAndRecoverRun)
   for scenarios with a pending war
```

- `DiplomacyWarTests.DeclareWar_TakesEffectAfterExactDelay`
- `DiplomacyProposalTests.PeaceDuringPendingWar_OverridesTheTelegraph`
- `DiplomacyWarTests.DeclareWar_PendingWar_SnapshotRoundTrip`
- `RecoveryTests.DeclareWar_RecoveryFiresAtCorrectTick`
- `DiplomacyDeterminismTests.FullScenario_TwinRunMatches`

## What landed

**Phase A — Multi-faction genesis.** `GenesisSpec.FactionStarts:
List<FactionStartSpec>` replaces the flat single-faction fields. Each
spec carries `OwnerId + CastlePosition + CastleHoldings + UnitSpawns`.
Genesis iterates factions in OwnerId order; per-faction castle + unit
placement + sight reveal. All existing tests/host/server callers
migrated to the new shape; `MultiFactionGenesisTests` covers N=2/3/4 +
twin-run + snapshot round-trip + fog isolation + reject-duplicate-owners.

**Phase B — Relationship state model.**
`src/Sim.Core/Diplomacy/`:
- `RelationshipState` enum (Neutral/Enemy/Ally).
- `FactionPair` canonical key (Lo, Hi) with `Of(a, b)` canonicalization.
- `Relationship` with `State` + `PendingEffectiveTick/Seq` anchor.
- `DiplomacyConfig(Delay, ProposalExpiryTicks)` — world-level constants.
- `Diplomacy` aggregate on `GameWorld` (relationships dict + proposals
  dict + config). Internal mutation surface, public read surface.
- Snapshot `FormatVersion` bumped to 3; diplomacy block serialized after
  groups.

**Phase C — Escalation.**
- `DeclareWarIntent`: validates declarer/target, sets pending anchor on
  the relationship, schedules `WarBecomesEffectiveEvent` at `now + Delay`.
- `WarBecomesEffectiveEvent`: fences on stale anchor (peace overrode it),
  otherwise flips state to `Enemy` and clears the anchor.
- `RegenerateQueue.From` extended to reconstruct pending-war events from
  the relationship anchor on snapshot restore (same M4 pattern as
  Unit/Extractor/ConstructionSite/Group anchors).
- `IntentJson` registers `DeclareWarIntent`.

**Phase D — Bilateral handshake.**
- `Proposal` data class (id, proposer, target, desired state, expiryTick).
- `ProposeRelationshipIntent`: validates non-Enemy desired state, rejects
  no-op transitions unless overriding a pending war.
- `RespondToProposalIntent`: only the addressee may respond; lazy
  expiry on touch; accept clears the relationship's pending war if any.
- `IntentJson` registers both new intents.

**Phase E — Determinism, view, host, docs.**
- `PlayerView` extended with `Factions`, `Relationships`, `PendingWars`
  (all public knowledge — every player sees the same), and
  `IncomingProposals` (per-viewer scoped).
- `DiplomacyPlayerViewTests` pins the public-knowledge invariants.
- `DiplomacyDeterminismTests` runs the full
  declare→effective→propose→accept→neutral scenario as a twin-run.
- `docs/diplomacy-model.md` — decision doc capturing the
  unilateral/bilateral asymmetry, public-knowledge framing, ally
  inert-but-present design, rejected alternatives, future expansion.

## Test counts

- Sim.Tests: **245 passing** (+28 new M6 tests).
- Sim.Persistence.Tests: **26 passing** (+1 diplomacy-recovery test).
- Total: **271 / 271 green**.

## Carried debts unchanged from M5

- Mid-haul cargo on unit death → M7 (combat) carries the capture mechanic.
- Emergent ford (water earning road condition) → still on the deferred
  ledger.
- M5 Phase 2 (split / merge / dispatch) → separable, deferrable.
