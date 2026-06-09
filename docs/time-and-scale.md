# Time and Scale

## The decision

**1 sim-tick = 1 game-minute. The in-fiction calendar is a 360-day year
(12 months × 30 days). All durations in the simulation are denominated
in ticks via the `Sim.Core.Time` vocabulary (`Time.Minute / Hour / Day /
Week / Month / Year`).** Real-world wall-clock pace is a separate,
orthogonal scaler (`Sim.Server`'s `TicksPerSecond`) that uniformly
stretches or compresses the calendar against real time without
disturbing a single in-fiction proportion.

## Why

The codebase had three uncoordinated time mappings:

1. **Food** assumed `1 tick ≈ 1 minute`
   (`FoodConsumptionPeriod = 60` as "1 hour",
   `StarvationStartDelay = 1440` as "1 day"). Self-consistent.
2. **Population** declared `TicksPerYear = 250`. Under (1), a "year" was
   ~4.2 hours — *shorter than a "day"*. Gestation at `300` was 5 hours;
   a "60-year lifespan" came out to ~2.5 hours of wall-clock at 20 tps.
   Breeding outran every other system.
3. **`TicksPerSecond`** in the server was a third number nobody had
   reconciled with the other two.

Comparing "how long is a build vs. a war telegraph vs. a lifespan" was
impossible because the numbers spoke three different languages. The
population snowball that surfaced in playtesting was a direct downstream
consequence — gestation in 5 hours / death in 2 days is just not a
breeding curve a balance pass can fix; the *units* were wrong.

The fix has two parts, and both matter:

- **Canonical tick (1 = 1 game-minute).** Picked so the food subsystem
  needed zero change — every existing constant there was already in
  coherent minutes, just unlabeled. Most movement / build / production /
  combat numbers were also already minutes-ish; the rename hardened
  them without re-tuning. The only structurally broken value was the
  population clock, which got rederived from the calendar.
- **`Time.*` vocabulary.** The point isn't legibility for its own sake;
  the point is that *you can't accidentally write an incoherent
  duration*. `9 * Time.Month` and `Time.Year` make proportionality
  structural — a reviewer reading `GestationTicks = 9 * Time.Month`
  alongside `TicksPerYear = Time.Year` cannot fail to see that gestation
  is well under a year. The literal-int regime hid this entirely.

### What was ruled out

- **Keeping the canonical tick at 6 seconds (a more typical RTS rate)**
  was considered and rejected. It would have forced every existing
  food / build / production constant to be multiplied by 10, and the
  resulting numbers would have lost any human-recognisable mapping to
  "an hour" or "a day" — the very legibility the vocabulary is meant
  to enforce.
- **A floating-point "game seconds" type** (à la Unity's `Time.deltaTime`)
  was rejected on the determinism contract from `docs/architecture.md`:
  durations participate in event scheduling and persistence, both of
  which require integer arithmetic across replays and snapshots.
- **A shorter game-year (120 or 240 days)** was considered for faster
  generational turnover. Rejected for the first pass because a 360-day
  calendar is the cleanest mental model and demographic tempo can be
  re-tuned later via `LifespanMinYears / LifespanMaxYears` and fertility
  windows without touching the clock itself. Calendar changes propagate
  to *every* `Time.Year` reference; balance changes don't. We want the
  knob, not the foundation, to be the tuning surface.
- **Keeping `TicksPerSecond` mixed into balance** was rejected — and
  this is the single most important separation. `TicksPerSecond` now
  has exactly one job (real-time pace) and is never read inside
  `Sim.Core`. A 2-hour trip will always be exactly 4× a 30-minute trip,
  in both fiction *and* wall-clock, regardless of how `TicksPerSecond`
  is set.

## Future expansion

- **Real-time pace is a free dial.** Servers can pick `TicksPerSecond`
  to match the campaign's intended cadence — a slow persistent campaign
  at 5 tps means a 60-year life is ~7 real-weeks of server time; a 60
  tps blitz compresses the same life to ~10 real-days. No balance
  constant changes; every in-fiction proportion is preserved.
- **Year-length tuning** stays cheap: changing `Time.Year`'s factor
  (or the `LifespanMinYears` range) rebalances generational tempo
  without touching food / build / combat math. The vocabulary is the
  insulation layer.
- **New domains adopt the vocabulary trivially.** Any new system that
  schedules an event N ticks in the future writes `N * Time.X` and gets
  the legibility benefit automatically — there's no central registry
  to update.
- **Persistence is forward-compatible.** Configs serialise their fields
  as raw ticks (see `Snapshot` config writers); a saved world remains
  loadable regardless of how `Time.Year` is later redefined, because
  the snapshot stores the *resolved* number, not the symbolic
  expression.

## What is intentionally deferred

- **`DiplomacyConfig.ProposalExpiryTicks`** is left at the legacy
  ~3.3-hour value (`200 * Time.Minute`) pending a dedicated diplomacy
  tuning pass. Relabeled, not re-balanced.
- **The starvation-cadence numbers** (1-day grace + 1-hour interval)
  are reasonable first-pass values pulled from the original
  `FoodConsumptionConstants` comment; tuning is a playtest-driven
  exercise downstream.
- **A `RoadConstants.DECAY_PERIOD` recalibration.** A maxed road now
  vanishes in ~70 game-days under the new clock — durable, but
  whether that's the *right* number depends on how persistent the
  intended campaign actually is. Out of scope for the clock work.

## Acceptance test (informal)

The following invariants pin the decision and should be visible from any
config file at a glance:

```
TicksPerYear        ==  Time.Year                ==  518,400
GestationTicks      ==  9 * Time.Month           ==  388,800
War telegraph delay ==  6 * Time.Hour            ==      360
Citizen meal period ==  Time.Hour                ==       60
Famine grace        ==  Time.Day                 ==    1,440
```

Year > Month > Day > Hour > Minute in *every* place a duration appears.
If any future config violates this ladder, the bug is in the config, not
the calendar.
