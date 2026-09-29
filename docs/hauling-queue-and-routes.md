# Hauling: one shared queue for the simple work, named routes for the precise work

## The decision

Hauling for human players is rebuilt as two tools:

1. **The haul queue.** Every hauler not on a named route is in one pool.
   Players post *jobs* (keep a stock at a level, or deliver an amount once).
   Jobs sit in a round-robin queue. The job at the front takes the free
   hauler nearest its *pickup* tile, and goes to the back the moment a
   hauler takes it. There is no priority.
2. **Named routes.** A looping list of stops, each with pickup/drop rules
   written as a percentage of the crew's carrying capacity, walked by one
   or more crews (haulers plus optional soldiers) that move together and
   never stop circling.

Units can carry several resource types at once (one total capacity), which
both tools need.

The existing automation substrate (orders, pull lines, the priority sort,
`MaxPulledHands`) is left running **for the AI only** until the user has
played the new system and decides to move the AI over. Then the old path is
deleted.

## Why

### What forced it (playtest, 2026-09-23)

The minimal sword chain (food, ore, wood, iron, a house) needs six supply
legs. With one owned hauler per leg that is 6 of ~15 starting bodies. Most
of those legs are tiny flows, so most of those haulers walk empty or wait.
The starting colony spends itself on logistics and ages out before it can
breed. And any one-off need (a construction site wanting wood) means
breaking a leg to free a hauler, then rebuilding it.

The fix is to tie hauler count to **total work**, not **number of legs**.
Roughly `Σ(flow × round-trip ÷ capacity)` over all jobs, rounded up once.

### The queue's rules (user decisions, 2026-09-23)

| Rule | Decision |
|---|---|
| Re-queue | A standing job goes to the back **when a hauler takes it**, not when the trip ends. Long hungry lines get several haulers in a row, one turn each |
| Need | Standing: `target − stock − in-flight`. One-time: `amount − delivered − in-flight`. In-flight = amount already committed to walking toward the destination |
| Trip size | `min(hauler capacity, need)` — no overshoot on the last trip |
| Empty source | No hauler is sent; the job goes to the back. Looking at a job costs no game time. A pass looks at each job at most once, so an all-empty queue cannot spin |
| No free hauler | The pass stops. The front job keeps its place |
| Who is in the pool | Hauler-role units, not on a named route, empty, dormant |
| Matching | The front job picks the free hauler nearest its pickup tile (distance, then y, x, id) |
| Source | Fixed per job (explicit intent). "Nearest granary" is deferred |
| Priority | None. The player's answer to a starving line is more haulers or a named route |

Weighting comes from need, not priority: a satisfied line is looked at and
skipped, so the hungry lines take the haulers.

### The route's rules (user decisions, 2026-09-23)

| Rule | Decision |
|---|---|
| Pickup X% of R | Fill R **up to** X% of capacity (held leftovers count toward it) |
| Drop X% of R | Unload **up to** X% of capacity worth of R; keep what the stop can't take |
| Percent base | Per hauler, its own capacity. Rounded down, minimum 1 |
| Stop can't be served | Skip it, continue. Never wait at a stop |
| Leftover cargo | Keep it. Balancing the chain is the player's job (Factorio) |
| Crews | One crew moves as a unit. Several crews on one route are staggered |
| Idle | Route crews never stop, and never help the pool. "Idle" is not a crew state |
| Guarding | Soldiers can be crew members. Patrols can run the same stops to guard the road |
| Demand | A stop never creates demand. "Keep the granary at 200" is the player's own pull job |
| Roads | Nothing special. Pathfinding takes the fastest path, so used routes pave themselves |

"Up to X% of capacity" was chosen over the user's first wording, "drop X%
of what you carry." That version depends on stop order: splitting evenly
over four houses would take 25/33/50/100. Measuring against capacity makes
25/25/25/25 work in any order and keeps the property the user wanted: the
schedule scales with capacity (more haulers, carts) and never reads stock.

### Alternatives that lost

- **Keep one owned hauler per leg (the named-crew supply line).** The
  playtest failure above.
- **The priority ladder** (`docs/automation-substrate.md`). Lost for
  hauling. Under a shortage priority hides the real problem (too few
  haulers) behind a setting the player has to tune. The round-robin makes
  the shortage visible as queue length and wait time.
- **Pull lines with held hands and a cap** (the 2026-09-19 rule). This
  was a patch on a per-line ownership model. With a queue no line owns a
  hauler, so there is nothing to hoard and nothing to cap.
- **A global job market** was rejected in `automation-substrate.md` on the
  grounds that escorts need claimed crews. That objection still holds, and
  named routes answer it: pooled work is unguarded and the player knows it.
  Anything worth guarding goes on a route.
- **Percent of what the hauler carries.** Order-dependent (above).
- **Wait-at-stop rules** (`DepartWhen` on the old Routine). Lost to
  "never stop": a waiting crew hides a broken chain, a circling one shows it.
- **Replacing the old substrate in place.** The AI runs on it and the user
  wants to play the new system before touching AI behavior.

## Shape

- **Sim state** (`Sim.Core/Hauling`): `HaulJob` (owner, source, dest,
  resource, standing target or one-time amount, delivered so far, queue
  stamp), `HaulRoute` (stops with rules, crews with a cursor). Mutated only
  by intents. The queue is not a list: it is the jobs sorted by
  `(QueueStamp, JobId)`, and "to the back" is a fresh stamp from a
  per-owner counter.
- **In-flight** is read from the haulers themselves: `HaulPlan` carries the
  job id and the planned amount. No claim is needed for pool work: a
  hauling unit is not dormant, and the plan is the record of who is
  working for which job.
- **The driver** (`Sim.Server/Hauling`) evaluates the queue each think and
  submits ordinary intents, the same trust boundary as the substrate.
- **Mixed cargo** (`Unit.Cargo`, a resource → amount bag). Loads and unloads
  take an explicit amount.

## Acceptance tests

- Twin run: queue plus routes, identical `Snapshot.Hash`.
- Snapshot round-trip mid-trip and mid-route; recovery resumes, doesn't repeat.
- Round-robin: with one hauler and three hungry jobs, dispatch order is
  1, 2, 3, 1… A job taken goes to the back.
- Need counts in-flight: two free haulers, one job short by one load → one trip.
- Empty source: skipped, sent to the back, no hauler sent, no infinite loop.
- No free hauler: the front job keeps its place.
- Nearest-to-pickup matching, with tie-breaks.
- Route crews never join the pool.
- Pickup/drop percentages: even split in any stop order; leftovers kept;
  unservable stops skipped.
- Mixed cargo: death drops every resource; snapshot round-trips the bag.

## Future expansion

- **Per-job source sets** ("any granary") fit as a source list on `HaulJob`.
- **Carts** already raise `CargoCapacity`. Both tools read capacity live,
  so carts work unchanged.
- **Moving the AI** onto the queue replaces most of its hauler-staffing
  code. Deferred until the user has played the new system.
- **Deleting the old substrate's haul recipe** follows the AI move.
- **Route escort behavior** (soldiers engaging threats) can reuse the M29
  patrol posture knobs.
- **Trade** between players would be a route stop at another kingdom's
  structure. Nothing here rules it out.

## Update 2026-09-23 — built (server), three refinements

Phases A–C are in (see `docs/m36-status.md`). Three rules came out of the build:

- **A job skipped because its source is empty keeps its place in line**
  rather than going to the back. Moving it would write a durable intent
  every think for every dry job, and keeping its place serves the
  longest-waiting job first when its source refills. A job that *takes* a
  hauler still goes to the back.
- **Two jobs drawing on one source don't both count its stock.** The
  driver reserves what is already walking to a source, so five haulers are
  never sent to a farm holding one load.
- **A crew regroups at every stop.** Members walk from the same tile to the
  same tile, so they share a path; a straggler is waited for at the stop,
  never on the road.

## Update 2026-09-24 — salvage: the queue may carry from what is nobody's

The queue's jobs ran only between the player's own buildings. **Salvage** (`docs/salvage.md`)
is a third job kind that carries **everything** from a charted or seen cache, ruin spill,
or pile that is nobody's, to a building of the player's. It keeps a crew of haulers at
work instead of meeting an amount, and it ends when a hauler arrives and finds nothing.
The rest of the queue is unchanged: one trip per turn, nearest free hauler to the
pickup, to the back of the line.
