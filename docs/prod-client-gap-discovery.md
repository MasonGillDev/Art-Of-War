# Production client gap discovery — sim → wire → client → art

**Status:** discovery complete 2026-09-20. Nothing changed; this is the inventory the
build-out phases will be cut from. Three audits ran in parallel and were spot-checked
against source before being merged here.

Scope, as asked: everything present in the sim but not in the production client
(`Art Of War(prod)`), across four layers:

1. **Sim → wire.** Sim state the v2 wire does not carry.
2. **Wire → client.** Wire fields the client receives but never surfaces (HUD, WorldUi,
   world), and intents it can build but no UI sends.
3. **Client → world.** Entity kinds that render nothing or a placeholder primitive.
4. **Stale wiring.** Authored art/assets that exist but are unreachable.

Line numbers are as of this date on branch `wip/2026-08-18-substrate-to-v2-wire`.

---

## 0. Structural facts that shape every fix

- The v2 view surface is exactly `ViewDto` (`src/Sim.Server/Wire/WireDtos.cs:42-112`) +
  `ViewV2Dto` (`Wire/WireV2.cs:297-313`); genesis is `WorldDto` (`WireV2.cs:21-102`).
- **There is no intent DTO layer.** `POST /intent` takes `{TypeName, Payload}` where the
  payload is the raw Sim.Core intent in PascalCase (`Sim.Persistence/IntentJson.cs`).
  Every client intent is hand-built against Sim.Core's shape (`Net/HttpServerLink.cs`,
  `Net/OrderIntents.cs`). `SetOrderIntent` ships a whole `Order` object graph this way.
- All 33 registered intent types are accepted by `GameHost.SubmitEnvelopeJson`
  (`GameHost.cs:220-257`) except bandit spawn/despawn, `OrderStatusIntent`, `ClaimUnitIntent`.
- Most own-player wire gaps are **not Sim.Core-blocked**: `ToStructDto`/`ToUnitDto`
  already look the real `Structure`/`Unit` up from `world` (`ViewProjector.cs:756, 819`),
  so `Health`, `Dock.Slip`, `House.Occupation`, `Unit.Pursuit`, `Unit.Protected`,
  `Unit.Home` can be added in Sim.Server alone.
- The prod client's `ViewDto` deliberately omits `scoutReports` and `graves` even though
  the server sends them (`Assets/Scripts/Wire/ViewDto.cs:93-95`). Declaring them is a
  pure client change.
- Weather and seasons **do not exist** anywhere in the sim. Not a gap; a non-feature.
- The only scene with art tables wired is `MainScene.unity`; `C1_World.unity` is 100%
  placeholder.

---

## 1. Sim → wire: state the client can never see today

### Absent entirely (highest signal first)

| # | Sim state | Where | Why it matters |
|---|---|---|---|
| W1 | `Structure.Health` + `StructureSpec.BaseHealth` | `Structure.cs:21`, `StructureSpec.cs:87` | Sieges and walls are unreadable. A castle at 5% HP looks new; the only siege signal is a `CombatDto` dot. |
| W2 | Unit `MaxHealth` / `UnitCombatCatalog` (BasePower, BaseHealth per role) | `Combat/UnitCombatCatalog.cs` | `UnitDto.Health` has no denominator. No unit catalog on genesis at all. |
| W3 | `Unit.Pursuit` (target id, leash tile, leash radius) | `Unit.cs:156`, `World/Pursuit.cs` | The whole M29 chase anchor is invisible; patrol posture is on the wire but "who is chasing whom from where" is not. |
| W4 | `House.Occupation` + `House.PendingBreed` | `House.cs:26-34` | Pregnancies and pair reservations are off the wire despite the code comment saying they exist "to be legible … through the wire". Only `GoalState` strings hint at it. |
| W5 | `Dock.Slip`, boat-production armed/period | `Structure.cs:588` | Placement mode says "choose a slip" but the chosen slip is never shown. |
| W6 | `ConstructionSite.CanalPath` / `CanalReservation` | `Structure.cs:407` | Reserved canal tiles are invisible yet silently reject other placements. |
| W7 | Live `ScoutMission` (waypoints, cursor, return rule, elapsed limit) | `GameWorld.ScoutMissions` | Only the finished report ships. `GoalKind` has no Scout value. `ScoutConstants.MaxWaypoints=16` not on genesis, so the client can build a rejected dispatch. |
| W8 | `Unit.Protected` (conscription-sacred flag) and `Order.ConscriptOptIn` | `Unit.cs:221`, `Order.cs` | Neither is reported back; `Protected` also has no intent to set it. |
| W9 | `ClaimPurpose` (Crew vs InFlight) on `OrderDto.HeldUnits` | `Claim.cs:19-23`, `ViewProjector.cs:451` | Client can't tell a standing crew from a one-shot errand. |
| W10 | Wall/Gate semantics `BlocksMovement`, `AlliedPassage` | `StructureSpec.cs:96-102` | Not on `BuildOptionDto`; client can't explain a gate or draw a wall as impassable. |
| W11 | Structured game-over / defeat / succession events | `GameHost.cs:326-346` | Only prose `NoticeDto`. No winner id, no crown-moved event. |
| W12 | `Unit.Home`, `Unit.ParentAId/BId`, `Unit.Assignment`, `Unit.DeathTick`, `Unit.CargoCapacity`, `Unit.HaulPlan`, `Unit.Passengers` ids, `Traversal`, `PathRemaining` | `Unit.cs` | Family tree (HUD coming-soon) needs parentage. Cargo shows amount with no cap. No route between origin and dest. |
| W13 | Embarked units (incl. the viewer's own passengers) | `Sim.Core/Vision/View.cs:170` | Boat shows `Passengers=3`; none can be inspected. **Sim.Core-blocked** (see §5). |
| W14 | Group detail: `RendezvousTile`, `PendingArrivals`, group `PathFinalDest` | `Groups/Group.cs` | No `GroupDto`; only `GroupId`/`GroupState` on units. Reinforce-a-group has no intent at all (`Order.cs:98-101`). |
| W15 | Tuning scales on genesis: `RoadConstants.CONDITION_MAX`, `RiverConstants.CrossingCost`, `HousingConstants`, `UnitCargoCatalog`, `DiplomacyConfig.Delay`, `ScoutConstants.MaxWaypoints` | various | Each one a number the client must hard-code or omit. |
| W16 | Combat detail: per-round damage, who fights whom, `CombatRoundEvent` | `Combat/` | Client infers deaths from units vanishing. **Update 2026-09-21 (P3 T3):** who fights whom is now on the wire as `CombatDto.Sides` (per-owner units + power on the tile) and `Besiegers`/`SiegePower` for sieges (P2). Per-round damage is STILL OPEN: `CombatState` retains nothing about the last round, so it needs a Sim.Core decision, not a projector one. |
| W17 | Extractor `TickArmed`/dormant flag; assigned worker ids | `Structure.cs` | Only worker count/cap; "is this farm running" is inferred. |
| W18 | Bandit party state (Raid/Camp mode) | `Sim.Server/Bandits/BanditDriver.cs:39-56` | Server-only ephemeral; no camp structure exists in Sim.Core. |

### Partial / conflated

| # | Sim state | Wire today | Gap |
|---|---|---|---|
| W19 | Refiner `Extractor.Inputs` vs output `Buffer` | merged into one untagged `StructDto.Holdings` (`ViewProjector.cs:886-892`) | "3 Ore waiting to smelt" indistinguishable from "3 Ore produced". |
| W20 | Fertility deviation | only own-claimed tiles via `ClaimFertility` | Unclaimed / enemy soil absent. |
| W21 | `Building` bool | conflates pending and paused | `BuildPaused` not distinguishable. |
| W22 | Buffs | kind strings only, own units | No per-instance modifiers/expiry; no "waiting for a sword" state. |
| W23 | `IsMinor` on royals | not sent | Client derives from `Age` vs `MajorityAge`. |
| W24 | Rejections | prose `NoticeDto` | No structured intent type / reason code / subject id. |

### Confirmed ON the wire (suspicions resolved)

River edge mask (`WorldDto.River[]`), road arcs with `Axis`, combats (v2 only), patrol
`EngageRadius`/`LeashRadius`, claim tiles, per-house food, realm food, diplomacy
(relationships, pending wars, proposals), factions incl. `Defeated`, orders + journal,
piles (visible tiles), graves, scout reports (attached in `GameHost`, not the projector),
light phase, pace.

---

## 2. Wire → client: received but never surfaced

All paths under `Art Of War(prod)/Assets/Scripts/`.

### Whole features on the wire with zero consumers

| # | Field | Note |
|---|---|---|
| C1 | `ViewDto.combats` (+ `CombatDto`) | Zero readers in `Client/` or `Net/`. Battles invisible except as health dropping. **Silent gap: no coming-soon row.** |
| C2 | `scoutReports`, `graves` | Not declared on the client DTO (`Wire/ViewDto.cs:93-95`). Scout reports has a coming-soon row (`Hud/Dock.cs:482`); graves does not. |
| C3 | Walls / Gates | In KnownWorld, pickable, selectable, but `EntityRenderer.cs:292` skips them. Two milestones of sim with no visual. **Silent gap.** |
| C4 | `ViewDto.piles` | Read for haul targeting only (`Input/TileIndex.cs:65`, `SelectionRenderer.cs:281`). No world mesh; salvage invisible until you enter haul mode. |

### Fields with no consumer

`ViewDto.playerId`, `ViewDto.waterLevel`, `ViewDto.foodPeriodTicks` (so `+N` food rate at
`Hud/Hud.cs:368` is unitless), `StructDto.buildEtaTicks` (sites show % but never "done in
N days"), `StructDto.claimFertility` (claims outlined but never graded), `OrderDto.retryCount`
/ `currentStep` (a patrol's current stop is never shown) / `lastFiredTick`,
`RelationshipDto.pendingEffectiveTick`, `ProposalDto.targetId` / `expiryTick` (offers
never say when they lapse), `EquipmentOptionDto.buffKind` (its documented "already carries
a sword" purpose is unimplemented), `BuildOptionDto.buildDurationTicks` (build menu never
says how long), `BuildOptionDto.name` (menus use `SimVocabulary.KindName` so a server
rename never reaches the player), `PopulationRulesDto.ticksPerYear` / `minTrainAge`
(`OrderIssuer.CanTrain:792` never checks age) / `gestationTicks`, `SelectorDto.minAgeYears`
/ `maxAgeYears` / `requireDormant` / `anchorX` / `anchorY`, all `PredicateDto` fields except
`trigger[0].all[0].threshold`, `RoutineStepDto.departWhen` beyond element 0.

### Read into HUD but not rendered in the world

`UnitDto.health` (no floating bar on figures), `buffs` (an armed soldier looks unarmed),
`destX/destY` for a plain Move (no destination marker; only goal targets get a line),
`cargoResource` (figure shows a generic bundle, never the resource), `groupState` (not in
the WorldUi bubble), `StructDto.buildProgress` (no scaffolding/height cue), `lightPhase`
(used only to assert the client sky agrees with the server).

### Intents: never sent or one path only

| Intent | Status |
|---|---|
| `PlaceSiteIntent` bare (`PlaceSitePayload`) | never sent; building uses `BuildIntent`. Dead payload class. |
| `EmbarkIntent` (`EmbarkPayload`, no dock) | never sent; `EmbarkAtPayload` covers it. Dead payload class. |
| `UnassignWorkersIntent` per-worker | no UI caller; only unassign-all. Cannot recall one worker. |
| `EngageUnitIntent` | right-click only (`PlayerController.cs:591`). No verb in wheel or panel; `LeashRadius` hard-coded. |
| `OrderPayload.Enabled` | always forced `true` (`OrderIntents.cs:21`). No pause/resume verb on the Machine page. |
| `OrderPayload.ConscriptOptIn` | never set. |
| `SubjectGroup` orders | round-tripped only; never authored. |
| Predicates `PredAlways`, `PredRoleCountAtLeast`, `PredPopulationBelow/AtLeast`, `PredWorkersAtLeast` | encodable, never authored. No predicate editor. |
| Depart gate "until stock below" | readable, never sent (`UntilStock` always `atLeast:true`). |
| Multi-clause (OR) triggers | `TriggerPayload.Any` always length ≤1. |

### Existing coming-soon rows (already declared to the player)

Control groups, Family tree (needs W12 parentage), Scout reports (C2), Trade (not in sim),
Overview markers, Key bindings, Graphics, Save and load (`Hud/Dock.cs:403-604`).
**Missing rows** for the silent gaps: combats, graves, walls/gates.

`docs/world-ui.md:150-157` already lists: no names on units/structures, haul carries a
resource but no quantity, no DeliverIntent (client chains Move → Unload), no pathfinder for
route-time estimates. W2–W4 of that roadmap (route ribbons, destination glyphs, stall chips,
cargo on figures) are scheduled, not built.

---

## 3. Client → world: render coverage per kind

Resolution order in `EntityRenderer.BuildStructures` (`Presentation/EntityRenderer.cs:277`):
Wall/Gate skip → `TileRecipeSet` (dressing) → `StructureModelSet` (.glb) →
`StructureDistricts` (procedural `PlaceholderMeshes`) → `Generic()`.
**`if (b.Ground) continue;` at line 342 drops every `Plot()` block** in the district table,
so farm fields, castle bailey, dock quay/jetties and spoil heaps are authored but never drawn.

### Structures (21 kinds: 7 with art, 11 primitives, 3 nothing)

| Kind | Status | Candidate assets already in project |
|---|---|---|
| ConstructionSite 2 | real (clutter only, by design) | — |
| Castle 4 | real via dressing, **but** same prefab as School (`rpgpp_lt_building_01`); `Castle.glb` unreachable (dressing wins first) | `Models/Structures/Castle.glb` |
| LumberCamp 5 | real (`LumberCamp.glb`), only kind using the model set | Polytope `_cut`/`_stump`/`_logs` tree variants for worked tiles |
| Farm 8 | real building, **15 claimed field tiles render nothing** (`ClearPlots:0`, `PlotCards:[]`) | `rpgpp_lt_terrain_grass_01/02`, `SM_Plant_Grass_*` |
| House 9 | real (`rpgpp_lt_building_03` ×2-3) | — |
| School 11 | real, indistinguishable from Castle | `rpgpp_lt_building_04/05` (imported, only used as bailey clutter) |
| Stockpile 1 | placeholder | `rpgpp_lt_shed_wood_01/02`, `SM_Gen_Prop_Crate_*`, barrels |
| Tower 3 | placeholder | `SM_Bld_Base_Wall_Round_01` + roof caps |
| Quarry 6 | placeholder | `SM_Rock_Pile_01..04`, `SM_Gen_Env_Cliff_*` |
| Mine 7 | placeholder | `SM_Gen_Env_Dirt_Cliff_*`, `PT_Ore_Rock_01`, `SM_Bld_Base_Door_Large_01` |
| Dock 10 | placeholder, quay + jetties dropped (plots) | `SM_Prop_Bridge_Curved_01`, `rpgpp_lt_wood_path_*` |
| Barracks 12 | placeholder | `rpgpp_lt_shield_wall_01a/b`, `SM_Prop_Fence_02`, awnings |
| Lodge 13 | placeholder | `rpgpp_lt_building_04/05` |
| Cache 15 | placeholder (3-5 boxes) | `SM_Prop_Chest_Wood_01`, `SM_Gen_Prop_Chest_01/02`, sacks, baskets |
| Rubble 16 | placeholder | `SM_Bld_Base_Wall_Destroyed_01/02`, broken pillars/arches |
| Smelter 19 | placeholder | `SM_Gen_Bld_Pipe_*`, `rpgpp_lt_shed_wood_02` |
| Workshop 20 | placeholder | `rpgpp_lt_building_04`, sheds |
| Smithy 21 | placeholder | `rpgpp_lt_building_05` + `SM_Gen_Wep_*` |
| Wall 17 | **nothing** (explicit skip) | `SM_Prop_StoneWall_01/02/03`, `SM_Bld_Base_Wall_*` |
| Gate 18 | **nothing** (explicit skip) | `SM_Bld_Base_Wall_Door_Double_Large_01` |
| Canal 14 | **nothing** (by design, "it IS water"; tile flips via biomeOverrides) | — |

### Units (14 roles: 13 on 4 shared peasant prefabs, 1 primitive)

`Settings/Atmosphere/Characters.asset` maps every role except Boat to one of four Polytope
peasant prefabs (`PT_Male_Peasant_01`, `PT_Female_Peasant_01_a/_b`, `PT_Boy_Peasant_01`).
Consequences:

- **Soldier, Archer, Bandit** render as an unarmoured peasant. A battalion is one villager.
- **Boat** is `PlaceholderMeshes.Hull` (box with a prow).
- **Crown** (King/Heir) is a `Block` + `Spire` assembly.
- **All tools in hand** (`Presentation/ToolKit.cs:150`) are block assemblies, though
  `SM_Gen_Wep_Pickaxe_01`, `PT_Hayfork_01`, `rpgpp_lt_rake_01` and the Synty weapon set exist.
- Far-distance figures degrade to a camera-facing `Block` pin (`UnitFigures.cs:235`).
- `CohortTable.Figures()` and `RoleSilhouettes` are dead for every role with a character
  entry; they only run for Boat and unknown roles.
- Candidates: all 22 `Synty/PolygonGeneric/Prefabs/Characters` + 22 attachments, every
  Polytope `Separate_Parts` and the 3 `*_Modular_Free_Pack` prefabs are unused. Modular packs
  are the obvious route to a soldier that isn't a peasant.

### Other entities

| Thing | Status |
|---|---|
| Ground piles | nothing in world |
| Combats | nothing (and unread) |
| Graves | nothing (undeclared); `SM_Prop_Grave_03`, `SM_Prop_Skeleton_Ground_01`, `SM_Prop_Skull_01` unused |
| Road surface | real (terrain shader SDF) |
| Road furniture | **nothing** — see S1 below |
| Resource/item kinds in 3D | nothing; only HUD glyph PNGs. A Cart-carrying hauler looks like any other. |
| Rivers, sea, terrain, biomes, nature scatter, night fires | real |

---

## 4. Stale / broken wiring (cheap wins, mostly editor steps)

| # | Problem | Fix shape |
|---|---|---|
| S1 | `Settings/Dressing/RoadDressing.asset` is authored but `TerrainRenderer.RoadProps` is not serialized in MainScene (`MainScene.unity:161-168`), so `RoadDressing.Update` returns early. Every road furnishing is dead. | Re-run **Window > Aow > Set Up Road Dressing** with MainScene open. Editor step; user-owned per the teach-don't-sidestep rule. |
| S2 | `Castle.glb` unreachable: dressing recipe for Kind 4 wins before the model set. | Decide: drop the Castle recipe, or move model-set lookup ahead of dressing for kinds that have both. |
| S3 | `Models/Structures/Farm.prefab` referenced by nothing. | Delete or wire. |
| S4 | Castle and School share `rpgpp_lt_building_01`. | Give School `rpgpp_lt_building_04/05`. |
| S5 | `Settings/Atmosphere/EntityScale.asset` orphaned (`MainScene.unity:197` `Scale: {fileID: 0}`); Look-Tuning edits don't persist. | Assign in inspector. Editor step. |
| S6 | `Settings/Atmosphere/Ambience.asset` referenced by no scene. | Assign to `AmbienceDirector`. Editor step (owed per ambient-audio notes). |
| S7 | All plot dials off in all 5 recipes; `EntityRenderer.cs:342` drops district plots. | Turn on plot cards per recipe; decide whether the district `Plot()` drop is intentional. |
| S8 | `C1_World.unity` `EntityRenderer` has stale `DrawRoads` field and no tables. | Delete or ignore; not the shipping scene. |
| S9 | Dead client payload classes `PlaceSitePayload`, `EmbarkPayload`. | Remove (refactor per CLAUDE.md consistency rule). |

No dangling prefab GUIDs; every table entry resolves.

Asset inventory: 874 prefabs, ~143 referenced, ~731 unused. Big unused pools: the entire
Synty PolygonGeneric `Base` modular kit (71: walls, doors, round walls, roofs, destroyed
walls), `Building` (25), `Environment` (222), `Characters` (44), Polytope trees with
cut/stump/log variants, `PT_Ore_Rock_01`. `IgniteCoders` water shader: 0 used.

---

## 5. Sim.Core-blocked items (need a `PlayerView` change; flag for user decision)

Sim.Core is normally not modified. These cannot be closed from Sim.Server alone:

1. **Remembered structures.** `GameWorld` remembers `Explored` tiles and `RememberedBiome`
   only. A scouted enemy castle vanishes from `StructDto` the moment fog returns.
2. **Embarked units.** `View.BuildPlayerView` skips them unconditionally (`View.cs:170`).
3. **Combats under reveal.** `OngoingCombats` hard-scoped to Visible (`View.cs:217-222`).
4. **Enemy-side visibility policy.** Every enrichment is own-player gated in the projector.
   Whether enemy structure HP, group state or heir tags should ever be visible is a design
   call, not a code gap (`WireDtos.cs:364-376` says the heir case is deliberately unsettled).

---

## 6. Proposed work packages (for the build phase, not started)

Ordered by player-visible payoff per unit of work. Each package is one milestone-sized
slice with its own status doc when started.

| Pkg | Name | Layers | Contents |
|---|---|---|---|
| P0 | **Editor rewire** | 4 | S1, S5, S6 (user-driven editor steps), S3, S8, S9 |
| P1 | **Declare what's already sent** | 2 | Declare `scoutReports`/`graves` on client `ViewDto`; consume `combats`; add coming-soon rows for graves/combats/walls until rendered; surface `buildEtaTicks`, `foodPeriodTicks`, `claimFertility`, `expiryTick`, `currentStep`, `buildDurationTicks`, `minTrainAge`, `gestationTicks` in existing panels |
| P2 | **Fortification visibility** | 1+3 | W1 structure Health/BaseHealth, W10 wall/gate semantics on genesis; render Wall/Gate/Tower/Rubble from the Synty Base kit; siege state on the context card |
| P3 | **Combat legibility** | 1+2+3 | W2 unit catalog on genesis (MaxHealth denominator), combats rendered on tile, health/buffs on figures, W3 pursuit anchor on `UnitDto`, Engage verb in wheel with leash choice |
| P4 | **Structure art pass** | 3 | Real art for the 11 primitive kinds using the candidate table in §3; farm plots on; district `Plot()` decision (S7); Castle vs School split (S2/S4) |
| P5 | **Military characters** | 3 | Soldier/Archer/Bandit/King from Synty or Polytope modular packs; real crown; real tools from `ToolKit`; cargo resource on the figure (world-ui W4) |
| P6 | **Logistics + water surfacing** | 1+2+3 | W5 dock slip, W6 canal path, W13 embarked passengers (Sim.Core decision), boat hull model, piles rendered, W19 tag refiner inputs vs outputs, cargo capacity |
| P7 | **Households + dynasty** | 1+2 | W4 occupation/pending breed, W12 home + parentage (feeds the Family tree coming-soon row), W23 IsMinor, succession events (W11) |
| P8 | **Automation surface completion** | 1+2 | W8 `Protected` + `ConscriptOptIn` round trip, W9 ClaimPurpose, order pause/resume (`Enabled`), per-worker unassign, predicate editor for the unauthorable vocabulary, `currentStep` on patrol overlay |
| P9 | **Scouting live** | 1+2 | W7 live mission on wire + `MaxWaypoints` on genesis; scout reports page (C4 of the client plan) |
| P10 | **Intent DTO layer** | 1 | Decision doc: keep raw Sim.Core PascalCase payloads, or add a mirror DTO layer under `Wire/` so `SetOrderIntent` and coordinate lists stop being hand-built. Cross-cuts every package above; decide before P8. |
| P11 | **Genesis constants** | 1 | W15 tuning scales on `WorldDto` so the client stops hard-coding |

Each package that touches the wire is additive on v2 (v1 stays frozen for the debug client),
and gets a `WireV2Tests` case plus a live equivalence check per the C0 workflow.

Open decisions for the user before the build phase starts: §5 items 1–4, S2 (Castle model
vs recipe), S7 (district plots), P10 (intent DTO layer).
