# M51 status: ore tiers (copper, iron, steel)

Spec: `docs/m51-ore-tiers-spec.md`. Built 2026-10-06 in one phase, on main.

## What was built

- **Renames first.** Today's chain became the bronze rung:
  - `Ore` → `CopperOre`, `Iron` → `Bronze`, `Sword` → `BronzeSword`, `Mine` → `CopperMine`;
  - the ids are unchanged;
  - the rename alone passed both full suites before anything new went in.
- **New resources:** `IronOre`, `Iron`, `IronSword`, `SteelOre`, `Steel`, `SteelSword`
  (ids 10–15).
- **New kinds:** `IronMine` and `SteelMine` (25, 26).
- **Vein ore:** `Veins.AssignOres` ranks the veins by wilderness minutes at genesis.
  - The farthest `SteelSharePercent` (10) are steel, the next `IronSharePercent` (20) iron, the
    rest copper.
  - The shares are dials on `VeinConfig`.
  - `world.VeinOre` holds the result.
- **Surveys** report the ore: `SurveyReportEvent.Ore`, and the notice names it.
- **Mines:**
  - `StructureSpec.RequiredOre`; `RequiresVein` is derived from it.
  - One catalog helper builds all three mines.
  - The iron mine costs 10 bronze, the steel mine 10 iron.
  - `PlaceSiteIntent` refuses a mine on another ore's vein.
- **The smelter:**
  - `StructureSpec.Recipes` (best first) replaced `InputCost`.
  - Recipes are steel 2 ore + 50 wood, iron 2 + 20, bronze 2 + 10.
  - Each tick makes the first recipe it can afford.
  - `InputCap` is 200 → 500.
- **The extractor output store:**
  - `Extractor.Output` (by resource) replaced the single `Buffer` int;
  - `Buffer` is now the derived total;
  - its setter works for ordinary extractors only.
- **Swords:**
  - +3 / +5 / +7 power, each costing 3 of its bar plus 2 wood, at the Smithy.
  - `EquipmentSpec.Slot` and `Rank`: the three swords share the "sword" slot, with their own
    buff kinds so a dropped sword is the right one.
  - A better sword swaps in, and the old one goes back to the store.
  - The same or a lesser sword is refused ("already carries …").
- **The AI** mines copper only (its free-vein list is copper veins) and smelts bronze only.
  Raids also target iron and steel mines.
- **Wire:**
  - `VeinDto.Ore`;
  - the spec DTO's `Recipes` (`RecipeDto`) in place of `Inputs`;
  - a smelter's holdings list every bar.
  - The client note is in `docs/client-intent-migration.md`.
- **One rule, one place:**
  - The server's `MineStandsOn` duplicated the core's mine check and now reads
    `Veins.IsMineAt`.
  - **Which kinds are extractors** was a hand-kept list in five places: the construction
    switch, the snapshot restore, two AI lists and the raid targets. It is now
    `StructureSpec.IsExtractor` (a required biome, or a refiner).
  - The review caught the new mines missing from those lists: an iron or steel mine site
    threw on completion. `OreTierTests.AHigherMine_IsBuilt_Mines_AndSurvivesASnapshot`
    reproduced the throw before the fix and passes after it.
  - The battle-board footprint and the scout lexicon also name the new mines.
- **Snapshot v55.**

## Results

`OreTierTests` covers:
- the shares and the remoteness ordering;
- the dial;
- deterministic genesis, and the ore surviving a snapshot;
- the survey reporting the ore;
- the mine-by-ore matrix and the mine build costs;
- each ore smelting at its wood price, best first then the next;
- a razed smelter spilling every bar;
- mixed bars surviving a snapshot and running on to the same hash;
- the sword ladder, forging an iron sword, the upgrade swap, and refusing a same or lesser
  sword;
- a fallen soldier dropping the right sword.

Updated tests:
- the refining tests run the bronze recipe;
- the mining tests lay copper veins;
- the sandbox's two-swords refusal now reads "already carries".

## Not done (follow-ups)

- **Unique uses for each metal,** so lesser metals stay needed (items and structures).
- **The AI climbs the ladder:** surveying for iron and steel, the higher mines, better swords.
- **Loot paying out iron and steel** in the far bands.
- **Balance labs** for the wood prices, the input cap and the sword powers.
