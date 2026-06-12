# M17 Spec — AI Players (fair, fogged, full factions)

Real opponents before multiplayer, and — more importantly — the
**balance lab**: headless matches of AI factions + bandits at cranked
tps that answer in minutes what hand-playtesting answers in hours. An
AI player is the M16 BanditDriver grown up: same proven architecture
(reads in, ordinary intents out, ephemeral brain, replay-safe because
the durable intent log carries its decisions), but speaking as a real
faction with a castle, citizens, claims, and a food bill.

## Locked decisions (user, 2026-06-11)

- **Completely fair.** No information cheats, no cost discounts, ever.
  The AI consumes exactly the fog-filtered view a human client renders,
  pays the same prices, obeys the same intent validation. Difficulty
  knobs, if ever wanted, will be HANDICAPS (start loadout, think
  cadence) — never wallhacks.
- **Homesteader first.** Phase order: economy → defense → rivalry.
  The testing value ships with phase one.

## The fairness contract (the load-bearing design)

**The AI brain's only input is the projected player view** — the same
`ViewDto` the Unity client gets from `GET /view/{id}` (built in-process
by `ViewProjector`, not over HTTP). Enforced STRUCTURALLY: the brain's
think signature takes `(ViewDto view, AiMemory memory)` and returns
intents — it never receives `GameWorld` or `Simulation`. A brain that
can't reference the world can't cheat. The driver shell (which owns the
sim handle for `SubmitIntent` and view-building) stays dumb.

Corollary: **anything the AI needs that a human can see on screen but
isn't in the DTO is a VIEW GAP and gets fixed own-only for both** (the
audit in Phase 1 — expected gaps: own units' cargo and activity; both
are things the human client already knows or can infer and belong in
the own-only enrichment alongside Power/Buffs/Dest).

This buys three things beyond fairness: the AI dogfoods the entire
view layer (every projection bug becomes an AI misplay), balance-lab
results are trustworthy (the AI wins or starves under real rules), and
the brain is a new-player simulator — every task it can't express
through intents is a real gameplay/ergonomics gap, found for free.

## What we're adding

1. **AI faction genesis** — `--ai N` (default 1): N extra
   `FactionStartSpec`s with the SAME starting loadout as the player
   (fairness includes the opening). Castle placement via the existing
   grassland-search machinery at a minimum separation (start with
   Chebyshev ≥ 64 on the default map; perfect fair-start placement
   stays deferred to M11 Phase 2). The legacy neutral scout faction is
   RETIRED — AI players are the "other" now.
2. **`AiPlayerDriver`** (`Sim.Server/Ai/`) — one per AI faction, hooked
   into the same clock-loop seam as the bandit driver (same thread,
   same lock, one think per `AiConfig.ThinkPeriodTicks`, default 1
   game-hour). Shell builds the faction's ViewDto, passes it to the
   brain, submits whatever intents come back.
3. **The Homesteader brain** (`HomesteaderBrain`) — a fixed PRIORITY
   LADDER evaluated each think (highest unmet need wins; no planner,
   no search — boring and debuggable):
   - **Eat**: `FoodRunwayTicks` below threshold (default 2 game-days)
     → ensure a Farm exists/under way on viable grassland (claims
     omitted → server auto-select, like any player can), keep a food
     haul cycling Farm → Castle.
   - **Build**: no LumberCamp → place one on visible forest; assign
     builders; haul build materials from castle.
   - **Work**: own extractors understaffed → assign matching-role
     citizens; full buffers → haul home.
   - **Grow**: food surplus above threshold → House + breeding;
     citizen-role deficits → train (School) when affordable.
   - **Idle fallback**: scout the frontier with one unit (reveals land
     for future farms; also how the AI ever finds anything — it's
     fogged).
   Each rung is OBSERVATION-DRIVEN: progress is read from the next
   view (a site exists, a buffer fell), not from remembered promises —
   so like the bandit driver, a restarted server just re-derives its
   goals. `AiMemory` holds only cheap hints (recently-ordered intents
   to avoid same-think duplicates, scout direction), all droppable.

### Arbitration is its own work item (not the behaviors)

The eight behaviors are individually small; **the real cost of a
utility-style brain is arbitration** — deciding which valid behavior
wins THIS think. That's where "the AI does something dumb that breaks
immersion" lives (it breeds while a bandit sacks its only farm), and
it's the utility-system equivalent of the pacing problem: budget it as
its own line of work, separate from authoring behaviors.

How this spec stages that cost honestly:

- **Phase 1 dodges weighted arbitration by construction** — a strict
  ladder has no weights. But the THRESHOLDS are arbitration in
  disguise: the food-runway trigger IS the "Eat preempts Build" rule,
  the surplus trigger IS "Grow yields to Eat." They live in `AiConfig`
  as named knobs, tuned like every other balance constant.
- **The deferral expires at Defender (phase 2)**, when behaviors first
  compete for the same units (recall the farmer to flee vs. keep
  farming through the raid). Plan that phase as: small behavior + REAL
  arbitration tuning pass, sized accordingly.
- **The decision trace is built in phase 1, not when it hurts.** Every
  think, the brain records (tick, rung fired, the inputs that fired
  it, intents emitted) — a ring buffer the balance-lab report dumps
  and a `--ai-trace` flag prints. Because the sim is deterministic and
  the intent log replays, a dumb decision is REPRODUCIBLE: re-run the
  seed, read the trace at the bad tick, see exactly which threshold
  misfired. This is the payoff of the whole architecture — arbitration
  tuning in a deterministic, replayable engine is debugging, not
  divination — but only if the trace exists from day one.
4. **The balance lab** — a headless harness (xunit + a `Sim.Host` mode)
   that runs K AI factions + bandits, no humans, for D game-days at
   full speed and reports: population curve, famine deaths, structures
   built, food/wood throughput, bandit losses. Pinned as tests (below)
   so balance regressions fail CI, not your evening.

## Explicitly deferred

- **Defender** (react to bandit raids: recall workers, train soldiers,
  recover dropped loot) — phase 2, after the Homesteader proves out.
- **Rival** (scouting pressure, diplomacy via the normal intents, raids
  on player supply lines) — phase 3. Full conquest waits on win
  conditions (post-multiplayer, user-tracked).
- Boats, equipment crafting, towers, road strategy — the Homesteader
  ignores them; each is a later rung on the ladder.
- Wire auth (any client can already submit any PlayerId — the known
  M10 gap; AI ids are no more exposed than player ids).
- Difficulty handicap knobs.

## Headline tests

- **`Homesteader_Survives100GameDays_NoStarvationDeath`** — THE balance
  pin: a lone AI on a generated map bootstraps food inside the 200-food
  runway and holds equilibrium. If a config retune breaks the opening,
  this fails — the test IS the "is the opening winnable?" question.
- **`Homesteader_BuildsFarmAndLumberCamp_WithinNGameDays`** — the
  bootstrap race, config-derived bounds.
- **`AiVsBandits_EconomySurvivesRaids`** — Homesteader + aggressive
  bandit config; population may dip but never hits zero in D days
  (will likely FAIL until Defender lands — mark expected/skip with a
  tracking note rather than tuning bandits down to pass).
- **`Ai_ReplayFromIntentLog_HashesMatch`** — the M16 headline, re-run
  for the AI driver (chronological interleave discipline).
- **`Brain_TouchesOnlyTheView`** — fairness pin: reflection over the
  brain type asserting no method takes `GameWorld`/`Simulation`
  (structural check; the real enforcement is the signature).
- **`DecisionTrace_RecordsEveryThink`** — the arbitration debugger
  exists and captures (tick, rung, inputs, intents) from day one; a
  dumb AI moment must always be replayable to a readable trace line.
- All thresholds config-derived per the standing convention.

## References

- `docs/bandits.md` — the driver architecture this inherits (and its
  pitfalls: Idle-while-moving, replay interleaving, ephemeral memory).
- `docs/intent-authorization.md` — Update 2026-06-11 (server-internal
  class; AI players need none of it — they're ordinary factions).
- `docs/food-consumption.md` — the famine-debt model the Eat rung
  must beat; `docs/extraction-claims.md` — auto-select placement.
- `docs/architecture.md` §8 — "player automation" candidate: this
  milestone is its second proof; the standing-orders layer becomes a
  brain whose ladder the PLAYER writes.
