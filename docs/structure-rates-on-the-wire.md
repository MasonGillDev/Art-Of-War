# Structure rates on the wire (kingdom analytics, first slice)

## The decision

Own structures carry two server-computed rates, both **per game day**:
`StructDto.OutputPerDay` (plus `Producing`) on extractors and refiners, and
`StructDto.EatsPerDay` on food homes (House, Castle). The production rate is read
from `Sim.Core.Logistics.ProductionRate`, which is the formula `ProductionTickEvent`
itself spends. The two numbers share one unit so a player can read a farm's
"+24 / day" directly against a house's "−8 / day".

## Why

- **One unit, per day.** Production periods differ by kind (lumber 30 min, farm 2 h,
  quarry 12 h, mine 1 day) and meals come every 6 h. Per-period numbers can't be
  compared without doing arithmetic, which is exactly what the user asked to get rid
  of. Every catalog period divides a day, so per-day is exact integer arithmetic.
  Losing options: per-period values (they compare poorly); per-hour (fractions for
  the slow kinds, like a mine's 1/24).
- **Server-computed, not client-derived.** The real rate depends on role bonus,
  settled-worker housing bonus, and the M35 live-fertility taper over claim tiles.
  The client can't see a worker's home or whether that home is fed, so it can't
  reproduce the rate. Rebuilding it in the client would also copy balance knobs
  into two codebases (see `ui-wire-only`: every number on the glass is a wire fact).
- **Extracted formula, not a parallel estimate.** Before this change the tick
  computed the rate inline. It now calls `ProductionRate.PerPeriod`, a pure read,
  so the view can never disagree with what the buffer actually receives.
  `ProductionTick_SpendsExactlyTheReportedRate` pins this. The losing option was a
  projector-side re-implementation, which drifts on the next balance pass (see
  `ThinkContext.LaborLedger` below, which has already drifted).
- **Rate at pace, plus a running flag.** `OutputPerDay` is what the building makes
  *while running*. The realized average over the last day is not reported, because
  that would need a history the sim doesn't keep. `Producing` (= `TickArmed`) says
  whether it is running now. The client dims the row and names the reason it can
  see: no workers, or the output store full. A refiner's rate assumes it is fed.
- **Castle eats its own mouths.** `EatsPerDay` on the castle is everyone not housed
  (`FoodConsumption.ResidentsOf`), not the whole realm. The castle bubble's realm
  rows still carry the realm totals.

## Future expansion

- Realized throughput (what actually left the buffer in the last day), a refiner's
  input burn per day, and a Workshop/Smithy craft rate can all be added as fields
  alongside these without changing them.
- Runways ("this pantry lasts 3 days") and "this farm feeds N houses" are client
  arithmetic over these fields and need no new wire.
- `ThinkContext.LaborLedger` keeps its own catalog price for a farmhand. See the
  update below: that is a planning price, not a copy of this formula.

Tests: `tests/Sim.Tests/StructureRatesWireTests.cs`, `tests/Sim.Tests/LaborLedgerPricingTests.cs`.

## Update 2026-09-23 — the AI does not price hands from OutputPerDay

**Decision:** the brain's labor ledger keeps pricing a farmhand from the catalog
(untapered, unhoused). It does not scale that price by its farms' reported
`OutputPerDay`. Only its demand side now reads the shared
`FoodConsumption.DemandPerDayPerCitizen` (same number as before).

**Why:** both ways to use the new field lost on the 160-day balance lab
(baseline: population 57 at d160, food never below ~2,250):

- *Scale both ways* (clamped to between 1/4 and 3 times catalog): faster early
  growth (26 vs 21 at d40), then the reserve ran out and the castle went into a
  famine at d140 (food −103, 2 deaths). `OutputPerDay` is the rate *while
  running*: a farm whose buffer sits full waiting for haulers still reports it.
  A reading above catalog is food not yet delivered.
- *Scale down only* (tired soil raises the hands asked for): population 47 at
  d160, falling. More hands on a worn field do not grow more food; worn soil
  calls for new land (Irrigate, new farms), not more labor.

The catalog price is cautious on purpose. The settled-worker bonus is headroom
the ledger does not count, and the colony's stability depends on it. The earlier
note here calling the ledger "drifted" was wrong.

**Future expansion:** if the brain should react to tired soil, the signal belongs
in the rung that sites farms (retire a farm whose `OutputPerDay` has fallen under
some fraction of catalog, found a new one), not in the ledger. A *delivered*
throughput field (food that actually left the buffer per day) would be a fair
input to the ledger; `OutputPerDay` is not.
