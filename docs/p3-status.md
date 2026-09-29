# P3 status — combat legibility

**Package:** P3 from `docs/prod-client-gap-discovery.md` §6, plus T3 (combat markers)
deferred from P1. Started 2026-09-21.
**Goal:** a fight is visible where it happens, a unit's health reads against a maximum, an
armed unit looks armed, a chase is visible as a chase, and the player can order one with a
leash they chose. Sim combat is fixed (`docs/combat-model.md`, `docs/patrols.md`); nothing
here changes it. Demolish (`DemolishStructureIntent`) already shipped end-to-end and is not
part of this package.

## Ownership (one seam per agent)

| Agent | Owns | P3 tasks |
|---|---|---|
| Wire projector | `src/Sim.Server/Wire`, `ViewProjector.cs`, `tests/Sim.Tests/WireV2Tests.cs` | T1, T2, T3 |
| Client contract | `Assets/Scripts/Wire`, `Net`, `Client/KnownWorld.cs` | T4, T5 |
| World renderer | `Client/Presentation`, `Client/World`, `Client/Dressing` | T6, T7, T8, T9 |
| Art and assets | `Settings/**`, `Editor/Setup*`, prefab choice | T10 |
| UI/UX | `Client/Hud`, `Client/WorldUi`, `Client/Input`, `Client/Automation` | T11, T12, T13 |
| Verification | tests, type-checks, live run | T14 |

Wire rules as in P2: additive on v2, v1 untouched, one `WireV2Tests` case per field. Visibility
per field is stated in its row; the P2 decision (`docs/siege-visibility.md`) covers
fortifications only and does NOT make enemy unit health public.

## Tasks

| # | Task | Owner | Status |
|---|---|---|---|
| T1 | **Unit catalog on genesis.** `WorldDto.Units[]` = one `UnitOptionDto { Role, BaseHealth, BasePower, CargoCapacity }` per role from `UnitCombatCatalog.Spec(role)` (`UnitCombatSpec { Role, BaseHealth, BasePower }`) and `UnitCargoCatalog.CapacityFor(role)`. Gives `UnitDto.Health` its denominator and closes the cargo-cap gap (W12) in the same block. | projector | done 2026-09-21 — `WorldDto.Units[]` of `UnitOptionDto`, every role but None ordered by id (bandits included: the player sees them). Test: `UnitCatalog_IsExactlyTheSimsRoleTable`. |
| T2 | **Pursuit on the unit row.** `UnitDto.PursuitTargetId`, `PursuitLeashX/Y`, `PursuitLeashRadius` from `Unit.Pursuit` (`World/Pursuit.cs:25-38`). **Own units only** (a chase is an order, orders are private); absent = -1. The projector does not touch `Unit.Pursuit` today (grep confirms), so this is a new read on the `world` lookup `ToUnitDto` already does. | projector | done 2026-09-21 — `UnitDto.PursuitTargetId/PursuitLeashX/PursuitLeashY/PursuitLeashRadius`, -1 = none/not yours, radius 0 is a real value ("no leash"). Both projection paths via `FillPursuit`. Test: `Pursuit_IsOnTheWireForOwnUnitsOnly`. |
| T3 | **Combat outcome per round, if cheap.** `CombatRoundEvent.Apply` (`Combat/CombatRoundEvent.cs:115-145`) computes per-owner damage and drops it; nothing is retained on `CombatState`. Do NOT add Sim.Core state. Instead extend `CombatDto` with what is derivable now: `Sides` = per-owner `{ OwnerId, Units, Power }` for units on the tile (public: they are on a visible tile). The client shows "12 vs 9, 340 vs 210 power". Last-round damage stays absent; note it in the discovery doc W16 as still open. | projector | done 2026-09-21 — `CombatDto.Sides[]` of `CombatSideDto { OwnerId, Units, Power }` for units ON the tile, ordered by owner, embarked excluded (matches `CombatRules.ForcePower`). A siege tile has nobody on it, so its forces stay in P2's `Besiegers/SiegePower`. W16 updated: damage still open. Test: `CombatRow_CarriesSidesPerOwner_ForAFieldBattle`. **Contract seam:** mirror `units[] { role, baseHealth, basePower, cargoCapacity }` on `WorldDto`; `pursuitTargetId`, `pursuitLeashX`, `pursuitLeashY`, `pursuitLeashRadius` on `UnitDto`; `sides[] { ownerId, units, power }` on `CombatDto`. |
| T4 | Mirror T1–T3 on the client DTOs; `KnownWorld.UnitSpec(role)`, `MaxHealth(unit)`, `PursuitOf(unit)`, `CombatSidesAt(x,y)`. | contract | done 2026-09-21 — `WorldDto.units[]` of `UnitOptionDto` (+ `UnitOption(role)`), `UnitDto.pursuitTargetId/pursuitLeashX/Y/pursuitLeashRadius` (+ `IsPursuing`), `CombatDto.sides[]` of `CombatSideDto`. `KnownWorld.UnitSpec`, `MaxHealth` (-1 = no catalog, show the bare number), `PursuitOf` (nullable tuple), `CombatSidesAt` (empty list, never null). |
| T5 | **Engage builder takes a leash.** `IntentFactory` for `EngageUnitIntent` already exists (`Net/HttpServerLink.cs:373`); expose `leashTile`/`leashRadius` as parameters instead of the hard-coded constant in `Input/OrderIssuer.cs:1389`. | contract | done 2026-09-21 — `OrderIssuer.Engage(target, Vector2Int? leashTile, int leashRadius)`; the old `Engage(target)` delegates with null (= each chaser's own tile) and `LeashRadius`. The pending-order confirm now matches `pursuitTargetId` exactly instead of guessing from `destX/Y`. |
| T6 | **Combat marker on the tile** (P1 T3 revived). Driven only by `CombatDto`: a licensed clash mark at the tile centre, intensity from `RoundNumber`, siege variant when `FortKind != 0` (P2 already sends it). No invented casualties, no bodies. Instanced; one draw per combat. | renderer | done 2026-09-21 — `Presentation/CombatMarks.cs` (added by `GameBootstrap`): the UI's licensed `sword` glyph on a billboard quad at the tile centre, `shield` glyph when `fortKind != 0`; size = max(world, pixel floor); intensity from `roundNumber` in 4 quantised steps (≤4 batches per glyph). Dials on the component. |
| T7 | **Health on figures.** For own units, a small bar above the figure from `Health / MaxHealth(role)`, hidden at full health and beyond the focus distance. Foreign units: none (health is own-only). | renderer | done 2026-09-21 — same component: back plate + band-coloured fill over `StandPoint`, own units with `health >= 0` and `MaxHealth > 0` only, hidden at full health and past `BarDistance` (1200). |
| T8 | **Buffs on figures.** Map `UnitDto.Buffs` kind strings to a held item via `EquipmentOptionDto.BuffKind` (unused today, `WorldDto.cs:145`): Sword/Bow/Shield prefabs in hand or on back through `ToolKit`, replacing the block assemblies for those three. Cart buff → a cart prop behind a hauler (world-ui W4 partial). | renderer | done 2026-09-21 — `ToolKit`: `ToolKind` +Bow/Shield/Cart; `For(pose, role, cargo, buffs)` arms by buff kind (`sword`/`bow` in hand), `UnitFigures.SetExtras` toggles shield (back) and cart (behind root) by `shield`/`cart` buffs. New `Presentation/WeaponSet.cs` asset with per-slot prefab/length/Euler/offset; empty slot = block placeholder (bow, shield and cart blocks added). `EntityRenderer.Weapons` field. |
| T9 | **Chase ribbon.** For an own unit with a pursuit: a line from the unit to its target, a leash ring at `PursuitLeash` of `PursuitLeashRadius` tiles, both only while selected (same rule as goal target lines in `SelectionRenderer.cs:317`). | renderer | done 2026-09-21 — `SelectionRenderer.DrawChases`: dashed path to the quarry (when on the view) + outline, `GroundRing` of `(leashRadius + 0.5)` tiles at the leash tile; radius 0 = no ring; `ChaseColour` dial. |
| T10 | **Weapon and cart prefabs.** Pick from the unused Synty weapon set (`SM_Gen_Wep_*`) and RPGPP shields (`rpgpp_lt_shield_wall_01a/b`) for T8, and a cart (`SM_Prop_Wagon_*` if present, else the road-dressing wagon). Extend `ToolKit`'s set asset and its setup menu; hand the editor step to the user. Soldier/Archer bodies stay peasants — that is P5. | art | todo — the asset CLASS exists (`Presentation/WeaponSet.cs`: Sword/Bow/Shield/Cart slots). No sword or bow prefab exists in any imported pack (`SM_Gen_Wep_*` is Axe/Pickaxe/Spade only); Shield → `rpgpp_lt_shield_wall_01a`, Cart → `rpgpp_lt_wagon_01`. Menu must create `Settings/Dressing/Weapons.asset` and assign `EntityRenderer.Weapons`. |
| T11 | **Health denominators everywhere.** Card, bubble and People page show `h / max` (max from T4) instead of a bare number; power shows `base → effective` where `Power` differs from `BasePower`. | UI | done 2026-09-21 — bubble and card: `h/max` bar with alarm under a third (bare number + old bar when `MaxHealth` is -1), power `base → effective` only when they differ, plus a chase row/field from `PursuitOf`. People page: `h/max` suffix on a hurt person's row only. |
| T12 | **Engage verb with a leash.** Engage appears in the wheel and card for a hostile target (today it is right-click only, `PlayerController.cs:591`); the leash defaults to the unit's current tile and a radius stepper (same widget as the pull radius stepper). Pursuit state on the card: "Chasing #id, leash R from (x,y)". | UI | done 2026-09-21 — `Input/EngagePlanner.cs`: Quarry → Leash → send; the world picks the quarry (hostile unit under the pointer or on the clicked tile; invalid slash otherwise) and the leash centre (click the ground during the leash step; defaults to the lead chaser's tile); the radius is a slider inside the bubble (0–12, 0 = no leash), built once per step so ticks never rebuild it under the pointer. Wheel: Engage verb on own units; Confirm/Back/Cancel during the gesture. Card: Engage… verb, −/+ readout, Send/Back/Cancel. Esc cancels, right-click steps back, mode pill names the step. Right-click on an enemy keeps the default-leash shortcut. Pursuit state on card and bubble. |
| T13 | **Battles row and combat card.** Remove the **Battles** coming-soon row when T6 lands. Clicking a combat tile shows the sides from T3 and the siege readout from P2 on one card. | UI | done 2026-09-21 — Battles coming-soon row removed (T6 draws the mark). A clicked battle tile shows Battle: per-side count and power, own side green, others alarm, bandits named, plus the round; a siege tile selects the fortification, whose P2 readout already covers it. |
| T14 | Server suite green; both client type-checks green; live run with `--rivals 1 --bandits 2`: a marker appears on a fight tile, an own soldier's bar drops during it, a sword shows on an armed unit, an engage order shows its ribbon and ring, and the card's sides match `GET /v2/view/1`. | verification | todo |

## Decisions taken in this package

- **Pursuit is own-only.** An enemy's chase is inferred from movement, never disclosed.
- **Combat sides are public** (unit count and summed power per owner on a visible combat
  tile). Reasoning: the units themselves are already on the wire with owner and role; the
  sum adds convenience, not information. Enemy *individual* unit health stays hidden.
- **No per-round damage on the wire** until Sim.Core retains it; that is a sim decision,
  flagged, not taken here.

## Verified

- 2026-09-21 (projector): T1–T3 landed. `WireV2Tests` 31/31 green; full server suite 1175/1175 green. Sim.Core untouched.
- 2026-09-21 (contract): T4–T5 landed. `_AowWireBoundary.csproj` and `_AowTypeCheck.csproj` both 0 errors. Not live-verified (T14).

- 2026-09-21 (UI): T11–T13 landed. `_AowTypeCheck.csproj` 0 errors. Not run in Play. The leash ring
  during composition is NOT previewed (the renderer draws pursuits from the view only); a live
  preview would be a renderer-seam request: draw `GroundRing` at `Controller.Engage.Anchor` of
  `Radius` while `Engage.Current == Leash`.

- 2026-09-21 (renderer): T6–T9 landed. `_AowTypeCheck.csproj` and `_AowWireBoundary.csproj` both
  0 errors, no warnings in touched files. Not run in Play. Engine doc:
  `Art Of War(prod)/docs/combat-legibility.md`.

## Handoffs from the renderer seam

- **UI seam (T13):** the Battles coming-soon row can go once T6's mark is seen in Play. The
  clash glyphs used are `sword` (field) and `shield` (siege) from `Resources/UI/Glyphs`; if the
  artist adds a dedicated `battle`/`siege` glyph, change two strings in `CombatMarks.EnsureMaterials`.
- **Art seam (T10):** slots and suggested prefabs above. Per-slot `Euler` must be turned by eye
  in Play: the tool root's up is the forearm direction and each prop's long axis is its own.
- **Bars stand over `UnitMotion.StandPoint`**, so a figure choreographed off that point carries
  its bar slightly aside. Cosmetic.

## Handoffs from the contract seam

- **T7 / T11 denominators:** `KnownWorld.MaxHealth(unit)` returns -1 when the server predates the
  catalog; draw no bar and show the bare number in that case. `UnitSpec(role).basePower` is the
  "base" for the `base → effective` readout.
- **T9 ribbon:** `KnownWorld.PursuitOf(unit)` is null unless the unit is yours and chasing.
- **T12 engage verb:** call `OrderIssuer.Engage(target, leashTile, radius)`; pass null for the
  default anchor. Radius 0 is accepted and means no leash.
- **T13 card:** `KnownWorld.CombatSidesAt(x, y)` for a field battle; `SiegeAt(x, y)` for a siege.

## Deferred out of P3

Military bodies and crown (P5), battlefield scene (client plan C5), per-round damage log
(needs Sim.Core state), enemy unit health visibility (no decision requested).
