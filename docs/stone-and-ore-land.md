# Stone and Ore: Hill Claims and Mountain Veins

> Decision doc (M44, 2026-10-01). Spec: `docs/m44-stone-and-ore-spec.md`.
> Audit: `docs/determinism-audit.md` § M44 addendum.

## The decision

Stone and ore are **slow but plentiful**. That sets them apart from farmland
and wood, which wear out and so are scarce.

- **Stone comes from Hills.** A Quarry stands on a Hills tile and claims 6
  Hills tiles within 2, the way a Farm claims grassland. The claim is
  exclusive across all owners and kinds, but the hillside **never wears
  out** (`DegradeAmount = 0`).
- **Ore comes from Mountain veins.** At genesis, about a third of Mountain
  tiles are seeded with a vein, and every connected mountain range gets at
  least one. A Mine may only stand on a vein its owner **knows about**.
  Veins never run out.
- **A faction finds a vein by surveying.** A Miner walks to a Mountain tile
  and digs for a day (`VeinConfig.SurveyTicks`). He then reports the
  **nearest** vein his faction doesn't know yet within 2 tiles
  (`SurveyRadius`). Mountain tiles nearer than that vein are marked proven
  barren. If he finds nothing, every tile in reach is proven barren.
- **Only the finder knows about a vein.** It stays private until a Mine or
  Mine site stands on it. From then on, any faction that sees the mine
  learns the vein for good.

This also fixes a bug: the code had Hills→Ore and Mountain→Stone, the
reverse of the design.

## Why

**Stone: claim the land, but don't wear it out.** Without claims, five
quarries fit on one hillside and the land costs nothing. Claims make hills
territory you hold and fight over, which is the "takes up land like a
farm" cost. Wear would make stone scarce, and the user ruled that out.
Hills stay off the fertility ladder. Their fertility is the fixed Hills
baseline, so the M15 taper always works out to exactly 1, and the quarry
runs at full rate for as long as it holds the land.

Options that lost:
- **A Hills fertility ladder that wears and recovers.** It would need new
  bands, a snapshot section, and client land-bar support. It makes stone
  scarce, which the user did not want.
- **No claims, as before.** Stacking stays optimal, and hills cost
  nothing.

**Ore: hidden veins found by survey.** Mountains are common knowledge
(M22), so before this change a mine site was visible and valid from tick
0, and ore never pushed anyone outward. Hidden veins make ore exploration.
Finding a vein costs time, but the answer is almost always yes. A vein
never depletes, so finding it is the whole cost.

Options that lost:
- **Search one tile at a time**, and a **daily luck roll.** The user chose
  an area sweep. A sweep takes fewer clicks and the wait is predictable.
- **Finite reserves.** These turn ore into a scarcity mechanic.
- **Public veins.** These remove the edge a faction earns by
  prospecting.
- **A Mountain ladder.** Same objection as the Hills ladder, and it adds
  nothing for a resource that doesn't wear out.

**Density over placement (user, 2026-10-01).** On 252×252 maps, Mountain
is only about 5% of land, and many starts are 30+ tiles from any mountain.
The spec first proposed start-placement rules and mountains raised near
each start. The user chose the simpler rule: about 1 in 3 mountain tiles
holds ore (`VeinConfig.OneIn = 3`), and every range gets at least one vein.
Changing how far starts sit from mountains is deferred (see below).
`WorldFactory` logs each start's nearest-mountain distance so tuning can
see it.

**Seeding draws no sim Rng.** Veins come from a SplitMix hash of
`(VeinConfig.Seed, x, y)`. The map seed is passed in as that seed. Every
other random stream (lifespans, caches, idols) stays byte-identical. Veins
are persisted rather than re-derived, following the world-generation
freeze rule.

**Only Miners survey.** "Miners stake the land." The AI trains exactly one
Miner when it needs ore and has none (`AiMemory.OreStarved`).

**A found vein counts straight away.** The Miner does not have to walk home
to report, unlike an M38 scout. Ore is not meant to be scarce, and a
risky walk home would mostly slow the early game.

**Old saves.** v44 saves are rejected, which is standard for any version
bump (`Snapshot.Restore` requires an exact version match).

## Future expansion

- **Start fairness for mountains and hills.** Options: raise a small
  mountain range near ore-poor starts, have starts prefer mountains, or
  lower `MountainMin`. That last one would also need the client's
  `WorldGeometry.MountainBand` to match. This is additive: veins seed off
  whatever terrain worldgen freezes.
- **Vein richness tiers or different ores.** A per-vein byte would go
  next to the vein set, as an additive snapshot field.
- **Scout charts carrying vein hints** (M38 `SecretHint`), and trading
  vein locations through diplomacy.
- **Wear on Hills.** This would reverse "slow, not scarce" and needs an
  addendum here first.
- **Rebalancing stone and ore rates.** Catalog rates are unchanged by M44.

## Update 2026-10-06 — three ores (M51)

Veins now hold one of three ores: copper, iron or steel (`docs/m51-ore-tiers-spec.md`).

- **Where they lie:** at genesis, after the wilderness field (`docs/wilderness-bands.md`), the
  veins are ranked by remoteness. The farthest `SteelSharePercent` (10) are steel, the next
  `IronSharePercent` (20) iron, the rest copper.
- **Seeding is unchanged:** density, the per-range safety net and private knowledge work as
  above.
- **Surveys:** a survey reports the ore.
- **Mines:** the Mine became the Copper mine. The Iron mine (costs bronze) and the Steel mine
  (costs iron) stand only on their own ore.
