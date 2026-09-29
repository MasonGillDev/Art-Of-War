# Progression: hidden one-shot milestones that answer what the player does

## The decision

Each human player carries a **progress ledger**: counters that only go up, bumped in the
sim where the thing happens (a soldier finishes training, a building completes, a raid is
beaten). A code catalog of **milestones** watches the ledger. Each milestone has a condition
and a list of **effects**, and fires **once** per player. Effects are a closed set of typed
records that grows one type at a time: an **Unlock**, a **Threat** (a telegraphed attack),
an **Arrival** (refugees, a found cache, later a merchant or a smith), a **Grant**.

A Threat or an Arrival does not happen at once. It becomes an **omen**: a saved record with
a due tick, shown to the owner as a countdown ("Bandits will attack in 7 days, from the
north-east"). At the due tick the sim makes it happen. The server-side brains (the bandit
driver) only decide how it plays out.

Milestones are **surprises**. Neither the catalog nor the counters go on the wire. The
player sees only what has fired: an omen's countdown, an arrival, a newly unlocked building.

Progression is **human only**. AI players are not enrolled. They are placeholders for
testing and filling the world until multiplayer, and will be phased out.

## Why

### What the user asked for (2026-09-23)

- A system that tracks a player's progress and attaches events to progress points. It has
  to be expandable: unlocks, world events, "whatever we end up with".
- The first example: after training 6 soldiers, a larger bandit force attacks the camp,
  telegraphed a week ahead so the player can prepare.
- Surprises, not a visible tree.
- AI players do not take part.
- **One-shot.** An event that repeats and scales with the player (6, 12, 24 soldiers, each
  a bigger raid) reads as boring and predictable, not as a special event. Bigger moments
  come from **separate, bigger milestones** with their own flavour, not tiers of one.
- **Threats motivate growth; they must never punish it.** A player should not hold back
  from training soldiers to avoid a raid.
- Arrivals and good fortune are the favourites.
- The chronicle (a written history of the kingdom) is left out for now.
- Automation is **not** a progression unlock. The haul queue has to work from minute one.

### Design rules that follow

1. **Every threat is a test the milestone says you can pass.** The reprisal for 6 soldiers
   is sized so 6 soldiers at home, warned a week ahead, win. The warning time is part of the
   threat record. There is no zero-warning threat.
2. **Every threat pays out when you beat it.** Beating the raid is itself a counter, and the
   best arrivals hang off it (refugees who heard you can protect them, the raiders' loot).
   Growing brings danger, and beating the danger brings more growth.
3. **Every event is its own row.** No row fires twice, and no row is a copy of another row
   with bigger numbers.

### Where it runs: in the sim, not in a driver

- **Chosen: the sim.** A milestone is a rule of the world, not a decision. The sim checks
  the rows that watch a counter when the counter goes up. Rows that read the world as it
  stands (explored tiles, population) are checked once a day. This is deterministic, needs
  no polling, and saves with the world: counters, fired milestones and pending omens are
  snapshot state, covered by the twin-run hash like hauling jobs.
- **Lost: a server-side ProgressionDriver submitting `ReachMilestoneIntent`.** This is the
  bandit and hauling pattern, but those drivers make choices. A milestone makes none.
  A driver would add a think period of lag, an intent per firing in the log, and a
  re-validation step that only repeats the rule.
- **The split:** the sim decides *that* something happens and *when*. The existing drivers
  decide *how*. A threat's raiders appear through the sim. The bandit driver adopts them and
  marches them at the threatened settlement, reading the target from the omen record.
  Driver state stays disposable, as it is now.

### Counters versus reading the world

- **Counters** are for things that happened: soldiers trained, buildings completed, iron
  smelted, raids repelled. They only go up, so "trained 6 soldiers" stays true after those
  soldiers die, and the event cannot be dodged or re-armed by disbanding.
- **World readings** are for how things stand now: population, tiles explored. They are
  read live by the daily check. Nothing is copied into the ledger.
- **Lost: deriving everything from the world.** History is not recoverable from the present
  state (the dead soldier is gone).
- **Lost: an event log the conditions scan.** It grows without bound, and a counter answers
  every condition we have.

### Why a code catalog, not a data file

`StructureCatalog`, `RoleTrainerCatalog` and `HaulingConstants` are code tables. Conditions
are small typed expressions (`AtLeast`, `All`, `Any`, `Fired`), and effects are typed
records. A JSON file would need a schema and a loader to express the same thing, and would
move balance out of the files the tests read. Tuning stays in `ProgressionConfig` as knobs,
and tests are derived from the config (the house rule for balance numbers).

### Enrolment, and why AI players are skipped

`Player.Progress` is null for a player who is not enrolled. `WorldFactory` enrols the human
seat, and later every human seat. An unenrolled player counts nothing, fires nothing, and
**has every unlock**, so an Unlock row can never lock an AI out of a building its brain
depends on. When the AI is retired, the null case can go.

- **Lost: enrolling everyone and teaching the brains to answer threats.** That work is
  thrown away when the AI goes.

## Shape

```
Sim.Core/Progression/
  ProgressStat.cs         enum: RoleTrained, StructureCompleted, ResourceRefined,
                          BanditsKilled, ThreatRepelled, ThreatLost, ...
  ProgressKey.cs          (Stat, Sub) record struct: Sub = role / kind / resource / 0
  ProgressLedger.cs       SortedDictionary<ProgressKey,long> + SortedSet<int> fired
  Condition.cs            AtLeast(key, n) | Gauge(Population|Explored, n) | All | Any | Fired(id)
  Effect.cs               Unlock(kind) | Threat(kind, warn, size, dir) | Arrival(kind, warn, ...) | Grant(res, n)
  Milestone.cs            (Id, Condition, Effects)
  MilestoneCatalog.cs     the rows
  Progression.cs          Bump(sim, player, key, n) → check rows watching key; Fire(...)
  ProgressionDailyEvent.cs  daily sweep for Gauge rows
  Omen.cs                 (Id, Owner, Kind, DueTick, DirectionHint, Size, State, PartyIds)
  OmenDueEvent.cs         at DueTick: spawn raiders / refugees / cache
  ProgressionConfig.cs    knobs
```

- **Bump points:** `TrainingRules.Train`, `Construction.Complete`, refiner output, combat
  death credit, omen resolution. Each is one call, listed in the determinism audit.
- **Firing** stores the id, applies Unlock and Grant at once, and turns Threat and Arrival
  into omens with a `ScheduledEvent` at the due tick.
- **The due tick picks a tile** in the omen's direction. It must be dark to every player,
  walkable, and past the bandit minimum distance, the same rules as
  `SpawnBanditPartyIntent`. The search order is fixed and the tie-break is a hash of
  (world seed, omen id). It never draws from the world RNG, so demographic rolls do not
  shift. If no tile qualifies, the search widens ring by ring. If it still fails, the omen
  slips one day, but never more than a few times.
- **Threat outcome:** the omen keeps its raiders' ids. When they are all dead, it is
  **Repelled**. When the survivors flee off the map with loot, it is **Lost**. Either way
  a counter goes up, so follow-up rows can hang off the result.
- **Refugees** are ordinary people for the owner. They are added through
  `Population.OnUnitAdded` at the edge of the fog and walk to the castle.
- **Wire:** `ViewDto.Omens`, owner-only (kind, days left, direction hint, a target tile when
  there is one). `PlayerDto` (or the view) carries the unlocked kinds once any Unlock row
  exists. No catalog, no counters.

## First catalog (every row one-shot, every number a config knob)

| Row | Condition | Effect |
|---|---|---|
| **Reprisal** | 6 soldiers or archers trained, ever | Threat: raid of a matching size in 7 days, direction shown |
| **Word spreads** | Reprisal repelled | Arrival: refugees (a family band) in 2 days, from the road side |
| **Spoils** | Reprisal repelled | Grant: the raiders' war chest drops where the last one fell (a pile, not a gift in the castle) |
| **Far horizons** | explored tiles ≥ N | Arrival: a ruin cache revealed in the fog, direction shown |
| **A good home** | 3 houses completed | Arrival: a small band of settlers |

Unlock rows are held back until the user picks what to lock. Locking a building the loop
needs today would change the early game.

## Acceptance tests

- A counter bump fires a watching row exactly once, and a second crossing does nothing.
- Soldiers dying after the reprisal fires changes nothing: the omen stands and does not
  fire again.
- Unenrolled (AI) players never count, never fire, and can build everything.
- An omen at its due tick spawns on a dark, walkable tile past the minimum distance. With
  every candidate lit, it slips and later lands.
- Raiders all dead means Repelled, and the follow-ups fire. Raiders fleeing with loot means
  Lost, and nothing fires.
- Twin-run hash across a reprisal (fire → countdown → raid → repelled → refugees).
- Snapshot/restore in the middle of a countdown and in the middle of a raid lands on the
  same hash.
- The wire shows omens to their owner only. The catalog and counters never appear.

## Future expansion

- **New effect types plug in without touching the core:**
  - **Bandit camp:** a bandit-owned building in the fog that raids until destroyed. Tracking
    raiders home is the way to find it. This is the M16 deferred seam.
  - **Wandering smith:** unlocks a better weapon tier.
  - **Travelling merchant:** needs a trade system first.
  - **Tribute demand:** needs a new player intent to answer "pay or fight".
  - **Drought** and **hard winter** omens.
- **Bigger milestones** are new rows at bigger scales with their own flavour: a warband at 20
  soldiers brings a camp and a named warlord. No tiers.
- **The chronicle** can read the fired set and the omen history later, with no change to how
  things fire.
- **Multiplayer:** enrolment is per seat. A threat's raiders are ordinary bandits, so they
  can land on a neighbour. Whether that is a feature is a later call.
- **Rework risk:** conditions that need something the sim does not hold (what a player has
  *seen* on the server, a client action) would need a server-to-sim bridge. Nothing in the
  first catalog does.

## Update 2026-09-23 — Phase A built: gauges are checked where they move, not daily

The plan had a daily sweep for rows that read the world (population, tiles
explored). Phase A checks at the points where those readings change instead:
`Population.OnUnitAdded` and every sim-side `Sight.Reveal`. A check is a
handful of O(1) reads over unfired rows, and it returns at once for an
unenrolled player or when every row has fired.

- **Why:** a daily sweep needs a recurring event with an anchor and a
  `RegenerateQueue` entry, fires up to a day late, and still needs the same
  check code. Checking at the change is exact and adds no scheduled state.
- **Cost accepted:** a new code path that reveals tiles or adds units must
  also call `Progression.Check`. `Sight.Reveal` carries that note.
- **Also:** a counter is a plain `long`, and a firing marks the row before
  applying its effects, so an effect that bumps a counter cannot fire its own
  row twice.

## Update 2026-09-23 — Phase B built: the reprisal

- **Knobs are world config.** `ProgressionConfig` is genesis-set and snapshotted like
  `RoyaltyConfig`. `GameWorld.Milestones` is rebuilt from it (rows are code), so a test or
  a server can retune the reprisal without touching the rows.
- **Where raiders come from:** the octant around the seat with the most walkable ground
  the owner has never explored ("out of the wilds"). They arrive on the nearest ring in
  that octant (between `OmenSpawnMinDistance` and `OmenSpawnMaxDistance`), past the bandit
  distance floor, on a dark tile. Ties break on a hash of the omen id; the world RNG is
  never drawn. The plan said the hash would include the world seed; the sim holds none,
  and the wildness count decides nearly every time anyway.
- **Slip, then fizzle.** If the player's own presence covers the whole bearing, the
  arrival slips a day at a time, `OmenMaxSlips` times; then darkness is waived; if even
  that finds no ground, the omen is dropped. The announced direction is never changed.
- **Outcome:** every raider dead, or fled empty-handed, is **Repelled**; loot carried off
  the map is **Lost**. Counted as `ThreatRepelled` / `ThreatLost` keyed on the source
  milestone, which is what Phase C's rewards hang off.
- **Enrolment switch:** the host enrols the human seat by default (`--progression 0` turns
  it off). A bare `ServerOptions`, as the AI labs build it, does not, because those labs
  drive player 0 with an AI brain.

## Update 2026-09-23 — Phases C and D1 built: good fortune, and the wire

- **"Spoils" is part of the threat, not its own row.** The war chest has to drop where
  the last raider fell, and only the raid knows that tile. So `Threat` carries the chest
  and the omen drops it when every raider is killed; a raider who ran (even empty-handed)
  takes it with him. The raid still counts as Repelled.
- **Newcomers come from the settled side**: the octant with the most ground the owner
  has already explored. Raiders and ruins come from the wildest. A band of newcomers never
  fizzles: with no ground out in its bearing, it appears at the seat.
- **A rumour has no countdown.** The ruin (an ordinary loot cache) is placed at once on
  unexplored ground in the wildest bearing and the omen lasts until it is emptied. The
  wire gives its bearing only, never its tile.
- **Resolved omens stay, with their outcome** (Repelled, Lost, Fulfilled, Fizzled and the
  tick). The plan removed them. Every row fires once, so a player has a handful in a whole
  game; keeping them costs nothing and lets the view say how a raid ended. The view shows
  ended omens for two days.
- **The first catalog is four rows:** Reprisal (1), Word spreads (2), Far horizons (3),
  A good home (4). Unlock rows are still held back for the user.

## Update 2026-09-23 — the rumour gets a search area; anyone may take the ruin

User decisions after the first playtest:

- **A rough area circle on the map.** The owner is told a circle
  (`RumourAreaRadius`, 8 tiles) that holds the ruin somewhere inside: the centre is the
  ruin pushed off by a hash-picked offset of at most radius − 1, so the ruin is always
  inside, a tile clear of the rim, and never at the centre. Saved on the omen at raise
  (snapshot v37), sent as `OmenDto.AreaX/AreaY/AreaRadius`. The prod client draws it
  as a dashed gold ring with a faint wash on the map texture (minimap and full map), and
  the announcement toast carries the centre as a clickable tile. The exact tile is still
  never on the wire.
  - Lost: an arrow at the fog edge (tells direction, not distance, so a scout still
    wanders); nothing but the text (pure exploration, too vague to act on).
- **Anyone can take the loot.** The ruin stays an ordinary unowned cache: first come,
  first served, bandits included (they steal from any stocked structure they see). The
  AI brains do not loot caches at all, so in practice it is you against the bandits.
  - Lost: reserving the ruin to the owner. A race is a reason to send the scout now.
- **Bug fixed with it:** a cache was removed when emptied only by the loot verb. Plain
  loading (how bandits steal) and hauls left an empty cache standing and the rumour
  open forever. `CacheLooting.RemoveIfEmptied` is now the one removal path, called
  from the loot verb, `CargoTransfer.WithdrawFrom` and `HaulPickupEvent`.

## Update 2026-09-24 — the first Unlock: the Lodge arrives with "A good home"

- **The user's call.** The Lodge (and so scouts, who train there since M38) becomes
  buildable when "A good home" fires: 3 houses finished, about day 5, around 30 real
  minutes in. A settled people starts looking outward.
- **Locks are derived, not stored.** A kind is locked for an enrolled player while some
  unfired row carries an `Unlock` for it (`Progression.IsLocked`). Firing the row is the
  whole of unlocking: no new state, nothing to snapshot. Players who are not enrolled
  (AI seats, AI-driven labs) are never locked; god mode ignores locks.
- **Enforced** in `PlaceSiteIntent`: "your people do not yet know how to build a Lodge".
- **On the wire:** `ViewDto.LockedKinds`, owner-only. It says what is locked, never what
  unlocks it. The prod client leaves locked kinds out of the build lists and toasts when
  one is learned.
- **The ledger.** Every milestone, when it happens and what it does, is kept in
  `docs/progression-milestones.md`.

## Update 2026-09-24 — rows 5 and 6: the first threat with an address

- **Smoke on the horizon (5):** population 25 → the `CampRumour` effect raises a bandit
  camp (`docs/bandit-camps.md`) in the owner's wildest direction, with an omen of kind
  `Camp` (a search circle; its countdown is to the first raid).
- **The camp burns (6):** `ProgressStat.CampRazed` (Sub = the camp's source milestone) →
  3 captives walk home.
- **Triggered by population, not by a Barracks,** so the camp never punishes arming (the
  proposal's rule). The camp is what makes a player arm; arming 6 then fires the Reprisal;
  beating both leads to the warlord (planned).
