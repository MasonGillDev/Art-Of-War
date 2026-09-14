# Walls & Gates (M26)

## Decision

Three coupled decisions that add player-built fortifications — the first
structures that **block movement**.

1. **Draw like a canal, build like structures.** A wall line is placed by one
   `PlaceWallIntent` carrying an ordered path (validated atomically,
   fail-clean), but it expands into **N independent per-tile
   `ConstructionSite`s** — each hauls its own materials, completes on its own
   schedule, and stands as its own `Wall` structure with its own `Health`.
   There is no whole-line atomic completion. Gates are ordinary single-tile
   placements via `PlaceSiteIntent`.

2. **Blocking is a spec property with an allied-passage flag.** A new
   `StructureSpec.BlocksMovement` marks a kind un-enterable;
   `StructureSpec.AlliedPassage` (the Gate) exempts the owner and
   `RelationshipState.Ally` factions. Enforcement follows the M-movement
   fog split: the **planner** (`MovementCost.PlanCost`) treats a blocking
   structure as `Impassable` only when the planning player owns it or can
   currently see it; **execution** is ground truth — a blocked next-hop
   (checked when the hop is scheduled *and* again when the arrival fires)
   makes the mover yield in place, exactly like the tile-cap rejection.
   Blocking applies on **entry only**: a unit standing on a fortification
   tile (e.g. its own just-freed builder) can always walk off.

3. **Fortifications are besieged from adjacent tiles, with no defender
   shielding.** Nobody can stand on a wall, so the M24 stand-on-the-tile
   siege cannot reach it. Instead: when a unit or group **ends its march**
   on a tile 4-adjacent to a hostile standing fortification — or is stopped
   by one mid-path — a combat state opens **on the fortification's tile**
   (the existing `CombatState` + `CombatRoundEvent` machinery, so recovery
   works unchanged). Each round gathers every non-bandit unit hostile to
   the fort's owner on the fort tile or its 4-neighbors and applies their
   summed `EffectivePower` to the fort's HP. Razing produces Rubble via the
   M24 pipeline — the breach opens, permanently (no rubble clearing yet).

Walls are deliberately expensive and tough (`BaseHealth` 500 vs Barracks
200 / Castle 1000); the Gate is the designated weak point (300). All numbers
are catalog knobs.

### Locked rules

- **One intent, whole line, fail-clean.** `PlaceWallIntent` validates every
  tile (in bounds, land — Water/None rejected, Mountain allowed; no
  structure; no extraction claim; no in-flight canal reservation),
  distinctness, and 4-connectivity of the chain before mutating anything.
  Max length 64 (the canal cap). No wall-specific reservation system is
  needed: the N sites occupy their tiles immediately, so overlapping
  placements reject via the ordinary "tile already has a structure" check.
- **`PlaceSiteIntent` rejects `Kind == Wall`** (single entry point, like
  Canal). It accepts Gate, with a new "blocking kinds need a land tile"
  check (no gates in open water).
- **Unfinished walls don't block.** A `ConstructionSite`'s spec has
  `BlocksMovement == false`; the wall starts blocking at
  `BuildCompleteEvent`. Scaffolding is fragile (site HP 25) and permeable —
  a half-built wall is a wall-shaped promise, not a wall.
- **Gate passage is own-or-Ally**, evaluated live at plan time, at
  hop-schedule time, and at arrival fire time — so an alliance broken
  mid-march stops the column at the now-hostile gate.
- **No defender shielding for fortifications** (unlike the M24 castle
  rule). The M24 rule works because attackers and defenders co-locate on
  the besieged tile and combat clears the defenders first. On a blocking
  tile co-location is impossible: any shielding rule (defenders on the
  tile, or adjacent) would let one unreachable unit make the wall
  permanently indestructible — a deadlock, not a defense. The wall IS the
  shield; defending it means sallying out through your gate and killing
  the attackers on their own tiles (ordinary M7 combat).
- **Marching past a wall does not attack it.** The siege trigger fires only
  at a mover's *final* arrival (no onward leg scheduled) or when a
  fortification physically stops it. Mid-path hops adjacent to enemy walls
  are just travel.
- **Walls contribute no vision** (`Sight.RadiusFor` stays 0). The Tower is
  the vision fortification.

## Why

### Why per-tile structures, not a canal-style whole-path job

The end state is per-tile no matter what: each wall segment must carry its
own HP so an enemy can breach *one* tile (M24 razing → Rubble is per-tile
too). A canal leaves no structure — atomic flooding is fine there. The
canal's single-site design was also *forced* by its "extend from water"
ordering constraint (tile N is only valid if tiles 0..N-1 flood first);
wall tiles are independently valid, so the constraint that justified the
shape is absent. And atomic completion would be actively bad here: a
40-tile wall would spend weeks as one 25-HP `ConstructionSite` and then
pop into existence all at once — zero protection during the build, one
fragile raze-me point erasing the whole investment, and materials
teleporting along the length from a single anchor. Per-segment builds give
incremental protection, distributed hauling, and "attack the unfinished
section" as real siege counterplay.

The rejected alternatives:

- **Pure one-at-a-time** (`PlaceSiteIntent` per segment): trivial sim
  change, but no atomic line validation — if tile 17 is claimed you get a
  wall with an invisible hole — and a 40-tile wall is 40 intents in the
  log and the arbitration ledger.
- **Canal-style single site, atomic completion:** rejected per above.

### Why blocking is fog-split exactly like crowding

`PlanCost` already embodies the contract "the planner sees what the player
could see" (own units always, strangers only on visible tiles). A wall in
fog must not bend A* around it — that would leak enemy fortification
layouts through pathfinding. So the plan-side check is: blocking counts if
the tile is currently visible **or the structure is the planner's own**
(you know your own walls regardless of vision; you do *not* get your
allies' blueprints — an unseen allied wall is learned by bonking into it,
same as an enemy's). Execution is ground truth and always was; the yield
behavior (clear path, `Idle`, epoch semantics) mirrors the existing
tile-cap rejection byte for byte, so "ignorance has consequences" gets its
third instance rather than a new mechanism.

Two enforcement points on the execution side because a hop has two
lifecycle moments: the hop is *scheduled* (cheap early exit if the wall
already stands — this also catches the fog case, since ExecutionCost-time
knowledge is ground truth) and the arrival *fires* (a wall can complete,
or a gate turn hostile, during the hop's multi-minute travel time). Both
checks are pure reads on `world.Structures` — O(1) per tile, no
`BuildersPresent`-style scan concern.

### Why adjacency sieges anchored on the fort tile

Anchoring the siege's `CombatState` on the fortification's own tile keeps
every existing invariant: one combat per tile, fencing via
`NextRoundTick/Seq`, `RegenerateQueue` restores mid-siege fights with zero
new code, and `ViewProjector`/clients see contested tiles uniformly. The
alternative — anchoring on each attacker's tile — would fragment one siege
into up-to-four combats and double-count power. `GatherForcesOnTile`'s own
header comment ("later ranged-from-adjacent will pass [tile, north, south,
east, west]") anticipated exactly this gather shape; walls are its first
consumer, implemented as a fort-specific gather rather than a change to
the shared helper (unit-vs-unit combat stays strictly same-tile).

Final-arrival-only triggering (plus the blocked-yield trigger) is what
makes intent legible from presence, the M24 idiom: "I marched my army *to*
your wall" starts a siege; "my trade caravan walked along your wall road"
does not. The blocked-yield trigger doubles as the natural UX for "attack
that wall": order a move onto/past the wall line, the column stops at the
face of it, and the siege opens by itself.

### Why breaches are permanent (for now)

Razed walls become Rubble, and Rubble blocks *placement* forever with no
clearing mechanic (M24). So a breached segment cannot be rebuilt — a
serious siege permanently scars the defense. That is accepted for this
milestone: it makes breaking a wall decisive, and rubble
clearing/repair is a self-contained follow-on (see Future expansion). The
alternative — special-casing wall rubble to be buildable — would silently
weaken the M24 "wreckage occupies the tile" rule for the one kind where
re-placement matters most, prejudging the repair design.

## Future expansion

- **Rubble clearing / wall repair.** A `ClearRubbleIntent` (work job →
  removes the Rubble) or a `RepairIntent` (haul stone, restore HP) —
  either slots in without touching the blocking or siege mechanics.
- **Remembered-wall planning.** Plan-side blocking currently reads
  *current* visibility; walls are static, so planning against the
  remembered map (`RememberedBiome`-style "remembered structures") would
  let a player route around walls they scouted last season. Deferred until
  remembered structures exist as a concept.
- **AI wall play.** Brains don't build walls yet, and a Rival whose path
  to a castle is fully walled gets a rejected `MoveGroupIntent` (no path)
  rather than a breach plan. A "path blocked → besiege nearest wall
  segment" rung is the designed follow-on; the blocked-yield siege trigger
  already handles the case where a path exists at planning time and a wall
  stops the column en route.
- **Ranged defense of walls** (archers shooting from behind/atop) — the
  M14 ranged-from-adjacent deferral; would make held walls fight back and
  soften the no-shielding rule from the defender's side.
- **Wall-mounted vision, per-edge chokepoints on roads, drawbridge gates
  over canals** — all placement-rule or catalog extensions; none touch the
  blocking core.

## Acceptance tests

`tests/Sim.Tests/WallsAndGatesTests.cs`:

- Wall line placement: N sites with per-tile (unscaled) cost; rejects
  empty / too-long / duplicate / disconnected / out-of-bounds / water /
  on-structure / on-claim / canal-reserved paths, mutating nothing.
- `PlaceSiteIntent` rejects Wall; places Gate on land; rejects Gate on
  water.
- A completed wall line makes A* route around it (visible); a fully walled
  destination yields no path.
- Gate passage: owner and Ally path through; Neutral and Enemy treat it as
  a wall; alliance revoked mid-hop stops the mover at the gate.
- Fog: an unseen enemy wall does not bend planning; the mover yields Idle
  at its face (and a siege opens if hostile).
- A wall completing mid-march stops a unit whose committed path crosses it.
- Siege: a hostile force ending its march adjacent opens a combat state on
  the wall tile; rounds drain HP by attacker power; razing leaves Rubble
  and the breach is walkable; walking past does NOT open a siege; a
  defender standing behind the wall does not shield it.
- Pure-read walls: blocking checks 100×-no-mutation.
- Snapshot round-trip mid-siege and mid-line-build.

## Headline determinism test

> **`WallsAndGatesTests.Walls_TwinRun_HashesMatch`** — two identical runs of
> (build a wall line + gate → enemy marches, is stopped, sieges → breach →
> marches through the breach) produce `Snapshot.Hash` equality. The M26
> contract per `architecture.md` §1.

## References

- `docs/canals.md` — the whole-path placement UX this borrows, and the
  ordering constraint it deliberately does *not* inherit.
- `docs/sieges-and-conquest.md` — HP/raze/Rubble pipeline reused verbatim;
  the defender-shielding rule this consciously diverges from.
- `docs/movement-cost.md` — the Plan/Execution fog split blocking slots
  into.
- `docs/architecture.md` §2.6 (fencing), §2.7 (sparse state), §6 (test
  standards).

## Update 2026-07-06 — AI wall-building landed (FortifyRung)

The "AI wall play" deferral above is half-paid: both AI brains now BUILD
fortifications via `FortifyRung` (same ladder slot in Homesteader and
Rival — peacetime curves stay identical). The rung rings the castle at a
config radius, gates first (one per side, shifting along the side when
the midpoint is blocked — never a gateless ring), walls in short bounded
segments, and bootstraps the colony's first Quarry since nothing else
mines stone. Rejection feedback bisects the failed line down to the bad
tile using the observation discipline (`docs/ai-players.md`, M26 update).
The other half — a Rival *breach* doctrine for fully-walled targets —
remains deferred.

## Update 2026-07-06 (later) — breaches are no longer permanent

"Rubble clearing / repair" (Future expansion) is half-paid the same day:
`ClearRubbleIntent` (see `docs/sieges-and-conquest.md` update) turns any
rubble — a breached wall segment included — into a materials-free clearing
job; the emptied tile then takes a fresh `PlaceWallIntent` segment. A
breach is still a real scar (a day of clearing plus a new segment's stone
and build time, all under fire if the siege continues), but the ring can
be healed. In-place REPAIR of a damaged-but-standing wall remains
deferred.

## Update 2026-09-14 — the client can finally draw one

Walls and canals had been buildable sim-side since M26 and M21, and
unreachable from the client the whole time: `PlacementModes.Path` was defined
on the wire from C2, but `IsPlaceable()` returned true only for `Single` and
`SiteAndSlip`, so both kinds were filtered out of the build menu. Two
milestones of simulation existed only for the AI to use against the player.

**The gesture is click-to-extend.** Each click appends a 4-connected L-run from
the head of the line to the clicked tile, so a straight seawall is two clicks
and a corner is three — while every tile is still explicitly the player's,
because a wall's exact line is the whole tactical point of building one.
Auto-routing the full line would be the client deciding where your
fortifications go.

Backspace and right-click undo the last CLICK, not the last tile — the run it
added goes with it, because a click is what the player thinks they made.
Escape still drops the whole tool. Enter commits, and so does a button in the
panel: a multi-click gesture that can only be finished with a keystroke is one
a good share of players never finish, so the panel makes a deliberate exception
to its "get out of the way while placing" rule and shows a banner with the
running cost and a commit button.

**The preview re-walks the same L the gesture would.** Committed tiles draw
solid, the pending run draws thinner and dimmer, and an unplaceable run draws
in the bad colour — the preview and the click's refusal agree because both ask
`BuildPlacement` the same question.

**The one rule that differs between the two kinds** is enforced in the preview
as well as by the sim: a wall may stand on Mountain (fortifying a pass is the
point), a canal may not (you dig through soil, not rock). The canal's
water-root rule is checked once at commit rather than per click, because the
player draws outward from the shore and it constrains only the first tile.

Verified live against a running server: a 3-tile wall produced three
independent `ConstructionSite`s at exactly those tiles, and a 2-tile canal
produced ONE site at `path[0]` priced at 2x the catalog row — the documented
difference between the two shapes, observed rather than assumed. Both rejection
paths were exercised too (a canal into Mountain, a wall with a gap), and each
came back as a readable notice.

