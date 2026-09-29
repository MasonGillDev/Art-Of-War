# Two-act pacing: a fast prelude, then day X

## The decision

**Every game starts with all players spawning into one server at the same moment, and the world
clock runs in two acts.** From genesis a countdown runs to a fixed **day X**, visible to everyone.
Until then the clock runs fast. On day X two things happen, for every player at once:

1. **The clock drops, drastically,** to a slow pace for the rest of the game.
2. **A large bandit force marches on every kingdom on the map.**

The days before X are the prelude. Players learn the game through dense milestones and events, and
everything they learn is preparation for a battle everyone knows is coming. That battle decides
what the second act looks like, for each player and for the map. When it is over the world is
slow, and this is the game the design base describes (`persistent-rts-design.md`: "Moves last
hours. Travel lasts hours.").

Decided by the user on 2026-09-24, after the scale and pacing audit
(`Art Of War(prod)/docs/scale-audit-2026-09-24.md`). The numbers below are proposals; the shape is
the decision.

## Why

### The problem: one pace can't serve both ends of the game

- **At 4 tps** (the default since August) the opening is full: the first act lands in about 40 real
  minutes. But every move resolves in seconds to minutes. A night's sleep is 80 game-days, so the
  whole strategic band (warnings, famine, war, land, demography) starts and ends while the player
  is away.
- **At 1 tps or slower** the long game works: a night is 20 game-days or fewer, marches take
  minutes, and figures walk at their own animation pace. But the opening has nothing to do: a House
  takes 30 real minutes and the first act about 3 hours.

`--tps` can't fix this on its own. It stretches every duration equally, and the two ends of the
game need different real-time lengths.

### What was considered, and why it lost

- **One fixed pace, 4 tps or 1 tps.** Each fails one end of the game, as above.
- **A smooth ramp** (fast at genesis, easing to slow over the first days). It works numerically;
  4 tps held to day 7 and eased to 1 tps by day 30 gives a 42-minute first act and about 436
  game-days in a real week. It lost because nothing happens at the transition. The slowdown is
  something that happens to you, not a moment the whole server prepares for, and it blurs when the
  slow-world rules start to apply.
- **A per-kingdom opening boost** (a new kingdom builds faster; the world clock never changes). It
  is the right tool for a world players join at different times. This game never has late joiners:
  all players spawn together, so a global clock change is fair to everyone. It is simpler, and it
  turns the transition into a shared event. Kept in reserve if drop-in joining ever arrives.
- **Shorter durations early, longer later, inside the sim.** It would change the ratios the balance
  labs pin. The clock drop changes none of them.

### Why a drop and a raid together

The drop alone is a speed change. The raid gives the prelude a purpose (every milestone is
preparation) and a climax, and the drop is what makes that climax playable. At the slow pace a
combat round is one real minute, so the battle becomes the "general" moment the vision describes,
fought at a speed where decisions matter. It is "time dilation as dramaturgy"
(`docs/automation-as-core-game.md`), scheduled.

### Why it is safe for the engine

- **Sim.Core still never reads tps** (`docs/time-and-scale.md`). Day X is a tick in the world's
  config. The host changes its pace when the sim reaches that tick, not on a wall-clock timer, so
  pauses, stalls and replays cannot move the drop. A replay of the intent log is identical whatever
  the pace.
- Everything scheduled in ticks keeps its game-time length. Only its real-time length changes at X.

## The shape (proposed; tune in play)

| | Prelude (genesis to day X) | Act II (after day X) |
|---|---|---|
| Pace | 4 tps | 1 tps |
| A game-day | 6 real minutes | 24 real minutes |
| A combat round | 15 s | 1 min |
| A night's sleep (8 h) | not expected; players are present | 20 game-days |
| Walking, 1 km of grass | 7.5 s (figures glide) | 30 s (figures walk at clip pace) |

**Day X: proposed game-day 30, about 3 real hours after genesis.** Day 20 would make it 2 hours,
with less time to arm. The resulting timeline:

| Game-day | Real time from genesis |
|---|---|
| 7 (first act done) | 42 min |
| 20 | 2 h |
| **30 = day X** | **3 h** |
| 80 | about 23 h |
| 160 | about 2.3 days |
| 300 | about 4.6 days |
| a real week | about 440 game-days in total |

### Sizing the raid

Outcomes from a model of `CombatRoundEvent` (both sides deal their start-of-round power; damage
lands lowest health first; archers stand behind the line), with every defender on one tile. Bandit:
25 health, 3 power. Soldier: 30 / 3; a sword adds 3 power, a shield 10 health; living in a fed House
adds 1 power; the king's aura adds 1.

| Defence | vs 8 | vs 12 | vs 16 | vs 20 |
|---|---|---|---|---|
| 6 bare soldiers | lose | lose | lose | lose |
| 6 housed soldiers, sword and shield | win, 2 lost | mutual wipe | lose | lose |
| 8 sword and shield + 4 archers with bows | win, 1 lost | win, 2 lost | win, 3 lost | win, 5 lost |
| 10 sword and shield + the king | win, 1 lost | win, 2 lost | win, 4 lost | win, 7 lost |
| 6 sword and shield + 10 housed citizens | win, 6 lost | win, 10 lost | win, 14 lost | lose |

**Proposed: 12 raiders per kingdom.** That is enough that a kingdom without soldiers loses, and a
prepared one (8-12 under arms) wins with few losses. Levying the townsfolk wins at a terrible
price, which is itself a good Act II story.

It is a real choice, because bodies are the constraint. The AI kingdoms averaged about 19 people at
day 30 in the harness, and a well-run human kingdom perhaps 25-30. Putting 8-12 under arms means
fewer farmers and haulers: arm or grow is the prelude's central decision.

## Rules this implies (recommended; to confirm)

1. **A truce until day X.** No war declaration resolves before X, so the prelude is about learning
   and preparing, not being rushed. Alliances may still form.
2. **The raid kills and steals, but cannot raze.** Bandits keep their siege exemption, so a player
   who was away or unprepared starts Act II poorer, never eliminated. That is the async fairness
   doctrine ("presence buys finesse, never survival").
3. **The same raid for every player,** sized so a prepared kingdom wins. Scaling it to each
   kingdom's strength would punish preparation.
4. **The outcome carries into Act II, for you and for the map.** A beaten raid drops its war chest
   and draws refugees (the M37 reprisal pattern). Where a raid wins, its survivors could dig in as
   a bandit camp near the kingdom they beat (M39 placement). The map's Act II bandit country is then
   drawn by how the battle went everywhere.
5. **Knobs that must outlast an absence take their Act II values from day X.** These are famine
   grace, larders, war telegraphs, omen warnings, proposal expiry and the automation retry budget.
   The prelude can keep short values because players are present. The sim knows X, so this stays
   deterministic.
6. **No pause or speed control for players.** The schedule is the server's; the client's pace
   buttons become admin-only.

## What gets built

1. **The pace schedule in the host.** Prelude tps, day X and Act II tps are server options.
   `ClockLoop` switches at tick X, splitting an iteration that crosses it. The schedule goes on the
   wire (genesis) and the current pace on every view. The client's `SimClock` follows it and a
   countdown shows day X in real time. Player pace controls are removed.
2. **Day X in the sim.** A genesis config (tick X, raid size), snapshotted. A world event is
   anchored at tick X that raises a raid for every living player, reusing the M37 omen-raid
   machinery (`Omens`, `OmenDueEvent`, the bandit driver's seat target). Plus the truce rule.
3. **Prelude content.** Milestones and omens re-timed to fill genesis to X
   (`docs/secrets-and-progression-proposal.md`, acts I-III), each pointing at day X.
4. **Act II knob values from day X** (rule 5).
5. **Presentation of the drop.** The moment itself; walk clips scaled to ground speed so the
   slowdown reads as the world settling, not lag; possibly tick X placed at dusk so the raid comes
   at nightfall.
6. **A day-X lab.** The overnight harness (scale audit, appendix A), with AI kingdoms that prepare
   for X, to calibrate the raid size.

## Acceptance tests

- The host's pace changes at exactly tick X. Replaying the intent log gives the same hash whatever
  the pace.
- At tick X every living player gets exactly one raid toward its seat, and none before.
- A war declaration before X rejects; after X it resolves.
- The client's countdown matches the schedule (real time computed from the prelude tps and X).

## Future expansion

- **More acts.** A second drop, or a season structure with its own schedule, reuses the same
  machinery: a list of (tick, tps) steps plus world events at those ticks.
- **Per-server presets.** A short "blitz" server with a faster Act II, or a slower "long reign"
  server, is only different schedule numbers.
- **Drop-in joining,** if it ever arrives, needs the per-kingdom opening boost instead. This
  decision assumes a common start.
- **Dramatic slowdowns later in the game** (a war, a siege) stay possible, but they are global:
  every player on the server feels them.

## References

- `docs/time-and-scale.md`: the pace dial (see its 2026-09-24 update).
- `Art Of War(prod)/docs/scale-audit-2026-09-24.md`: every timing at 4, 1 and 0.25 tps; what breaks
  overnight.
- `docs/automation-as-core-game.md`: the async doctrine; time dilation as dramaturgy.
- `docs/progression.md` and `docs/secrets-and-progression-proposal.md`: prelude beats.
- `docs/bandits.md` and `docs/bandit-camps.md`: the raid and camp machinery.

## Update 2026-09-24 — the bones are built

Step 1 of "What gets built", plus the truce (rule 1). Nothing else yet.

**Sim** (`src/Sim.Core/Landing/`):
- `LandingConfig(Tick)`: genesis-set and snapshotted (v40). 0 is a one-act world, the
  default for every hand-built test world and lab.
- `LandingRules.HasLanded` / `TruceHolds`: pure reads. The landing tick itself counts
  as landed.
- `DeclareWarIntent` rejects while the truce holds ("the peace holds until the
  landing"). The Rival's `WarRung` reads `ViewDto.LandingTick` and does not try.

**Host** (`src/Sim.Server/`):
- `PaceSchedule`: fast until the landing tick, slow after. `Advance` splits a span that
  crosses the landing, so the drop lands on that exact tick however long the clock loop
  slept.
- Server options: `--landing-day N` (default 30; 0 = no landing), `--prelude-tps`
  (default 4) and `--tps` (the pace after the landing, default 1). A bare
  `ServerOptions` (the labs) has no landing.
- `/v2/pace` is now a dev/admin tool. A rate holds an override until cleared, and a
  POST without one hands the pace back to the schedule. Players have no pace control.

**Wire:**
- `WorldDto.Pace` carries the schedule.
- The base `ViewDto.LandingTick` is there so the brains see the countdown too.
- `ViewV2Dto.TicksPerSecond` and `Paused` carry the pace each view was made at.

**Client:**
- `SimClock` takes its rate from every view.
- The HUD's pace buttons are gone. The strip shows the date and "The landing in N days ·
  about H h M min" (real time from the host's own schedule), then "The landing was N
  days ago".
- `PaceControl`, `Wire/Pace.cs` and `IServerLink.Pace` are deleted.

**Tests:**
- `LandingTests`: the config, the boundary tick, the truce, pure reads, snapshot and
  hash.
- `PaceScheduleTests`: the drop, the split, step-size independence, the inverse, the
  options, the factory, the host's pace, and the wire.
- Full suite green (1,344 + 55). The client type-check is clean; not run in Play yet.

**Next, in order:**
1. The day-X raid for every living player (M37 omen-raid machinery).
2. Prelude content timed to X, with the fiction (the landing from the sea).
3. Act II values for the absence-sensitive knobs.
4. The drop's presentation.
5. A day-X lab.

Known gap in the truce: it stops war declarations only. Loading cargo from another
player's structure (the raiding-by-design path in `docs/intent-authorization.md`) is
still possible before the landing. Decide whether the peace should forbid it too.

## Update 2026-09-24 — open questions after the first review

The user agreed with the review's two biggest points: **training will not stay instant**, and
**combat needs positioning** (a battlefield grid on contested tiles is being designed; it gets its
own decision doc). The rest are open:

1. **Arming needs lead time, or the countdown does nothing.** Training and crafting are instant, and
   a soldier can retrain at the School straight afterwards. The best play today is to grow flat-out
   and levy the hour before X. In a model of the combat rules, 2 soldiers with shields and 8 archers
   with bows (about 90 wood and 10 stone, no iron) beat 12 raiders and lose 2.
2. **The prelude is a three-hour appointment.** Everyone must be there at genesis and stay through
   X, so each game is a launch night: a product decision, not only a pacing one. The prelude needs
   the vision's "unkillable lifeline" as much as Act II does: at 4 tps, 20 minutes away is over 3
   game-days, and a kingdom nobody touches starves in about 1.4 real hours.
3. **What losing X means.** "Cannot raze" is not enough. A winning raid walks from stock to stock
   (`BanditDriver.FindTarget`: anything stealable in sight beats a fight) and fights whoever stands
   there, so an unprepared kingdom loses its workers, not only its goods. Define the loss as
   deliberately as the win (for example: goods and pride, not people).
4. **The snowball.** Winners get a chest and refugees; losers get poorer and perhaps a camp next
   door. Make the loss a goal, not a spiral: the camp that beat you holds your stolen goods.
5. **Decisions inside the slow battle.** The drop is justified by "a speed where decisions
   matter", but disengage, target priority and stances are unbuilt. The battlefield grid is the
   answer being designed.
6. **After the peak, a dip.** Act II needs a pull on its first evening (things born at X), and the
   week needs an ending for Act II to build toward.
7. **Which game Act II is.** At 1 tps a march to a neighbour is 12 minutes: a slow RTS checked
   hourly, not the design base's "moves last hours". Both are defensible; they suit different
   players.
8. **Smaller ideas:** the raid's muster as a secret scouts can find (its size and bearing); the
   Reprisal as the dress rehearsal, never landing on top of X; bandit camps born from raids that
   won X; settle the calendar (the HUD day, the sky day, the countdown) before building the
   countdown.

## Update 2026-09-25 — the landing is built

The user's brief: the strangers come "from the sea" in the fiction, but mechanically
they come **out of the fog, and it must be fog for everyone**, so they never appear
beside another kingdom. They path to the kingdom they attack and form up.

**Sim** (`src/Sim.Core/Landing/`):
- `LandingEvent` is scheduled at genesis for the landing tick. `GameWorld.LandingSeq`
  is its anchor and `RegenerateQueue` restores it.
- On the tick, every living kingdom with a castle gets one `LandingHost` (by target
  id): 3 bands (`Fronts`) of 4 bandits (`BandSize`), each from its own compass side.
- **Placement.** A band comes out of the fog 14–26 tiles from the target's castle,
  searched in its side's octant, then the octants either side.
  - The tile is dark to every player (`BanditRules.IsSeenByAnyPlayer`).
  - It is at least `BanditConstants.MinSpawnDistance` (10) from every kingdom's
    units and buildings. Caches, idols and rubble don't count as kingdoms.
  - It is open land with no building on it.
- **Darkness is never waived.** A side with no such tile is skipped for the next
  side, and a kingdom with none left gets fewer bands.
- Side order is a hash of kingdom and tick, not an Rng draw, so no later roll moves.
- Each band records where it landed, its **approach** tile (the castle's
  neighbour on its side) and its **staging** tile (`StagingDistance`, 2, out
  along that side, in sight of the castle).
- A dead bandit leaves its band through `Population.OnUnitRemoved`.
- Snapshot v40's landing block now also carries the host knobs, the anchor and
  the hosts.

**Driver** (`BanditDriver.ActLanding`, Sim.Server):
- Before the assault tick (`AssaultDelayTicks`, one game-day after the landing),
  each band marches to its staging tile and holds there.
- At the assault tick every band moves at once: to its approach tile, then onto
  the castle. So each band enters through its own side, which is its own edge row
  on the battlefield grid (`docs/battlefield-grid.md`).
- Defenders met on the way are fought where they stand, since combat starts on
  co-location.
- A bandit that reaches the castle or carries loot is released to the ordinary
  raider FSM: it steals, then flees.
- If the target's castle has fallen or its kingdom is out, the whole host is
  released.
- The release set is ephemeral. After a restart, bandits still short of the
  castle and carrying nothing re-form their bands, which is harmless.

**Notices:**
- Everyone gets "THE LANDING: war bands have come out of the fog...".
- Each target also gets its own warning: "3 war bands march on your castle from
  the north, east and west. They will gather in sight of your walls and strike
  together in 1 day."
- The same pass fixed a latent bug. The war-effective and castle-fallen
  broadcasts tested `Outcome is null`, which never holds, so they had never fired.

**Tests:** `LandingHostTests` covers:
- genesis scheduling and one-act worlds;
- placement: dark to all, at least 10 from every kingdom, in the ring, full bands;
- distinct sides and staging tiles;
- defeated kingdoms get no host;
- snapshot survival before the landing (the headline) and after it;
- dead bandits leaving their bands, and twin-run determinism;
- the driver: every band gathered before the assault, nobody on the castle
  before it, each band entering through its own approach tile.

**Known limits:**
1. **Walls stop the host dead.** Bandits cannot siege (M16), so a castle walled on
   every side is immune. Walls are the intended counter, but the grid spec and
   the siege question have to settle it.
2. **`--bandits 0` freezes the hosts.** They still land but never move.
3. **The fight at the castle is still the old stat pool** until the battlefield
   grid lands. The landing already delivers what the grid needs: bands arriving
   from separate edges at the same moment.
4. **Sizing is untuned.** The default host (12 per kingdom, 3 × 4) should be
   re-derived on the grid's numbers in a day-X lab.
