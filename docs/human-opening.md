# The human opening

## The decision

The human seat starts with a roster that teaches the game: a **King, 2
Builders, 2 Haulers, 2 Farmers, 1 Lumberjack and 11 untrained citizens** (19
bodies), standing beside an **empty House** instead of a School. AI seats keep
the old start: two of each role beside a School, with one Builder crowned.
Starting holdings are unchanged (70 wood, 50 stone, 200 food). Decided by the
user on 2026-10-01.

Code: `WorldFactory.HumanRoster`, `FactionStartSpec.HousePosition`
(`Genesis.Build`). Tests: `HumanOpeningTests`.

## Why

The old opening handed every faction two of each role plus a School. That
gave the player everything at once and told them nothing. The School also let
them change anyone's role on the fly, so the opening never asked a question.
The two-act pacing (`docs/two-act-pacing.md`) makes the prelude about *arming
before the landing*, so its first minutes should point the player at the
economy that arming needs.

The roster is that pointer. Who you start with says what to do first:

- **Lumberjack, Farmers, Haulers, Builders**: cut wood, put up a farm, move
  the goods.
- **The House**: breed. It starts with no residents and no food, so to start
  a birth, haulers must carry 20 food there.
- **Eleven untrained citizens**: hands with nothing to do. As the kingdom
  needs trained roles, the player finds out they need a **School**. It is a
  discovery, not a gift.

### Choices inside the decision

- **The King is a body of its own.** AI seats crown a Builder (the M31 note in
  `WorldFactory`), which their builder-floor rung repairs at the School. A
  human seat has no School to repair that loss, so its crown goes on a
  separate body.
- **Two Builders, not one.** Only Builders build, and only a School trains
  Builders (the circular lock, `FactionStartSpec.SchoolPosition`). With one
  Builder and no School, a single early death would lock the player out of
  construction forever. Two Builders don't close the lock, but they make it
  take two deaths. *Rejected:* letting the Castle train Builders (a new rule
  just for the opening), and accepting the one-death risk.
- **The House starts empty.** A House with residents feeds them from its own
  cache, and a dry house starves them even with a full castle (the M19 harsh
  doctrine, `docs/m19-per-house-food-spec.md`). So every founder keeps the
  castle as home until the player moves them in.
- **Human seat only.** The AI brain assumes a School from tick 0, and the
  balance labs are tuned on the fourteen-unit start. Same precedent as M38's
  scoutless human seat. Lab worlds (bare `ServerOptions`, `Progression`
  false) build every seat the old way, so their hashes don't move. This
  breaks M17's "identical start" rule for the human seat; the AI seats remain
  identical to each other.
- **Food left at 200, deliberately.** 19 mouths eat 76 a game-day, which is
  about 2.6 days of runway against about 3.6 before. Two farmers are meant to
  close the gap. The user's call: playtest first, scale if needed.

## Future expansion

- **Food scaling** is the first knob to turn if playtests starve. It's a
  one-line change in `CastleHoldings`, or make it per-seat if only the human
  start needs more.
- **AI parity**: teaching the AI "build a School first" would let every seat
  share this opening and restore identical starts. That needs a new early
  rung and re-baselined balance labs.
- **Progression hooks**: the genesis House is placed, not completed, so it
  does not bump the `Completed(House)` counter behind *A Good Home*. If the
  opening should count toward that milestone, bump it at genesis explicitly.
- **The circular lock** is still reachable for the human seat (two Builder
  deaths before the first School). If playtests hit it, the fallback is a
  Castle that can train Builders only.
