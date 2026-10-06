# M51 spec — ore tiers: copper, iron, steel

**Status:** built 2026-10-06 (`docs/m51-status.md`).

**Builds on:**
- stone and ore (M44): `docs/stone-and-ore-land.md`;
- refining: `docs/refining-structures.md`;
- equipment: `docs/equipment-model.md`;
- wilderness bands: `docs/wilderness-bands.md`.

## The decision

Mountain veins hold one of **three ores**. The rarer the ore, the farther it lies from every
kingdom. Each ore climbs a ladder:

| Tier | Ore | Metal (smelted) | Sword | Where |
|---|---|---|---|---|
| 1 | Copper ore | Bronze | Bronze sword | the nearest veins (common) |
| 2 | Iron ore | Iron | Iron sword | farther out |
| 3 | Steel ore | Steel | Steel sword | the farthest veins (rare) |

The user's rules (2026-10-06):

1. **Tiers are different resources,** with real metal names. Today's chain becomes the
   bronze rung.
2. **A miner's survey reports which ore it found.**
3. **The tier shares are a balance dial.**
4. **One mine per ore.** The mine for a tier costs the **metal** of the tier below. To mine
   steel you must have mined and smelted copper and iron first.
5. **One Smelter smelts every ore.** Better ores burn more wood. When it holds several ores,
   it smelts the **best one first**.
6. **Each metal makes a straight better sword.** This milestone is swords only. Later, lesser
   metals stay useful through unique items and structures only they can make. This
   milestone builds no such items.

## How a vein gets its ore

Rank every vein by remoteness (`world.Wilderness.MinutesAt`; a vein no foot reaches counts as
farthest; ties by (y, x)) and cut the list by share:
- the farthest `SteelSharePercent` (default **10**) are steel ore;
- the next `IronSharePercent` (default **20**) are iron ore;
- the rest are copper ore.

Both shares live on `VeinConfig`. This happens at genesis, after the wilderness field is
measured. It draws no `Rng`, and the ore is terrain: it never changes.

**Why rank the veins and not use the land bands.** Measured on the default 252 map, 5 seeds,
6 kingdoms:
- the Deep band holds as few as 8 veins on one seed;
- ranking the veins themselves always yields every tier while there are mountains at all.

Ranked like this, most kingdoms walk 2.2–3.9 days to their nearest steel vein (one outlier
per seed at most, 5.1–5.3), and 1.9–3.9 days to iron. Steel ranges are shared between
neighbours: contested ground, by design. A per-kingdom ranking lost because some kingdoms'
land holds no mountains at all.

## Numbers (balance knobs; the AI labs tune them)

**Smelting.** One batch is 2 ore plus wood → 1 bar. Rate and crew are as today.

| Bar | Ore per bar | Wood per bar |
|---|---|---|
| Bronze | 2 copper ore | **10** (today's iron price) |
| Iron | 2 iron ore | **20** |
| Steel | 2 steel ore | **50** |

Wood climbs steeply on purpose (the user, 2026-10-06). Wood is too easy to produce and too
little needed in today's game; a steel economy is a forestry economy.

**The Smelter's input cap.** A full Smelter crew (2 Miners, double rate) makes up to 4 bars a
day. At 50 wood a bar that is 200 wood a day, so today's `InputCap` of 200 per input holds
just one day of feed for steel. The cap rises to **500**: "a few days of feed" for iron, about
2.5 days for steel. It is the knob to watch in the labs.

**Mines.** A Mine produces ore exactly as today's Mine (rate, crew, buffer). Build costs:

| Mine | Build cost |
|---|---|
| Copper mine | 15 wood + 10 stone (as today) |
| Iron mine | 15 wood + 10 stone + **10 bronze** |
| Steel mine | 15 wood + 10 stone + **10 iron** |

**Swords.** Forged at the Smithy, Soldier only. One sword per unit, as today.

| Sword | Power | Cost |
|---|---|---|
| Bronze sword | +3 (as today) | 2 wood + 3 bronze |
| Iron sword | **+5** | 2 wood + 3 iron |
| Steel sword | **+7** | 2 wood + 3 steel |

A soldier is 30 health and 3 power, so each step adds about two thirds of a bare soldier's
punch.

**Upgrading a sword.** A soldier already wearing a sword may equip a **better** one: the new
sword's buff replaces the old one, and the old sword goes back into the store it was equipped
at, so bronze swords hand down to the militia. A sword of the same or a lower tier is still
refused (one sword per unit).

## What changes in the code

### Resources and kinds (renames first, then new values)

The bronze rung **is** today's chain, renamed, so everything tuned around it keeps its
balance:
- the first raid and its war chest;
- the AI's arming rung;
- cache and camp loot.

| Today | Becomes | Id |
|---|---|---|
| `Resource.Ore` | `CopperOre` | 3 (kept) |
| `Resource.Iron` | `Bronze` | 9 (kept) |
| `Resource.Sword` | `BronzeSword` | 5 (kept) |
| `StructureKind.Mine` | `CopperMine` | kept |
| — | `IronOre`, `Iron`, `IronSword`, `SteelOre`, `Steel`, `SteelSword` | new, appended |
| — | `IronMine`, `SteelMine` | new, appended |

**Order matters:** rename first, building after each rename, and only then append the new
`Iron`. Otherwise a stale `Resource.Iron` reference (31 test files touch these names) would
silently compile against the new iron.

### Veins

- `world.Veins` goes from a tile set to tile → ore, a `SortedDictionary` in (y, x) order.
- `KnownVeins` and `SurveyedBarren` stay tile sets; the ore is read from `world.Veins`.
- `Veins.Seed` still decides *where*. A new `Veins.AssignOres` decides *which*, in
  `Genesis.Build` right after the wilderness field.
- The safety net (every mountain range gets a vein) is unchanged.

### Survey

`SurveyReportEvent` reports the ore with the vein. On the wire, each known vein carries its
ore, and so does a unit's survey report.

### Mines

- `StructureSpec` gains `RequiredOre` (the vein ore a mine must stand on).
- Placing a mine checks that the vein is known (as today) **and** that its ore matches.
- Each mine's `OutputResource` is its ore.

### Smelter: recipes

The refiner's single `InputCost` / `OutputResource` becomes an ordered **`Recipes`** list,
best first. Each recipe is an output plus its inputs. One source of truth: the single-recipe
fields go.

- **Accepting input:** the Smelter accepts any input some recipe names, up to `InputCap`
  each (500; wood is shared by all three recipes).
- **Each production tick:** take the first recipe with at least one affordable batch, then
  produce `min(rate, affordable, free storage)` of it. One recipe per tick.
- **Dormancy and re-arm:** unchanged. The tick sleeps when no recipe is affordable and wakes
  on a deposit or new workers.
- **Integer and all-or-nothing,** as today.

### Smithy and equip

- New catalog rows for the iron and steel swords.
- `EquipRules` gains the upgrade: a better tier of the same buff kind swaps in, and the old
  item is deposited back into the store.
- The tier order lives in one place: a rank on the equipment spec.

### AI (this milestone: keep it working, don't teach the ladder)

- The AI's mining, smelting and arming rungs read the renamed bronze chain.
- They only place copper mines.
- Climbing to iron and steel is a follow-up milestone.

### Snapshot v55

- Vein rows carry the ore.
- New `Resource` and `StructureKind` values need no format change: holdings and structures
  are serialized by value.

### Wire (the client is not touched; a client note is updated)

- `VeinDto` carries the ore.
- The structure spec DTO carries recipes in place of `Inputs` / `OutputResource` for
  refiners.
- Resource ids 3, 5 and 9 keep their ids but now mean copper ore, bronze sword and bronze.
  The client's labels for them, and its lack of icons for the new ids, go in
  `docs/client-intent-migration.md`.

## Acceptance tests

- **Ore assignment:**
  - on generated worlds, the shares hold (within ties);
  - every steel vein is at least as remote as every iron vein, and every iron vein as remote
    as every copper vein;
  - changing a share dial moves the cut;
  - genesis is deterministic.
- **Survey:** the report and the known-vein wire carry the ore.
- **Mines:**
  - a mine on the wrong ore is refused;
  - an iron mine without bronze waits for materials like any site;
  - a steel mine needs iron.
- **Smelter:**
  - each ore smelts to its bar at its wood price;
  - with copper and iron both stocked, iron goes first;
  - when iron runs out, the same tick picks copper.
- **Swords:**
  - each sword's power applies in combat;
  - a soldier upgrades bronze → iron, and the bronze sword is back in the store;
  - a same-tier or a downgrade is refused.
- **Twin-run and mid-run restore** across smelting and an upgrade: the hashes match
  (snapshot v55).
- **The AI labs still pass** on the renamed bronze chain.

## Future expansion (explicitly not this milestone)

- **Unique uses for each metal,** so lesser metals stay needed. Ideas from the discussion:
  - bronze for tools, fittings and carts;
  - iron for armour or fortification fittings;
  - steel for elite gear or crown regalia.
  Each is its own item or structure row.
- **The AI climbs the ladder:** it surveys for iron and steel, builds the higher mines, and
  arms with better swords.
- **Loot by metal:** caches, camp hoards and war chests pay out iron and steel in the far
  bands (`docs/secrets-and-progression-proposal.md`, cache tiers).
- **More tiers:** another share on `VeinConfig`, another recipe, another mine.
