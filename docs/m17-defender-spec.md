# M17 Phase 2 Spec — the Defender

The Homesteader learns to fight back. This is the phase the M17 spec
pre-budgeted as "small behavior + REAL arbitration tuning pass": the
engine needs ZERO new mechanics (combat triggers on contact, Barracks/
TrainUnit/Craft/Equip shipped in M14, dead units drop cargo to tile
piles, bandits appear in the fogged view like any hostile unit) — the
work is brain-side, and most of it is arbitration: defense and economy
competing for the same units out of the same reservation pool.

## Locked decisions (user, 2026-06-12)

- **Standing army.** Soldiers are trained and maintained in peacetime,
  to a quota — a constant food/labor tax, like real defense spending.
  Not militia-on-demand: training requires a walk to the Barracks, and
  a reactive army means the first raid always lands on civilians.
- **Pursuit.** The army chases fleeing thieves into the fog to kill
  them and recover the stolen goods — bounded by a leash knob (an
  unleashed chase is the scout-job-creep bug wearing a helmet).
- **Civilian doctrine is undecided BY DESIGN.** Whether threatened
  workers recall to the castle (lose production, save lives) or farm
  through the raid (the famine clock doesn't pause) ships as an
  `AiConfig` A/B knob; the balance lab runs both and the data picks
  the default. This is the first knob whose value the lab CHOOSES
  rather than confirms.
- **Within the Homesteader, not a second brain.** See below.
- **The monolithic brain file is retired first.** `HomesteaderBrain.cs`
  reached 905 lines growing one lab failure at a time; before the new
  rungs land, the brain decomposes into a file-per-rung structure
  (Phase 0 below). One ARBITER stays — one ladder, one reservation
  pool — but rungs become classes, and Defend/Muster are born as
  files, not regions.

## Architecture: rungs, not a new brain

The Defender is two new rungs on `HomesteaderBrain`'s existing strict
ladder plus a threat-memory layer in `AiMemory`. It is NOT a separate
`DefenderBrain`, because the brain's one-arbiter design is load-bearing:
defense and economy compete for the SAME units (the fleeing farmer is
mid-haul; the None-role native could become Farmer or Soldier; the
pursuit squad still eats), and the per-think reservation ledger plus
cross-think designations are the only thing preventing same-think
double-tasking (arbitration bug #2). Two brains over one unit pool
would reintroduce that bug class at architecture scale. Staying inside
also keeps every existing tool working unchanged: DecisionTrace, the
balance lab, the replay hash tests, and the `Brain_TouchesOnlyTheView`
fairness pin (the view already shows bandits fairly — they have no
`Sight.Reveal`, so they appear as `OwnerId = -1` `UnitDto`s exactly
when a human would see them).

### Phase 0 — decompose the brain (move code, change nothing)

The arbiter-vs-file distinction: one arbiter is architectural and
stays; one FILE is not standard for behavior systems and goes. Target
shape:

```
Sim.Server/Ai/
  HomesteaderBrain.cs   the ladder as DATA (ordered rung list) + the
                        think loop + trace plumbing (~80 lines)
  ThinkContext.cs       what every rung sees: view, now, memory,
                        config, the reservation/designation ledgers,
                        and the shared perception/selection helpers
                        (TakeIdleCarrier, IsIdleStill, IsPocket,
                        NearestPocketTile, NearestFreeTile,
                        LaborLedger) — today's "Digest" region
  AiMemory.cs           extracted verbatim
  Rungs/
    EatRung.cs BuildRung.cs TrainRung.cs GrowRung.cs ScoutRung.cs
    LogisticsLayer.cs   the background haul pass
```

Each rung: one class, `bool TryEmit(ThinkContext ctx)` — emits intents
through the context, returns whether it claimed the think. The ladder
is an ordered array; first claimant wins, logistics always runs.

Why beyond readability: **Rival (phase 3) becomes composition** (same
rungs, different order, plus a Raid rung) instead of a 900-line fork,
and the rung vocabulary is the concrete form of the architecture.md
"player-authored ladder" idea for the automation layer.

Refactor safety net (no new tests needed): the BalanceLab per-decade
curve is a behavioral golden signature — same seed, same config, a
pure move-only refactor must reproduce it digit for digit. And
`Brain_TouchesOnlyTheView` widens its reflection sweep to the whole
`Sim.Server.Ai` namespace so no rung class can ever grow a
`GameWorld`/`Simulation` parameter.

### The ladder

The ladder grows from
`Eat → Build → Train → Grow → Scout` to:

**Defend → Eat → Build → Train → Muster → Grow → Scout**

- **Defend** sits at the TOP and fires only while the threat memory is
  hot — dead farmers don't farm, so an active raid preempts everything.
  When no hostile has been seen recently it emits nothing and the
  ladder behaves exactly as today (phase-1 lab curves must not move).
- **Muster** (standing-army maintenance) sits between Train and Grow:
  after the food economy is staffed (Eat/Build/Train outrank it — an
  army you can't feed is a famine with swords) but before breeding
  (a colony that grows before it garrisons feeds the bandits, who scale
  with prosperity).

## The new rungs

### Muster — build, train, equip to quota

- **Barracks**: place one when affordable. Cost 100 wood + 20 stone —
  genesis stone (50) covers it; no Quarry required. Placement near the
  castle (it's a depot, not a frontier fort); hybrid pocket/free
  placement reused.
- **Soldier quota**: `SoldierQuota(view)` — a config-derived target
  that scales with what bandits punish: prosperity. Knob shape:
  `SoldiersPerStructures` (e.g. 1 soldier per N own structures, min
  `SoldierQuotaFloor`). Bandit pressure scales by `StructuresPerParty`
  server-side; the AI can't read that config (fairness — it learns
  pressure the way a player does), so the quota is a knob the lab tunes
  against measured raid sizes, not a mirror of bandit config.
- **Training**: None-role natives are apprenticed to the Barracks via
  the existing `DesignatedTrainee` machinery (generalized to remember
  WHICH role the walk is for). Train competes with Muster for the same
  natives — ladder order resolves it: Farmer demand first, then
  soldiers. Trained soldiers get a cross-think designation
  (`MilitiaUnits`) so Eat/Build/logistics never conscript them.
- **Equipment is staged**: Phase A ships BARE soldiers — Soldier
  (30hp/3pw) beats Bandit (25hp/3pw) one-on-one, so a quota of bare
  soldiers is a real army on day 1. Shields (5 wood + 5 stone, +10hp)
  come second: leftover genesis stone affords ~6 without a Quarry.
  Swords need ORE → a Mine → an entire economy rung the Homesteader
  has never run — deferred until the lab shows bare/shielded soldiers
  losing fights the quota can't fix (then it lands as a Build-rung
  extension with its own scouting demand, not a special case).

### Defend — intercept, pursue, recover

- **Threat memory** (`AiMemory.SightedHostiles`): every think, scan the
  view for units with `OwnerId == BanditConstants.OwnerId` (the brain
  may read Sim.Core constants — rules, not state) and record (tile,
  tick, count). Entries expire after `ThreatMemoryTicks` or when the
  tile is re-observed empty. "Hot" = any unexpired entry; hot gates the
  rung.
- **Intercept**: move militia (as a group — `FormGroupIntent` keeps
  the force concentrated; bandits flee from groups) toward the
  newest/nearest sighting. Combat itself is automatic on contact.
- **Pursuit with a leash**: chase continues while sightings stay fresh,
  bounded by `PursuitLeashTiles` (Chebyshev from the castle). Past the
  leash, or once the memory goes stale, the militia walks home. The
  leash is the anti-job-creep guard: bug #3 (scout job-creep) taught
  us every open-ended behavior needs a budget.
- **Loot recovery**: killed bandits drop cargo to a tile pile
  (`CombatRules` drop-to-tile); the Defend rung issues `LoadCargoIntent`
  on piles at remembered kill/sighting tiles and hauls home. Piles the
  militia walks past on the way back are free money.
- **Civilian doctrine knob** (`RecallCiviliansUnderRaid`): when a
  sighting is within `CivilianDangerRadius` of a worked extractor —
  ON: unassign + move workers to the castle, re-staff when the threat
  cools (the existing Eat staffing logic re-fills automatically);
  OFF: workers keep working, militia handles it. Lab A/B decides the
  shipped default (see tests).

## Arbitration budget (the deferral expires here)

Expected collisions, named in advance so the trace hunts have a map:

1. **Defend vs Eat for the same hands** — recalled farmers crater food
   throughput; the famine-debt clock runs through the raid. The
   doctrine A/B measures exactly this trade.
2. **Muster vs Train for None-role natives** — ladder order (Train
   first) is the rule; the risk is oscillation when both are starved.
   Watch for designation thrash in the trace.
3. **Pursuit vs the labor pool** — soldiers are population: they eat,
   they count toward carrying-capacity gates, and a long chase is paid
   in farm-hours. `PursuitLeashTiles` is the tuning surface.
4. **Quota vs famine** — every soldier is a mouth that produces
   nothing. `SoldiersPerStructures` against the lab's famine line is
   the headline tuning pass.
5. **Reservation coverage** — militia must be reserved by Defend
   BEFORE logistics runs (strategic-first ordering already does this)
   and excluded from `TakeIdleCarrier` like Builders are.

Per the M17 spec: behaviors are small, THIS list is the milestone.

## Headline tests

- **`AiVsBandits_EconomySurvivesRaids`** — UN-SKIP the phase-2 gate:
  Homesteader + default bandit config, 50 game-days, population may
  dip, never zero, no famine at the end.
- **`Doctrine_RecallVsWorkThrough_LabReport`** — same seed, knob ON
  vs OFF, prints both curves (population, food, raids survived, loot
  recovered). Starts as a REPORT; once the data picks a winner the
  default is pinned and the report keeps printing as regression
  context.
- **`Muster_ReachesQuota_WithoutFamine`** — quota filled within
  config-derived days; food debt never positive while doing it.
- **`Pursuit_RespectsLeash`** — militia never exceeds
  `PursuitLeashTiles` Chebyshev from the castle (trace-derived).
- **`Ai_ReplayFromIntentLog_HashesMatch`** — re-run WITH bandits +
  AI both driving (the chronological-interleave discipline now covers
  two drivers' same-tick batches).
- **`Brain_TouchesOnlyTheView`** — unchanged, still passes (the
  fairness pin outranks every feature).
- All thresholds config-derived per the standing convention — every
  expectation computes from `AiConfig`/`BanditConfig`/catalogs.

## Explicitly deferred

- **Archers** — ranged-from-adjacent is still deferred in core; an
  archer is a worse soldier until then.
- **Sword/ore economy** (Mine rung) — until the lab demands it.
- **Towers, walls, hauler escorts, forward forts** — no engine support
  yet / no measured need.
- **Inter-faction war** — Rival (phase 3); conquest waits on win
  conditions.

## Update 2026-06-12 — shipped (Phases 0–2 in one day of lab time)

**What shipped:** the Phase-0 decomposition (move-only, curve replayed
digit-for-digit), the Muster rung (standing army to a
population-capped quota; bare soldiers), the Defend rung (threat
memory, sortie, leash-bounded pursuit, civilian recall doctrine), and
the un-skipped `AiVsBandits_EconomySurvivesRaids` gate — green, with
the pursuit-leash pin sampled every think.

**What the lab decided:**

- **Doctrine pinned: recall = ON.** The A/B's first run forced it —
  recall OFF got faction 1 WIPED by day 50 (bandits steal from worked
  posts; blades fall on field hands); recall ON kept both colonies
  alive. `Doctrine_RecallVsWorkThrough_LabReport` now pins the
  decision comparatively (ON must keep everyone alive and do at least
  as well as OFF) so future tuning re-opens it automatically.
- **Sparta starves** (ledger #11): the flat 4-soldier floor was a
  ~30% defense budget against a 14-person genesis — colony froze at
  pop 17 and died with its founders. Quota is now population-capped
  (one soldier per `PopulationPerSoldier` mouths).
- **The siege poverty trap** — ANSWERED same-day (second lab pass; the
  autopsy overturned the first diagnosis). The freeze wasn't economic:
  the day-1 raid had killed both BUILDERS (and both Haulers), and only
  Builder-role hands may raise a site — four provisioned-or-fundable
  sites sat frozen over 315 banked wood for 40 days. Three fixes
  shipped (ledger #12): ROLE FLOORS in the Train rung (Builders →
  Haulers → Scouts before Farmer coverage), the WAR-FOOTING quota in
  Muster (headcount + 1 under a counted threat, capped by a wartime
  population share, larder gate waived, veterans demobilized at the
  School when the trail goes fully cold), and FORCE PARITY in Defend
  (the garrison holds instead of trickling into losing fights). The
  recoverable colony under default pressure now ends day 50 at pop 21
  — double the trapped baseline.
- **The circular lock (open GAME finding, user's call):** builder
  extinction BEFORE a School stands is unrecoverable for any player,
  human or AI — the School is builder-built and the Builder is
  School-trained. Faction 0 on the lab seed sits in exactly this state
  and no brain change can free it. Candidate core fixes: the Castle
  doubles as a basic-role trainer, a genesis School, or relaxing the
  builder-only gate on AssignBuildersIntent.

**Deferred beyond the spec's original list:**

- **Loot recovery** — dropped cargo piles are not on the wire AT ALL
  (no DTO): neither the brain nor the human client can see them, so
  recovery can't be played fairly by anyone. Found by this phase's
  design pass — a real game gap, not a view gap. Pile visibility is a
  core/wire work item that benefits humans equally; `LoadCargoIntent`
  already loads from piles once they're findable.
- **Group sorties** — soldiers converge individually and it survives
  the gate; `FormGroupIntent` concentration waits until the lab shows
  trickle-in deaths.

## References

- `docs/m17-ai-players-spec.md` — the fairness contract and the
  arbitration section this phase cashes in.
- `docs/ai-players.md` — the arbitration ledger (bugs #1–9) the
  collision list above extends.
- `docs/bandits.md` — the opponent's brain: Ambush/Raid/Flee FSM,
  flee-from-groups behavior the intercept design exploits.
- `docs/military-training.md`, `docs/equipment-model.md` — the M14
  machinery Muster drives.
- `docs/combat.md` / `CombatRules.cs` — contact-triggered combat and
  the drop-to-tile loot economy Defend recovers from.
