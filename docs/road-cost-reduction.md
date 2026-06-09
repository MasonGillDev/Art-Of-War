# Road Cost Reduction: Proportional, Not Flat

## Decision

A maxed-out road reduces the tile's biome cost by a **percentage**
(`RoadConstants.MAX_REDUCTION_PERCENT`), not by a flat absolute amount.
Reduction scales linearly with road condition:

```
reduction = biomeCost * MAX_REDUCTION_PERCENT * condition / (100 * CONDITION_MAX)
cost      = max(MIN_COST, biomeCost - reduction)
```

Initial value: `MAX_REDUCTION_PERCENT = 66`. At max condition every
foot-traversable biome gets a ~3× speedup:

| Biome     | Raw cost | Maxed road | Speedup |
|-----------|---------:|-----------:|--------:|
| Grassland |       10 |          4 |  2.5×   |
| Hills     |       25 |          9 |  2.8×   |
| Forest    |       30 |         11 |  2.7×   |
| Desert    |       40 |         14 |  2.9×   |
| Mountain  |       45 |         16 |  2.8×   |

Roads do not apply on water — `MovementCost.TerrainCostFor` routes
`Traversal.Water` through `BoatMovementCost`, bypassing `Road.EffectiveCost`.

## Why

The original implementation used a flat `MAX_REDUCTION = 8` cost
subtraction. That produced wildly disproportional speedups by biome:

| Biome     | Raw | Old maxed road | Old speedup |
|-----------|----:|---------------:|------------:|
| Grassland |  10 |              2 |       5.00× |
| Hills     |  25 |             17 |       1.47× |
| Forest    |  30 |             22 |       1.36× |
| Desert    |  40 |             32 |       1.25× |
| Mountain  |  45 |             37 |       1.22× |

A road through grassland was transformative; a road over a mountain
pass shaved 18% off travel time and wasn't worth the investment. The
gameplay incentive — "build roads where movement is hardest" — was
inverted: it paid to road the *easy* terrain because that's where the
absolute speedup landed.

Proportional reduction inverts this back. The road shaves the same
*fraction* of cost everywhere, so the absolute time saved on a mountain
tile dwarfs the time saved on a grassland tile (both proportionally,
and in raw ticks: mountain road saves 45−16=29 ticks per tile vs.
grassland's 10−4=6). Biome differentiation is preserved at the
absolute level — mountain road (16) is still 4× slower than grassland
road (4) — but the *strategic value* of investing in a road is now
proportional to how much it hurts to traverse the terrain.

### Why percentage, not a per-biome flat reduction

A per-biome flat-reduction table (e.g. road on Forest reduces by 22,
road on Mountain reduces by 33, etc.) would have the same effect at
max condition. Rejected because:

- Adds a static table that has to be kept in sync with the biome cost
  table — two sources of truth for one piece of game balance.
- Doesn't compose with the linear-in-condition reduction: a half-built
  road on mountain should give half the speedup, which falls out
  naturally from `cost × percent × condition` but not from a flat table.
- A future biome (jungle, swamp, snow) needs only a cost — its road
  behavior is automatic.

### Why 66% (not 80%, not 50%)

Considered three values:

- **80%** — preserves the old grassland behavior (cost 10 → 2) exactly,
  but at the cost of making every maxed road a near-superhighway (5×
  speedup). Mountain at 9 ticks/tile is faster than raw Grassland (10).
  Roads start to dominate over terrain choice.
- **50%** — conservative, terrain still dominates. But mountain at 23
  ticks/tile is only 1.95× faster than raw mountain — not a big enough
  payoff to incentivize the road-building feedback loop on hard terrain.
- **66%** — chosen value. Strong incentive to road (3× speedup) without
  letting a road erase the terrain underneath it (mountain road is
  still 4× slower than grassland road).

This is a tuning knob, not a structural choice. The percentage can
move as balance feedback comes in; the *shape* (proportional, linear
in condition, percentage-based) is what this doc pins.

### Why no MIN_COST floor change

`MIN_COST = 1` was a guard for the old flat-8 model when reduction
could exceed biome cost (a fictional cost-4 biome would go negative).
With proportional reduction the floor is mathematically unreachable
at `MAX_REDUCTION_PERCENT < 100` for any positive biome cost — the
floor is now belt-and-suspenders, kept for defense-in-depth against
future percent values approaching 100 and for the integer-math
guarantee that movement is always strictly positive.

## Future expansion

- **Per-biome reduction caps** — a future "stone holds in cold" or
  "mountain pass road is naturally weaker" mechanic could multiply
  `MAX_REDUCTION_PERCENT` by a biome-specific factor. Easy to add as
  a per-biome multiplier without disturbing the math.
- **Per-unit-kind road benefit** — wheeled units (carts) get extra
  reduction on roads; foot infantry gets baseline. Adds a unit-side
  multiplier; the road tile's stored state stays the same.
- **Per-edge condition (M8)** — roadmap entry. Edge-based condition
  would still use this same percentage-of-cost shape; only the per-tile
  → per-edge lookup changes.

## Out of scope (intentionally deferred)

- **Non-linear reduction curve** — e.g. road at 50% condition gives 80%
  of the max speedup. The bandedness of the rest of the movement model
  (crowding, biome) argues against a curve here; if it's needed, model
  it as a band table not a continuous function.
- **Removing MIN_COST** — see "Why no MIN_COST floor change" above.
  Keep it.

## Acceptance tests

- `RoadCostTests.Cost_NeverDropsBelow_MIN_COST` — grassland at cap = 4,
  above MIN_COST.
- `RoadCostTests.ForestRoad_AtCap_ProportionallyReduced` — forest at
  cap = 11 (was 22 under flat-8).
- `RoadCostTests.MountainRoad_AtCap_ProportionallyReduced` — the
  load-bearing case: mountain at cap = 16 (was 37). Roads matter on
  hard terrain.
- `RoadPathfindingTests.UnitWalksFasterOnRoad_ThanOnRaw` — pinned at
  5 tiles × 4 = 20 ticks (was 5 × 2 = 10).
- `RoadDecayTests.EffectiveCost_AppliesDecay_BeforeCostReduction` —
  decay-then-reduce math pinned at grassland-at-cap = 4.

## References

- `src/Sim.Core/Roads/RoadConstants.cs` — the tuning knob.
- `src/Sim.Core/Roads/Road.cs::EffectiveCost` — the formula.
- `docs/movement-cost.md` — the surrounding cost model (crowding,
  fog-aware planning, ground-truth execution) that this reduction
  composes with.
