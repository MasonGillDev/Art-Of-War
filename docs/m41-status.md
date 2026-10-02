# M41 status: battlefield grid — the build plan

**Spec:** `docs/battlefield-grid.md` (locked, with every 2026-09-25 amendment).

**What decides:** the spec, and the world as it is built (the sim's systems, its structures, factions, drivers and the combat around them).
- The standalone prototype (`C:\Users\ghgma\combat-prototype`) is only the record of how the rules were found. The user: "the demo was used to find the rules and mechanics. the actual scenarios aren't logical to the world, don't copy them."
- Consult it to understand what a rule means, never as a source of code structure, AI, scenarios, controls or defaults.
- Where the spec is silent, the answer is decided against the world and confirmed with the user.

**Goal:** the grid runs in the real sim and host, and a client **test-bed scene** loads small battle situations taken from the real game, in the real rendered world. It exists to build the battle UI/UX and play situations out without playing the full game.

**Status:** built 2026-09-25 through Phase 6, the same day it was planned (see "What was built" at the bottom). The sim and host are tested; the client is compile-checked and **not yet run in Play**.

**Baseline:** 1,344 + 55 tests (the landing's update in `docs/two-act-pacing.md`). Verify before Phase 1.

**Rules:**
- No commits unless asked.
- `dotnet test` green after every phase.
- Balance numbers are config knobs, and tests derive from them.
- Client edits go to the prod client (`Art Of War(prod)`).

| Phase | Scope | Size | Status |
|---|---|---|---|
| 0 | Decisions | S | **done** (confirmed 2026-09-25) |
| 1 | The turn resolver in `Sim.Core`, built from the spec | M | **built**, 42 tests |
| 2 | The wire contract and the scenario format, on paper | S | **built** in code (no separate paper step) |
| 3 | The grid in the world, behind `--combat grid` | L | **built**, 11 world tests, snapshot v41 |
| 4 | The host: projection, battle behaviour per driver, scenario mode, dev routes | L | **built**, except per-driver battle behaviour (see below) |
| 5 | The client: battle presentation and the planning UI | L | **built**, compile-checked, not run in Play |
| 6 | The battle test-bed scene | M | **built**, compile-checked, not run in Play |

Phases 1 → 3 → 4 run in order. **Phase 5 can start as soon as Phase 2 is written**, in parallel with 3 and 4; it only needs Phase 4 to go live. M42 (below) takes the grid into the full game.

---

## Strategy

1. **The spec and the world decide.** Every rule is built in the sim's own idiom:
   - units, intents and events;
   - anchors and fencing tokens;
   - `Unit.Buffs` and `EffectivePower`;
   - the drivers.

   Every choice the spec leaves open is decided against the world. Demo conveniences that do **not** carry over:
   - quiet tiles walking subtile by subtile (the world walks tile hops, and subtiles exist only on an open board);
   - units placed straight onto subtiles;
   - timed "waves" appearing at board edges (the world's reinforcements walk in from real tiles);
   - a looting counter on a crate subtile;
   - a generic nearest-enemy AI;
   - its scenario files, its controls, its world size.
2. **A pure resolver first.** The turn resolution (leaving, moves, collisions, clashes, duels, morale, damage) is a pure function, the "pure-read wall" pattern of `docs/architecture.md`. Its inputs are read from real units: role stats, `EffectivePower` with equipment, the fed house and the king's aura. It can be tested exhaustively before any world integration.
3. **A launch switch.** `--combat pooled|grid` sets `CombatConfig.Model` at genesis, and it is snapshotted. The default stays `pooled` until M42 flips it, the same pattern as `--fertility` and `--landing-day`.
   - The existing suite stays green while the grid grows.
   - The ~79 pooled-combat tests migrate in M42.
4. **The wire contract is written early** (Phase 2), so the client work can run in parallel with the sim integration.
5. **The test bed runs the real host,** in a `--scenario` mode that builds a small world from a file of **real game situations**. It runs the real sim, drivers, projector and wire. Battles open the way they open in the game: by units arriving.

---

## Phase 0 — decisions (confirmed by the user, 2026-09-25)

Each is a question about the world, not about the demo. The user confirmed every default, with two changes (D2, D3). The same decisions are recorded in the spec (`docs/battlefield-grid.md`, "build decisions").

| # | Question | Decision | Needed by |
|---|---|---|---|
| D1 | Overflow when a board opens: a castle tile can hold 25–30 of one side, and the board has 16 subtiles | The rest **shelter off the board** (in the castle, on a castle tile): safe, unable to act. They come on like arrivals when a lane frees | Phase 3 |
| D2 | Where units already on the tile stand when a board opens | **Centre rows for now.** A confirmed requirement for later: "the player needs to be able to form formations before the tile becomes a battle field." So opening placement reads positions from a **formation source**. v1's source is the centre-rows default, and the player's pre-battle formation plugs in later without touching the rules | Phase 3 |
| D3 | Board terrain from the world (§6) | **The board stays open in v1.** Every subtile is a standard tile of the tile's biome, with no effect on movement or combat: no blocked subtiles, no forest cover, no river fords. But the model is built so a subtile **can be occupied by different things** (terrain, obstacles, cover, structures), a per-subtile layer that later contents fill in. Walls and gates stay whole world tiles | Phase 3 |
| D4 | Sieges | The board decides who holds the tile. When it closes with only attackers left, today's siege rounds (M24) run unchanged. `FortSiege` adjacency sieges are untouched | Phase 3 |
| D5 | What you see of the enemy on a board you can see | Positions, HP and morale: yes. Orders and paths: never | Phase 2 |
| D6 | Default doctrine by role | Soldiers Hold, archers Support, bandits Advance and withdraw at a threshold (the party's existing flee), **non-combatants Withdraw** by the edge away from the enemy | Phase 3 |
| D7 | Looting a contested tile | No, as today: bandits steal only where no board is open, so looting starts when the board closes. Whether structures stand on the board is M42's question, decided against the world | Phase 4 |
| D8 | The king on a board | v1 keeps today's world-tile aura through the modifier seam. The spec's "R subtiles" aura is later | Phase 3 |
| D9 | Patrol pursuit (M29) meeting a board | Pursuit ends when the quarry is on an open board, and the pursuer enters it with doctrine Advance | Phase 3 |
| D10 | What a unit is on screen | One figure per unit, as the client draws today. Battalion formations (§10) can come later | Phase 5 |

---

## Phase 1 — the turn resolver (`Sim.Core/Battlefields/`)

**Goal:** given a board and each unit's step for the turn, produce the next board, exactly as spec §3 and §5 say.

**Work:**
- **The model:**
  - `Subtile` (0..3 within a tile, plus edges);
  - **the subtile layer** (`SubtileContents`): what occupies each subtile besides units. In v1 every subtile holds only the tile's biome, with no effect (D3). The resolver asks the layer about passability and cover, so later contents change no rule code;
  - a board snapshot built from real units: id, owner, role, HP, damage from `EffectivePower`, whether it is ranged, and the subtile it last moved from.
- **Orders:** `BattleOrder`, one of Hold, MoveTo, Route, Swap, Withdraw, Stop, plus the entry lane. `BattlePathing` is orthogonal, ties N, E, S, W.
- **`TurnResolver.Resolve(board, steps) → TurnResult`**, following §5:
  1. leaving;
  2. moves, by the collision table, iterated until nothing changes;
  3. duels, and clashes;
  4. morale (the line);
  5. damage: duels, clashes, archers (weakest adjacent enemy, same board only; cover comes from the subtile layer, and there is none in v1), applied simultaneously;
  6. deaths.

  Plus "no passing through", and a failure reason per unit.
- **`BattleDoctrine.Plan`**: Hold, Advance, Support, Withdraw-at-threshold, from the positions at the start of the turn.
- **`BattleConfig`:** `LineSupport` 25, the half-turn first-turn delay, and the withdraw threshold. It needs explicit parameterless-constructor defaults (the record-struct trap: `new()` must not zero them). `RoundTicks` stays `CombatConfig.RoundIntervalTicks`.

**Tests** (`tests/Sim.Tests/Battlefields/`):
- **From the spec:** one per collision-table row, clashes, no passing through, relief, stepping out, morale, archer reach, doctrines, routes. Plus a test that the subtile layer is consulted: a test-only occupant that blocks or covers one subtile changes the outcome.
- **Values** from `UnitCombatCatalog`, `EquipmentCatalog` and `BattleConfig`, never hard-coded.

**Exit:** all green; the full suite unchanged.

---

## Phase 2 — the wire contract and the scenario format (paper)

Written into this doc as a Phase 2 section, reviewed, then frozen for Phases 4–5.

- **`BattlefieldDto`**, in the per-tick view:
  - tile, turn number, next beat tick, suspended;
  - the subtile layer (all plain in v1, D3);
  - occupants: id, owner, role, subtile, HP, max HP, morale, in duel;
  - held arrivals by edge;
  - sheltered units (D1).
- **Your side only:** each unit's standing order and route, the step it will try next beat, and why its last step failed.
- **`LastTurn`**: the events of the turn just resolved, for playback:
  - moves (from, to);
  - failed moves with reasons;
  - clashes;
  - archer shots (from, to, damage);
  - duel damage;
  - deaths;
  - units leaving and arriving.
- **Fog:** only boards a player can see (D5). Enemy orders and paths are never sent.
- **Intents:**
  - `SetBattleOrderIntent(unitId, order)`, covering Hold, MoveTo, Route, Swap, Withdraw, Stop and EntryLane;
  - `SetBattleDoctrineIntent(unitId, doctrine)`.

  Intent JSON is PascalCase and view JSON camelCase, per the wire rules.
- **The scenario file describes a situation in the world,** not a board. What it can state:
  - The ground: a small world (12×12 tiles by default) of biomes and river edges, written the way worldgen writes them. Or `generated seed=… size=… at=x,y` to stage the situation on a real generated map.
  - Structures: castle, houses, farms, stockpiles, barracks, walls and gates, bandit camps.
  - Factions: player seats and bandits, and who is at war (bandits always are).
  - Units, by tile: role, equipment, HP, doctrine, group.
  - Standing world orders: a move to a tile, a patrol, a haul route.
  - Real scheduled events, such as an omen raid due at a tick with its bearing, or a bandit party in the dark.
  - Which factions an AI drives.

  There are no subtile placements: every battle opens by the real rules.
- **Dev routes,** on only with `--scenario`:
  - `GET /v2/dev/scenarios`;
  - `POST /v2/dev/scenario` (load);
  - `POST /v2/dev/reset`;
  - `POST /v2/dev/clock` (run, pause, **step one turn**, **hold at every beat**, speed);
  - `GET/POST /v2/dev/replay` (save the order log, or load it and verify the digest);
  - `POST /v2/dev/spawn`;
  - a `?director=1` view flag that adds every side's orders.

---

## Phase 3 — the grid in the world (`Sim.Core`, behind `--combat grid`)

**Work:**
- **`CombatConfig.Model`** (Pooled, Grid), set at genesis. Snapshot **v41** (it is 40 today).
- **Per tile: `GameWorld.Battlefields`,** holding the turn anchor (next beat tick and Seq), a suspended flag, and the sheltered units. The subtile layer is derived from the tile, so it is not stored.
- **Per unit:**
  - subtile;
  - standing order;
  - committed path and route flag;
  - held arrival;
  - `EnteredFrom`;
  - last subtile moved from;
  - busy-until tick;
  - doctrine.
- **Opening:** in `CombatTrigger.MaybeBeginCombatOnTile`, when the model is Grid. Placement follows spec §2 and D1–D2, through the **formation source** (v1: centre rows). The first turn is set on the first beat at least half a turn away.
- **`BattlefieldTurnEvent` on the beat** (ticks divisible by `RoundTicks`, the same for every board):
  1. doctrine fills steps for units without orders;
  2. the resolver runs;
  3. results go through existing primitives: health, then deaths through `CombatRules.OnUnitDeath` → `Population.OnUnitRemoved` (cargo piles, groups, the crown, omens, camps, goals);
  4. the board closes, reschedules, or suspends.
- **Movement:**
  - Arrivals onto an open board are held on their source tile until the beat (`MoveArrivalEvent`, `GroupArrivalEvent`).
  - Hops into an open board skip the crowding bands.
  - Leaving by any edge starts a world hop with a busy-until tick.
  - On close, routes continue. That means keeping the world path through the battle: in Grid mode `PinBelligerents` no longer clears it, or cancels a leaving hop.
- **Intents:** `SetBattleOrderIntent` and `SetBattleDoctrineIntent`. They validate own unit, an open board or held arrival, route contiguity and no blocked subtiles.
- **Hand-offs:** sieges (D4), patrol pursuit (D9), groups (members act alone, and the group resumes on close; spec §8).
- **Persistence:** snapshot write and read for boards and unit fields; `RegenerateQueue` rebuilds turn events from their anchors.

**Tests:**
- The spec's §12 list where it touches the world: the beat, held arrivals, leaving and the hop, suspend and resume, overflow and shelter, the sieges hand-off, groups, pursuit.
- **The headline determinism test** (architecture §6): a twin run of a full grid battle, and snapshot/restore mid-battle with duels, standing orders, held arrivals and a suspended board.

**Also:** a `docs/determinism-audit.md` addendum (who writes each new field).

**Exit:** full suite green with Pooled as the default; every grid test green.

---

## Phase 4 — the host (`Sim.Server`)

**Work:**
- **Projection:** `ViewProjector.FillBattlefields` builds the Phase 2 DTOs and `LastTurn`: fog-gated, own orders only, with the director flag in scenario mode. `IntentJson` gets the new intents.
- **Battle behaviour, per driver, from its goal in the world.** It is built on doctrine, and the spec's rule holds: "no special combat code for AI factions". A driver chooses its units' doctrine and, at most once a turn, their steps:
  - **Bandit raid parties, omen raiders, the landing host:** Advance on the defenders. Withdraw at the party's flee threshold, or when carrying loot, toward darkness, as today's `BanditDriver` flees.
  - **Camp garrisons (M39):** Hold at the camp.
  - **AI kingdoms:** defenders Hold at the seat and extractors with archers in Support; a Rival's campaign Advances; each rung's existing retreat rules become the withdraw threshold.
  - **Patrols (M29):** Advance on engage, withdraw past the leash.
  - **Haulers and workers:** Withdraw (D6).

  Two server behaviours must change:
  - The bandit driver's "no LoadCargo on a tile in CombatStates" becomes "no board open here".
  - `DefendRung` and `WarRung` power estimates assume pooled combat. In Grid mode they read a conservative estimate until M42 re-tunes them.
- **Scenario mode:** `--scenario <file>` (and `--combat`).
  - `ScenarioFile` parses the format.
  - `ScenarioWorld` builds a `WorldBuild`: a `GeneratedMap` (biomes, rivers) and an elevation that matches it, because the client's terrain comes from the map. On top of that, a `GenesisSpec` holds the factions and units.
  - A deterministic setup step before tick 0 adds what `GenesisSpec` can't express: equipment, HP, wars, structures, scheduled events.
  - `GameHost` is swapped in-process when a scenario loads.
- **Dev routes** (Phase 2). "Hold at every beat" pauses the host clock at each beat until the client says go. It is a host pace rule, so the sim never knows.
- **A headless runner:** `dotnet run --project src/Sim.Host -- --scenario <file> [--ticks N] [--save-log f] [--replay f]` prints each turn and a digest. It follows `Sim.Host`'s existing console demos (`--siege`, `--groups`) and is the milestone's smoke run.
- **The scenario library** (`scenarios/` at the repo root): situations the game really produces, each one exercising real systems:

  | Situation | What it tests |
  |---|---|
  | **A bandit party hits a worked farm.** Farmers on the farm tile, a small guard two tiles away at the castle | civilians withdrawing, a guard arriving through a lane, bandits fleeing |
  | **The reprisal** (M37). An omen raid marching on the seat from its bearing, the townsfolk at the castle | deployment when the board opens, overflow and shelter, the telegraph giving time to prepare |
  | **Day X, the landing.** The host reaching the castle from two neighbouring tiles (ordered by hand in director mode; AI splitting is M42) | two fronts, a full board, morale on a crowded tile |
  | **A caravan ambush.** Haulers with an escort on a road, bandits lurking in a forest tile | haulers on a board, cargo dropping to piles, an escort's line |
  | **A river crossing.** A Rival's army attacking across a river edge into a held tile | an army crossing a river edge at world pace into a held tile (the board itself stays open in v1: D3) |
  | **The gate.** A walled castle whose only way in is its gate tile | walls as whole tiles, a chokepoint on the map, sieges from the next tile unchanged |
  | **Burning the camp** (M39). An army attacking a camp and its garrison | the siege hand-off, the camp razed, the captives' arrival |
  | **Relief of an outpost.** Two soldiers at a mine under attack, help marching from the castle three tiles away | held arrivals, relief swaps, going round a battle vs through it |
  | **A patrol meets bandits** (M29) | pursuit handing over to the board, the leash as withdrawal |
  | **The king at the front** | the aura on a board, succession if he falls (M31) |
  | **A battle seen through fog.** A scout next door watching two others fight | what a board shows to a third player, hidden orders |

**Tests:**
- wire tests: a hidden order never reaches another side's view, and `LastTurn` is complete;
- a `ScenarioFile` round trip;
- each situation runs headless to an outcome derived from config values;
- replay-digest equality.

---

## Phase 5 — the client: presentation and planning (the game's real UI)

Everything here is the game's battle UI; the test bed only hosts it. **The UX is designed in the test bed on the prod client's own conventions**, guided by spec §10: the wheel (`VerbCatalogue`), the bubble, drag gestures like the haul job's, the hotbar. Nothing is copied from the demo's controls.

**Work:**
- **Wire mirror** (`Aow.Wire`): `BattlefieldDto`, the order payload classes, `SimVocabulary` for orders and doctrines, `IntentFactory` entries, `KnownWorld` accessors.
- **`BattlefieldOverlay`:** a faint 4×4 grid on each open board, with the terrain shown per subtile.
- **Units on a board are placed by subtile,** not by tile or hop. `EntityRenderer` takes positions from the board. A duel puts both figures in one subtile, facing each other.
- **Marks:** the duel, morale, HP for both sides on a visible board (D5), clashes, archer shots. They extend `CombatMarks` rather than replacing it.
- **`TurnPlayback`:** when a new `LastTurn` arrives, animate it over a couple of seconds: moves, then strikes and arrows, then casualties. Never teleport.
- **A battle planner in the Input layer,** on the Scouting slice's pattern (a planner class, an `OrderIssuer` partial, a `VerbCatalogue` partial, WorldUi wiring). It covers the spec's orders:
  - move;
  - a traced route;
  - swap (relief);
  - withdraw, hold, stop;
  - doctrine;
  - the entry lane.

  Planning shows order arrows, ghost positions, the step each unit will try, and why its last one failed. The gestures and keys are decided in the test bed.
- **HUD:** one beat countdown for all your battles, and a battles list. **Split `Hud.cs` into partials first** (see Refactors).
- **Fight poses:** `FigurePose` has none. Add attack, shoot, hit and die, with a procedural fallback. Mixamo clips are the user's asset step.
- **Director mode:** with the view's director flag on, the planner may order any unit. Intents carry that unit's owner as `PlayerId`, since the host has no per-seat authentication. It is test-bed-only, and needs no seat switching.

**Exit:** `_AowTypeCheck` and `_AowWireBoundary` clean. Play-checked in Phase 6.

---

## Phase 6 — the battle test-bed scene

**Work:**
- **`Window/Aow/Create Battle Testbed Scene`**, on the `CreateBiomeTestbedScene.cs` pattern. It builds `BattleTestbed.unity` from MainScene's live stack:
  - Game: `GameBootstrap` with a Live link, the scenario host's URL, and `PlayerId` 0 (MainScene's is 5, an AI seat);
  - World renderers, Atmosphere, Rig, and the loading screen;
  - a `BattleTestbed` component.

  Following the teach-don't-sidestep rule, the few editor steps left are listed in the doc.
- **The `BattleTestbed` panel** (UI Toolkit):
  - scenario list, load and reset;
  - clock: pause, step one turn, hold at every beat, speed;
  - who you play: director (both sides), or one seat against the AI;
  - reveal enemy orders (debug);
  - spawn a unit;
  - save, load and verify a replay;
  - a turn log (the text of each `LastTurn`).
- **Loading a scenario** swaps the host's world. The panel then reloads the scene, so the client fetches the new genesis. On a 12×12 world the relief bake and chunk build take seconds.
- **The camera** frames the situation on the first tick.
- **`tools/testbed.ps1 <scenario>`** starts the scenario host on its own port.
- **Prod doc** `docs/battle-testbed.md`.

**Exit (the user's check in Play):**
- load every situation;
- play one against the AI and one as director;
- replay it and see the same digest;
- F9 perf capture during a busy turn.

---

## Reused, not rebuilt

- **Sim:**
  - `MaybeBeginCombatOnTile` (the opening hook);
  - the anchor and `RegenerateQueue` pattern of `CombatState` (the turn event);
  - `CombatRules.OnUnitDeath` (every death);
  - `Unit.Buffs` and `EffectivePower` (sword, bow, shield, the fed house, the king's aura);
  - the bandit driver's modes (raid, flee, ambush);
  - omens, camps, patrols;
  - `GenesisSpec` and `Genesis.Build` (scenario worlds);
  - `WorldFactory.Build` (staging on a real map);
  - `/v2/pace` and `?reveal=1`.
- **Client:**
  - `GameBootstrap`, `Session` and `HttpServerLink`;
  - the MainScene stack and the relief cache;
  - `CombatMarks`, `ToolKit` and `WeaponSet`;
  - `UnitFigures` clips;
  - the planner pattern;
  - `UiMount` and `UiHold`;
  - the `CreateBiomeTestbedScene` editor pattern.

## Out of scope (M42 and later)

- **Flipping the default to Grid,** and migrating the pooled-combat tests. About 79 tests in 16 files use the rounds directly; 44 files touch the combat APIs.
- **AI on the map:**
  - bandits and rivals choosing several approach tiles (day X depends on it: spec, "holes", 7);
  - power estimates that know about boards.
- **Day X in play:**
  - sizing the day-X raid on the board;
  - `MainScene` getting the battle UI.
- **Rules:**
  - **a formation set before the battle** (a confirmed requirement, D2): the intent, the tile's standing formation, and its UI;
  - **terrain on the board** (D3): forest cover, blocked subtiles, river fords, then structures as occupants;
  - structures on the board and whatever they mean for looting and sieges (D7);
  - high ground, and archers aiming at a subtile;
  - fog on the board;
  - morale sources beyond the line;
  - healing at home or the Barracks.
- **Presentation and play:**
  - battalion formations;
  - runtime seat switching (see Refactors);
  - the prelude turn length (§11 question 10). A scenario can override `RoundTicks` to try 120 meanwhile.

## Verification

- **Each phase:** `dotnet test` on the whole solution, built with `-p:OutDir=<scratch>` if a server is running.
- **Phase 1:** a spec-derived test per rule, with values from config.
- **Phase 3:** the headline determinism test; snapshot/restore mid-battle; the determinism-audit addendum.
- **Phase 4:** every situation runs headless to its expected outcome; replay digests match.
- **Phase 5:** both client check projects clean.
- **Phase 6:** the user's Play checklist above.

## Refactors worth doing on the way

- **A shared test world builder.** Tests have none: each file has a private `MakeSpec()`, and there are 775 direct `AddUnit`/`AddStructure` calls across 114 files. `ScenarioFile` plus `ScenarioWorld` can double as it, since a situation in a string is a readable fixture.
- **`Hud.cs` into partials.** At 795 lines it is the one UI class that isn't partial, while Dock, OrderIssuer, VerbCatalogue, Bubble and WorldUi all are. Split it before adding the battle HUD.
- **A `Seat` object.** The player id is passed by value into eight or more components at `Initialise`, and `Session._playerId` is readonly. A shared seat with a changed event would let the test bed, and later the game, switch sides without a scene reload. Not needed for M41: director mode covers it.

## Risks

- **Demo drift:** taking a prototype convenience for a rule. The spec and the world decide; anything the spec leaves open goes to the user.
- **Held arrivals and routes that continue** touch world movement, which the whole game depends on. They are behind the switch, with twin-run tests.
- **No authentication on the host.** Director mode relies on it. It is dev-only, and scenario mode must never be the production default.
- **Scene reload per scenario** is fine on a tiny world. If staging on a generated map is slow, the `Seat` refactor allows an in-place reload later.

---

## What was built (2026-09-25)

The user asked for a playable test scene by the time they got back, so every phase was built
in one pass. **Nothing is committed.**

**Tests:**
- Full suite green at the end: **1,416 + 56, 0 failed** (from 1,344 + 55 before M41).
- New: `TurnResolverTests` 25, `TurnPlannerTests` 17 (with `DoctrinePathingTests`),
  `BattlefieldWorldTests` 11, `ScenarioLibraryTests` 7, and `IntentJsonTests.M41_…` (the
  client's exact order bytes).
- The host was also driven over HTTP by a script, the way the client does it:
  - a battle opened at tick 210 and the clock held;
  - an order in the client's JSON was accepted and followed;
  - duels, archer fire, morale and a death all resolved on the beats.

**Sim** (`src/Sim.Core/Battlefields/`):
- The pure model: `Subtile`, `SubtileLayer` (the per-subtile slot, all plain in v1),
  `BoardState`, `BattlePathing`.
- Orders and doctrine: `BattleOrder`, `BattleDoctrine`, `BattleConfig`.
- The turn: `TurnPlanner` (orders and doctrine to steps) and `TurnResolver` (leaving,
  moves by the collision table, clashes, duels, morale, damage, deaths).
- The world: `Battlefields` (open / admit / turn / leave / close) and
  `BattlefieldTurnEvent` (on the beat).
- Intents: `SetBattleOrderIntent`, `SetBattleDoctrineIntent`.
- Hooks:
  - `CombatTrigger`: the Grid branch, plus a siege-only start for D4;
  - `MoveArrivalEvent` and `GroupArrivalEvent`: `EnteredFrom`, and stopping on a board with
    the route kept;
  - `MoveIntent.BeginMove`: a world move given in battle becomes a board order to that edge;
  - `CombatConfig.Model` and `LineSupport`;
  - `GenesisSpec.StartingWars`;
  - snapshot v41 (`WriteBattlefields`) and `RegenerateQueue`.

**Host** (`src/Sim.Server/`):
- `BattlefieldProjection` and `Wire/BattlefieldWire.cs`: fog-gated boards, own orders only,
  every side's orders under `?reveal=1`.
- `--combat pooled|grid` and `--scenario`.
- `Scenarios/ScenarioFile` and `ScenarioHost`: a small real generated world per scenario,
  and the dev routes (scenarios, load, reset, and the clock's step, resume, pause, hold and
  free).
- `GameHost`: a setup hook, `StepTurn`, `HoldAtBeats`.
- `HttpApi`: a swappable host and the dev handler.
- `tools/battle-testbed.ps1` starts it all.
- Five scenarios in `scenarios/battle/`.

**Client** (prod, `Art Of War(prod)`; docs `battlefields.md`, `battle-testbed.md`):
- `Client/Battle/`: `BattleBoards`, `BattleOverlay`, `BattleInput`, `BattleHud`.
- Hooks in `UnitMotion`, `UnitFigures` and `GameBootstrap`.
- The wire mirror, and the `IntentFactory` entries.
- **Test-bed-only** code in `Assets/Scripts/Testbed` (its own assembly, `Aow.Testbed`) and
  `Testbed/Editor` (the menu that copies MainScene).

**Calls made while building** (each is marked in the code):
- **A unit arriving on an open board** completes its world hop (it is on the tile) but
  waits off the board, outside its lane, until the beat. It is not held on the tile it came
  from. This keeps every arrival's bookkeeping (roads, sight, scouting) on the one arrival
  path.
- **Doctrine walks round its own line** when there is a way round; orders keep the spec's
  plain rule (units ignored). Found in the live test: a supporting archer kept walking into
  the back of its own soldiers.
- **A moving group halts as a body** when its members join a board. Members fight as
  individuals, and the group does not resume on close; the player re-orders it (a gap
  against spec §8).
- **Sheltered units** (D1) come on at the start of a turn into free subtiles, centre rows
  first.
- **Director mode reuses `?reveal=1`** rather than a separate `?director=1`.
- **Looting on an open board is refused in the sim** (D7): `LoadCargoIntent` rejects a unit
  standing on a battlefield, and the bandit driver no longer tries.
- **On screen, board units face the fight** (their foe, next step or nearest enemy) instead of
  a resting angle, and duelling fighters use the melee swing clip (`FigurePose.Chop`) until
  proper fight clips exist.

**Not built yet, and to watch:**
- **Per-driver battle behaviour** (Phase 4): the bandit driver and the AI rungs don't plan on
  boards. In a Grid world their units follow doctrine defaults. Their world moves become
  leave orders on a board, and their pooled power estimates are unchanged. The Grid is off
  by default, so no running game is affected.
- **Hops into an open board still pay crowding bands** (carry-over 3 of the spec).
- **`CombatDto` still describes a board tile as a pooled combat** for the old clash marks.
  Harmless: the Grid is opt-in.
- **Not run in Play:** the whole client half. The user's checklist is in the Phase 6 section.

---

## Update 2026-09-29 — milestone numbering

"M42" above (flip the default to grid, migrate the tests, the AI) is now **M43**. The new
**M42 is subtile movement** (`docs/subtile-movement.md`, `docs/m42-status.md`): every unit
always stands on a subtile. The flip stands on it.

## Update 2026-09-29 — M42 replaced seating, shelter and the outside lane

`docs/subtile-movement.md` (M42 phase 4) changes what this doc built. A board now reads the
subtiles units already stand on (`Unit.Subtile`):
- **Seating at open is gone.** Everyone on the tile joins where they stand: arrivals no longer
  deploy on the edge row, and units already present no longer go to the centre rows.
- **Overflow shelter (D1) is gone.** A unit with no room has no subtile, and is not on the
  board: it can neither act nor be hit. The per-side cap (`docs/structure-footprints.md`) keeps
  that to test worlds that pile units up.
- **The "outside lane" waiting position is gone.** A hop onto an open board is put off to the
  next beat; the unit waits on its source subtile, then lands on an entry lane and joins.
- **`BoardSlot`** holds only the order, the subtile last moved from, and the last step note. The
  wire keeps `Waiting` and `Sheltered` (always false) so the client contract is unchanged.
- **Battle routes:** a subtile route a unit is walking when a battle opens, or is given on an
  open board, becomes its battle route, walked one subtile a turn.

## Update 2026-09-30

The Phase 6 battle test bed is replaced by the **battle sandbox** (`docs/battle-sandbox.md`):
an Edit mode where you compose a situation in the real world (palette, inspector, marches,
the hour, the pace) and a Play mode that is the real game but for the map's size and fog.
Scrapped with it: the five `scenarios/battle/*.scenario` files and their format
(`Sim.Server/Scenarios`), `ScenarioLibraryTests`, `tools/battle-testbed.ps1`, `GameHost`'s
hold-at-beats and step-turn clock tools, and the client's director mode (the reveal switch is
now split: `?reveal=1` lifts the fog, `&orders=1` shows the other side's orders). The test
bed's reasons for those crutches are recorded above; the user chose to play battles on the
game's real clock and information instead.
