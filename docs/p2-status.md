# P2 status — fortification visibility

**Package:** P2 from `docs/prod-client-gap-discovery.md` §6. Started 2026-09-20.
**Goal:** a player can see a wall line, a gate, a tower and a ruin; can read a structure's
health; and can tell a siege is happening and how it is going. Sim design is fixed in
`docs/walls-and-gates.md` and `docs/sieges-and-conquest.md`; nothing here changes the sim.

## Ownership (one seam per agent)

| Agent | Owns | P2 tasks |
|---|---|---|
| Wire projector | `src/Sim.Server/Wire`, `ViewProjector.cs`, `tests/Sim.Tests/WireV2Tests.cs` | T1, T2, T3 |
| Client contract | `Assets/Scripts/Wire`, `Net`, `Client/KnownWorld.cs` | T4 |
| World renderer | `Client/Presentation`, `Client/World`, `Client/Dressing` | T5, T6, T7 |
| Art and assets | `Settings/**`, `Editor/Setup*`, prefab choice | T8 |
| UI/UX | `Client/Hud`, `Client/WorldUi`, `Client/Input` | T9, T10 |
| Verification | tests, type-checks, live run | T11 |

Wire rules: additive on v2 only, v1 untouched, one `WireV2Tests` case per new field, own-player
gating decided per field below. Client DTO names camelCase and identical to the server's.

## Tasks

| # | Task | Owner | Status |
|---|---|---|---|
| T1 | **Structure health on the view.** `StructDto.Health` and `MaxHealth` (from `Structure.Health` and `StructureCatalog.Spec(kind).BaseHealth`). Visibility rule: own structures always; **any visible fortification** (Wall/Gate/Tower/Castle) too, since a besieger must see what they are breaching. Other enemy kinds stay hidden (-1). Kinds with BaseHealth 0 (Cache/Canal/Rubble) send 0/0. | projector | done 2026-09-20 — `StructDto.Health/MaxHealth` (default -1/-1), filled by `ViewProjector.FillHealth` on both the reveal and fogged paths; fortification set = Wall/Gate/Tower/Castle by kind (broader than `IsStandingFortification`, which excludes Tower/Castle). Test: `StructureHealth_OwnAlways_FortificationsPublic_OtherEnemyKindsPrivate`. |
| T2 | **Wall and gate semantics on genesis.** `BuildOptionDto.BlocksMovement` and `AlliedPassage` from `StructureSpec` (`StructureSpec.cs:96-102`). Plus `BaseHealth` on `BuildOptionDto` so the build menu can say "500 HP". | projector | done 2026-09-20 — `BuildOptionDto.BlocksMovement/AlliedPassage/BaseHealth`. Test: `BuildCatalog_CarriesFortificationSemanticsAndBaseHealth`. |
| T3 | **Siege state on the combat row.** `CombatState` retains only `Tile/RoundNumber/NextRoundTick` (`Combat/CombatState.cs:14-22`), so nothing about the last round survives to project. Send what is derivable at projection time with a pure read: `CombatDto.FortKind` (the standing fortification kind on the tile, 0 if the combat is not a siege), `Besiegers` (count of non-bandit units hostile to the fort owner on the tile + 4-neighbours) and `SiegePower` (their summed `CombatRules.EffectivePower`, i.e. the damage the NEXT round will deal — the same formula as `FortSiege.cs:78-85`). Own-side and visible-side both get it (the fight is on a visible tile by construction). No Sim.Core change. | projector | done 2026-09-20 — `CombatDto.FortKind/Besiegers/SiegePower`, filled by `ViewProjector.ToCombatDto` (mirrors the `FortSiege.TryResolveFortRound` sum; a change there must be echoed). Field battles carry 0/0/0. Test: `CombatRow_CarriesSiegeState_ForAFortAndZerosForAFieldBattle` (both sides see the same numbers). **Contract seam:** mirror `health`, `maxHealth` on `StructDto`; `blocksMovement`, `alliedPassage`, `baseHealth` on `BuildOptionDto`; `fortKind`, `besiegers`, `siegePower` on `CombatDto`. |
| T4 | Mirror T1–T3 on the client DTOs; expose `KnownWorld.HealthAt(x,y)` and `SiegeAt(x,y)`. | contract | done 2026-09-20 — `StructDto.health/maxHealth` (+ `HasHealth`), `BuildOptionDto.blocksMovement/alliedPassage/baseHealth`, `CombatDto.fortKind/besiegers/siegePower` (+ `IsSiege`). `KnownWorld`: new per-tile `StructureAt`, `HealthAt` → `(health, maxHealth)?` (null when absent or private), `SiegeAt` → the `CombatDto` or null for a field battle. |
| T5 | **Render walls and gates.** Remove the skip at `Presentation/EntityRenderer.cs:292`. A wall tile draws a segment set that joins to 4-adjacent wall/gate tiles of the same owner (straight, corner, T, cross, end cap); a gate draws the door piece oriented along its wall run; a lone wall tile draws a short stub. Per-tile structures, so no edge data is needed; adjacency comes from `KnownWorld`. Instanced through the existing structure batching. | renderer | done 2026-09-21 — `Dressing/FortificationDresser.cs`: 4-bit join mask off `KnownWorld.StructureAt` (same owner, Wall/Gate/Tower/Castle), half-runs of panels centre→edge per joined side + a post where runs meet/turn/end; lone wall = stub; gate piece turned along the run axis. Skip removed from `EntityRenderer`. |
| T6 | **Render towers and rubble** from real prefabs instead of `StructureDistricts` primitives. Rubble should read as a breach: a destroyed-wall piece when a wall/gate neighbour exists, a broken-pillar pile otherwise. | renderer | done 2026-09-21 — tower = stacked bodies + cap, joins runs; rubble joins any fortification/rubble of any owner → destroyed-wall pieces along that axis (hash-gapped), else pillar cluster. Districts remain the fallback until the kit is assigned. |
| T7 | **Damage states.** For any structure with `MaxHealth > 0`, drive a visible damage cue from `Health / MaxHealth` (three bands: intact, damaged, critical). Cheapest licensed cue: a tint or a swap to a cracked variant where the kit has one; no fire or smoke, those belong to P3 combat. | renderer | done 2026-09-21 — `Presentation/DamageTint.cs`: bands from `HealthAt` (thresholds on the set, defaults 0.66/0.33), darkened material twin per (material, band) applied in `EntityRenderer.DrawModels` to authored models, dressed yards and fortifications; critical wall/gate swaps to `WallCritical` panel. |
| T8 | **Pick and wire the fortification kit.** Candidates from discovery §3: `SM_Prop_StoneWall_01/02/03`, `SM_Bld_Base_Wall_*`, `SM_Bld_Base_Wall_Round_01` + roof caps (tower), `SM_Bld_Base_Wall_Door_Double_Large_01` (gate), `SM_Bld_Base_Wall_Destroyed_01/02` (rubble), broken pillars. Produce a `FortificationSet` asset + `Window > Aow > Set Up Fortifications` menu and hand the editor step list to the user. | art | done 2026-09-21 — `Scripts/Editor/SetupFortifications.cs`: creates `Settings/Dressing/Fortifications.asset`, fills EMPTY slots with the renderer's suggested Base-kit pieces, assigns `EntityRenderer.Fortifications`, logs each piece's measured extent and warns when an along-X slot holds a piece longer on Z. Pieces are placeholders by the user's decision ("whatever assets you decide on will be replaced"); the slot contract is what stays. Editor steps owed to the user; not yet run in the editor. |
| T9 | **Health and siege on the card.** Context card and world bubble show `Health/MaxHealth` as a bar for any structure that carries it; a besieged fortification shows "Under siege: N attackers, D damage next round, breach in ~R rounds" (R = Health / SiegePower). Build menu shows HP and "blocks movement / allies pass" from T2. | UI | done 2026-09-21 — bubble: health bar with `h/max` caption (alarm under a third) for any structure with `HasHealth`, own or foreign; siege rows on the engage and hourglass glyphs. Card: Strength bar + one alarm line. Build option card: Strength field + passage note; world build ring caption gains `N HP` and `allies pass` / `nobody passes`. Also folded `TileIndex.StructureAt` onto `KnownWorld` (contract handoff). |
| T10 | Remove the **Walls and gates** coming-soon row when T5 lands. Remove the **Battles** row only if T3's siege marker is drawn (T3 field-level siege is P2; the general combat marker stays deferred). | UI | done 2026-09-21 — walls row removed at the user's direction ahead of the asset being seen in Play; Battles row stays. |
| T11 | Server suite green; both client type-checks green; live run: the server has no walls flag (`ServerOptions.cs:38-49`), so place a wall line from the client (route gesture, Enter) and run `--rivals 1` for a siege, or drive the hostile with a bandit party: wall segments visible, gate oriented, HP falls on the card, rubble appears on breach. | verification | todo |

## Decisions taken in this package

- **Fortification health is exact and visible to anyone who can see the tile** (user
  decision 2026-09-20, "exact" chosen over coarse bands or hidden). Consequence, stated
  plainly: siege damage is deterministic (`FortSiege.cs:78-96`) and an attacker knows their
  own power, so the attacking client can compute the exact round of breach. Sieges are an
  open arithmetic race. This is the first enemy-side enrichment on the wire; it does NOT
  extend to enemy houses, stores or unit health. Decision doc: `docs/siege-visibility.md`.

## Verified

- 2026-09-21 (verification, T11 first pass): every "done" row (T1–T10) is in code.
  - `dotnet test` Sim.Tests: 1164 passed, 0 failed (4 m 41 s). The three P2 `WireV2Tests`
    cases are present and green.
  - `_AowWireBoundary.csproj` 0 errors / 0 warnings; `_AowTypeCheck.csproj` 0 errors, the 3
    pre-existing MSB3245 warnings (covers `Editor/SetupFortifications.cs`).
  - Code: `StructDto.Health/MaxHealth`, `BuildOptionDto.BlocksMovement/AlliedPassage/BaseHealth`,
    `CombatDto.FortKind/Besiegers/SiegePower` on the server; camelCase twins + `HasHealth`,
    `IsSiege`, `KnownWorld.StructureAt/HealthAt/SiegeAt` on the client; `FortificationDresser`,
    `DamageTint`, `FortificationSet` (8 slots), `SetupFortifications` menu, skip removed from
    `EntityRenderer`; bubble + context card read `HasHealth`/`SiegeAt`; `TileIndex.StructureAt`
    forwards to `KnownWorld`; the walls coming-soon row is gone, the Battles row stays.
  - Live (`Sim.Server --rivals 1`, fresh build): `GET /v2/world` — every `buildable` entry
    carries the three T2 fields (Wall blocks/no allies/500, Gate blocks/allies/300, Tower 150,
    Rubble 0). `GET /v2/view/1` — own Castle 1000/1000, School 75/75, sites 25/25.
    `PlaceWallIntent` (3 tiles) was rejected on farm claims next to the castle and accepted 5
    tiles out: three Wall sites appear with `targetKind` 17, `health`/`maxHealth` 25/25 and
    `needed` 5 wood + 20 stone each. `combats` carries the T3 fields (empty in a fresh world).
  - NOT live-verified: a completed wall, a gate's orientation, HP falling under siege, rubble
    on breach. Each wall tile needs 5 wood and the fresh castle has 0, so this needs a
    play-through with lumber. Also not run in Play: the fortification props and damage tint
    (editor step `Window > Aow > Set Up Fortifications` owed).
  - Doc repair this pass: the Art ownership row and the T8 row each carried a duplicated copy
    of the T8 task text from a concurrent edit; cut back to one row each.

- 2026-09-20 (projector): T1–T3 landed. `WireV2Tests` 28/28 green; full server suite 1124/1124 green. Sim.Core untouched.
- 2026-09-20 (contract): T4 landed. `_AowWireBoundary.csproj` and `_AowTypeCheck.csproj` both 0 errors. Not live-verified (T11).

- 2026-09-21 (UI + art): T8–T10 landed. `_AowTypeCheck.csproj` 0 errors (its glob covers `Scripts/Editor`, so
  `SetupFortifications` is type-checked too). Seen in Play 2026-09-21: the user ran the menu and a long wall line renders along the terrain (panels join across tiles, run climbs the hills). Gate orientation, damage bands, rubble and the siege readout not yet seen.

- 2026-09-21 (renderer): T5–T7 landed. `_AowTypeCheck.csproj` and `_AowWireBoundary.csproj` both
  0 errors, no warnings in touched files. Not run in Play. Engine doc:
  `Art Of War(prod)/docs/fortifications.md`.

- 2026-09-21 (coordinator, from the user's Play screenshot): walls and towers confirmed live.
  A long run hugs the lakeshore, turns a corner and climbs the hills; panels join across tiles
  and follow the relief. Two things to look at on the next Play pass:
  - the run on the far ridge (top-left of the shot) shows as spaced dashes, not a joined line.
    Either those tiles are still per-tile construction sites (walls complete one tile at a
    time, so a half-built line SHOULD look like this) or the join mask breaks on steep slope.
    Select one of the gapped tiles: if it is a site, no bug; if it is a standing Wall, T5 join
    on slope needs a look.
  - no gate in frame, so gate orientation is still unseen.
  Still unseen: damage bands (T7), rubble on breach (T6), siege readout (T9). All wait on T11.

## Handoffs from the renderer seam

- **Art seam (T8):** `FortificationSet` slots to fill: `WallPanel` (SM_Bld_Base_Wall_01),
  `WallCritical` (SM_Bld_Base_Wall_Destroyed_02), `WallPost` (SM_Bld_Base_Pillar_01), `Gate`
  (SM_Bld_Base_Wall_Door_Double_Large_01), `TowerBody` (SM_Bld_Base_Wall_Round_01), `TowerCap`
  (SM_Bld_Base_Roof_Cap_01), `RubbleWall` (SM_Bld_Base_Wall_Destroyed_01), `RubblePile`
  (SM_Prop_Pillar_Broken_01). Panels and the gate are laid along their prefab X; if a piece's
  length is on Z the dresser's `Along` rotation needs a per-slot yaw dial — say so and I add it.
  Assign to `EntityRenderer.Fortifications` (a public field on the component in MainScene).
- **UI seam:** the picker boxes walls from the district table; the drawn run is now the
  fortification props. `EntityRenderer.DressedAt(x, y)` does NOT cover fort tiles (separate
  cache); if the picker wants the real box, ask for a `FortAt` accessor. Remove the walls/gates
  coming-soon row (T10) once T8's asset is assigned and seen in Play.
- **Assets or nothing:** with no set, walls and gates draw nothing and warn once; towers and
  ruins keep their districts.

## Handoffs from the contract seam

- **T5 adjacency:** `KnownWorld.StructureAt(x, y)` is the per-tile structure lookup; join wall
  segments off it (kind + ownerId of the 4-neighbours). `Client/Input/TileIndex.cs` has an older
  copy of the same lookup; the UI agent should fold it into KnownWorld when next in that file.
- **T7 damage bands:** `HealthAt(x, y)` returns null for "no bar"; `(0, 0)` means indestructible.
- **T9 card:** `SiegeAt(x, y)` gives `besiegers`, `siegePower`, `fortKind`; breach rounds =
  ceil(health / siegePower) with a guard for siegePower 0.

## Deferred out of P2

General combat markers and casualties (P3), fire/smoke on damaged structures (P3),
Castle model vs recipe (P4, S2), remembered structures under fog (Sim.Core-blocked, §5).
