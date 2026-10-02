# Unit healing: rest at your own shelter

**Status:** decided and built 2026-10-02.
**Depends on:** M7 per-unit Health, M14 equipment (the shield's `HealthModifier`), M41 battlefield
grid (`Unit.Board`), M19 houses.
**Answers:** `docs/battlefield-grid.md` §8 ("decide whether HP recovers over time or requires rest
in a friendly district") and the audit finding "Unit Health is never restored anywhere".

## The decision

A wounded unit heals by **resting on a tile that holds its own Castle, House or Barracks**, off any
battlefield and not aboard a boat. It heals a **flat** `RestConstants.HealPerPeriod` (1) at the end
of every **completed** `RestConstants.PeriodTicks` (one game-hour) of uninterrupted rest, never past
`CombatRules.MaxHealth` (role base + live gear). Healing is **free**: no food, no workers.

User decisions (2026-10-02):

1. **Own buildings only:** Castle, House, Barracks (`StructureSpec.Shelters`). Not anywhere outside
   combat, and not "own territory".
2. **Free** in v1.
3. **Flat rate**, the same for every role.
4. **The wounded walk home.** A worker wounded at a field stays wounded until it stands on a
   shelter.

## Why

### What it fixes

Before this, Health only went down. Every fight permanently degraded an army, so even a decisive
win cost strength for good. A wounded soldier had no reason to leave the line, and the "wounded
soldier crawls home" texture (`docs/combat-model.md`) had nothing to crawl home to.

### Why shelters, not anywhere out of combat (the main losing option)

- **Withdrawal becomes a decision.** Sending the wounded home opens a gap in the line, and a campaign
  far from home still wears down. That is the attrition the conquest and raid AIs are tuned around.
- **Async fairness.** Healing anywhere would refresh a raiding party in the field while its owner
  sleeps. Healing at home favours the defender, consistent with the rest of the async doctrine.
- **Buildings matter militarily.** A house by the fields is also a field hospital, and the barracks
  is where soldiers recover.

"Own territory" also lost: the sim has no territory, and "district" is a client-only idea
(`docs/tile-scale-and-districts.md`).

### Why flat, not a percent of max

A flat rate makes tough units take longer to patch up. A 40-HP shielded soldier needs 30 hours from
10 HP; a 10-HP citizen needs at most 9. That is a real cost of fielding heavy infantry. A percentage
would make everyone heal in the same time and hide that cost. Flat is also integer-exact with
nothing to round.

### Why free (for now)

Houses already eat (M19), so rest at a house is paid for by the meals the household already buys.
A food or medicine cost belongs with a faster, dedicated healer (below), not with ordinary rest.

### Why not tied to "settled" (a fed house)

`Housing.IsSettled` flips when a house's lazily computed food level crosses zero, which is not a
scheduled event. A heal rate tied to it would be the coupled-interval trap (`docs/architecture.md`
§2.5). The shelter test reads only things that change at events: the unit's tile, its board, its
boat, and the structure on the tile.

### Why a per-unit event, not lazy catch-up

The architecture's default for continuous state is lazy catch-up (store a rate and a last tick;
compute on read). It lost here because `Unit.Health` is read raw by about ten Core sites (the
board, training, equipment, views, the wire). Each would need a `CurrentHealth(now)` read, and one
missed site would silently show stale health. A self-rescheduling event with re-arm (§2.4) keeps
Health a plain stored number that is always current. Event volume is one per wounded, resting unit
per game-hour; dormant units cost nothing.

### No healing in battle

Healing on a board would muddy the turn math and favour the player who is present. A unit on a
board (`Unit.Board != null`, including castle overflow sheltering off the board) never heals.

## Mechanics

| Piece | Where |
|---|---|
| Knobs: `PeriodTicks`, `HealPerPeriod` | `src/Sim.Core/Healing/Rest.cs` (`RestConstants`) |
| Shelter flag | `StructureSpec.Shelters`; set on Castle, House and Barracks in `StructureCatalog` |
| Rules: `IsSheltered`, `IsResting`, `ArmIfDormant`, `ArmAllOn`, `Interrupt` | `Healing/Rest.cs` |
| The heal | `Healing/RestHealEvent.cs`: fence, then heal + reschedule or go dormant |
| Ceiling | `CombatRules.MaxHealth(unit, now)`, moved into Core from `Sim.Server.BattlefieldProjection` |
| Anchor | `Unit.NextRestHealTick/Seq`; snapshot v47; `RegenerateQueue` |
| Re-arm sites | `TileEntry.Enter`, `Battlefields.Close`, `Construction.Complete` (shelter kinds), `DisembarkIntent` |
| Interrupt sites | `TileEntry.Enter` (before re-arming), `Battlefields.Enroll`, `EmbarkIntent`, `EmbarkGoal` |
| Wire | `UnitDto.MaxHealth`, `UnitDto.Resting`; own units only, both projection paths |

**Completed periods only.** Every tile change clears the anchor before re-arming, so a unit passing
through a house never heals, and a unit that steps out and back starts a fresh hour.

**Timings at 1 HP an hour.** A 40-HP soldier healing from 10 HP takes 30 game-hours. That is about
7.5 real minutes in the prelude (4 tps) and 30 minutes after day X (1 tps). A board turn deals 3–9
damage, so healing never competes with a fight.

**Interplay with gear and training.** Equip adds `HealthModifier` to both Health and MaxHealth, and
a strip removes it from both (clamped at 1, as before), so neither creates a new wound. The
equipment-model note about a "convoluted minor heal" through the strip clamp is now moot: resting
is the heal.

## Acceptance tests (`RestHealingTests`)

- Castle, House and Barracks each heal exactly `HealPerPeriod` per completed period; nothing
  before the period ends.
- Heals stop at MaxHealth (shield included) and the unit goes dormant.
- No healing off a shelter, on a non-shelter (Stockpile), or on an enemy's house.
- Leaving before the period ends earns nothing, and returning starts a fresh period.
- No healing on an open board; survivors start healing when it closes.
- A shelter finishing under its wounded starts them healing.
- A dead unit's queued heal is a no-op; `IsResting` / `MaxHealth` are pure reads.
- Twin-run hash equality; mid-rest snapshot → restore heals identically (hash and Health).

## Future expansion

- **Infirmary / Healer** (the designed layer 2): a faster shelter that consumes food or medicine
  from a hauled-in input store (the refiner `Inputs` pattern). Shelter strength becomes a per-kind
  rate on `StructureSpec` (today a single flag plus a flat constant). This is additive: the event,
  the anchor and the hooks stay the same.
- **AI:** a "send the wounded home" behaviour for the Homesteader and Rival, and health-aware
  assembly in `ConquerRung`. Today the brains neither seek nor avoid shelters. Their units heal only
  when they happen to stand on one.
- **Bandits** do not heal: a camp is not one of their shelters. Making `BanditCamp` a shelter for
  bandits would give raids an ebb. It is one catalog flag plus a camp-ownership check in
  `IsSheltered`.
- **Morale:** "low health" is listed as a future morale source (`docs/battlefield-grid.md` §5).
- **Structure repair** (walls, castles) could reuse the same per-entity event shape; it is a separate
  decision (`docs/sieges-and-conquest.md`).
