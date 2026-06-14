# M20 Spec — Scouting Reports (the first narrative surface)

Fog made information a resource (M3); M17 made fair vision the spine
of AI; M18 put automation in the player's hands. But the player's
intelligence is still raw pixels — explored tiles, remembered biomes.
This milestone adds dispatched reconnaissance: from an intelligence
structure, send a scout out under return rules; while traveling the
scout accumulates a fog-honest OBSERVATION LOG tick-by-tick; on return
the log is compiled into simple natural-language CLAIMS by a
deterministic translation layer, and an LLM voices the claims as a
firsthand report from a road-worn scout ("I rode the northern pass as
you bid, my lord..."). The exploration payoff the 2026-06-11 fun audit
flagged ("things-in-the-fog"), delivered as the game's first narrative
surface.

The architecture is two-layer and the layering IS the milestone:

- **The claims compiler is the truth oracle.** Deterministic code,
  golden-file tested. Everything load-bearing — what was seen, how
  certain, what was NOT seen — is decided here.
- **The LLM touches style only.** It embellishes claims into prose. If
  the claims are correct and complete, the worst it can do is
  embellish badly; it cannot lie about the world, because the world it
  knows is exactly the claims.

## Locked decisions (user, 2026-06-12)

- **LLM is presentation-only.** Prose never enters sim state, never
  the hash, never feeds back into anything. Replays regenerate
  observation logs, never prose. Report text lives in a side store
  (with the intent/snapshot SQLite stores), not in `GameWorld`.
- **Uncertainty is computed server-side.** Estimate bands ("10–14
  men") are pure functions of (clear count, occluded estimate, vision
  quality). True values NEVER enter the LLM context — a model can't
  leak what it was never given, and players must not learn that prose
  leaks truth.
- **Capture happens at traversal time, stamped.** Observations are
  recorded tick-by-tick as the scout moves, each with
  `ObservedAtTick`. Staleness is the feature: the palisade seen
  two-fifths built two days ago is further along now, and the report
  says so. Reconstructing at return time would erase the aging that
  makes intel intel.
- **Prose is flavor, not the sole interface.** The structured claims
  ship alongside the report (sketch-map pins on the client; raw claim
  list as the fallback view). One lying report would kill trust in the
  whole mechanic if prose were the only surface.
- **Negative claims are explicit.** Absence is never in the data: the
  compiler diffs intended/possible coverage against vision actually
  touched and emits "the land east of the ridge: NOT OBSERVED."
  Recorded-empty and no-record are distinct in the schema; every path
  tile is accounted for by one or the other.
- **Closure rules generate the canonical unknowns.** Every positive
  claim type auto-emits its unknowns: enemy force seen → intent
  unknown; incomplete structure → purpose unknown. This guarantees the
  report always closes with honest limits without the LLM inventing
  them.
- **Inference only as tagged IMPRESSION claims, by explicit rule.**
  `Idle` + adjacent half-built structure → "encamped" is a good
  scout's read, but it enters the claims tagged IMPRESSION, never
  blended into a fact line. The LLM's saw/guess contract gets clean
  inputs.
- **The LLM never sees a game unit.** All distances and durations are
  pre-converted to traveler's terms by the compiler (tiles → ride-time
  via `docs/time-and-scale.md`, 1 tile = 1 km). Audited failure that
  locked this: handed "~7 tiles north," a real LLM wrote "seven days
  of ordinary walking from home" on a three-day journey — and patched
  its own contradiction with fiction. Never let the model do unit
  math.
- **Referents by relative anchoring.** "The ridge two days' ride east
  of your sawmill" — anchored to player-known structures and the home
  castle. No toponym table this milestone (deferred to world-gen
  "named locations").
- **Report linting.** Every numeral and unit in the generated prose
  must trace to a claim. Lint failure → regenerate once → fall back to
  the raw claims view. Catches the highest-stakes failure class (wrong
  numbers a player acts on) mechanically.
- **Async and failure-tolerant.** Claims compile synchronously at
  return (deterministic); the LLM job runs off the sim thread
  afterward. The in-fiction excuse is free — the scout is "writing up
  his report." The sim never blocks on, and is bit-identical with or
  without, the LLM.

## The model

### Mission + observation log (Sim.Core — durable, hashed)

`ScoutMission` on `GameWorld` (FormatVersion 14): owner, scout unit
id, dispatch tick, waypoints (the planned path), return rule, state,
and the accumulated log. One mission slot per scout — a new dispatch
replaces the previous completed record (deterministic pruning; the
report store keeps history, the sim does not).

The log has two parts, kept compact:

- **Coverage**: per movement leg, the vision disc extent actually
  swept (the same Euclidean discs `Sight` already computes). Coverage
  minus content = recorded-empty, by construction.
- **Content records**: only for non-empty observed tiles — tile,
  `ObservedAtTick`, biome, occupants (units seen clearly + occluded
  estimate + vision quality), structures (kind, owner, build
  progress), movement state.

Capture hooks into the scout's `MoveArrivalEvent` — the same event
that already drives `Sight.Reveal`, keeping the one-write-path
discipline. Fog-honesty is by construction: records can only come
from the scout's own live vision disc.

**Vision quality** is new and minimal this milestone: a pure function
of observation range vs `Sight.RadiusFor(Scout)` (cover/terrain
occlusion deferred). It drives two things downstream: whether a count
is exact or banded, and how wide the band is.

### Dispatch + return rules (intents + M18 atoms)

`StructureKind.Lodge` (= 13; name negotiable) is the intelligence
structure. It gates the feature: `DispatchScoutIntent` (own idle
scout, waypoint list, return rule) validates against a completed
Lodge. Return rules are conditions in the M18 sense — waypoints
exhausted, hostile force sighted, N days elapsed — and a `Patrol`
template over the standing-order engine makes recurring
reconnaissance an automation ("ride the northern pass weekly"),
exactly the layering M18 was built for. The scout walks on ordinary
`MoveIntent`s; the driver shape is the proven M16/M17/M18 one: pure
reads in, ordinary intents out.

### The claims compiler (Sim.Server — pure read, deterministic)

Input: a completed mission's log + the owner's known-state (Explored /
RememberedBiome / own structures, for novelty and anchoring). Output:
an ordered list of CLAIM objects, each rendered to one canonical
English sentence. Two passes:

**Semantic pass** (what claims exist):
- Compress runs of empty coverage into single claims ("the high
  ground: empty, three days of it").
- Dedupe repeated sightings of one entity across ticks; best fidelity
  wins.
- Emit negative claims from the coverage diff.
- Flag novelty by diffing against the owner's known-state ("your own
  eastern farmland — already known to you").
- Apply closure rules (the canonical unknowns).
- Apply impression rules (tagged IMPRESSION).

**Lexical pass** (how to say them plainly):
- Bearings and distances as pure functions of coordinates, rendered in
  traveler's terms. (Hand-translation audit: a straight 21-tile NE
  diagonal got narrated as "north then east, ~4 tiles" — geometry must
  be computed, never narrated.)
- Counts exact or banded per vision quality; band width a pure
  function, never a flavor number. (Audit: 3 clear + ~6 occluded
  became "10–14" by vibes.)
- Day stamps from `ObservedAtTick` ("on my second day out") so the LLM
  has real pacing and invents none.
- Faction display names; relative anchors to player-known structures.

Claim shape: sequence, location phrase + tile anchor (for pins),
subject, predicate, certainty (OBSERVED / ESTIMATED / IMPRESSION /
NOT_OBSERVED), provenance (range, quality), observed tick, novelty
flag. The canonical sentence list is ONE artifact with TWO consumers:
the LLM's entire world, and the player's raw fallback/sketch-map
view. Golden-file tests pin the compiler byte-for-byte; the flat,
repetitive output is CORRECT — fixing the prose is the LLM's whole
job.

The compiler stays scout-agnostic (input is "records + known-state,"
not scout fields) — battle aftermath reports, a steward's census, and
trader rumors are future clients of the same layer.

### The report (Sim.Server — outside the sim wall)

On mission return: compile claims (sync) → persist claims to the
report store → queue the LLM job (async). The prompt is the
scout-persona contract (Appendix A; checked in as an asset, not code):
first person, saw vs guess, never assert unobserved intent, length
matches content. Generated prose is linted (numerals trace to claims),
retried once, then falls back to raw claims. Store: SQLite report
store keyed by mission id — claims, prose, status; recovery recompiles
any returned mission with no stored claims (the mission record is
still in the snapshot).

Wire: a `NoticeDto` announces the return (existing monotonic-id toast
pattern); a new own-only wire surface carries reports + claims
(private, like standing orders). LLM endpoint/model/enable live in
`ServerOptions`.

### Knobs

- `Lodge` build cost/footprint (catalog).
- Vision-quality thresholds + band-width function constants.
- Waypoint cap per mission; `Patrol` template defaults.
- LLM config (endpoint, model, off-switch — off = raw claims reports,
  fully playable).
- All balance knobs follow the standing convention: tests derive from
  config, never hard-code.

## What gets built (phases, each ends green)

1. **Observation capture (Core)** — `ScoutMission`, coverage +
   content records on the scout's arrival events, recorded-empty vs
   no-record by construction, one-slot-per-scout pruning, snapshot
   FormatVersion 14. Twin-run + replay prove logs deterministic.
2. **Claims compiler (Server)** — both passes, claim schema, canonical
   sentences; golden fixtures including the worked Maddox example
   (Appendix B) with its audited corrections; fog-honesty pin.
3. **Dispatch (Core + automation)** — `StructureKind.Lodge`,
   `DispatchScoutIntent` + validation, return-rule conditions, the
   `Patrol` standing-order template; host `--scouting` smoke prints a
   raw-claims report.
4. **LLM integration (Server)** — async job, prompt asset, lint +
   retry-once + fallback, SQLite report store, recovery recompile,
   notices + report wire DTOs.
5. **Client (Unity repo, after the server pushes)** — the dispatch
   box (report reader), sketch-map pins from claim anchors, raw-claims
   toggle, return toast.

## Headline tests

- **`Scouting_TwinRun_HashesMatch`** — dispatch, full journey past
  foreign units/structures, return: twin runs hash-equal, observation
  logs identical (the M-level contract).
- **`Report_PresentationWall`** — the same seed run with LLM disabled,
  enabled, and failing: identical hashes, identical claims; only prose
  differs, and prose is outside the hash. THE pin of this milestone.
- **`Claims_GoldenFiles`** — raw log fixtures → byte-exact canonical
  claim lists: geometry computed not narrated, bands pure functions,
  impressions tagged, day stamps present.
- **`Claims_FogHonest`** — no claim references data outside the
  mission's own records (the M17 fairness signature, applied to
  reports).
- **`NegativeClaims_CoverTheJourney`** — every path tile is covered by
  an observed claim or an explicit not-observed claim; closure
  unknowns emitted for force/structure sightings.
- **`ReportLint_NumeralsTraceToClaims`** — prose with an untraceable
  numeral/unit is rejected and falls back to raw claims.
- **`Snapshot_RoundTrip_V14`** + **`Recovery_MidMission_Identical`** —
  persistence contract; crash between return and report generation
  recompiles identical claims.

## Explicitly deferred

- Toponyms / named-places table — relative anchoring suffices;
  revisit with world-gen Phase 2 "named locations."
- Scout skill progression (tighter bands, closer safe approach) and
  scout personas beyond a name — the upgrade axis is all server-side
  data; the prompt never moves.
- Cover/terrain-based vision quality (this milestone: range-derived
  only).
- Counter-intelligence: spotting enemy scouts, misinformation, report
  interception.
- Other narrative surfaces (battle reports, census, rumors) — the
  compiler is built scout-agnostic so these become clients, not
  rewrites.
- In-mission re-tasking; multi-scout coordinated sweeps; report
  history UI beyond the store.

## References

- `docs/architecture.md` §2 — pure-read walls (the compiler is one),
  the M16/M17/M18 driver shape (dispatch automation is its fourth
  instance).
- `docs/automation-layers.md` + `docs/m18-automation-engine-spec.md` —
  the atom engine return rules and `Patrol` build on.
- `docs/ai-players.md` — the fairness signature `Claims_FogHonest`
  re-applies.
- `docs/time-and-scale.md` — 1 tile = 1 km; the lexical pass's
  ride-time conversions.
- `persistent-rts-design.md` — exploration payoff; fog as gameplay.
- Companion decision doc `docs/scouting-reports.md` lands when the
  milestone ships (the M16/M17 pattern); the candidate decisions it
  pins: prose-as-view-of-canonical-log, server-side uncertainty,
  claims-compiler-as-truth-oracle.

## Update 2026-06-13 — Phase 1 shipped (observation capture, Core)

The canonical artifact exists and is deterministic. What landed:

- `Sim.Core/Scouting/ScoutMission.cs` — the durable types: `ScoutMission`
  (keyed by scout unit id, one slot per scout, `Active`/`Returned` state),
  `ObservationLeg` (Center + Radius = coverage; Sightings = content),
  `Sighting`, `SightedUnit`, `SightedStructure`. Stored-as-observed (biome,
  units, structure progress frozen at the observation tick) — the staleness
  substrate.
- `Sim.Core/Scouting/ScoutObservation.cs` — `Capture`, THE one write site,
  called from `MoveArrivalEvent.Apply` right after `Sight.Reveal`. Records
  only the scout's own live vision disc (fog-honest by construction; no
  `Explored` consulted), in canonical (y, x) then unit-id order. Empty tiles
  omitted — observed-empty is implied by the leg's coverage, never stored,
  which is what makes recorded-empty distinct from never-observed. Excludes
  the scout itself. Build progress via a pure `EffectiveProgress` helper
  (banked + live delta, no mutation of the site).
- `GameWorld.ScoutMissions` (SortedDictionary, canonical) + snapshot
  **FormatVersion 14** (`WriteScoutMissions`/`ReadScoutMissions`, modeled on
  the M18 StandingOrders block). No new scheduled events → `RegenerateQueue`
  untouched.
- `docs/determinism-audit.md` — `Capture` recorded as a one-write-site
  inverted-pure-read mutation point, plus its bounded `Units.Values` scan.

Tests (`tests/Sim.Tests/ScoutObservationTests.cs`, 8 green): content-in-disc
with empty/out-of-disc/self all correctly excluded; active-build live-delta
progress; leg-per-arrival ordering; capture-only-while-Active; a real walk
that is fog-honest; no-mission-no-log; **twin-run hash equality**; **snapshot
round-trip v14**. Full suite green at **681/681** (the FormatVersion bump
touches every round-trip/twin-run test; none regressed).

Not yet built: the claims compiler (Phase 2), dispatch + Lodge + return rules
(Phase 3), LLM integration (Phase 4), client (Phase 5). Capture currently has
no in-sim trigger — Phase 1 missions are created in test code; Phase 3's
`DispatchScoutIntent` is what creates them in play.

Design notes settled while building:
- The log holds **ground truth** (true unit ids/owners/roles). It is
  sim-internal and never reaches the LLM; banding + fog-novelty are the
  compiler's job at the presentation wall. This keeps Core simple and the
  "true values never enter the LLM context" rule enforceable in one place.
- Capture is **owner-blind** for content (records own + foreign + bandit
  units and own + foreign structures alike). Novelty ("you already know
  this") is a compiler diff against the owner's known-state, not a capture
  concern.
- **Fauna deferred**: the worked example's deer need a fauna system that
  doesn't exist yet. Capture records `Units` (real `UnitRole`s) + structures;
  deer appear when fauna does. The Phase-2 golden fixture will reflect what
  the sim can actually produce.

## Update 2026-06-13 — Phase 2 shipped (the claims compiler, Server)

The truth oracle exists. A raw observation log now compiles to an ordered,
honest, natural-language claims sheet — deterministic, pure-read, golden-
tested. What landed (all in `src/Sim.Server/Scouting/`):

- `Claim.cs` — the schema. `Claim` (Sequence, Kind, Certainty, Text, Anchor
  pin, provenance: ObservedTick/RangeTiles/VisionQualityPct, Novel flag),
  `ClaimKind` {EmptyGround, Force, Structure, Impression, Unknown,
  NotObserved}, `ClaimCertainty` {Observed, Estimated, Impression,
  NotObserved} — the SAW/GUESS axis kept orthogonal to subject. `ScoutReport`
  exposes `CanonicalSentences` — the ONE artifact, two consumers (LLM input +
  raw fallback view).
- `Lexicon.cs` (public, pinnable) — the LEXICAL pass, all pure integer math:
  `RangeTiles`/`Isqrt`, `QualityPct`, `CountPhrase` (exact vs band — the band
  is where the true count is dropped), `Bearing` (8-wind, pure-integer
  diagonal test), `RideTime` (tiles → traveler's terms via `RideTicksPerTile`
  + the `Time` vocabulary — the conversion that kills the "seven days" bug),
  `DayStamp`, `WorkDone`, `PercentDone`, deterministic `FactionName` house
  table, `StructureNoun`.
- `ScoutReportConstants.cs` — the knobs (ride pace, exact-count quality
  threshold, band-width divisor, force-cluster radius, diagonal ratio). Tests
  derive from these.
- `ClaimsCompiler.cs` — the SEMANTIC pass + assembly. `Compile(world,
  mission, cfg)`: dedup foreign units/structures by best fidelity; cluster
  units into forces (single-link DSU on Chebyshev distance); compress empty-
  leg runs; closure rules (force → intent-unknown + strength-unknown-if-
  banded; incomplete structure → purpose-unknown); tagged impression rule
  (force beside a same-faction works rising → "digging in", Impression
  certainty); the negative "beyond the turnaround" claim; journey claims
  ordered by observation tick (stable, so an impression trails its force),
  limits block last. Pure read — reads the log + own structures only (own =
  always known to the player, used for relative anchoring), never foreign
  world state, so fog-honesty is inherited from capture.

Tests (`tests/Sim.Tests/ClaimsCompilerTests.cs`, 19 green): Lexicon golden
cases (bearing/ride-time/banding/day-stamp) + the worked **Maddox fixture**
(Appendix B, adapted: owner 1 = House Ashford, a House construction site, 9
soldiers) asserting all five audited drifts are now correct by construction —
geometry computed ("half a day's ride to the south-east", not tiles), count
banded ("between 6 and 12", true 9 never present), day-stamped ("third day"),
impression tagged separately, negative claim closing the report — plus
closure unknowns, empty-ground compression, journey-before-limits ordering,
fog-honesty (only the seen faction named), and determinism. Full suite green
at **700/700**.

The compiled fixture report, for reference (this is the raw fallback view;
Phase 4 wraps these sentences in the scout-persona prompt):

```
[0] EmptyGround:  The country half a day's ride to the south-east of your keep lay empty — no men, no works, no fresh tracks.
[1] Force/Est:    Armed men, House Ashford: between 6 and 12, at a halt, half a day's ride to the south-east of your keep, on the third day — I came no closer than a few hours' ride, and the ground hid their number.
[2] Impression:   By the dwelling rising beside them, they looked to be digging in, not passing through — though that is my read, not a thing I can swear to.
[3] Structure:    A dwelling half-raised by House Ashford, half a day's ride to the south-east of your keep — about 40 in the hundred done, perhaps 2 days' work in it, on the third day.
[4] Unknown:      What House Ashford intends, I cannot say — I saw no march on us, only men at their ground.
[5] Unknown:      Their full strength I could not count; take my number for a scout's guess, not a tally.
[6] Unknown:      Whether that dwelling is meant as a border fort or a staging camp, I cannot tell.
[7] NotObserved:  I rode as far as half a day's ride to the south-east of your keep. Beyond there, further to the south-east, my road did not carry me; that country is dark to me.
```

Design notes / deferrals settled while building:
- The compiler is **pure-read, server-side** (like `ViewProjector`) — not
  hashed, so no determinism-audit entry; purity is documented in its header.
- **Banding hides truth in one place**: once `CountPhrase` bands, the true
  count is gone from the claim. Bands are currently centered on the true
  count (a metagamer could infer the midpoint); non-centered bucketing is a
  noted future hardening, not needed yet.
- **Novelty** is scoped to content (foreign = novel, own = known/excluded).
  Empty-ground novelty (newly-explored vs already-known) needs a pre-mission
  Explored baseline the log doesn't carry — deferred to Phase 3 dispatch,
  which can snapshot it.
- **Own structures** are excluded from claims (the player sees them already)
  and used only as anchors. The "own withered farmland" aside from the
  example is deferred.
- Minor cosmetic: coarse ride-time buckets can make two nearby claims share a
  phrase ("half a day's ride to the south-east" for both the empty deep-point
  and the camp). Honest, just slightly repetitive — finer phrasing is a
  polish knob.
- **Activity** was added to `SightedUnit` (still v14, unreleased) so the
  posture fact (Moving = on the march vs at a halt) and the entrenching
  impression have real input.

Not yet built: dispatch + Lodge + return rules (Phase 3), LLM integration
(Phase 4), client (Phase 5). The compiler has no in-game caller yet — Phase 3
wires `DispatchScoutIntent` → mission → (on return) `Compile`.

## Update 2026-06-13 — Phase 3 shipped (dispatch + Lodge + return rules)

The compiler now has an in-game caller: a scout can be dispatched from a
Lodge, rides its waypoints observing as it goes, recalls by rule, and on
return its log compiles to a report. End-to-end and deterministic.

What landed:
- **`StructureKind.Lodge` = 13** — the intelligence structure. A plain
  `Lodge : Structure` (mirrors `School`), catalog spec (buildable, 60 wood +
  20 stone, ~50 min), `BuildCompleteEvent` factory case, snapshot read/write
  cases. Its completed, owned presence gates dispatch.
- **`DispatchScoutIntent`** (`Sim.Core/Scouting/`) — own idle Scout + a
  completed owned Lodge + 1..`MaxWaypoints` in-bounds waypoints + (for the
  ElapsedTicks rule) a positive budget; all validated at resolution time
  (a reject mutates nothing). Creates the durable `ScoutMission` (replacing
  any prior sortie by that scout) and launches the march. Registered in
  `IntentJson` (durable type-name frozen) for replay.
- **`ScoutMission` extended** (still v14, unreleased): `HomeTile`,
  `Waypoints`, `ReturnRule`, `ElapsedLimitTicks`, `WaypointCursor`, and a
  `Returning` state. Capture now appends while State != Returned (the
  homeward leg is observed too).
- **`ScoutReturnRule`** {WaypointsExhausted (backstop), ElapsedTicks (time
  budget), HostileSighted (recall on seeing a `Diplomacy.AreHostile` unit —
  bandits or an Enemy faction)}.
- **`ScoutMissionRunner`** — the in-sim driver. Hooked into
  `MoveArrivalEvent.DispatchOnFinalArrival` (the HaulPlan precedent, NOT a
  server driver), it advances the cursor / sets Returning / marks Returned
  and issues the next `MoveIntent.BeginMove`. A bounded `Drive` loop skips
  trivially-satisfied targets (already-there / unreachable) so no wedge. A
  waypoint-less Active mission is treated as "manual / log-only" (no
  autopilot) — that is the Phase 1 manual-mission path, kept working.

**Determinism for free:** the runner is in-sim, so replay-from-intent-log and
mid-mission snapshot/restore reproduce exactly (plan + cursor + state
snapshot; the in-flight move regenerates from unit anchors). No new event
types → `RegenerateQueue` untouched. Determinism audit updated with the
mission-lifecycle write sites.

Tests (`tests/Sim.Tests/ScoutDispatchTests.cs`, 10 green): Lodge-gated /
scout-only / bounds validation; waypoint progression then return-home; the
HostileSighted and ElapsedTicks recall rules; end-to-end **dispatch → travel
→ compile** (a returned mission compiles to a report with a foreign Structure
claim); twin-run hash; snapshot round-trip mid-mission. Full suite green at
**710/710**.

**Host smoke `--scouting`** dispatches a scout past a House-Ashford camp and
prints the compiled report, then asserts twin-run + round-trip. Live output:

```
Final mission state: Returned; observation legs: 58; scout home at (5,20).
  [EmptyGround] The country half a day's ride to the east of your works lay empty — no men, no works, no fresh tracks.
  [Force] Armed men, House Ashford: between 8 and 10, at a halt, half a day's ride to the east of your works, on the first day out — I came no closer than a short ride, and the ground hid their number.
  [Impression] By the dwelling rising beside them, they looked to be digging in, not passing through — though that is my read, not a thing I can swear to.
  [Structure] A dwelling being raised by House Ashford, half a day's ride to the east of your works — about 40 in the hundred done, perhaps 12 hours' work in it, on the first day out.
  [Unknown] What House Ashford intends, I cannot say — I saw no march on us, only men at their ground.
  [Unknown] Their full strength I could not count; take my number for a scout's guess, not a tally.
  [Unknown] Whether that dwelling is meant as a border fort or a staging camp, I cannot tell.
  [NotObserved] I rode as far as half a day's ride to the east of your works. Beyond there, further to the east, my road did not carry me; that country is dark to me.
```

Design notes / deferrals:
- **In-sim runner vs M18 driver.** The spec originally framed dispatch as an
  M18 server-side driver. Built it in-sim instead (the HaulPlan precedent):
  cleaner, replay/restore-safe for free, no parallel cursor-intent machinery.
  Recurring **patrols** still layer on as M18 automation later — a standing
  order whose action re-issues `DispatchScoutIntent` (a new ActionKind);
  deferred, so this milestone ships the single sortie, not the `Patrol`
  template.
- Recall rules are evaluated **at waypoint arrivals**, not every hop — "the
  moment a hostile is seen" is approximated to "by the next checkpoint."
  Per-hop evaluation is a cheap future refinement.
- Player-override of a scout mid-mission (issuing a manual `MoveIntent`)
  fences the mission's next move and effectively abandons the sortie (mission
  stays Active but un-driven). Acceptable; explicit cancel/handoff deferred.
- The report still has no auto-compile-on-return wiring into a store or the
  wire — that is Phase 4 (the async LLM job + report store + notices). Today
  the host smoke compiles a Returned mission directly.

## Update 2026-06-13 — Phase 4 shipped (LLM integration, real Claude)

The presentation layer is live: a returned mission's claims are narrated into a
first-person scout's report by **Claude (real Anthropic Messages API)**, linted,
and fall back to the raw claims sheet on any failure. The sim is bit-identical
with narration off, on, or failing.

What landed (all in `src/Sim.Server/Scouting/`):
- `ScoutPrompt.cs` — the scout-persona system prompt (Appendix A) as a checked-in
  asset.
- `ReportText.cs` — builds the OBSERVATIONS block handed to the LLM (the
  grounding text: fog-honest header + ordered canonical sentences, split into
  what-was-seen and the limits block) and the `RawFallback` claims sheet.
- `ReportLinter.cs` — `NumeralsTraceToGrounding`: every digit-run in the prose
  must appear in the grounding text. Catches the highest-stakes failure (an
  invented number a player acts on — the audited "seven days"/"10-14") with no
  LLM-judge. Word-number linting deferred (paraphrase risk).
- `ScoutNarration.cs` — `IReportNarrator` (the narrate boundary, returns null on
  any failure), `ReportStatus` {Narrated, RawFallback}, `NarratedReport`.
- `ClaudeReportNarrator.cs` — **the real one.** Raw `HttpClient` POST to
  `https://api.anthropic.com/v1/messages` (`x-api-key`, `anthropic-version:
  2023-06-01`); body = model + max_tokens + system + one user message; extracts
  `content[].text`; treats non-2xx / `stop_reason:"refusal"` / empty / any
  exception as null → fallback. No SDK dependency.
- `ScoutNarrationOptions.cs` — endpoint, version, **model (default
  `claude-opus-4-8`; `claude-haiku-4-5` is the documented one-line cheap swap
  for this short flavor text), max_tokens (1024), API key**.
  `FromEnvironment()` reads `ANTHROPIC_API_KEY`; empty key = disabled =
  raw-claims fallback (fully playable offline).
- `ScoutReportNarrationService.cs` — orchestrates compile → narrate (async, off
  the sim thread) → lint → **retry once** → raw fallback. Claims recompile on
  demand from the snapshotted mission, so this is recovery-clean for free (a
  crash before narration loses only regenerable prose, never the intel).

Tests (`tests/Sim.Tests/ScoutNarrationTests.cs`, 7 green) — **none call the real
API** (user directive, cost): fake narrators cover the linter (grounded pass /
invented fail / words ignored), narrate-success, retry-once-then-fallback,
transient-fail-then-success, invented-number-fails-lint-falls-back, and the
headline **`Report_PresentationWall`** — sim hash and claims identical across
narration off/on/failing; only prose differs. Full suite **717/717**.

Host `--scouting` now narrates via Claude when `ANTHROPIC_API_KEY` is set
(prints the prose + the canonical claims it's a view of), else prints the raw
claims sheet. The live Anthropic call was **not** exercised in this session
(cost) — the wiring follows the current Messages API reference; set the key and
run `--scouting` to see it.

Design notes / deferrals:
- **Model default** is `claude-opus-4-8` per the standing "don't downgrade for
  cost without the user's say-so" rule; `claude-haiku-4-5` is the natural cheap
  swap for short flavor text and is a one-line config change.
- Config lives in a **scout-specific `ScoutNarrationOptions`**, not `ServerOptions`
  — keeps the narration knobs with the feature.
- **No determinism-audit entry**: narration is pure presentation (like
  `ViewProjector`), never touches the hash — `Report_PresentationWall` is the pin.
- **Deferred to a follow-up (not blocking playability):** the SQLite report
  store + history (claims recompile deterministically on demand, so it's an
  optimization, not a correctness need), the notice + report **wire DTOs** and
  `GameHost` tick-loop integration (compile-on-return is demonstrated by the host
  smoke; the Unity client is Phase 5), and word-number linting.

## Update 2026-06-13 — Phase 4b + Phase 5: end-to-end to the client

Wired the live server and the Unity client so a scout can be dispatched and its
report read in-game.

**Server (`GameHost`):** on each clock tick, `HarvestScoutReturns` detects
missions that reached `Returned` (each handled once by (scout id, dispatch
tick)), compiles claims under the sim lock (fast pure read), deposits a raw
`ScoutReportDto` into a per-player inbox + a "scout returned" notice, then
narrates **off the lock** (fire-and-forget Task) and swaps the prose in place
when Claude answers. The narration service is built from
`ScoutNarrationOptions.FromEnvironment()` — so the **gitignored
`anthropic-key.txt`** (or `ANTHROPIC_API_KEY`) drives the live server too;
no key → raw-claims reports still reach the wire. Reports ride the existing
own-only view path (`ViewDto.ScoutReports`, attached in `BuildViewJson` like
notices). New: `ScoutReportNarrationService.NarrateReportAsync(report, …)` so
the compile (sync) and the HTTP turn (async) are separable. Scouts get stable
persona names (Maddox, Ren, …) by id.

**Wire:** `ScoutReportDto` { id, scoutUnitId, scoutName, dispatch/returnTick,
prose, status, claims[] } + `ScoutClaimDto` { sequence, kind, certainty, text,
anchor(x,y)+hasAnchor, novel } — claims carry the pin coords for a sketch map.

**Client (`../Art Of War`, Unity):**
- `Wire.cs` — `StructureKind.Lodge = 13`; `WireScoutReport`/`WireScoutClaim` +
  `WirePlayerView.scoutReports`; `ScoutReturnRule`/`ClaimKind`/`ClaimCertainty`
  enums.
- `Intents.cs` — `DispatchScoutIntentPayload` + `IntentFactory.DispatchScout`.
- `BuildMode` — Lodge added to the build palette (60 Wood + 20 Stone, any land).
- `ScoutMode` (new, hotkey **K**) — pick an own Scout, left-click waypoints in
  order, **Enter** dispatches (WaypointsExhausted); R-click undoes, Esc exits;
  translucent waypoint markers.
- `GameClient` — registers ScoutMode; `SurfaceScoutReports` toasts + logs +
  auto-opens a new report; `DrawReportPanel` is a draggable reader window
  (toggle **P**) with Prev/Next, showing the prose (raw claims first, narrated
  prose when it lands).

Both projects compile clean (server suite **717/717**; the Unity client builds
headlessly via `Assembly-CSharp.csproj`). The Lodge renders via the generic
building object (no per-kind art needed). Still deferred: SQLite report
store/history, sketch-map PINS on the 3D map (claims carry anchors; the client
currently shows prose + raw claims, not yet map markers), word-number lint.

## Appendix A — the scout prompt (draft, user, 2026-06-12)

Ships as an asset in Phase 4. Preserved verbatim:

> You are a scout in a medieval realm, reporting back to your lord on
> what you observed during a reconnaissance journey. You are a real
> person: road-worn, observant, loyal, and honest about the limits of
> what you saw. You report ONLY what you witnessed with your own eyes,
> voiced as a firsthand account delivered upon your return.
>
> VOICE
> - Speak in the first person, directly to your lord, as someone just
>   back from the field: immediate, grounded, a little weary. ("I rode
>   the northern pass as you bid, my lord...")
> - You are a soldier, not a poet. Plain, vivid, soldierly speech.
>   Convey what you saw and how it struck you.
> - You MAY share impressions and read the situation as a scout
>   naturally would — the bearing of men you saw, whether ground looked
>   freshly worked, how a force carried itself. Impressions are part of
>   good reconnaissance.
>
> WHAT YOU MAY DO (this is what makes a scout believable)
> - Voice UNCERTAINTY about what you observed, exactly as the data
>   marks it. If a count is estimated, say so ("a band I counted near a
>   dozen, though the trees hid their flank"). If something was
>   glimpsed at distance, say so. Never sharpen a guess into a
>   certainty.
> - Offer sensory impressions grounded in the observation ("their
>   banners were Ashford's, and they rode west with purpose").
> - Admit the limits of your journey — what you could NOT see, where
>   your path did not take you, what lay beyond your sight.
>
> ABSOLUTE RULES (these override the desire for a vivid report)
> - Report ONLY what is in the OBSERVATIONS data. Never invent a
>   person, force, structure, number, banner, or event you did not
>   observe. If it is not in the data, you did not see it.
> - You may WONDER, but you may never ASSERT intent you do not know.
>   "Whether they mean to march on us, I cannot say" is good. "They are
>   massing to attack the eastern gate" — when no such thing was
>   observed — is a lie that could cost your lord dearly. Never state
>   an enemy's plan as fact.
> - Distinguish clearly between what you SAW (fact) and what you GUESS
>   (impression). Your lord must be able to tell them apart and weigh
>   your guesses for himself.
> - Do not invent danger to seem useful, nor downplay it to seem brave.
>   Report true.
>
> STRUCTURE
> - Open with your return and where you went.
> - Recount what you saw along the way, in the order you saw it.
> - Close by naming honestly the limits of your report — what lay
>   beyond your reach, what you could not confirm.
> - Length matches what you saw: an uneventful ride is a short report.
>   Do not pad an empty journey with invented sights.
>
> FORMAT
> - Plain spoken prose, first person. No headers, no lists, no game
>   terms. Translate distances and directions into a traveler's terms
>   ("half a day's ride east", "the high country north of the river").

Known prompt gap from the first live sample: atmospheric world-state
(snow, cold) was embellished from nothing. Resolution is in the
claims, not the prompt — feed a per-claim flavor field (biome, season,
weather) so the atmosphere is OUR atmosphere.

## Appendix B — the worked example (golden fixture seed)

Raw mission data (user, 2026-06-12), the Phase-2 fixture input:

```
scout_id: 14, owner: 1 (the player)
home_castle: (40, 100)
dispatch_tick: 4900, return_tick: 5200, ticks_per_day: 100
path_tiles: [(41,101),(43,104),(45,107),(47,110),(48,112),(50,115),(52,118)]

per-tile vision record (accumulated as scout traveled):
  (43,104): biome=Mountain elev=high occupants=[] structures=[] last_event_tick=null
  (45,107): biome=Mountain elev=high occupants=[] structures=[] last_event_tick=null
  (47,110): biome=Grassland occupants=[fauna:deer x7] structures=[] last_event_tick=null
  (52,118): biome=Grassland occupants=[unit:323 owner=2 role=Soldier,
                                        unit:324 owner=2 role=Soldier,
                                        unit:325 owner=2 role=Soldier,
                                        (+ ~6 partially occluded, vision_quality=0.45)]
            structures=[id=88 kind=Palisade owner=2 build_progress=0.40]
            unit_state=Idle (not moving)
            observed_at_range=4  vision_quality=0.45
  east_of (52,118): NOT_IN_PATH_VISION
faction_names: {2: "Ashford"}
```

The fixture's EXPECTED claims are produced by the compiler, not
hand-written — the hand translation of this record drifted in five
audited ways, and each drift is now a compiler requirement:

1. Geometry was narrated, not computed ("north then east, ~4 tiles
   east" for a straight ~21-tile NE diagonal; the "4" leaked from
   `observed_at_range`). Bearings/distances are pure functions of
   coordinates.
2. The count band was a flavor number ("10–14" from 3 clear + ~6
   occluded ≈ 9). Bands are pure functions of (clear, occluded,
   quality).
3. "Making camp" was an untagged inference from `Idle` + the adjacent
   half-built palisade. Legal, but only as a tagged IMPRESSION claim
   from an explicit rule.
4. No `ObservedAtTick` per record → the LLM invented day-by-day
   pacing. Every record is stamped; the lexical pass emits day stamps.
5. Three path tiles had no record at all — recorded-empty vs no-record
   was ambiguous. Coverage discs + content records make the
   distinction structural.
