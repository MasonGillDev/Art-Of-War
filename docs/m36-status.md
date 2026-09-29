# M36 status: haul queue and named routes

**Decision doc:** `docs/hauling-queue-and-routes.md`. Started 2026-09-23.
**Rule:** the old substrate stays untouched for the AI. No commits unless asked.
No balance targets; the user tests by hand. Each phase ends with its tests
green and the full suite not worse than the baseline.

## Phases

| Phase | Scope | Status |
|---|---|---|
| A | Mixed cargo: `Unit.Cargo` bag, explicit amounts on load/unload/haul, death drops every resource, snapshot + wire (`UnitDto.Cargo`) | done 2026-09-23 (`MixedCargoTests` 10, `LoadCargoTests` rewritten for mixing) |
| B | Haul queue: `HaulJob` + Set/Clear/Requeue intents, `HaulingDriver` (round-robin, nearest-to-pickup, in-flight need, shared-source reservation), wired into `GameHost` | done 2026-09-23 (`HaulQueueTests` 16 incl. twin-run + mid-queue recovery) |
| C | Named routes: `HaulRoute` + Set/Clear/AddCrew/RemoveCrew/ServeStop intents, percent-of-capacity rules, crews that regroup at stops, `Unit.RouteId` excluded from dormancy | done 2026-09-23 (`HaulRouteTests` 15 incl. twin-run + mid-route recovery) |
| D1 | Wire: `ViewDto.HaulQueue` (free haulers, waiting count, longest wait, jobs in line order with driver verdicts) and `ViewDto.HaulRoutes` (stops, rules, crews with state), owner-only | done 2026-09-23 (`HaulWireTests` 2 incl. 100x pure-read) |
| D2 | Prod client (world UI, user's design 2026-09-23): drag from source building to destination, release = fly to the source and choose cargo / once-or-recurring / amount on the bubble; routes on the patrol template (crew selected, click stops, per-stop pickup/drop % on the rim + slider); job rows on building bubbles, queue line on the castle, route + mixed cargo on people, Tab chips. `SetHaulRouteIntent` gained an optional first crew. | built 2026-09-23, `_AowTypeCheck` + `_AowWireBoundary` clean, **not run in Play** |

## Baseline

- Full suite 2026-09-23 before any code change: 1185 + 49 passed, 0 failed.
- After Phase A: 1194 passed, 1 failed (`ALadenCarrier_IsRefusedBeforeItWalks` pinned the retired
  single-resource rule; rewritten as `AFullCarrier_IsRefusedBeforeItWalks`).
- After Phase B: 1211 + 51 passed, 0 failed.
- After Phase C: 1226 + 52 passed, 0 failed.
- After Phase D1: 1228 + 52 passed, 0 failed.
- After D2's server change (route + first crew, client JSON pinned): 1229 + 53 passed, 0 failed.

## Deviations from the decision doc, for the user

- **A job skipped for an empty source keeps its place** instead of going to the back. Sending
  it back would write a durable intent every think for every dry job (unbounded log growth), and
  keeping its place means the job that has waited longest is served first when its source refills.
  Every job still gets its turn. A job that TAKES a hauler goes to the back, as decided.
- **`RequeueHaulJobIntent` has no stamp fence.** One think can send a hungry job to the back
  several times; applying "move to back" in submission order reproduces the driver's line
  exactly, where a fence would need predicted stamps that a same-tick player intent breaks.
- **Crews travel together by regrouping at each stop**, not in formation: every member walks
  from the same tile to the same tile (the same path), and the crew waits at a stop for a
  straggler. Soldiers do not yet engage threats on their own; that is the patrol posture,
  deferred.
- **Only Hauler-role members carry.** Soldiers and archers in a crew are escort only.
- **A route stop trades only with a structure of the route owner's.** No ground piles, no raiding.

## Open

- Route editing in place (today: clear and recreate; crews are lost).
- Escort engagement (reuse the M29 patrol posture knobs).
- An unreachable job source dispatches a hauler that is rejected at `HaulIntent` every think;
  harmless but noisy in the log. Candidate: report it as Unserviceable.
- `HaulPickupEvent` still has its own withdraw code; `CargoTransfer.WithdrawFrom` now exists and
  `LoadCargoIntent` uses it. Folding the pickup event onto it is a cleanup worth doing.
