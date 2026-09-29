# M37 status: progression (hidden one-shot milestones, omens, arrivals)

**Decision doc:** `docs/progression.md`. Started 2026-09-23.
**Rules:**
- Human players only.
- Every row fires once.
- Surprises: no catalog or counters on the wire.
- The chronicle is out of scope.
- No commits unless asked. Each phase ends with its tests green and the full suite no worse
  than the baseline. Balance numbers are config knobs, and tests derive from them.

## Phases

| Phase | Scope | Status |
|---|---|---|
| A | Ledger + catalog: `ProgressStat`/`ProgressKey`/`ProgressLedger` on `Player.Progress` (null = not enrolled), `Condition` (AtLeast, Gauge, All, Any, Fired), `Effect` (Grant only yet), `MilestoneCatalog` (empty until B), `Progression.Bump`/`Check`/`Fire`; bump points in training, construction, refining; gauge checks at unit-add and every sim-side reveal (not a daily sweep, see the doc's update); `WorldFactory` enrols the human seat (`--progression 0` turns it off); snapshot v36; determinism-audit addendum | done 2026-09-23 (`ProgressionTests` 13) |
| B | Omens + the reprisal: `ProgressionConfig` (genesis-set, snapshotted; `GameWorld.Milestones` derived from it), `Omen` + `Omens` + `OmenDueEvent` (bearing = the wildest octant, arrival = nearest ring in the bearing, hash order, distance floor + dark; slip, then fizzle), `Threat` effect, the **Reprisal** row (id 1), raiders through `SpawnBanditPartyIntent.Materialize`, bandit driver adopts omen raiders from the durable record and marches them on the seat, Repelled/Lost counters keyed on the source milestone; snapshot v37; `--progression` on for the host, off for a bare `ServerOptions` (AI labs) | done 2026-09-23 (`OmenTests` 14) |
| C | Good fortune: `Arrival` (newcomers walk in from the *settled* side and march to the seat; never fizzle), `Rumour` (a stocked `Cache` in unexplored ground in the wildest bearing, omen ends when it is emptied), the reprisal's **war chest** (on `Threat`, dropped where the last raider falls if none escaped); rows **Word spreads** (2), **Far horizons** (3), **A good home** (4); resolved omens now KEEP their outcome (Repelled / Lost / Fulfilled / Fizzled + `ResolvedTick`); `CacheLooting.TryLoot` takes the sim | done 2026-09-23 (`ArrivalTests` 7) |
| D1 | Wire: `ViewDto.Omens` owner-only (kind, state, bearing, size, due / ticks left, seat tile or -1 for a rumour, raiders left, resolved tick); ended omens stay on the wire `OmenDto.RecentTicks` (2 days) so the client can tell the outcome; no milestone or counter on the wire | done 2026-09-23 (`OmenWireTests` 5) |
| D2 | Prod client: `Wire/OmenDto.cs` + `OmenVocabulary`; realm-strip lines for live omens (raid countdown + compass, raiders left, newcomers, rumour); chronicle toasts for each announcement and each outcome (told once per state; not re-told on reconnect) | built 2026-09-23, `_AowTypeCheck` + `_AowWireBoundary` clean, **not run in Play**. Rumour search area drawn on the map texture (dashed gold ring + wash; user's choice over a fog-edge arrow) |

## Baseline

- After Phase A: 1245 + 53 passed, 0 failed.
- After Phase B: 1259 + 53 passed, 0 failed.
- After Phases C, D1 (final server state 2026-09-23): 1271 + 53 passed, 0 failed.
- After the rumour search area + cache-removal fix: 1272 + 53 passed, 0 failed.

## Tests owed

- ~~`ProgressionTests`~~ done (Phase A).
- ~~`OmenTests`~~ done (Phase B).
- ~~`ArrivalTests`~~ done (Phase C). ~~Wire~~ done (D1).

## Notes from the build

- **Darkness is implied by the distance floor today.** The widest sight in the game is a
  tower's 7 tiles, under the bandit floor of 10, so a tile past the floor is always dark.
  The dark check stays (it is the rule if sight ever grows); in practice an omen slips only
  when the player's own presence covers the whole bearing, and fizzles after
  `OmenMaxSlips` if it never clears.
- **North is +Y** on the omen's bearing, matching how the prod client draws tile y along
  world +Z. The river edge mask in the sim names its bits the other way round.
- **The bearing hash does not include the world seed** (the sim does not hold one): it
  mixes the omen id with the bearing or tile. The wildness count decides almost always;
  the hash only breaks ties.
- A raider that flees empty-handed counts as **Repelled**; only loot getting away is
  **Lost**.

## Open (user decisions)

- **Unlocks:** which building(s), if any, start locked, and what earns them? Candidates:
  - walls after the first threat repelled;
  - paved roads after enough traffic.
  - Holding back until picked, because locking a building the loop needs today changes the
    early game.
- **First numbers:**
  - reprisal size vs 6 trained;
  - warning time (7 days);
  - refugee band size;
  - explored tiles for "Far horizons".
  - All knobs; the user tunes them by hand.
- **Neighbours:** whether a threat's raiders may hit a neighbouring player (multiplayer, later).

## Later slices (not in M37)

Bandit camp (M16 deferred seam) · warlord milestone · wandering smith (needs weapon tiers) ·
travelling merchant (needs trade) · tribute demand (needs an answer intent) · drought /
hard winter omens · the chronicle.
