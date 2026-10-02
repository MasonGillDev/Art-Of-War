# Battle sandbox — spec

**Status:** specified 2026-09-30, user decisions confirmed the same day. Phases 0–5 built the
same day (see "Build status" at the end); the client is compile-checked, not yet run in Play.
It replaces the M41 battle test bed: its dev panel (`docs/m41-status.md` Phase 6; client
`Art Of War(prod)/Assets/Scripts/Testbed`), its scenario library and its `.scenario` format.

## The decision

The battle test bed becomes a **sandbox with two modes**:

- **Edit.** You compose a situation in the world. You drag unit types from a palette onto
  tiles, choose each unit's faction, gear and doctrine, give enemy units marches, and set
  the hour and the pace.
- **Play.** The composition runs on the real sim, host, drivers, wire and client UI. It
  is the real game except for two things: **the world is small, and the map has no fog.**

A composition is a **document, not a live world.** While you edit, the host keeps a
**paused real world** built from the composition. So what you see in Edit is what the
game really does with your placements: the per-side cap, castle shelter, subtile seating
and snapping off water. It is not a client-side guess. Play un-pauses that world, and
Back to edit rebuilds it from the document.

**The five hand-written scenarios, the `.scenario` text format and its parser are
scrapped.** You compose every situation yourself in the editor. Compositions are saved
as JSON in a folder git doesn't track.

### Decisions confirmed by the user (2026-09-30)

| # | Question | Decision |
|---|---|---|
| 1 | What drives a non-player kingdom? | **Doctrine only.** No AI brain in the sandbox. Its units follow their doctrine on a board and the marches you give them in Edit. |
| 2 | Wounded units? | **No.** Every unit you drag in is at full health: its role's base plus its gear. There's no health control. The scenarios are scrapped; you compose your own. |
| 3 | Can Blue's opening moves be composed? | **No.** Blue takes orders only in Play, through the game's UI. Marches in Edit are for the other factions only. |
| 4 | Where do compositions live? | **Not committed.** A gitignored folder (see "Storage"). |

## Why

### Composition as a document, not live editing

| Option | Verdict |
|---|---|
| **A. Live editing:** dev routes add and remove units in the running world, mid-game | **Rejected.** It breaks the determinism contract (`docs/architecture.md`): units appear outside any intent, and a run can't be replayed or reset. A battle half-fought and then edited isn't a situation the game can produce. It also adds a second writer of `Unit`, `Unit.Subtile` and board state to the sim. |
| **B. Client-only ghosts:** placements drawn on the client, sent to the host only on Play | **Rejected.** The ghosts would lie. The host moves units: the per-side cap puts extras on the next tile (`TileCapacity.RoomNear`), units on water snap to land, the castle shelters 16, and `Placement.Seat` picks the subtile. You would place a unit and then see it somewhere else in Play. It would also need a second figure renderer. |
| **C. Document plus a host-built preview** (chosen) | Each edit sends the composition to the host. The host rebuilds a paused world from a cached `WorldBuild` (worldgen runs once per seed and size; a rebuild is only the setup step). The client draws that world with the game's own renderers. The same composition always plays out the same way. |

### Play mode is the game, not a test bed

Everything the current test bed adds that a real battle doesn't have is removed from Play:

| Test bed today | In Play |
|---|---|
| Hold at beats, on by default; Next turn | **Gone.** The clock runs and turns run out. |
| Director mode: command both sides | **Gone.** You are seat 0 (Blue). |
| `RevealAll` also shows the enemy's orders and next steps | **Split** (see "Fog, not orders"). The map is revealed and **orders stay hidden.** |
| Camera flies to the battle on its own; Go to battle button | **Gone.** You find the battle like a player does. |
| Dev turn log with exact hit numbers | **Gone** from Play. Kept in Edit as the "last run" report. |
| Daylight pinned at 0.4, only a client look | **Gone.** The world's light cycle opens at the hour you chose, and the sky follows the world's clock as in the game. |
| 15 s turns (4 tps, no landing) | Pace is a composition setting. The default is the **main act's 1 tps** (60 s turns); the prelude's 4 tps is a preset. |
| Scenario Red soldiers on `doctrine=advance` | Every unit starts on `BattleDoctrine.DefaultFor(role)`, as it would in the game. Anything else is set in the inspector. |
| Units placed pre-wounded (`hp=`) | **Gone.** Full health always (decision 2). |
| Scripted moves for both sides | Marches only for the other factions (decision 3). Blue starts standing still. |

The only UI Play adds on top of MainScene is **one "Back to edit" button**. It belongs to
the sandbox; the game's HUD is left alone.

### No AI brain in the sandbox (decision 1)

The sandbox's world is tiny and has no economy, so a Defender or Rival brain would have
almost nothing to run, and it would act differently than in a real kingdom. Doctrine only
is predictable, which is what composing needs. In a real game today the AI brains don't
act on a board anyway (per-driver battle behaviour isn't built, `m41-status.md`), so a
kingdom's units already follow their doctrine defaults. The AI's *world* moves (recalls,
reinforcements, retreating to the castle) are what the sandbox leaves out. You compose
them by hand as marches.

### Fog, not orders

`GameBootstrap.RevealAll` and `?reveal=1` do two jobs today: they lift the map fog, and
they show every unit's orders on a board (`BattlefieldProjection.cs:64`). The sandbox
needs the first without the second, because hidden orders are core to real battles
(`battlefield-grid.md` §5). The switch splits into:

- `revealMap`: fog off, every board visible;
- `revealOrders`: the other side's orders and next steps visible. The Edit inspector may
  use it; Play never does.

This refactor is worth doing even outside the sandbox. One flag with two meanings is the
kind of inconsistency `CLAUDE.md` asks us to flag.

### The hour is the world's light cycle

In the game, time of day is not in the sim at all. The server owns one mapping from tick
to hour (`Sim.Server/Atmosphere/WorldClock`: a light cycle of `Time.Week` ticks, with an
offset saying where tick 0 falls), the client's sky evaluates it from genesis, and nothing
in `Sim.Core` reads it. So the sandbox's hour **is that offset**: the world still starts
at tick 0, and tick 0 falls at the chosen hour, on the game's own cycle length.

- **Rejected: running the empty world forward to the hour.** The first version of this
  spec said so, before the light cycle was found. It would cost a pre-run and change
  nothing the sim reads, and a sim day (`Time.Day`) isn't a sky day anyway.
- **Rejected: pinning the sky on the client** (the old test bed's Daylight dial). The sky
  would disagree with the world's own clock, which the server's narration also reads.
- **When day and night become a mechanic,** `WorldClock` moves to `Sim.Core` (its own
  header says so) and the sandbox's hour keeps working unchanged: it is the same offset.

Because the light cycle is part of genesis, **changing the hour reloads the scene**, like
changing the map.

### JSON, not the `.scenario` text format

The text format existed so that hand-written files were easy to read and diff. With no
hand-written files, it would only be a second encoding of what the editor already sends
the host. One format means one parser and one schema to extend. A saved composition is
exactly the `PUT /v2/dev/composition` body.

## Edit mode

### Layout

```
┌──────────────────────────────────────────────────────────────────────────────────┐
│ [Edit ▣ | ▷ Play]  night-raid ▾  Save  Save as  New    Hour ◷ 22:00   Pace 1 tps ▾ │
├──────────┬─────────────────────────────────────────────────┬─────────────────────┤
│ PALETTE  │                                                 │ INSPECTOR           │
│ Faction  │                                                 │ Soldier #5003       │
│ ● Blue   │              the real world, paused             │ Faction   Red       │
│ ○ Red    │           (game camera, game renderers)         │ Gear      ⚔ 🛡       │
│ ○ Bandits│                                                 │ Doctrine  Hold      │
│          │                                                 │  (default: Soldier) │
│ Soldier  │                                                 │ Withdraw below  —   │
│ Archer   │                                                 │ March → (7,3)       │
│ King     │                                                 │   leaves 22:30      │
│ Farmer … │                                                 │ [Delete]            │
│          │                                                 │                     │
│ Brush ×1 │                                                 │ Last run ▸          │
└──────────┴─────────────────────────────────────────────────┴─────────────────────┘
```

The game's HUD (hotbar, dock, context panel, battle panel) is hidden in Edit, so it
can't take clicks meant for placing. The camera, zoom bands and map mode are the game's.
Panels use the parchment skin (`HudWidgets`, `HudTheme`), like the rest of the prod UI.

### The palette

- **Faction selector** at the top. The choice sticks: every drop takes the selected
  faction. Colours are the factions' own.
- **Unit types** come from the host's role catalogue (`UnitCombatCatalog`: role, base
  health, base power), not a list written into the client. Fighters come first (Soldier,
  Archer, King), then workers (Farmer, Hauler, Builder, Scout, citizen). With the Bandits
  faction selected, the palette offers a **bandit party** (see "Factions").
- **Brush count** ×1 / ×5 / ×10. One drop places that many on the tile, and the host
  spills the extras by the real cap rule.

### Gestures

These follow the prod client's own gestures (the haul job's drag, the build placement
ghost):

| Gesture | Does |
|---|---|
| Drag a palette entry onto a tile | Place it. A tile highlight shows while dragging. It turns red on impassable ground (the host would snap the unit, and the drop says where it went). |
| Click a unit | Select it; the inspector shows it. Shift adds to the selection, and the inspector edits all selected units at once. |
| Drag a placed unit to another tile | Move it. |
| Right-drag from a selected non-Blue unit to a tile | **March order** to that tile, leaving at the inspector's time (default: when Play starts). A dashed line shows the path. It's refused for Blue units (decision 3). |
| Delete / Backspace | Remove the selection. |
| Ctrl+Z / Ctrl+Y | Undo / redo. The undo stack is client-side and holds whole compositions, so an undo just re-sends the previous one. |

Placement is **per tile.** The subtile is the game's choice (`Placement.Seat`), as it is
for any unit the game creates. This keeps M41's rule that nobody is placed on a subtile
by hand.

### The inspector

For the selected unit or units:

- **Faction.**
- **Gear:** only what the role may wear, under the real slot rule. The host checks it
  with `EquipRules.Blocker`, so an impossible loadout can't be composed. (Today's
  `ScenarioHost.Setup` skips this check.) Health follows from the gear: a shield's
  `HealthModifier` is added, as `EquipRules.TryEquip` does.
- **Doctrine:** chosen from the doctrine catalogue (below). The default shows as
  "Hold (default: Soldier)". A value you set shows as set.
- **Doctrine parameters,** only those the doctrine takes, as listed in the catalogue.
  Today that's `Withdraw below` N health.
- **March** (non-Blue only): destination, and when it leaves (game-minutes into Play,
  in 30-minute steps). It becomes a `MoveIntent` at that tick, the same shape
  `ConquerRung` uses (gather at a rally tile, then strike). One unit can have several
  marches, in order; a later one replaces the walk of an earlier one, as in the game.
- **No health control** (decision 2).

For the composition:

- **War with…:** which factions start at war. The default is every faction at war with
  Blue.
- **Last run** (collapsed): the turn log from the most recent Play, kept for reading.

### The top bar

- **Composition** picker: the saved compositions (see "Storage"). **New** starts from an
  empty world on the default map, containing Blue's castle and Red's castle.
- **Save / Save as.** The host writes the file.
- **Hour:** 00:00–23:45 in 15-minute steps, applied with **Set**. It reloads the scene
  (the light cycle is genesis).
- **Pace:** presets for *main act 1 tps* (the default) and *prelude 4 tps*, plus a custom
  value. It is the host's pace (`GameHost.SetPace`), within the host's limits.
- **Map:** size and seed, behind a disclosure. Changing them re-runs worldgen, which is
  the one slow edit.

## Play mode

- **▷ Play** saves nothing. It un-pauses the host world that is already built and hides
  the sandbox panels. The client is then exactly the MainScene client: HUD, battle panel,
  wheel, bubbles, alerts and camera. Blue is yours to command from here on.
- **Back to edit** (one small button, top-left) pauses, rebuilds the world from the
  composition, and shows the editor again. The turn log goes to the inspector's "Last
  run".
- Only two things differ from the game: the map size, and `revealMap`.

## Factions

| Faction | Seat | Driven by |
|---|---|---|
| Blue | 0 | You, in Play, through the game's UI. Its castle anchors the composition. |
| Red (Red 2, Red 3… later) | 1+ | **Doctrine only** (decision 1): its units' doctrine on a board, plus the marches you composed. It gets a castle from worldgen or `FarLand`, as today. |
| Bandits | −1 | The real `BanditDriver` with **natural spawning off**; it takes over the parties you placed. A bandit drop is a `SpawnBanditPartyIntent(tile, size)` at setup, so the party exists the way the game makes one, and the driver raids, flees and loots with it. Its moves come from the driver, so bandit units take no composed marches. |

Bandits are the one faction with a brain, because in the game their world behaviour is
the driver, and bandits without it wouldn't be bandits. This doesn't break decision 1,
which is about kingdoms.

Placing a **King** gives that faction its crown, so the aura is real in the sandbox.
Being housed and fed ("settled", +1 power) needs a House, which is a structure. See
"Future expansion".

## Doctrines are a catalogue

Today `DoctrineBehaviour` is a sim enum, the prod `BattleHud` has four hard-coded
buttons (`BattleHud.cs:89–92`), and `BattleVocabulary` repeats the numbers. A new doctrine
means edits in three places, and the sandbox would make it four.

- **Sim:** a `DoctrineCatalog` next to `DoctrineBehaviour`. Each entry has an id, a name,
  a one-line description, the parameters it takes (for example `WithdrawBelow`), and the
  roles that may use it (for example, a bandit-only doctrine).
- **Wire:** the catalogue goes out once, in genesis.
- **Client:** the game's `BattleHud` doctrine row and the sandbox inspector are both
  built from it.

Adding a doctrine then means writing its rule in `TurnPlanner.Doctrine` and adding one
catalogue entry. It shows up in the game's panel and in the sandbox with no client
changes. This is a **game change, not a sandbox change**, and it comes first.

## Wire, host and storage

### Dev routes

These are served by the sandbox host (`Sim.Server/Sandbox`, which replaces
`Sim.Server/Scenarios`). They are dev-only, as now.

| Route | Does |
|---|---|
| `GET /v2/dev/sandbox` | The state: mode, name, both epochs, tick, the composition, where each piece really landed (`placed`: its sim ids, its tile, whether the host moved it), and Blue's castle tile (the anchor). |
| `PUT /v2/dev/composition` | Replace the composition. The host checks it, rebuilds the paused world and bumps `worldEpoch`. A bad composition is refused (400, `{"error": …}`) with the reason, and the world is left alone. |
| `POST /v2/dev/composition/new` | Start over from an empty composition. |
| `GET /v2/dev/compositions` | The saved compositions' names. |
| `POST /v2/dev/composition/save` | `{name}`: write the current composition to storage. |
| `POST /v2/dev/composition/open` | `{name}`: load a saved one (checked like a PUT). |
| `POST /v2/dev/composition/delete` | `{name}`: delete the file. |
| `POST /v2/dev/play` / `POST /v2/dev/edit` | Un-pause / pause and rebuild. |
| `GET /v2/dev/catalogue` | Placeable roles, the battle gear with the roles that may wear it, the kingdoms, the party and withdraw limits. Doctrines come from genesis. |

- **`worldEpoch`:** a rebuild on the same map keeps genesis the same, so the client
  **must not reload the scene** on every drop. When the epoch changes, it takes the next
  view whole (`Session.ForceNextView`: the tick is 0 again, and a view at the tick it
  already has would otherwise be dropped). **`genesisEpoch`** bumps when the size, seed
  or hour changes; then the client reloads the scene.
- **The host setup step** (`SandboxWorld`), before tick 0:
  - a **King** is a genesis spawn named as the faction's crown
    (`FactionStartSpec.KingUnitId`), the only way a realm gets one;
  - everyone else goes through `Population.OnUnitAdded` (the per-side cap, then
    `Placement.Seat`), with ids from the world's own counter (`GameWorld.TakeUnitId`) and
    the genesis adult age;
  - gear through `EquipRules.Grant`: the storehouse road's rules and buff, with no shelf;
  - bandits as parties of `Bandit`s, adopted by the real `BanditDriver`'s census
    (`MaxLiveParties = 0`: it spawns nothing of its own). `SpawnBanditPartyIntent` itself
    isn't used, because it refuses any tile a player can see, and a composed party is
    usually in sight;
  - doctrine through `SetBattleDoctrineIntent`; marches as `MoveIntent`s at their tick;
  - no health is set, and the combat config is the game's own (not overridden).
- **Checks on PUT:** loadouts pass `EquipRules.Blocker`; marches reference non-Blue
  units only; a bandit party's size is within `BanditConstants.MaxPartySize`.

### The composition (JSON)

Shaped for Unity's `JsonUtility` (no nullables, no nested arrays, no dictionaries), with
the wire's ints for roles (`UnitRole`), items (`Resource`) and doctrines
(`DoctrineBehaviour`; `-1` = the role's default):

```json
{
  "size": 64, "seed": 4242, "hour": 22.0, "pace": 1.0, "castleFacing": 0,
  "wars": [{ "a": 0, "b": 1 }],
  "units": [
    { "id": 1, "faction": 1, "role": 9, "dx": 7, "dy": 0,
      "gear": [5, 7], "doctrine": -1, "withdrawBelow": 0 }
  ],
  "parties": [{ "id": 2, "dx": 9, "dy": 4, "size": 5 }],
  "marches": [{ "unit": 1, "dx": 2, "dy": 0, "after": 0 }]
}
```

Positions are tiles relative to Blue's castle, so a composition survives a seed change.
Ids are the composition's own, stable across edits; the state's `placed` says which sim
units each became. Bandits are everyone's enemy already, so `wars` only pairs kingdoms.

### Storage

Compositions are saved in `sandbox/` at the server repo root, which is **added to
`.gitignore`** (decision 4). The host creates the folder when it first saves. The folder
is the library: drop a JSON file in and it shows up in the picker.

### What is scrapped

- `scenarios/battle/*.scenario` (all five files) and the `scenarios/` folder.
- `Sim.Server/Scenarios/ScenarioFile.cs` (the text format and its parser).
- `ScenarioLibraryTests`, replaced by composition tests built in code (see "Acceptance
  tests").
- The `/v2/dev/scenarios`, `/v2/dev/scenario`, `/v2/dev/reset` and `/v2/dev/clock`
  routes. So are hold-at-beats and step-turn in `GameHost` (`StepTurn`, `HoldAtBeats`,
  `_stepTarget`, `_battlesSeen`), since nothing uses them any more.
- The client's dev panel in `BattleTestbed.cs`, with its director toggle, turn log and
  daylight pin.
- `tools/battle-testbed.ps1` becomes `tools/battle-sandbox.ps1`, with the same job (build
  to its own folder and start the host), minus the scenario argument.
- The prod doc `docs/battle-testbed.md` is rewritten as `docs/battle-sandbox.md` there.

## Build phases

| # | Scope | Size |
|---|---|---|
| 0 | Game changes: split `revealMap` / `revealOrders`; doctrine catalogue on the wire; `BattleHud` built from it | S–M |
| 1 | Host: composition JSON and checks, cached `WorldBuild`, rebuild on PUT, `worldEpoch`, start at the hour, gear via `EquipRules`, the routes, `sandbox/` storage; scrap the scenario code and clock tools | M |
| 2 | Client: Edit/Play modes, top bar, "Back to edit"; Play strips every test-bed extra; session reset on `worldEpoch` | M |
| 3 | Client: palette, drag-to-place, select, move, delete, undo | M |
| 4 | Inspector: gear, doctrine and its parameters, marches (non-Blue), "Last run" | M |
| 5 | Bandit parties (driver with spawning off) and kings | S–M |

Phase 0 doesn't depend on the rest and improves the real game.

## Acceptance tests

- **Same composition, same battle:** two Plays of one composition produce identical turn
  logs (host, headless).
- **Edit shows the truth:** placing 20 Blue on one tile shows 16 there and the rest on
  the nearest tile with room, matching `TileCapacity.RoomNear`.
- **Full health:** every placed unit's health is its role's base health plus its gear's
  `HealthModifier`. A composition has no health field to change that.
- **No impossible loadouts:** a PUT with two swords, or a bow on a soldier, is refused
  with `EquipRules.Blocker`'s reason.
- **Blue can't be scripted:** a PUT with a march on a Blue unit is refused.
- **The hour is the light cycle:** a composition at hour 22 puts tick 0 at phase 22/24 on
  the game's own cycle length.
- **Orders stay hidden in Play:** Blue's view of a board has no Red orders or next steps.
- **Default doctrine by default:** a placed Red soldier with no doctrine set is on `Hold`.
- **Doctrine catalogue drives both UIs:** a test-only catalogue entry shows up in
  genesis. (Checking the client is a Play step, not a unit test.)
- **Bandits are the game's:** a placed party is adopted by `BanditDriver` and moved by it,
  and no party spawns that wasn't placed.
- **A king is the crown:** a composed King is `Royalty.King` of its faction; two are refused.
- **Epochs:** an hour change bumps `genesisEpoch`; a unit change doesn't.

## Future expansion

- **Structures in the palette:** houses (so "settled" is real), walls, gates, towers and
  camps. Same document model: the host places them in the setup step through the game's
  own construction-complete path, not a shortcut.
- **AI controllers:** if a kingdom's world behaviour is ever wanted in the sandbox, add a
  per-faction controller (Defender, Rival) with doctrine-only kept as the default.
  Deferred by decision 1.
- **Branch from here:** pause mid-Play and edit from that moment. The sim already writes
  snapshots, so a composition could start from a snapshot instead of genesis. Deferred:
  it raises the question of which edits are allowed on a half-fought board. It would
  also allow wounded units without a health control.
- **Swap seat:** play Red's side through the real UI (the `Seat` object idea in
  `m41-status.md`). Not director mode: one seat at a time, with orders hidden.
- **More kingdoms and diplomacy:** allies on your side, and peace mid-battle.
- **Terrain brush:** not planned. The world stays generated, because the sandbox tests
  real situations on real ground.
- **Never in Play:** hold at beats and director mode. If a planning aid is wanted, it
  belongs in the game's design, not the sandbox.

## Build status (2026-09-30)

Built in one pass, phases 0–5. Server: the focused `Sim.Tests` filters are green
(`DoctrineCatalogTests`, `SandboxTests`, the battlefield and wire tests), and the host was
driven over HTTP (build, refuse, save, open, play, edit). Client: compile-checked
(`_AowTypeCheck.csproj`), **not run in Play**.

**Server** (`Art-Of-War`):
- Phase 0: `Sim.Core/Battlefields/DoctrineCatalog.cs` (`SetBattleDoctrineIntent` checks
  it); `WorldDto.Doctrines` in genesis; `?reveal=1` and `&orders=1` split
  (`BattlefieldProjection`, `ViewProjector.ProjectV2`, `GameHost.BuildViewV2Json`).
  Tests: `DoctrineCatalogTests`.
- Phases 1 and 5: `Sim.Server/Sandbox/` (`Composition`, `SandboxWorld`, `SandboxHost`);
  `--sandbox [DIR]`; `tools/battle-sandbox.ps1`; `/sandbox/` in `.gitignore`;
  `EquipRules.Grant` and `GameWorld.TakeUnitId` in `Sim.Core`. Tests: `SandboxTests`.
- Scrapped: `scenarios/`, `Sim.Server/Scenarios/`, `ScenarioLibraryTests`,
  `tools/battle-testbed.ps1`, and `GameHost`'s `HoldAtBeats` / `StepTurn`.

**Client** (`Art Of War(prod)`):
- Phase 0: `WorldDto.doctrines` + `DoctrineOptionDto` (with the four built-in ones for an
  older server); `BattleHud`'s doctrine row built from them and filtered by role;
  `GameBootstrap.RevealOrders`; `IServerLink.FetchView(…, revealMap, revealOrders, …)`.
- Phases 2–4: `Testbed/BattleSandbox.cs` (modes, top bar, palette, inspector, gestures,
  marks on the land, last-run log), `Testbed/SandboxLink.cs` (routes and wire),
  `Session.ForceNextView` + `GameBootstrap.RefreshView`, and the menu
  **Window > Aow > Create Battle Sandbox Scene** (`BattleSandbox.unity`; it drops the old
  `BattleTestbed.unity` from Build Settings).
- Scrapped: `Testbed/BattleTestbed.cs`, `Testbed/DevLink.cs`, the old scene menu.

**Editor steps owed:** run **Window > Aow > Create Battle Sandbox Scene**, start
`tools/battle-sandbox.ps1`, press Play. The old `Assets/Scenes/BattleTestbed.unity` can be
deleted (its component no longer exists).

**Rough edges to watch in the first Play:**
- In Edit, the game's UI is hidden with `visibility` and its input components are
  disabled; anything mounted outside the HUD and world-UI documents stays visible.
- After Back to edit, the same sim ids come back at tick 0, so a figure may slide for a
  moment from where it fell back to its composed tile (the renderer interpolates).
- A bandit party that the tile cap splits over two tiles becomes two parties in the
  driver's census.
- Duplication to fold later: the sandbox's ground-line drawing repeats
  `BattleOverlay.Draped` (worth a shared `GroundLines` helper in the client).
