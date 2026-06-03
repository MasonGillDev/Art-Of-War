# Diplomacy Model

## Decision

The world supports a configurable fixed number of factions (N), known at
genesis time and fixed for the world's lifetime. Each pair of factions has
a **single shared relationship**, canonical pair-keyed `(min, max)`, with
three states:

- **Neutral** (default).
- **Enemy** — the combat gate. `AreHostile(a, b)` returns true *only* for
  `Enemy`.
- **Ally** — inert-but-present. Behaviorally identical to Neutral for M6;
  expansion point for shared vision, passage, and gated corridors in later
  milestones.

Transitions are **asymmetric by direction**:

- **→ Enemy (aggression): unilateral, delayed, telegraphed.** A
  `DeclareWarIntent` from the aggressor schedules a
  `WarBecomesEffectiveEvent` at `declaredTick + Delay`. The pending
  hostile transition is **visible to every player** during the Delay
  window via `PlayerView.PendingWars`.
- **All friendlier transitions** (→ Neutral, → Ally, Ally → Neutral)
  are **bilateral**: `ProposeRelationshipIntent` creates an offer;
  `RespondToProposalIntent(accept: true)` flips the relationship
  immediately. Decline removes the offer. Expiry is passive — a
  proposal is invalid once `now > expiryTick`; no event fires.

`Delay` is a **single world-level constant**, set at genesis (lives on
`DiplomacyConfig`), serialized in the snapshot, immutable for the world's
lifetime. Programmatically configurable at world-build time.

**Diplomatic state is public knowledge.** All players see every faction
(`PlayerView.Factions`), every current relationship
(`PlayerView.Relationships`), and every pending war
(`PlayerView.PendingWars`). Fog still hides **positions and holdings**.
The only per-viewer-scoped diplomatic state is `IncomingProposals` —
offers stay private to the proposer/target pair until acceptance turns
them into a public relationship change.

## Why

### Unilateral aggression vs bilateral peace — the principled asymmetry

Each direction gets the fairness mechanism that *works* for it:

- **Aggression must be unilateral.** Requiring the target's consent to
  attack them would make attacking impossible. Instead, fairness comes
  from the **telegraph**: the target has the full `Delay` window to see
  the war coming and react (sue for peace, reposition, ready defenses).
- **Peace and alliance must be bilateral.** They can't be imposed without
  consent — that would make refusing them meaningless. Fairness comes
  from **agreement**: both sides accept, or it doesn't happen.

The good strategic texture this produces: the aggressor holds initiative
on both ends. They choose when war starts (unilateral declaration), and
they can refuse a losing defender's peace offer (bilateral peace requires
the *aggressor's* consent too). A defender can sue for peace; a winning
attacker can decline and press. That's conquest working as intended.

### Why diplomatic state is public knowledge

Faction *existence* is public from tick 0 — there are no hidden factions.
The logical extension: diplomatic *posture* (who's at war with whom, what
alliances exist) is also public. It's a **fact of the world**, not a
perception subject to fog.

The alternative (per-faction views of "who I know is at war with whom")
would require either omniscience-from-presence (you see what your scouts
see, but how do you "scout" a relationship?) or arbitrary fog rules. Both
add complexity for no gameplay payoff — the interesting tension in this
game is *physical* fog (positions, holdings, in-flight forces), not
political fog. Pinning diplomatic state public keeps the model clean.

Proposals are the one exception: they're offers, not facts. They stay
private to the pair so the proposer doesn't expose their negotiation
posture to third parties. Once accepted, the resulting relationship
change IS public.

### Why ally is inert-but-present

Putting `Ally` in the enum from day one — even without mechanical effects
— means combat (M7) never has to be retrofitted when ally grows teeth.
`AreHostile(a, b)` already excludes ally; future mechanical benefits
(shared vision, free passage, gated corridors) layer on top of an enum
that's already there.

The alternative (a two-state Neutral/Enemy enum that grows a third value
later) would force every consumer of the relationship state to be revised
when ally lands. The cost of an extra enum value now is zero; the cost
of the revision later is meaningful. So: three states, two behaviors,
deliberate.

### Why `Delay` is a world constant

A single uniform `Delay` for every declaration is simpler than per-pair
or per-declaration delays, and the game design doesn't yet need the
variation. If specific factions later become "harder to declare war on"
(diplomatic insulation as a built ability), the data model can grow
to per-pair without breaking existing callers — the constant becomes a
default. We don't need that flexibility yet.

### Rejected alternatives

- **Per-side asymmetric relationship state.** I.e. faction 0 thinks it's
  at war with faction 1, faction 1 doesn't. Rejected: the "do these
  fight?" gate must answer the same in both directions, otherwise combat
  would need to consult both sides and resolve disagreements. The
  asymmetry the game *does* need (who declared on whom; who's the
  aggressor) is captured in the resolved-event log, not the per-pair
  state.
- **Symmetric war-declaration handshake** (war requires both sides to
  agree). Rejected because it makes attacking impossible.
- **Faction existence behind fog.** Rejected: it would force the
  diplomacy UI to deal with "you have a relationship with a faction you
  don't know exists," which has no design payoff and complicates the
  view.
- **Diplomatic state per-side fogged.** Rejected for the public-knowledge
  reasons above.

## Future expansion

- **Ally mechanical benefits.** Shared vision (overlap `View.VisibleTiles`
  across allies), free passage (skip enemy-blocked-tile checks for ally
  units), gated corridors (allies pass through your gates without
  combat). All layer on top of the existing `AreHostile` gate without
  retrofitting it.
- **Late-join.** The world is born with its final faction count today,
  but the model supports growing the set: register a new player in
  `world.Players`, add `FactionStartSpec` for spawn-time placement, and
  every new pair starts at Neutral by default. The diplomacy aggregate
  needs no schema change.
- **Per-pair delays.** If diplomatic insulation becomes a desired
  ability, the world-level `Delay` constant moves to a per-pair table
  keyed on the same `FactionPair`. Existing callers default to the
  world-level value; the snapshot grows a side table.
- **Push-notification infrastructure.** Pending wars and incoming
  proposals are surfaced via `PlayerView` today; richer notification
  (badge counts, audible alerts, "you've been declared on" emails) is a
  product-multiplayer concern, deferred.
- **Resolved-event log per-faction filtering.** Today the resolved-event
  log is sim-global; future work may scope it per-faction for the
  notification surface.

## Acceptance tests

- `DiplomacyRelationshipTests.Default_AllPairsNeutral` — bare-world
  default state.
- `DiplomacyRelationshipTests.RelationshipBetween_IsSymmetric` — pin the
  per-pair canonical key.
- `DiplomacyWarTests.DeclareWar_TakesEffectAfterExactDelay` — the
  unilateral-delayed-telegraphed flow.
- `DiplomacyWarTests.DeclareWar_PendingWar_SnapshotRoundTrip` — pending
  war survives snapshot+restore via the M4 regen pattern.
- `RecoveryTests.DeclareWar_RecoveryFiresAtCorrectTick` — pending war
  survives crash + recover from the durable stores; the war becomes
  effective at the same tick as an uninterrupted run.
- `DiplomacyProposalTests.ProposeAccept_TransitionsImmediately` — the
  bilateral-handshake flow.
- `DiplomacyProposalTests.PeaceDuringPendingWar_OverridesTheTelegraph`
  — consensual peace cancels an in-flight hostile transition.
- `DiplomacyPlayerViewTests.PendingWar_VisibleToEveryPlayer` — the
  public-knowledge invariant for pending wars.
- `DiplomacyPlayerViewTests.IncomingProposals_ScopedToTargetOnly` —
  proposals stay private until acceptance.
- `DiplomacyDeterminismTests.FullScenario_TwinRunMatches` — the
  declare-→-effective-→-propose-→-accept-→-neutral scenario is
  bit-for-bit deterministic across twin runs.

## Reference

Realizes the design doc's diplomacy concepts (§10) at the simulation
layer. Combat (M7) consults `Diplomacy.AreHostile(a, b)` and otherwise
lands thin on top of this gate. The persistence discipline (M4) carries
diplomatic state through snapshot+recovery: relationships are pure state;
pending hostile transitions are per-pair anchors that
`RegenerateQueue.From` reconstructs the `WarBecomesEffectiveEvent` from
on restore; proposals are pure state with passive expiry.
