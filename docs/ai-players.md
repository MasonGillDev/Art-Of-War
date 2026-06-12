# AI Players — fair, fogged, full factions

## The decision

AI players are **ordinary factions driven by a view-only brain**. Each
AI faction is a normal `FactionStartSpec` with the player's exact
starting loadout (`--ai N`, default 1; the old token "neutral scout"
faction is retired). Its driver (`Sim.Server/Ai/AiPlayerDriver`) follows
the M16 bandit-driver architecture — reads in, ordinary durable intents
out, ephemeral state — with one stricter rule: **the brain's only input
is the projected `ViewDto`**, the same fog-filtered payload a human
client renders. Fairness is structural (the brain's signature cannot
receive `GameWorld`/`Simulation`; pinned by
`AiPlayerTests.Brain_TouchesOnlyTheView`), not a policy.

The first brain is the **Homesteader** — a peaceful economy player:
bootstrap a farm inside the 200-food runway, build a lumber camp, keep
buffers hauled, breed when the larder allows, scout with Scout-role
units. Strict-priority ladder + background logistics; every think is
recorded in a `DecisionTrace` ring buffer (`--ai-trace 1` prints live).

## Why

- **Completely fair (user decision, 2026-06-11).** No information
  cheats, no discounts — ever. Difficulty, if wanted later, will be
  handicaps, never wallhacks. Fairness is what makes the balance lab
  trustworthy and the AI a genuine new-player simulator. The brain MAY
  read Sim.Core catalogs/enums — game rules a human knows from the UI —
  but no world state.
- **The balance lab is the point.** AI-vs-AI headless matches turn
  balance questions into test runs:
  `Homesteader_Survives100GameDays_NoStarvationDeath` IS the "is the
  opening winnable?" question, frozen as CI. Retune food constants and
  the answer is a 3-second test, not an evening of play.
- **Second proof of the automation architecture.** Replay-from-intent-log
  hash equality holds for a full 15-game-day economy run
  (`Ai_ReplayFromIntentLog_HashesMatch`) — the driver's decisions live
  in the durable log, the brain is disposable. The player standing-orders
  layer inherits a twice-proven shape.

## The arbitration ledger (the real cost, paid and documented)

The spec predicted arbitration — not behaviors — would be where the work
lives. It was right five times in one milestone. Each was found in
minutes via the decision trace + deterministic replay; each is the kind
of bug that's an afternoon of mystery in a non-deterministic engine:

1. **Priority greed** — Eat claimed the think for routine food hauls;
   Build/Grow starved. Fix: hauling is BACKGROUND (logistics layer runs
   every think), only real decisions arbitrate.
2. **Double-tasking** — two layers tasked the same unit in one think;
   move-on-busy silently cancelled the first order (food hauls yanked
   off carriers by scout moves). Fix: a per-think unit reservation
   shared by every selector.
3. **Job creep** — Scout conscripted any idle adult, marching the whole
   village on wander legs; the carrier pool died. Fix: scouting is for
   Scout-role units only.
4. **Capacity blindness** — hauls went out on capacity-5 villagers while
   the two 25-capacity Haulers farmed; the farm sat dormant-at-cap for
   days and food flatlined at the famine line. Fix: swarm buffers with
   enough carriers to cover them (capacity is catalog knowledge);
   Haulers excluded from non-hauling duties.
5. **Reservation order** — logistics ran before the strategic ladder and
   reserved every idle unit, so staffing never got a candidate (the camp
   sat unstaffed for 29 days). Fix: strategic decisions reserve first,
   hauls take whoever's left. Priority isn't just rung order — it's who
   reserves people first.

Plus one **systemic find** (game knowledge, not AI knowledge): a haul
that over-picks for a small need strands the leftover ON the carrier —
`HaulIntent` has no amount parameter, so a 25-capacity pickup against a
10-wood site need leaves 15 wood stuck on a unit that every selector
then ignores. The brain works around it (laden idle units walk home and
`UnloadCargo`; one delivery in flight per site) — but a human player
hits the same trap, which makes "partial-amount hauls or auto-unload"
a UX candidate for the core game.

## Future expansion

- **Defender** (phase 2) — react to raids: recall workers, train
  soldiers, recover dropped loot. Re-enables the skipped
  `AiVsBandits_EconomySurvivesRaids` test. This is where weighted
  arbitration (behaviors competing for the same units under threat)
  actually starts — budgeted as real tuning work.
- **Rival** (phase 3) — scouting pressure, diplomacy through the normal
  intents, supply-line raids. Conquest waits on win conditions.
- **Training** — the Homesteader lives with its genesis roster; a School
  + TrainUnitIntent rung (more Haulers!) is the highest-value economy
  upgrade and the natural fix for arbitration find #4.
- **Multi-house breeding, second farms, claim rotation** as the
  demographic curve demands.

## References

- `docs/m17-ai-players-spec.md` — the spec (fairness contract,
  arbitration section, ladder design).
- `docs/bandits.md` — the driver architecture inherited (and its
  pitfalls: Idle-while-moving, replay interleaving, ephemeral memory).
- `docs/food-consumption.md` — the famine-debt opening the Eat rung
  beats; `docs/extraction-claims.md` — auto-select placement the brain
  leans on.
