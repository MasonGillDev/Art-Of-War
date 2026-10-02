# Combat damage playback: metered on the client, never in the sim

## Decision

A battlefield turn's damage lands in the sim **once, on the beat**, exactly as
`docs/battlefield-grid.md` §5 says. Showing it land *during* the round is the
**client's job**: the prod client splits each hit of `lastTurn.hits` into a few
blows spread over the seconds after the moves have slid, draws each blow as it
lands (a strike flash, a number over the struck unit's head, the health bar
dropping a step), and shows a unit's HP as the wire's number **plus the damage
whose blow has not landed yet**. The last blow always leaves the number the sim
sent. The sim and the wire are unchanged.

Decided 2026-10-01 with the user, who asked to "see damage being applied during
the round rather than all the damage being applied at the beginning and end of a
round", and for the numbers: "a small number above the unit's head that fades
away, red for enemy damage and white for the damage you're dealing".

## Why

**What the player saw.** `TurnResolver.Resolve` collects every hit of a turn and
applies the sum per target at once. The view after the beat carries the hits and
every unit's new HP; the client slid the moves over 0.9 s, drew all the strike
lines together for 1.8 s, and the bars dropped in one step. Meanwhile the figures
loop their fight clips for the whole turn (a minute after day X, 60 s in the
sandbox at pace 1), so a fight read as a minute of swinging and one jolt.

**Everything needed was already on the wire.** `BattleTurnDto.hits` has
`(attacker, target, damage, kind)` for every hit, the units carry their post-turn
`hp`, so pre-turn HP is exactly `hp + Σ damage taken`. `Battlefield.LastTurn` is
already declared presentation-only and is not snapshotted. No new field, no new
event, no new state.

### Alternatives

- **Sub-rounds in the sim, cosmetic** (the turn's hits computed on the beat,
  applied in N installments by a new scheduled event, deaths only on the last).
  Outcomes identical, so the sim would be doing presentation work and paying for
  it: a new anchor on `Battlefield`, a snapshot `FormatVersion` bump,
  `RegenerateQueue` rebuilding the installment event, fencing, twin-run and
  mid-installment round-trip tests. `docs/architecture.md` §2.2–2.3 keeps derived
  and presentational state out of the hash; this is the same discipline.
- **Sub-rounds in the sim, real** (the dead stop hitting mid-turn, morale
  recomputed per installment). A rules change to the beat model: first strike
  goes to the higher damage, even duels stop being double kills, the
  simultaneous-damage guarantee of §5 is gone. And orders still only land on the
  beat, so players gain nothing to react with. Rejected unless the user wants
  that balance change on purpose.
- **Spread the blows across the whole turn** on the client. Then the bars would
  lie for up to a minute while positions, duels and deaths are already post-turn,
  on the very board the player plans the next turn from. The window is instead a
  few seconds and capped at half a turn.
- **Carry pre-turn HP on the wire** (`HpBefore`). Not needed: survivors' pre-turn
  HP is exact from the hits, and the dead have no bar.

### What was accepted

- The dead still **vanish at the beat** (the view no longer lists them). Their
  last blows still draw and their number still appears where they fell
  (`BattleBlows` keeps every board unit's last stand), but there is no ghost
  figure falling on the last blow. That is a later piece, with the missing die
  clip (`Art Of War(prod)/docs/battlefields.md`).
- A turn that arrives while the previous one's blows are still in the air
  **snaps** the display to the new numbers (at most a couple of seconds, only
  when turns are shorter than the window).
- Archers' arrows (`ShootCycle`) keep looping on their own; they are not tied to
  the blows. The arrow strike flash is.

## Future expansion

- **Ghost the dying** until the last blow lands, with a fall clip, in
  `UnitFigures`. Presentation only; nothing here needs to change.
- **Arrows that land with the blow**: `ArrowFlights.Launch` knows its flight
  time, so an archer's blows could be timed to its releases (or the release to
  the blow).
- **Siege rounds** (`CombatRoundEvent`) drain a structure's HP once a round and
  the view carries the structure's health row; the same metering could apply to
  structure bars from the delta between views.
- If a **real** mid-turn rule is ever wanted (installments that change outcomes),
  it is a change to `docs/battlefield-grid.md` §5 first, and this doc gets an
  addendum. The client's metering would then read the sim's installments instead
  of inventing its own.

## Where it lives (prod client)

- `Client/Battle/BattleBlows.cs` — the schedule: blows per board per turn, the
  displayed HP, where every board unit last stood. Indexed by `BattleBoards.Index`.
- `Client/Battle/DamageNumbers.cs` — the numbers over heads, a pooled label layer
  owned by `BattleHud`.
- `BattleOverlay.Blows` — per-blow strike flashes and the death pad while the turn
  plays; dials `DamageSeconds`, `BlowsPerHit`, `FlashSeconds` on the component.
- `CombatMarks.DrawBars` and `UnitCard.Show` read `BattleBlows.DisplayedHp`.
