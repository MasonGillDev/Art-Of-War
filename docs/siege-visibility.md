# Siege visibility: fortification health is exact and public on sight

## The decision

Any player who can see a fortification tile (Wall, Gate, Tower, Castle) receives its exact
`Health` and `MaxHealth` on the wire. Combined with the attacker's own unit power, which is
already own-side information, this means an attacking client can compute the exact round a
breach will happen. That is accepted.

Decided 2026-09-20 during P2 (fortification visibility, `docs/p2-status.md`).

## Why

Sieges were unreadable: the only signal was a combat dot on the fort tile. Siege damage is
deterministic (`src/Sim.Core/Fortifications/FortSiege.cs:78-96`, damage per round = summed
`EffectivePower` of hostile units on the tile and its four neighbours, no roll). Once any
health number reaches the attacker, the round count follows by division, so the real choice
was between three legibility levels:

- **Exact (chosen).** Both sides plan to the round. The siege becomes an open race: the
  defender must reinforce or sortie before round N, the attacker must hold the ring until
  then. Fits the game's "presentation amplifies truth, never invents it" rule and the
  deterministic-sim identity; nothing on screen is a guess.
- **Coarse bands for outsiders** (intact / damaged / critical, exact own-only). Keeps some
  fog-of-war feel and costs nothing extra since the renderer needs the bands anyway. Lost
  because it makes the attacker's most important number a deliberate blur in a game whose
  other combat numbers (unit power, health) are exact where visible.
- **Hidden from outsiders** (round number only). The status quo, rejected as the reason the
  package exists.

Trade-off accepted: a scouted wall is a countdown. Rework cost if reversed is small: the
projector gates one field, the client already draws bands.

## Scope limit

This is the first enemy-side enrichment on the v2 wire and it stops at fortification
kinds. Enemy houses, stores, extractor holdings and unit health stay own-only. Any further
enemy-side field needs its own decision.

## Future expansion

- Per-round variance, if ever added to siege damage, turns the exact countdown into an
  estimate without a wire change: the client would show a range.
- Remembered structures (Sim.Core-blocked, discovery §5) would let a re-fogged wall keep
  its last-seen health as stale memory, matching how remembered terrain works.
- A "besiegers / next-round power" field on `CombatDto` is derived at projection time and
  adds no information beyond this decision; it exists for convenience, not disclosure.
