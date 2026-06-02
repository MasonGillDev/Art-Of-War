# Persistent Logistics RTS — Design Document

*A long-running, asynchronous real-time strategy game played on geological time.
Working draft.*

---

## 1. Core Vision

A large, procedurally generated world covered in fog. Each player starts with a
patch of land, a castle, some starting resources, and a handful of citizens. The
goal is to conquer every other player's castle or force them to surrender.

The defining feature is **time and distance as the primary constraints**. Moves
last hours. Travel lasts hours. Nothing teleports. Everything — resources,
goods, even any future currency — must be physically carried across the map by
citizens. This is a game about **logistics, exploration, and supply lines** as
much as it is about combat.

The whole world is a **robust simulation**: to build something on a tile you
need the required resources *physically present* on that tile and the citizens
to do the work; to get resources there, citizens must haul them.

### The one principle everything derives from

> **Knowledge comes from presence.**

This single idea generates most of the game's systems and is the test for whether
a new mechanic belongs:

- You only see terrain/activity where you currently have eyes (fog of war).
- You only *know* a route's true travel time once you've actually walked it
  (estimate first, exact after).
- You only hold an advantage where you've invested presence — towers to pin
  vision, roads to pin speed, gates to pin control.

When a proposed mechanic falls naturally out of this principle, it fits. When it
fights it, reconsider.

---

## 2. Technical Architecture

### 2.1 Authoritative server, dumb clients

There is **one authoritative simulation** running on the server. It is
omniscient and resolves everything with full knowledge of the true world state.

Clients (web, desktop, mobile, companion app) are **dumb viewers**. They:

- **Issue intents** — "send this group to point P," "build X here when the
  resources arrive," "list these goods at this trade post." Intents are rare
  events, issued at command time.
- **View filtered state** — each client sees only what that player's fog allows.

Clients never drive the simulation. This is what makes platform choice a
non-issue: the server doesn't care whether you're on a phone or a browser. 3D vs.
2D is purely a client rendering concern and never touches the sim (see §6.1).

### 2.2 Event-driven core (not a global tick)

The simulation is **discrete-event**, not tick-based. Instead of advancing the
whole world every N seconds, the server computes *when* the next meaningful thing
happens, drops it in a priority queue keyed on timestamp, and does nothing until
then.

This scales with the **number of decisions**, not with elapsed time or map size
— exactly the property you want when hours pass between actions. A citizen
walking for three hours is a single "arrival" event, not 10,800 per-second
updates.

**Consequence to internalize:** anything continuous (resource depletion from a
mine, an ongoing battle) must be modeled as **rate + start-time**, computed
lazily when observed, rather than ticked. This affects how you model everything.

### 2.3 One global queue, processed independently of who's online

There is **a single event queue for the entire world**, processed in strict
timestamp order. The sim advances on its own schedule regardless of logins.

> ⚠️ **Do not** process events per-player on login. The moment two players'
> entities interact (a caravan and a raider), per-player catch-up produces
> contradictions — the caravan "arrives safely" before the battle that should
> have stopped it is processed. The sim runs continuously; players merely
> observe it.

### 2.4 Determinism + replay from day one

Seed all RNG. Log every event. This makes the entire game **replayable from its
log**. In an async game, bugs surface *hours* after their cause — replay is the
only sane way to debug that. Retrofitting it is painful; build it in first.

### 2.5 Tile-based simulation, continuous-looking rendering (decided)

The sim is **tile-based**: the world is a grid (hex or square — see §13), and
units occupy and move between tiles. The **UI renders movement as smooth and
continuous** — the client interpolates (and can spline) a unit's drawn position
between tile centers, so there are no visible grid steps. This is how most
"grid games that don't look gridded" work.

> **Key realization:** smooth-looking movement is a *rendering* concern and is
> free regardless of the sim model. Naturalness of appearance was never an
> argument for a continuous *simulation* — you get the glide either way. So the
> sim is chosen purely on its merits, and for this game tiles win (see §6).

The single grid *is* the simulation substrate and provides everything for free:

- **Biome cost** — movement-speed data per tile.
- **Fog raster** — per-player explored/terrain memory, on the same grid.
- **Road condition** — a value per tile (or per edge between tiles).
- **Collision/combat** — trivial: a unit arriving on a tile is already an event;
  just check who else is on that tile. No spatial hash, no closest-approach math.

One structure, not two. Far less code and far fewer edge cases — which matters
enormously in a game where bugs surface *hours* after their cause.

---

## 3. World & Terrain

- Large, procedurally generated world of **biome regions** (forest, plains,
  mountains, water, etc.).
- Biomes set **movement cost** — some speed you up, some slow you down — making
  route choice a real decision.
- Each player starts with a region of land, a **castle**, starting resources,
  and a few citizens.
- Everything outside the start is hidden under fog.

---

## 4. Fog of War

### 4.1 Fog is derived, never stored or actively updated

Do **not** write code that "closes fog behind a unit." Instead:

- Persist a **sparse per-player explored layer** that stores **terrain only**.
- **Currently visible** is *computed on demand* from the positions and radii of
  that player's vision sources (units, towers, castle, buildings).

When a unit walks forward, the tiles behind it are simply no longer in any
source's radius, so they automatically fall back to fog. The "circle of fog
closing in behind a citizen" isn't an event — it's just what the derived view
looks like when the only vision source is a moving unit. **Static structures
(towers, buildings) pin vision** so it doesn't collapse.

### 4.2 The memory rule (decided)

- **Terrain is remembered** once seen — a forest you mapped stays a remembered
  forest even after re-fog.
- **Activity is always hidden by fog** — who is there *now*, current resources,
  live enemy movement — regardless of whether you've been there before.

Re-fog means "stale memory," not amnesia.

### 4.3 Fog never touches the simulation

Fog is purely a **per-player information filter**. The server is omniscient; a
battle happens whether or not you saw it coming — that *is* the point of ambush.
The one deliberate exception is an **ambush bonus** for attacking from outside
the enemy's vision: a specific sim rule that *reads* vision. That's allowed as
the exception, not as general coupling.

### 4.4 Vision infrastructure

- **Scouts** — larger vision radius; how you de-risk logistics by revealing
  routes before committing caravans.
- **Watchtowers** — expose a large area and *pin* it (static vision that doesn't
  collapse when units move on).

---

## 5. Citizens & Roles

Citizens are trained into roles that specialize them:

- **Builder** — builds structures faster.
- **Scout** — larger vision radius, reveals more fog.
- **Porter / hauler** — carries resources (capacity matters; see §7).
- **Soldier** — fights; escorts caravans.

(Role list is extensible. Roles are the main character-progression lever.)

---

## 6. Movement

### 6.1 Tile-based movement, smoothed in the UI

Units move from tile to tile across the grid. Each arrival on a tile is an
**event**. The client **interpolates/splines** the drawn position so movement
*looks* smooth and continuous — the grid is invisible in motion. **3D rendering
is optional and client-side only**; the sim only needs grid coordinates plus
per-tile terrain/biome cost.

### 6.2 Why tile-based was chosen over continuous

We initially explored a continuous-plane sim and confirmed it was *feasible*
(predictable trajectories let you solve closest-approach analytically instead of
ticking). But feasible isn't optimal. For *this* game, tiles are the better fit
because they make the three things the game lives on **easier**, while the only
thing continuous buys — arbitrary sub-tile positioning — is something the design
never uses (units travel and stop; they never need sub-tile precision).

- **Collision & combat → trivial.** Arrival on a tile is already an event; check
  who's on the tile, resolve. No closest-approach quadratics, no spatial-hash
  broadphase.
- **Pathfinding → grid A\*** — the most battle-tested algorithm in games, robust
  and fast, instead of nav-meshes / flow fields over a continuous cost field.
- **Timing → legible.** "Four tiles, two hours each" is something a player can
  reason about *in their head* while planning hours ahead — which is the core
  skill of the game. Continuous space turns every arrival into a floating-point
  estimate, actively fighting the requirement that players know *exactly* when
  citizens arrive (§6.4). Tiles serve the design; continuous worked against it.
- **One structure, not two.** Biome cost, fog raster, and road condition all live
  on the single sim grid (§2.5) rather than on a separate coarse grid hidden
  under a continuous world. Much less code, far fewer edge cases.

The naturalness that originally motivated continuous lives entirely in the
renderer (§2.5, §6.1), so nothing is lost by simulating on tiles.

### 6.3 Tile artifacts and how they're handled

The only real downsides of tiles are cosmetic and cheaply solved:

- **Small tiles** so interpolated paths read as smooth, and/or
- **Hex grid** — every neighbor equidistant, avoiding the square-grid diagonal
  problem (diagonals being √2 longer, or forbidding them and looking blocky).
  Hex looks and behaves more naturally for terrain/distance-driven movement.
  (Hex vs. square is an open decision — see §13.)

### 6.4 Timing: estimate first, exact after ("explore first, know after")

This resolves the tension between "smooth movement = fuzzy estimates" and
"players want exact arrival times," by making the uncertainty **diegetic** — it's
fog of the *path itself*. Tiles make this *more* legible: a known route is a
known number of tiles at known per-tile costs.

- **First traversal of raw ground:** the player gets an **estimate**, and the
  trip takes the **honest, deterministic terrain time** (no lucky/unlucky roll —
  see §8.2 for why).
- **After traversal:** that route is now *known*, and its time is **exact** from
  then on.
- Uncertainty in the game comes from **the unknown (unexplored paths)** and
  **the threat of other players** — never from arbitrary dice on outcomes you
  can't react to.

---

## 7. Logistics, Caravans & Capture

### 7.1 Everything physical

Resources and goods do not teleport. Citizens carry them. Building requires the
inputs to be physically on the tile.

### 7.2 Caravans as composite group entities

A caravan needs **no special-case code** — it's a **group** containing porters,
**carts** (capacity), **horses** (speed), and **escort soldiers**. It moves as
one unit at the pace of its slowest/heaviest member, and biome cost hits carts
harder than foot — so **speed vs. capacity vs. protection** falls out naturally.

Because combat is "everyone on the contact resolves together" (§9), an escort
**automatically** fights when the caravan is ambushed. No separate
caravan-combat system.

### 7.3 Capture, not destruction (decided)

When an escort loses, the goods are **captured, not destroyed** — creating a
**raiding economy**. The on-theme wrinkle: a raider who captures 500 wood must
now **haul 500 wood**, so they needed their own carts along. Raiding becomes its
own logistics puzzle, in keeping with the game's spirit.

---

## 8. Roads

The strongest emergent system: exploration becomes **permanent capital**, supply
lines become **physical objects** that can be threatened, and map structure
(arteries, chokepoints) **emerges from play** rather than being hand-placed.

### 8.1 One continuous "condition" value per road segment (decided)

There are **no hard tiers and no big jumps**. Each segment has a single
continuous **condition** value:

- **Traffic feeds it** — each traversal nudges condition up.
- **Time drains it** — "the forest reclaims it." Condition bleeds down slowly
  when unused.
- Everything visible (speed, dirt→stone look) is a smooth readout of this one
  value. The road is **never static** — always being worn smoother or reclaimed.

### 8.2 First traversal is deterministic; speed is earned by use (decided)

We explicitly **rejected** a lucky-first-roll-locks-in-a-fast-road mechanic
because it (a) becomes a reroll slot machine — scout a route, abandon it if the
roll is bad — and (b) reintroduces an unfair *permanent* outcome from a *single*
roll resolved while the player is asleep.

Instead: first trip = honest terrain time. The road then **earns** speed through
**sustained use**, drained against decay.

### 8.3 Emergence thresholds fall out for free

- **One citizen can't make a road** — a lone walker's trickle is erased by decay
  before it amounts to anything. "Needs X traffic to appear" isn't a counter
  you check; it's the natural **break-even point where input beats decay**.
- Busiest arteries climb fastest; faint shortcuts stay faint. Natural texture.

### 8.4 Diminishing returns up, steady-ish decay down (decided)

- **Gains diminish** as the road improves: early dirt-track formation feels
  responsive; the **stone road is prestigious** — only a sustained, defended
  artery ever reaches and *holds* it, because near the cap the trickle barely
  outpaces drain. A stone road is something you must **keep feeding**.
- **Speed reads off condition smoothly** (continuous in, continuous out) so every
  bit of traffic gives felt return — no dead zones.
- **Visuals may snap** at thresholds (dirt→stone) because the art has to pick a
  sprite; the *speed* underneath stays a smooth curve.

### 8.5 Decay tempo (decided direction; exact rate still open)

- **Lean slow**, so roads feel like durable infrastructure and async players who
  miss a few days don't return to a vanished highway.
- **Slower at higher condition** ("stone holds up longer than dirt"): reaching
  the top tier buys not just speed but **resilience** — a few quiet days forgiven
  where a dirt track wouldn't be.
- A busy road you're still using daily should not be at meaningful risk; decay is
  primarily a threat to **abandoned** routes.

### 8.6 Roads × raiding × gates (composes)

- Cutting a high-traffic supply line **compounds**: traffic stops, condition
  slides back toward dirt, and clawing it back fights diminishing returns.
- **Roads are public** (decided, §10) — anyone who finds your road gets the
  speed benefit, including an attacker marching on you. This is the *reason*
  walls and gates exist: gate the **chokepoints** to protect both passage and
  the **accumulated condition investment**.
- A road is **terrain memory**, so it persists through re-fog (you remember the
  route), but **who is currently walking it is live activity** — still fogged
  unless you have eyes on it. A road you built can be used against you by a
  raider you can't see — a fair tension, since you *could* have watched it with a
  tower.

---

## 9. Combat

### 9.1 Engagement is event-driven, not monitored

Combat triggers from **tile arrival** (§6.1): when a group arrives on a tile, the
resolver checks whether any hostile group is on that tile (or an adjacent one,
for ranged — §9.4). If so, fight. No global scanning, no global clock — you only
ever look at the one tile where something just happened. (This is the simplest
possible model, and is exactly why tile-based was the right call.)

The one case this misses is two groups swapping adjacent tiles and passing on the
edge between events. Most games ignore this; see §13 if you'd rather special-case
it.

### 9.2 Instant melee; self-rescheduling rounds for ongoing fights

- **Melee on contact** resolves in the engagement event — needs nothing extra.
- **Ranged-from-adjacent and sieges/standoffs** are ongoing processes. Use a
  **self-rescheduling event**: when a round resolves and the fight isn't over,
  schedule the next round a few minutes later. This *looks* like a tick but
  only fires on tiles where fighting is actually happening — the rest of the
  world stays dormant. You get continuous processes without a global tick.

### 9.3 Two async-specific design rules

- **Low variance / lean deterministic.** "Purely statistical" by headcount and
  equipment is fine, but a coin-flip that wipes an army while the player sleeps
  for eight hours feels catastrophically unfair — they had no chance to react.
  Make losses **proportional to the power ratio**, no wild swings.
- **Doomstacking is self-punishing — no frontage cap needed (decided).** Earlier
  drafts proposed a frontage cap to stop everyone piling into one blob. We
  dropped it, because the map and the clock already solve it: a doomstack is in
  **one place**, and the world is huge with hours-long travel, so a stack is an
  absence everywhere else. If your whole army besieges one front, an opponent
  simply **goes around** and hits your undefended castle and structures — and you
  can't recall an army that's hours away. Unlike fast-relocating RTS deathballs,
  concentration here carries a real, geographic cost. The design handles it.
  - *Optional lever:* a per-tile occupancy cap (units can't all stack on one
    tile) comes essentially for free with tiles if you ever want to force armies
    to spread along a front. Not required.
  - *Residual concern (tuning, not architecture):* if combat is pure low-variance
    headcount, the bigger force always wins predictably, so players may avoid all
    uncertain fights and stalemate into pure maneuvering. Watch for this in
    balancing; it's not a reason to add tactical rules now.

### 9.4 Ranged from neighboring areas

Players can deal damage from **adjacent tiles** (ranged units check neighbors,
not just their own tile, during the arrival check in §9.1). Resolved through the
self-rescheduling round mechanic in §9.2.

---

## 10. Diplomacy: Three States (decided)

Combat is **NOT** automatic on all contact. It is automatic **only between
declared enemies**. Three relationship states:

- **Enemy** — auto-combat when paths cross.
- **Neutral (default)** — paths can cross **without** auto-combat; no ally
  benefits. This is what makes meeting a stranger at a border possible without a
  bloodbath, and what makes diplomacy possible at all.
- **Ally** — no fighting; shared benefits (e.g. gated trade corridors, §11.4).

### 10.1 Escalation must be telegraphed and costly (closes the neutral exploit)

Without this, "neutral" becomes a shield: march an army in safely as neutral,
flip to enemy at point-blank for a free ambush.

> **Declaring enemy is not instant.** It takes effect after a **delay** (some
> hours), and the target gets a **notification immediately** ("Player X has
> declared war, effective in 6 hours"). The async notification system (§12) is
> exactly what makes this fair — the sneak-army must survive the target
> *reacting* before the switch flips.

Betrayal becomes a genuine, risky, telegraphed act rather than a free exploit.

---

## 11. Trade & Cooperation (Barter only — decided)

Trade is core, not a nicety: the game is an economy of **scarcity and distance**,
and exchange is its natural release valve. **Barter only** at launch — no
currency — which keeps the world pure: everything has weight, everything moves,
everything can be taken. No exceptions to the physical rule.

### 11.1 Keep the physical layer; drop synchronous rendezvous

We **rejected** "both players bring goods to a tile and swap in the moment"
because it demands **synchrony** (two players online, both present, at once) from
an **asynchronous** game — it almost never resolves cleanly and strands goods.

### 11.2 Trade post as asynchronous intermediary (decided)

- Build a **trade post** (a structure).
- **Physically deposit** goods into it via caravan (the sim layer is preserved —
  goods still travel and can be raided en route).
- **List barter terms** ("200 wood for 150 stone").
- The offer **persists asynchronously**. Later — minutes or hours, both players
  possibly offline at different times — a partner's caravan arrives, the client
  shows the listing, they accept, the caravan drops its side and picks up the
  goods, and hauls home.

**Nobody must be online simultaneously. Nobody must rendezvous.** The building
absorbs the timing mismatch.

### 11.3 The correct seam: menus for agreement, sim for movement

- **Menu:** browsing listings and accepting terms — this is information; making
  it physical is friction, not depth.
- **Simulated:** goods getting to and from the post — caravans, escorts, roads,
  ambush, capture, all of it.
- **Test for "too much sim":** does making it physical create an interesting
  decision (which route? what escort? is the road safe?) or just a chore
  (clicking "accept")? Physicalize the former, menu the latter.

### 11.4 Trade composes with existing systems

- A stocked trade post is a **raid honeypot** — but it sits at a border, so
  robbing it means **declaring enemy** (with the telegraphed delay), so the owner
  is **warned first**. Trade creates targets; diplomacy governs the strike.
- **Roads** make posts viable (partners must reach them affordably), so a
  well-roaded border becomes a **market** organically.
- **Allied gated corridors** — an allied pair can build a walled, gated trade
  route enemies can't easily raid, giving cooperation **physical expression on
  the map**.

---

## 12. Offline Play, Notifications & Clients

- The authoritative sim runs continuously, independent of logins.
- Every meaningful thing is already an event (arrival, battle started, under
  attack, build complete, war declared). A **push notification is just a
  side-effect** hooked onto event resolution — tag events with "notify player X."
  No separate notification system.
- Clients issue intents and view state; web, desktop, mobile, and companion app
  are all the **same dumb client** talking to the **same server**. Platform/3D
  decisions can be deferred.
- **Action queuing** is essential for fairness (see §13): players plan ahead and
  log off, so being *present* doesn't beat being *smart*.

---

## 13. Open Questions / Decisions Still to Make

1. **Hex vs. square grid.** *(New — top priority alongside the resource model.)*
   - **Hex** — every neighbor equidistant; no diagonal problem; more natural for
     terrain/distance-driven movement; looks better in motion. Slightly more
     complex coordinate math.
   - **Square** — simplest to build and reason about; renders fine with smoothing;
     but diagonals are awkward (√2 longer, or forbid them and look blocky).
   - Leaning hex on the merits for a movement-centric game, but square is the
     faster path to a prototype.
2. **Tile size / role of the grid in the UI.** Two flavors:
   - **Invisible substrate** — small tiles, grid never shown; movement reads as
     fully continuous; players reason about *time*, not tiles.
   - **Visible planning tool** — larger, boardgame-legible tiles players actually
     count when planning ("four tiles, two hours each").
   This is a feel decision about whether the grid is a planning aid or hidden
   plumbing. (The sim is tile-based either way — §2.5.)
3. **Exact road decay rate.** Direction decided (slow; slower at higher
   condition; abandoned routes only). Needs a concrete number: how many days of
   total neglect before a maxed stone road is visibly degrading?
4. **Anti-whale / "fair while offline" meta.** The genre lives or dies on whether
   being logged in 24/7 beats playing smart. Needs an explicit action-queuing
   design and possibly catch-up mechanics.
5. **Combat variance band** — exactly how deterministic; what (if any) low,
   symmetric variance is acceptable. Watch for the predictable-winner stalemate
   noted in §9.3.
6. **Resource model granularity** — discrete stacks vs. continuous quantities
   (affects the whole data model and caravan capacity math). *Settle early.*
7. **Surrender mechanic** — how a player "gives up" and what happens to their
   land/assets.
8. **Edge-swap combat case** — two groups swapping adjacent tiles and passing on
   the edge between events; ignore (simplest, common) or special-case (§9.1).
9. **Future currency** — deferred. If ever added, keep faith with the world:
   make coin a **physical, carriable, raidable** good (a treasury caravan as the
   juiciest raid target), never a weightless abstract balance.

---

## 14. Suggested Build Order

Build the **simulation core first** — headless, deterministic, no graphics, no
networking — drivable entirely from a script:

```
World state (tile grid: biome cost, fog raster, road condition — one structure)
  ↓
Entities (groups: citizens w/ role, tile position, carried goods, carts, horses, escort)
  ↓
Event queue (priority queue keyed on timestamp; ONE global queue)
  ↓
Resolver: pop next event → mutate state → schedule consequent events
  ↓
Persistence (snapshot + seeded event log → fast-forward / full replay)
```

Suggested sequence:

1. **Event engine + replay** — the foundation; prove fast-forward and replay
   work before anything else.
2. **Movement** — tile grid, grid A\* pathfinding, arrival events. (Pick hex vs.
   square first — §13.)
3. **Logistics** — resources, caravans as groups, capacity, capture-on-ambush.
4. **Combat** — arrival-based collision, self-rescheduling rounds,
   deterministic-ish resolution.
5. **Roads** — condition value, traffic feed, decay, smooth speed, emergence.
6. **Fog** — derived visibility, terrain memory, towers/scouts.
7. **Diplomacy** — three states, telegraphed escalation.
8. **Trade** — persistent barter posts.
9. **Clients + notifications** — dumb viewer issuing intents (with smoothed
   movement rendering); event-tagged pushes.

---

*End of draft.*
