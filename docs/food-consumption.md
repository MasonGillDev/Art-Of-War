# Food Consumption

## Decision

Population continuously consumes food. The food sink is **the player's
castle** — population × per-citizen-per-period drains
`Castle.Holdings[Food]` over time. When the castle runs dry, a **famine
anchor** (`Castle.FamineStartTick`) is set; if no food returns within
`StarvationStartDelay`, citizens begin dying in a deterministic order
(oldest first), one per `StarvationDeathInterval`, until food returns or
the population is gone.

The consumption math is **M9-style lazy catch-up**: rate × elapsed
periods integer-exact, anchored at every event that changes the rate
(birth, death, capture-on-death) or the food level (`HaulDeposit` of
Food to the castle). Between those rate-changing events, a self-
rescheduling **`FamineCheckEvent`** is scheduled at the moment food
would run out at the current rate, so the famine transition is never
missed — even if no other event happens to land near the dry point.

Hauling food into the castle is **`HaulIntent` exactly as today**.
There is no auto-haul, no shared stockpile pool, no rule engine in the
sim. Standing-orders-style automation is intentionally a **separate
future system that submits intents from outside**, leaving the sim
deterministic and atomic.

Scope: **food only**. Other resources are consumed at construction
events (already physical, already one-shot) and do not need the
continuous-consumption machinery.

## Why

### Why the castle, not stockpiles or houses

The user's two-option framing was castle-only vs. shared-pool-across-
stockpiles. **Shared pool violates the design's spine** (§7.1 "Resources
and goods do not teleport"): food deposited in a far stockpile would
feed mouths at the castle 100 tiles away with no hauler in between,
which kills the systems that depend on physical flow — raids on supply
lines have nothing to bite (contents redistribute by magic), roads stop
mattering for food, capture-on-ambush has nothing to apply to. The
"breaking the logistics mechanics" intuition was correct and the cost is
not subtle.

Castle-only keeps food flow physical. Stockpiles still earn their place
as **buffers along the haul chain** — a farm 80 tiles out hauls 10 tiles
to a regional stockpile; periodic consolidation hauls run regional →
trunk → castle. Short cheap legs near production, long expensive legs
along trunk routes that are exactly where roads pay off and raids matter.
The "different stockpiles have different security priority" property the
user flagged is the gameplay, not a side effect.

Per-House consumption (the "spatial refinement" floated during design)
was deferred. It is more in the spirit of the world but requires a
sharper architectural lift (House food state, House↔stockpile assignment,
per-region famine state) and the castle-only model is already playable
and recoverable. See [Future expansion](#future-expansion).

### Why citizens die over time (not breeding-stop, not productivity-drop)

Three candidate consequences for empty food:
- **Death** (chosen).
- Breeding stops but no deaths.
- Productivity collapses but no deaths.

Death is the only one that creates a real time-pressure failure mode.
Breeding-stop is invisible during a famine because the player wasn't
breeding right now anyway; productivity-drop is a soft nudge that
spreads pain without forcing action. Both are escape valves the
game shouldn't have — the whole design is about logistics consequences
and the harshest of those is "if your supply line breaks, your civ
shrinks."

Death is also the only consequence that **cleans up after itself**: a
shrinking population reduces the consumption rate, so an underfed civ
naturally finds a smaller equilibrium instead of grinding forever.

### Why lazy catch-up at rate-changing events (M9 pattern, applied to a global field)

M9 established the discipline: when a rate varies based on other
entities, catch up only at the events that change the rate, anchor
`lastUpdateTick = now` at every transition, and read derived current
values via pure-read elsewhere. Food consumption is the same shape with
one **simpler** dimension (it is a single global value per player, not a
spatial field), so the math is one variable not many. The rate-changing
events are:

1. **Population change**: birth (M8), death (M7 / aging M8 / starvation
   here), capture-on-death (M7) — old owner's pop drops, new owner's pop
   rises, both castles' rates change.
2. **Castle food level change**: `HaulDepositEvent` (food arrives at the
   castle) — not strictly a rate change, but a famine-transition trigger
   that needs catch-up to compute correctly.
3. **`FamineCheckEvent`**: the self-rescheduled "predicted next
   transition" — see below.

Between those events, the rate is constant by construction. A pure-read
`FoodConsumption.CurrentLevel(castle, world, now)` derives the live food
level for views without writing.

### Why a self-rescheduling `FamineCheckEvent`

The rate-changing events alone are not enough. If no birth, death, or
deposit happens during a long dry period, the famine transition would
never officially occur — the math says food ran out at tick T, but the
sim's `FamineStartTick` would still be `null` until the next rate-
changing event lands. Views would show "Food: 0" while citizens kept
working.

The fix: every catch-up that leaves Food > 0 computes `ticksUntilEmpty
= Food / (pop × perCitizenRate)` and schedules a `FamineCheckEvent` at
`now + ticksUntilEmpty × FoodConsumptionPeriod`. The event's only job is
to fire `FoodConsumption.CatchUp` — which will officially flip
`FamineStartTick` and schedule the first `StarvationDeathEvent`.

If a rate-changing event lands first, the `FamineCheckEvent` is
**fenced via `Castle.NextFamineCheckTick / NextFamineCheckSeq`
anchors** — fires, sees the mismatch, no-ops. Standard §2.6 fencing.

If population becomes 0, no famine check is scheduled (rate is 0; food
never depletes). The next birth event re-arms.

### Why staggered starvation deaths (oldest first), not a cliff

Naive rule: at `FamineStartTick + StarvationThreshold`, **every**
hungry citizen dies. Mathematically clean, fully deterministic, no
ordering questions. But it produces a population cliff: 100 → 0 in one
tick. Players who log in mid-cliff have nothing to react to.

Staggered deaths preserve the same total drain over time but give the
player a visible decline they can fight: first death at `FamineStartTick
+ StarvationStartDelay`, then one per `StarvationDeathInterval`, until
either food returns or the population is empty. **Death order:
ascending `BornTick` (oldest first), ties broken by ascending unit
`Id`.** Both fields are immutable, deterministic, and already in the
snapshot — no new RNG, no ordering ambiguity.

A self-rescheduling `StarvationDeathEvent` (single per-castle anchor:
`Castle.NextStarvationDeathTick / NextStarvationDeathSeq`) drives the
sequence. When it fires:
- Fence on the anchor + check `castle.FamineStartTick != null`. No-op
  on mismatch.
- Find the oldest hungry citizen of `castle.OwnerId`. Apply the same
  removal path other deaths use (`Population.OnUnitRemoved`, which
  fences pending births and triggers food catch-up — see Composition).
- Schedule the next death at `now + StarvationDeathInterval`. Update
  the anchor.

### Why hauling stays purely player-issued (no auto-haul, no rule engine)

The user's stated principle: "atomic actions are player-defined; with
those atoms we can build user-defined automation." The sim's job is
the atoms (`HaulIntent`, `MoveIntent`, etc.). Anything that watches
state and submits intents is an **automation layer above the sim** —
not the sim's concern.

This composes cleanly with intents-as-truth (§3 of architecture.md):
the intent log is the canonical record whether intents come from a
human clicking or a standing-order rule firing. Replay is unaffected.

The food system is **fully playable without any automation** —
manual hauls work the same as for stone, wood, or any other resource.
There is just more of them, and a famine teaches the player to plan
ahead. The automation layer (durable, deterministic, server-side
because async games must run while the player is offline) is its own
substantial milestone, deferred until the food mechanic is in
production.

### Why food only (no generalization)

Wood, stone, ore, etc. are consumed at **construction events** —
one-shot, already physical, already part of the engine. They have no
continuous-consumption ergonomic problem. Generalizing the food
machinery preemptively to a "continuous resource consumption framework"
risks paying upfront for hypothetical resources (fuel, wages, water)
that may never land or may land with their own shape. Solve food;
revisit if a second continuous-consumption resource appears.

### Why one castle per player as the canonical sink

The current engine has exactly one `Castle` per player (planted at
genesis), and `persistent-rts-design.md` §1 defines castle loss as the
loss condition for the game. M13 takes the simplification: **the food
sink is the player's single Castle**. If a player has zero castles
(after capture / loss), they've already lost — no rate to compute.

Captured castles change owner via the existing M7 path. After capture,
the conqueror has two castles. The chosen behavior for that edge case:
**each castle consumes for its own owner's full population** — i.e.,
the population eats from *both* castles simultaneously. This is a
known imperfection (the conquering player gets a "for free" second
larder) accepted to keep the system trivial, but see
[Out of scope](#out-of-scope-intentionally-deferred) — the proper fix
("home castle" designation; per-castle regional pops) is deferred to
the per-House refinement milestone.

## What gets built

### New

- `src/Sim.Core/Food/FoodConstants.cs` — `FoodConsumptionPeriod`
  (ticks per meal), `FoodPerCitizenPerPeriod` (typically 1),
  `StarvationStartDelay`, `StarvationDeathInterval`.
- `src/Sim.Core/Food/FoodConsumption.cs` — `CatchUp(Castle, Simulation,
  long now)` is the **single mutation point** for
  `Castle.Holdings[Food]` reduction, `LastFoodConsumedTick`,
  `FamineStartTick`, and the famine-anchor scheduling. Pure-read
  `CurrentLevel(Castle, Simulation, long now)` for views.
- `src/Sim.Core/Food/FamineCheckEvent.cs` — self-rescheduling predicted
  famine-start event. Fences on
  `Castle.NextFamineCheckTick / NextFamineCheckSeq`.
- `src/Sim.Core/Food/StarvationDeathEvent.cs` — self-rescheduling
  per-castle starvation event. Fences on
  `Castle.NextStarvationDeathTick / NextStarvationDeathSeq` and on
  `castle.FamineStartTick != null`.
- `tests/Sim.Tests/FoodConsumptionTests.cs` — see Acceptance tests.

### Modified

- `src/Sim.Core/World/Player.cs` — `PopulationCount` (int).
  Single-mutation discipline: incremented at every Unit-add site,
  decremented at every Unit-remove site, swapped at capture.
- `src/Sim.Core/World/Structure.cs` (`Castle`) — new persistent fields:
  - `long LastFoodConsumedTick` (anchor).
  - `long? FamineStartTick`.
  - `long? NextFamineCheckTick`, `long? NextFamineCheckSeq`.
  - `long? NextStarvationDeathTick`, `long? NextStarvationDeathSeq`.
- `src/Sim.Core/World/GameWorld.cs` (or wherever `AddUnit`/`RemoveUnit`
  live) — call `Player.IncrementPopulation` / `DecrementPopulation`,
  then call `FoodConsumption.CatchUp` on that player's castle. This is
  the **rate-change rule**: every population mutation triggers a
  catch-up.
- `src/Sim.Core/Population/Population.cs` (`OnUnitRemoved`) — already
  the single hook for death paths (combat M7, aging M8). Add the
  population-count decrement and food-catch-up calls here so combat,
  aging, **and** starvation all flow through one mutation site.
- `src/Sim.Core/Logistics/HaulDepositEvent.cs` — when depositing Food
  to a Castle, run `FoodConsumption.CatchUp` first (so the math sees
  any famine transition that happened during the haul), then apply the
  deposit, then if `FamineStartTick != null` and the new Food level is
  > 0: clear `FamineStartTick` and reschedule (or anchor-clear) the
  next `FamineCheckEvent` based on the new food level.
- `src/Sim.Core/Combat/CombatRules.cs` (capture path) — when a unit
  changes owner, decrement old owner's `PopulationCount`, catch-up old
  owner's castle, increment new owner's `PopulationCount`, catch-up
  new owner's castle. Two catch-ups in one event, both with the
  same `now`.
- `src/Sim.Core/Persistence/Snapshot.cs` — serialize `PopulationCount`
  and the new Castle fields. Format version bumps.
- `src/Sim.Core/Persistence/RegenerateQueue.cs` — regenerate
  `FamineCheckEvent` and `StarvationDeathEvent` from the castle's
  per-event anchors.

### Persistence

Snapshot format version bumps. New fields: `Player.PopulationCount`,
`Castle.LastFoodConsumedTick`, `Castle.FamineStartTick`,
`Castle.NextFamineCheckTick/Seq`, `Castle.NextStarvationDeathTick/Seq`.
No new event types reach durable storage.

## Composition with existing systems

- **M1 logistics** — `HaulIntent` to a Castle with `Resource.Food`
  already works; M13 adds a famine-end side-effect in
  `HaulDepositEvent.Apply` (catch-up; clear famine; reschedule
  predicted next famine check).
- **M7 combat** — capture-on-death triggers both an old-owner
  decrement (with catch-up) and a new-owner increment (with
  catch-up). Combat deaths flow through `Population.OnUnitRemoved`
  which already exists; M13 piggybacks the pop-count and catch-up
  on that single hook.
- **M8 population** — birth (`BirthEvent.Apply`) calls a single
  helper `Population.OnUnitAdded` that increments
  `Player.PopulationCount` and catches up the owner's castle. The
  symmetric counterpart of `OnUnitRemoved`. Aging-death flows through
  `OnUnitRemoved` as today.
- **M9 biome degradation** — independent. Food is grain in the
  larder; the dirt biome under the farm is a separate state. Both
  use the lazy-catch-up pattern but neither writes the other's
  state.
- **M12 boats** — embarked passengers still belong to their owner's
  population count (they age and starve as normal). Their physical
  off-tile status doesn't affect food consumption — the castle drains
  regardless of where its citizens are standing.
- **Roads** — food hauls benefit from roads exactly as today's hauls
  do. No new interaction.
- **Fog** — views of own castle food are pure-read (`CurrentLevel`).
  Enemy castle food level is **not** revealed even on visible tiles
  (the value lives in the structure's Holdings, which views already
  filter on owner). No change.

## Future expansion

- **Per-House consumption.** Citizens consume at their `House`
  (M8 already gave us Houses with a small food cache); Houses
  replenish from the nearest stockpile / castle via auto-haul or
  player intent. A region without a local granary starves regardless
  of central stock. The harder system; the more interesting gameplay.
  Designed as the upgrade path from M13's castle-only sink.
- **User-defined automation layer.** Durable, deterministic,
  server-side rule engine that submits intents on the player's
  behalf when conditions match. The right answer to the
  ergonomic cost of fully-manual hauling, and the architectural
  realization of the user's "atoms + automation built on top"
  principle. Its own large milestone.
- **Per-resource consumption framework.** Generalize `FoodConsumption`
  to any continuous-consumption resource. Triggered only if a second
  resource needs it (winter fuel; wages-as-physical-coin if currency
  is added). Not yet.
- **Multi-castle / home-castle designation.** When capture creates a
  multi-castle owner, designate one as `HomeCastle` (the consumption
  sink). Or partition the population by which castle they're
  associated with (per-citizen `HomeCastleId`). Deferred to whenever
  the per-House refinement is built.
- **Famine effects beyond death.** Productivity drop in the late
  stages of starvation; morale/diplomacy effects; defection to feeding
  enemies. Layer on after the death core lands and the gameplay is
  tested.
- **Variable consumption rates.** Soldiers eat more; children eat
  less; embarked passengers eat ration-pack rates. Per-role
  multiplier in the rate computation; no structural change.

## Out of scope (intentionally deferred)

- **Stockpile virtualization in any form.** No shared pool, no
  remote access, no teleportation. Stockpiles remain pure physical
  buffers.
- **Auto-haul of food.** Sim atoms only.
- **Per-House consumption.** Castle-only sink for the MVP.
- **Soldier / child / specialist diet variation.** Flat
  per-citizen rate.
- **Sieges that block food haul.** Sieges are not a thing yet
  (gates / walls / fortifications are long-term roadmap). A
  food-cut famine emerges naturally from any sustained raid on
  supply lines.
- **Morale / migration / unrest.** Starvation only deaths citizens
  for now.
- **Famine notifications via the M10 push system.** Eventually the
  famine-start transition is a natural push-notification trigger.
  Wire it when M10 lands.
- **Capture-multi-castle home designation.** Captured castles get
  the same consumption math; the double-larder edge case is accepted
  until per-House lands.

## Acceptance tests

- `FoodConsumptionTests.PerPeriodConsumption_HitsExpectedDrain` —
  N citizens × T periods → consumption equals
  `N × T × FoodPerCitizenPerPeriod`. Integer-exact.
- `FoodConsumptionTests.CurrentLevel_IsPureRead_NoMutation` —
  100×-no-mutation hash test on the pure-read level query.
- `FoodConsumptionTests.CatchUp_IsObservationIndependent` — catch up
  once at T vs. many irregular times along the way to T; same final
  Holdings and same `LastFoodConsumedTick`. (M9 pattern.)
- `FoodConsumptionTests.PopulationGrowth_AnchorsRateMidPeriod` —
  birth at tick K within a period drains old-rate × (K - lastTick)
  before new-rate kicks in.
- `FoodConsumptionTests.PopulationDeath_AnchorsRateMidPeriod` —
  symmetric to the above.
- `FoodConsumptionTests.Capture_TransfersCitizen_DrainsBothCastles` —
  capture-on-death triggers catch-up on both old and new owner's
  castles at the same `now`.
- `FoodConsumptionTests.Famine_StartsExactlyAtPredictedTick` —
  predict the dry-out tick from a known starting Holdings and
  rate; the famine anchor is set to exactly that tick.
- `FoodConsumptionTests.FamineCheckEvent_FencesOnRateChange` —
  schedule a famine check; trigger a birth before it fires; check
  fires, sees the bumped anchor, no-ops; a new famine check is
  scheduled.
- `FoodConsumptionTests.HaulDeposit_DuringFamine_ClearsFamineAndReschedulesCheck` —
  the famine-end side effect is correct.
- `FoodConsumptionTests.StarvationDeath_FiresAtDelay_KillsOldestFirst` —
  per-citizen order is ascending `BornTick`, ties by `Id`.
- `FoodConsumptionTests.StarvationDeath_FencesWhenFamineEnds` —
  schedule, end famine via deposit, event fires, no-ops, no further
  scheduling.
- `FoodConsumptionTests.StarvationDeath_PiggybacksPopulationOnUnitRemoved` —
  starvation deaths trigger the same M7/M8 cleanup path (any pending
  Birth is fenced, etc.).
- `FoodConsumptionTests.PopulationCount_HasOneMutationPoint` —
  reflection / grep test that all increments/decrements go through
  `Player.IncrementPopulation` / `DecrementPopulation`. (Same shape
  as M2's audit for `Road.CreditTraffic`.)
- `FoodConsumptionTests.SnapshotRoundTrip_PreservesAllFields` —
  including the anchors.
- `FoodConsumptionTests.Recovery_MidFamine_Identical` — M4 contract:
  snapshot mid-famine, restore, finish; hash matches an
  uninterrupted run.
- `FoodConsumptionTests.Food_TwinRun_HashesMatch_AcrossPopulationChurn` —
  twin-run with births, deaths, captures, deposits, famines, and
  starvation events — hashes match end-to-end. **Headline test.**

## Headline determinism test

> **`FoodConsumptionTests.Food_TwinRun_HashesMatch_AcrossPopulationChurn`**
> — two identical scenarios (initial population, hauls, M7 raids
> causing captures and deaths, M8 births and aging deaths, a
> deliberate dry-out and refill, several starvation deaths during
> the famine) produce `Snapshot.Hash` equality at every common
> checkpoint.

This is the milestone's M-level contract per architecture §1.

## References

- `docs/architecture.md` §1 (determinism contract), §2.1
  (event-driven, never tick-driven), §2.2 (pure-read wall — `CurrentLevel`),
  §2.5 (lazy catch-up math — including the spatial extension's anchor-at-
  rate-transition discipline that food consumption inherits), §2.6
  (fencing tokens for `FamineCheckEvent` / `StarvationDeathEvent`),
  §2.7 (sparse per-entity state — `Player.PopulationCount` is one
  small per-player counter; not sparse but small-cardinality), §2.8
  (anchors for in-flight state), §3 rule 7 (one mutation point per
  state — applied to `Player.PopulationCount` and to
  `Castle.Holdings[Food]` reduction via `FoodConsumption.CatchUp`).
- `docs/biome-degradation.md` — the spatial lazy-field pattern that
  M13's per-castle field re-uses without the spatial dimension.
- `docs/population-model.md` — M8 birth / death / `OnUnitRemoved`
  hook that M13 extends.
- `docs/combat-model.md` — capture-on-death path that triggers
  M13's both-castles catch-up.
- `docs/persistence-model.md` — anchor pattern for the two new
  per-event scheduling fields.
- `persistent-rts-design.md` §1 (castle as the heart of the
  civilization; loss condition), §7 (everything physical).

## Update 2026-06-09 — close the trickle-deposit exploit

**Change:** When a deposit clears a famine, the scheduled
`StarvationDeathEvent` is no longer cancelled. Its anchor stays in
flight. If a new famine starts before the original death tick,
`FoodConsumption.CatchUp` declines to schedule a fresh death (the
existing anchor wins). The original death fires on its original
schedule.

**Why:** The original implementation called
`ClearStarvationDeathAnchor` on any deposit that brought
`Holdings[Food] > 0`. A player could then trickle in 1 food unit during
famine, fully cancel the death cadence, and earn a fresh
`StarvationStartDelay` grace window before the next death — repeatable
indefinitely. Death-by-starvation became a soft warning instead of a
real consequence. The doc's stated intent — "if your supply line
breaks, your civ shrinks" — failed in practice.

**Alternatives considered:**
- *Debt-tracking* (track a `FamineDebt` field; deposits pay it down
  before counting as new stock): most rigorous but requires a new
  persistent field and snapshot-format bump.
- *Threshold-clear* (famine clears only if the deposit covers one full
  period of consumption): simpler but feels like food "vanishes" when
  the deposit is small.
- *Preserve cadence* (this fix): leave the death scheduled; let the
  fence harmlessly cancel it if the recovery is genuine. No new field,
  no snapshot bump, no balance surprises — the cadence is the
  punishment.

**How the fence stays clean:**
- If the deposit's food covers all of the time until the original death
  tick, no new famine arises before then. The death fires, sees
  `FamineStartTick == null`, fences via the existing branch
  (`StarvationDeathEvent.cs:46-50`), and clears its own anchor. A
  later famine schedules fresh — that's the "true recovery" path.
- If a new famine arises before the original death tick, `CatchUp`'s
  famine-trigger branch sees the existing anchor and declines to
  reschedule. The original event fires on its scheduled tick, finds
  famine active, and proceeds. The player gained only the time their
  deposit's food actually paid for — no free reset.

**Acceptance tests:**
- `FoodConsumptionPhaseDTests.TrickleDeposit_DoesNotResetStarvationCadence`
  — pins the fix: tiny deposit during famine, food runs out, original
  death tick still claims a citizen.
- `FoodConsumptionPhaseDTests.StarvationDeath_FencesWhenFamineEnds` —
  updated: anchor STAYS at deposit time, then gets cleaned up when the
  orphan death fences itself.

**Files touched:**
- `src/Sim.Core/Logistics/HaulDepositEvent.cs` — removed the
  `ClearStarvationDeathAnchor` call from the deposit-during-famine branch.
- `src/Sim.Core/Food/FoodConsumption.cs` — guarded
  `ScheduleNextStarvationDeath` in `CatchUp`'s famine-trigger branch
  with `if (!castle.NextStarvationDeathTick.HasValue)`.

## Update 2026-06-11 — famine DEBT model (negative food)

**Change:** Famine is no longer a boolean larder-is-empty state — it is a
**debt**. The consumption clock never stops: every meal the larder can't
cover accrues in a new persistent field, `Castle.FoodDebt` (snapshot
FormatVersion 11). The castle's effective food level is
`Holdings[Food] − FoodDebt`, which goes **negative on the HUD** during
famine — the magnitude is exactly the deposit needed to stop the deaths.
Deposits pay the debt before restocking the larder; famine (and the death
cadence) ends only when the debt hits **exactly zero**. Alongside the
model change, the cascade was retuned: `StarvationStartDelay` 1 → **3
game-days** (≈ one farm-bootstrap cycle) and `StarvationDeathInterval`
1 → **12 game-hours**.

**Why:** Two pressures converged. First, the 2026-06-09 trickle-deposit
fix (preserve-cadence) worked but was a patch on the real problem: with a
boolean famine, *any* deposit > 0 ended it, so the system needed careful
anchor carry-over rules to stay exploit-free. The debt model kills the
exploit **structurally** — a deposit smaller than the hole leaves you in
the hole — which is exactly the "debt-tracking" alternative the 2026-06-09
addendum called *most rigorous but requires a new persistent field and
snapshot-format bump*. That cost is now paid. Second, playtesting at 20
tps showed the cascade was too fast to react to once famine hit; but
simply extending grace under the boolean model would have widened the
trickle window. Debt makes a long grace SAFE to grant: the 3 days aren't
free, they're a loan at full interest (debt grows at the full population
rate throughout).

**Design choices locked in:**
- **Debt grows at the full population rate while starving.** That is the
  bleeding. It is self-limiting: each death shrinks the rate; at
  population zero the debt freezes.
- **Deaths do not reduce the debt.** The dead stop eating going forward;
  the hole they left stands. If the last citizen dies the cadence stops
  (nothing to kill) but the famine is NOT forgiven — the debt waits for
  a deposit.
- **No debt cap** initially. The death cascade bounds growth naturally;
  a cap is a one-line knob if deep holes prove unrecoverable in play.
- **A full repayment earns a fresh grace window** on the next famine.
  Legitimate by construction: you cannot reach it with trickles.

**What got simpler:** `CatchUp` lost both special cases — the famine
clock-freeze (the anchor used to stop at the failure boundary; now it
always advances and the shortfall is owed) and the 2026-06-09 anchor
carry-over guard (famine-end now clears the death anchor, and famine-end
is unreachable by trickling). `CurrentLevel` is signed and uniform.

**Acceptance tests** (`FoodConsumptionTests`): shortfall-becomes-debt
arithmetic; signed `CurrentLevel` (negative by exactly the debt);
observation-independence across the famine boundary (debt + onset tick
identical under any catch-up pattern); deposit pays debt first /
remainder restocks; partial payment leaves famine active and the death
fires on schedule; full repayment cancels the scheduled death and the
next famine gets fresh grace; debt outlives a fully-dead population;
snapshot round-trip of `FoodDebt`.

**Files touched:** `FoodConsumptionConstants.cs` (retune),
`Structure.cs` (`Castle.FoodDebt`), `FoodConsumption.cs` (uniform
catch-up; signed `CurrentLevel`), `CargoTransfer.cs` (debt-first deposit
+ famine-end at zero), `StarvationDeathEvent.cs` (debt-gated; debt
outlives the dead), `Snapshot.cs` (v11), `ViewProjector.cs` (signed
`CastleFood` on the existing wire field — no schema change).
