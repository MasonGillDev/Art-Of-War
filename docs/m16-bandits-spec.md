# M16 Spec — Bandits (an NPC faction that spawns in the fog)

The first antagonist. Bandit parties spawn where no player can see,
hunt what *they* can see, steal from unguarded extractors, and vanish
back into the dark with the loot unless someone stops them. In
Sim.Core's eyes they are just another faction — a reserved hostile
OwnerId whose units move, fight, carry, and die through entirely
existing machinery. The "AI" lives **outside the sim** as a server-side
driver that reads world state and submits ordinary intents — the
architectural dry run for the player automation layer.

## What we're adding

1. **A reserved bandit faction** — `BanditConstants.OwnerId` (a value no
   wire client may claim), registered as a `Player` row at genesis with
   no castle, no spawns, no holdings. Hostile to every faction always,
   immune to diplomacy.
2. **Spawn-in-darkness** — `SpawnBanditPartyIntent` (server-internal):
   a party of bandit units materializes on a tile **no player can
   currently see**, at a configurable minimum distance from any player
   presence. Validated in core at resolve time, so a scout walking in
   during the intent's flight kills the spawn.
3. **Despawn-in-darkness** — `DespawnBanditPartyIntent`: surviving
   bandits who reach an unseen tile melt back into the fog, taking
   their cargo with them. Stolen goods you fail to intercept are gone.
4. **`LoadCargoIntent`** — the missing half of `UnloadCargoIntent`: a
   unit loads from the structure/ground pile on its tile into its own
   cargo, no destination leg. A general atom (players get it too); for
   bandits it is the *stealing* verb.
5. **`UnitRole.Bandit`** — combat stats, cargo capacity, age-exempt
   (no lifespan roll; they die by sword, not calendar).
6. **The bandit driver** (`Sim.Server/BanditDriver`) — a per-game-hour
   brain hooked into the existing clock loop that decides spawns,
   targets, raids, and retreats, and submits everything through
   `SubmitIntent` like any player would.

## The gap (why now)

The 2026-06-11 fun audit found the engine is all pressure and no
antagonist: combat (M7), military roles + equipment (M14), towers, and
the capture-on-death raiding economy are built and **never fire** —
nothing in the world acts against the player. Food is a predictable
treadmill rather than an eventful threat. Bandits give every dormant
system its customer: the military gets a job, towers get a purpose
(vision = safety, see below), routes get risk, and the food system gets
*events* ("they burned through my farm crew") instead of a drip.

The design pillar pays for itself here: **"knowledge comes from
presence"** (persistent-rts-design.md §1) becomes a defense mechanic.
Spawns land only in the dark, so every tower, patrol route, and scout
physically pushes the bandit frontier away from your land. A well-lit
core is safe; deep, dark territory is dangerous — exactly the gradient
the user asked for.

## Locked decisions (user, 2026-06-11)

- **Parties only** — no bandit camps/structures this milestone. The
  spawn rule and faction plumbing are designed so camps can be added
  later without rework (a camp is just a structure-shaped spawn source
  for the same faction).
- **Bandits steal**, not just kill. Stealing uses the haul/load path;
  killing the carrier drops the loot to the ground (existing capture
  economy) for the player to recover.
- **Prosperity-scaled spawning with tunable config** — the pressure
  scales with total player structure count (sprawl attracts wolves),
  every knob in a config.
- **Some parties spawn idle** — ambushers with no agenda that stand in
  the fog until something walks into *their* sight. Exploration has
  teeth.
- **Bandits get fog too** — they hunt only what the bandit faction can
  currently see. No omniscient AI; dark territory is dangerous because
  *they* found you, not because the server cheated.

## Proposed mechanics

### Faction plumbing (Sim.Core)

- `BanditConstants.OwnerId = -1` — out-of-band below all player ids.
  Audit pass required: grep for any assumption that OwnerId ≥ 0
  (dictionaries everywhere suggest none, but verify).
- `Genesis.Build` registers `world.Players[BanditOwnerId]`
  unconditionally — every world has the faction, usually empty. No
  castle: `FindCastleFor` already returns null defensively, so the
  food/famine machinery never engages (`Population.cs:85-87`). No
  Houses → no breeding. No snapshot format change (Players serialize
  generically; the new UnitRole byte is append-only).
- **Hostility hard-wired**: `Diplomacy.AreHostile(a, b)` returns true
  whenever exactly one side is the bandit id (`Diplomacy.cs:39`). No
  relationship rows, no war telegraph, no pending-war machinery.
- **Diplomacy immunity**: `DeclareWarIntent`, `ProposeRelationshipIntent`,
  `RespondToProposalIntent` reject any party naming the bandit id —
  note they currently validate faction *existence* against
  `world.Players`, which the bandit row would now pass, so the
  rejection must be explicit.
- **No remembered map**: `Sight.Reveal` skips the bandit owner. Bandit
  targeting uses live `View.VisibleTiles(world, BanditOwnerId)`
  (`View.cs:62-93`, already a pure read over any owner's units). Keeps
  snapshots lean; bandit "memory" is the driver's problem (below).

### Spawn / despawn (server-internal intents)

- `SpawnBanditPartyIntent(tile, size, role…)` resolves only if the tile
  is (a) in bounds and walkable, (b) **invisible to every player
  faction** — computed from `View.VisibleTiles` per player at resolve
  time, (c) at least `BanditConfig.MinSpawnDistance` (Chebyshev) from
  any player unit or structure, seen or not. Units are created through
  the `BirthEvent`-shaped path (`world.NextUnitId`, `Population.OnUnitAdded`)
  with `ScheduleLifespan` **skipped** (the idempotent `DeathTick` guard
  at `Population.cs:51` makes this a clean bypass: pre-set, or simply
  never roll).
- `DespawnBanditPartyIntent(unitIds)` resolves only if every named unit
  is a live bandit on a tile invisible to all players. Removes the
  units through the M7 single-death pipeline **minus** the cargo drop —
  the loot leaves the world with them (that's the punishment for not
  intercepting).
- **Wire guard**: `GameHost.SubmitEnvelopeJson` rejects any envelope
  whose intent resolves to `PlayerId == BanditOwnerId`, and rejects the
  two bandit intents by type regardless of claimed id. The driver
  submits in-process via `SubmitIntent` directly, below the HTTP gate.
  (This is the first intent authorization that is *server-internal*;
  record it in docs/intent-authorization.md.)

### Stealing (the LoadCargo atom)

- `LoadCargoIntent(unitId, resource?)`: unit must be on a tile with a
  structure holding compatible output/holdings or a ground pile; loads
  up to its cargo capacity into `CargoResource/CargoAmount`. Mirrors
  `UnloadCargoIntent`'s shape and shares `CargoTransfer`-adjacent code
  where sensible. Authorization: unit ownership only — **source
  ownership stays unchecked**, exactly like `HaulIntent`
  (`HaulIntent.cs:59-66`, deferral pinned at
  docs/intent-authorization.md:76-85). This milestone **pins that gap
  as the raiding economy by design**: hostile units looting an
  unguarded buffer is intended gameplay, symmetric for players raiding
  bandits or each other. The intent-authorization doc gets an addendum
  saying so (the eventual trade milestone closes it for *allied* cases,
  not hostile ones).
- Bandit loot loop, end to end with zero new combat code: party walks
  onto your lumber camp tile → engagement pin + combat rounds (M7) kill
  the workers → `LoadCargoIntent` empties the buffer → party flees dark
  → despawn. Kill the carrier anywhere along the way and the cargo
  drops to the ground for re-haul (existing `OnUnitDeath`).

### The driver (Sim.Server, the proto-automation layer)

- `BanditDriver` runs **on the clock-loop thread** immediately after
  `_sim.Run(until: _virtualTick)` (`GameHost.cs:53-71`) — same thread,
  so its pure reads never race the sim. It acts on a game-time cadence
  (`BanditConfig.ThinkPeriod`, default 1 game-hour), not per wall-tick.
- Per think-tick: (1) census — live parties vs the prosperity target
  `min(MaxLiveParties, totalPlayerStructures / StructuresPerParty)`;
  shortfall → pick a spawn site (seeded RNG over candidate dark tiles)
  and submit a spawn, with `IdleSpawnFraction` of parties flagged as
  ambushers; (2) per party, a tiny FSM:
  - **Ambusher**: do nothing until `VisibleTiles(bandit)` contains a
    player unit or structure → become Raider.
  - **Raider**: move toward the nearest seen target (extractor >
    laden hauler > anything); on arrival combat auto-triggers; when the
    tile is uncontested and has a buffer/pile → `LoadCargoIntent`;
    cargo full or target dry → Flee.
  - **Flee**: move toward the nearest dark tile away from player
    presence; on arrival submit despawn.
- Driver memory (last-seen targets, party states) is **ephemeral and
  out-of-sim**. This is safe by construction: intents are the durable
  truth, so crash recovery replays the *decisions*, not the brain. A
  restarted server forgets what bandits were chasing — acceptable and
  even flavorful. The determinism contract is the headline test below.
- `BanditConfig` (server-side knobs, the user tunes these): ThinkPeriod,
  SpawnPeriod floor, MaxLiveParties, StructuresPerParty,
  PartySizeMin/Max, IdleSpawnFraction, MinSpawnDistance, FleeWhenCargoFull,
  RNG seed. Core-side stats live in catalogs as usual:
  `CombatStats` for Bandit (suggest Soldier-ish: 25 HP / 3 power),
  `UnitCargoCatalog.BanditCapacity` (suggest 15 — stealing matters but
  a party can't empty a stockpile in one visit).

### Persistence & wire

- Bandit units serialize as ordinary units; the bandit Player row
  serializes generically. **No FormatVersion bump expected** — verify
  with round-trip tests; bump only if a field proves necessary.
- New intents register in `IntentJson` (frozen typeNames) + JSON
  round-trip tests in Sim.Persistence.Tests, like every intent.
- ViewProjector needs nothing: enemy units on visible tiles already
  project, so bandits render the moment they step into your light.
  Enemy cargo is already own-only-enriched — players see *that* a
  bandit exists, not what it carries (scouting tension preserved).

### Client (separate pass, same milestone)

- `Wire.cs`: `UnitRole.Bandit` enum mirror; `BiomePalette` color
  (suggest charcoal/blood-red); nameplate.
- HUD "⚠ bandits sighted" ping derived **client-side** by diffing
  visible bandit units between view polls — no server notice machinery
  needed (M10 push notifications can adopt the spawn/sighting hooks
  later).

## Explicitly deferred

- **Bandit camps** (structure spawn sources you can find and burn — the
  exploration payoff). Designed for: same faction, same spawn rule,
  camp replaces driver-chosen spawn site.
- Structure damage/burning — bandits kill workers and steal; they
  cannot destroy buildings until sieges exist.
- Server notices / push notifications for sightings (M10).
- Bandit boats, bandit roads avoidance/preference, ransom/tribute
  diplomacy, difficulty curves beyond the prosperity scaler.
- Closing the haul source-ownership gap for allied/neutral players
  (trade milestone).

## Headline tests

- **`BanditDeterminism.ReplayFromIntentLog_HashesMatch`** — THE
  architectural claim: run a scripted bandit scenario (spawn → hunt →
  combat → steal → flee → despawn) by submitting the driver's intents;
  capture the intent schedule; replay it into a fresh sim;
  `Snapshot.Hash` equality at every checkpoint. Proves out-of-sim AI
  preserves determinism — the same proof the future player automation
  layer rides on.
- **`Spawn_OnSeenTile_Rejects` / `Spawn_DarknessRace_ScoutArrivesFirst_Rejects`**
  — darkness validated at resolve, not submit.
- **`AreHostile_BanditVsEveryone_Always` + diplomacy-immunity matrix**
  — declare-war / propose / respond naming the bandit id all reject.
- **`StealLoop_BufferEmptied_CarrierKilled_LootRecoverable`** — load
  from a player extractor, kill the carrier, ground pile holds the
  goods, player re-hauls them.
- **`Despawn_RemovesCargoFromWorld` / `Despawn_OnSeenTile_Rejects`**.
- **`Bandits_NoFamine_NoBreeding_NoAgeDeath`** — faction exemption pins.
- **`Wire_RejectsBanditPlayerId_AndBanditIntentTypes`**.
- Snapshot round-trips mid-raid (bandit units + their cargo + pinned
  combat), restore, finish — hash equal vs uninterrupted.
- All expectations config-derived (`BanditConfig`, `CombatStats`,
  `UnitCargoCatalog`) — never hard-coded, per the standing convention.

## References

- `persistent-rts-design.md` §1 (knowledge comes from presence; supply
  lines as raidable infrastructure).
- `docs/combat-model.md` (engagement pin, capture-on-death, "Mobs: a
  new faction whose intents are AI-generated" — this milestone).
- `docs/intent-authorization.md` (the haul source-ownership deferral
  this milestone pins as raiding-by-design; the new server-internal
  intent class).
- `docs/food-consumption.md` (famine-debt model — bandit raids are the
  eventful pressure the treadmill lacked).
- `docs/architecture.md` §2 (pure-read walls the driver leans on;
  intents-as-truth §3 — why out-of-sim AI is determinism-safe).
