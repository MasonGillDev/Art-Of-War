# Subtile movement: units always stand on a subtile

**Status:** decided with the user, 2026-09-29; reviewed and amended the same day (see the
update at the bottom). Being built as M42 (`docs/m42-status.md`). It applies to grid-combat
worlds (`--combat grid`) until the grid becomes the default (M43).

## The decision

Every unit always stands on a **subtile**, not just while a battle is open. A world tile is
a 4×4 block of subtiles. **Only enemies share a subtile**, and sharing one is the battle
signal: a subtile holding units of two hostile owners is a duel. Everyone else, allies and
neutrals included, stands on a subtile of their own. Movement comes at two resolutions:

1. **World moves, as today.**
   - Tile-level pathfinding: terrain, rivers, crossing masks, fog.
   - The **road bonus between tiles**.
   - These are the strategy view's orders, most orders in the game: bringing units to
     their post.
2. **Subtile moves: a route the player draws, walked exactly.**
   - No pathfinding: the unit takes the steps as drawn.
   - Each step costs a quarter of the world hop onto that terrain, rounded up (the
     combat prototype's world pace), with **no road bonus**.
   - **Crossing a river edge pays the ford cost.**
   - A route stays within the **3×3 block of world tiles around the tile the unit starts
     on** (12×12 subtiles), and within a step limit (the block allows 144 steps; the
     limit keeps saves and the wire small).
   - Steps must be 4-adjacent. The client fills a fast drag's gaps in a straight line,
     never by pathfinding, and the sim refuses a gap.
   - Use them to put units in place, to choose where they come in from, and to meet an
     enemy where you want.

**On an open battlefield** the same route is walked one subtile per turn, on the beat, by
the collision rules (`docs/battlefield-grid.md`). A battle's move and route orders become
subtile routes.

## Units never appear on a subtile: they walk there

**Arriving by a world move.**
1. The hop lands the unit on an **entry-edge subtile**, in the lane nearest the subtile it
   stood on (so a hop never shifts it sideways for free). It is a real position, on the
   edge it came in by.
2. A unit that **stops** on the tile then walks on, at quarter-hop pace, toward the back:
   - the row opposite the edge it entered by is first, moving toward that edge;
   - within a row, open ground before walls, gates and towers, then lower lane first;
   - it walks through subtiles it may enter (footprint rules: blocked squares, walls,
     closed sides). **A unit never passes through a wall or a blocked structure**, and
     there are no fixed doors.
3. A unit that carries on through the tile (its route has another hop) hops on from where
   it is, and never walks in.

So a column coming in by one road fills the far side of the tile first and never blocks
its own way in, and **an arriving enemy meets whatever holds the front**. A line on the
entry edge stops the column at the edge, and the fight starts there: nothing lands behind
a wall. A castle's gate is the same: attackers file in through the gap one at a time.

**The four entry lanes are the bottleneck.**
- A hop lands only if a lane of the entry edge is free for the unit. A lane subtile held by
  an **enemy** is a duel on arrival; one held by anyone else blocks that lane.
- If no lane is free, the arrival **waits on its source tile** (still on its own subtile
  there) and tries again each step time. Nothing lands on top of a friend.
- **A group lands in files.** Its hop lands as many members as there are free lanes; the
  rest wait on the source tile and land as lanes free. The group counts as arrived when its
  last member is in.
- **A hop onto a tile with an open battlefield** is not landed at all. The arrival is put
  off to the next beat (`MoveArrivalEvent` reschedules), the unit stays on its source
  subtile, and on the beat it comes on as a battle move. This replaces the M41 "outside
  lane" position, which was not a subtile. (Built in phase 4, with the board.)
- **Throughput:** a unit passing through holds its entry lane until its next hop lands, so
  an edge carries four units per hop time. That is the lane bottleneck, working as meant.

**Units that are created, not moved.** Births, training, refugees, landings, bandit spawns
and scenario units are placed directly:
- centre rows first, then the outer rows (the M41 default), lowest lane first;
- only on **usable** subtiles: a unit may stand on it and can reach it from some edge;
- prefer a subtile nobody holds; failing that, one held only by enemies (a duel);
- a tile with no room puts the unit on the nearest tile with room (`TileCapacity.RoomNear`);
- a unit still left with no subtile (a tile over its cap, as piled-up test worlds do) has
  none (null); it is never put on a friend.

**Boats and passengers.** A boat stands on a subtile of its water tile like any unit. A
passenger aboard has none until it disembarks, when it is placed like any created unit.

**Formation is simply where your units stand.** When an enemy comes onto the tile and a
battle opens, everyone fights from the subtile they were on. What you set up with subtile
moves is your formation.

## Blocked steps

There are no beats on a quiet tile and no collision table. A step into a subtile a friend
holds, or an arrival with no free lane, **waits and tries again once per step time**. The
route stays open. The player sees it, cancels the order and draws another. Nothing drops a
route for them, so two columns walking through each other simply wait until someone
intervenes. The retry costs one event per step time, not per tick.

## Who shares a subtile, and the cap

- **Only hostile owners share a subtile.** A subtile holds at most one unit of any
  non-hostile owner, allied or neutral. This is the rule on quiet tiles and on boards.
- **The cap counts everyone non-hostile.** A side may stand on as many subtiles as it may
  stand on (`docs/structure-footprints.md`), and units of non-hostile owners on the tile
  also take subtiles from the same 16, so a tile can fill with a mix of allies and
  neutrals. An enemy can always step in, as before: hostile units don't count against you.
- **Peace ends a fight in place.** If two owners stop being hostile while their units share
  a subtile (a proposal accepted mid-duel), they are separated: the lowest unit id stays and
  the others are popped by the same nearest-open push as a footprint change, in id order.

## When the ground changes under a unit

A footprint changing under standing units (a structure completing, a rotation, a gate, a
wall going up) can leave a unit on a subtile it may not stand on. It is **popped to the
nearest open subtile of the same tile**:
1. never onto a subtile an enemy holds (that would force a duel);
2. "nearest" is **walking distance** over subtiles the unit may enter, not straight-line;
3. ties are broken by N, E, S, W, and units are handled in ascending id order, each taking
   the nearest free subtile as things stand at that moment;
4. a tile with no free subtile puts the unit on the nearest **other** tile with room
   (`RoomNear`, excluding this tile), as a birth does. If there is none, it stays and takes
   the best subtile left, even one shared with an enemy: better a duel than a lost unit;
5. its route is trimmed or cancelled if it now crosses the changed subtile;
6. it happens at the event that changed the footprint, not on a beat, and a pushed unit
   keeps its place in the next turn's resolution as normal;
7. a unit in a duel whose subtile changes is pushed like any other, and the duel ends.

## Why

- **The user's direction:** "From strategy view you move world tiles; when you zoom in you
  move units across subtiles and they occupy a specific subtile", and "moving from one
  subtile to another is completely user defined … this takes weird pathfinding out of the
  equation." This follows the combat prototype's two scales (quiet tiles walk at world
  pace, battlefields in turns) without copying its code.
- **Why not pathfind at subtile resolution everywhere.** It was proposed first, as a
  two-level search: a tile route, then subtile legs inside it. It lost for two reasons.
  - Most orders are strategic and gain nothing from subtile detail. A large map has
    hundreds of thousands of subtiles.
  - For the orders where subtiles matter (placing a formation, lining up an approach),
    the player wants the exact route, not the pathfinder's guess.
- **Why a 3×3-tile limit.** Subtile routes are for fine placement, not marching. Beyond
  that, the world move is the right tool.
- **Why arrivals walk in, back to front.** Edge-first loading and instant placement were
  both proposed. Instant placement teleports an attacker past a defending line. Edge-first
  fills the entry row, which then blocks everything behind it. Landing on the edge and
  walking to the back fills the tile the way a column files in, and stops at the first
  defender.
- **Why this can't be a speed exploit.** The hop lands on the entry edge, not the far
  side, and the walk is paid for at a quarter hop a step: a hop (30) plus three steps
  (24) plus one more (8) is 62 ticks to cross a tile edge that two hops cross in 60.
- **Why roads only between tiles.** A road is travel between places. Inside a tile the
  player is manoeuvring, and there the ground itself sets the pace.
- **Why only enemies share a subtile.** It makes "the same subtile" mean exactly one thing,
  a fight, and removes every question about allies stacking or diplomacy changing under
  units. Peace mid-duel is the one case that needs a rule.
- **Why before the default flip.** Grid combat stands on units having real positions.
  Board-only positions (M41) left the formation question, overflow shelter, and the
  canal-bank ambiguity unsolved; all three disappear once every unit has a subtile.

## What it replaces or settles

- **M41's board places** (`BoardSlot.At`) become the unit's own subtile. Seating at battle
  open is gone: everyone is already somewhere.
- **Battle overflow (shelter)** goes too: every unit always has a subtile, and the cap
  keeps it that way.
- **The "outside lane" waiting position** goes: a held arrival stays on its source subtile.
- **The world-scale entry-edge approximation** (`CrossingRule`) can use the unit's real
  subtile. The rule "each edge leads into one area" is no longer needed for correctness;
  canal banks can be walked at world scale.

## Future expansion

- **Group subtile formations:** drawing one route for a group, with the members shifted
  by their places, as in the prototype.
- **Preset formations** (a line, a column, archers behind).
- **Doctrines that act at world scale,** e.g. a guard that steps into a gap.
- **The client's in-tile walking animation** can follow the unit's actual subtile path
  instead of a made-up route.
- **Counting only reachable subtiles in the cap.** The cap counts every subtile a side may
  stand on. A walled-in pocket (the raider camp's corner at (0,0)) is counted but can't be
  reached, so the last unit in waits on the entry edge. Harmless; count reachable
  subtiles if it ever matters.

## Acceptance tests (to pin)

- A unit always has exactly one subtile (none while embarked). No two non-hostile units
  share one; hostile units may, and that is a duel.
- A hop lands on an entry-edge subtile, in the lane nearest where the unit stood; a unit
  that stops walks on toward the back, open ground first, only through subtiles it may
  enter, and never past an enemy holding the front.
- An arrival with no free entry lane waits on its source tile and lands when one frees. A
  group lands in files and counts as arrived when its last member is in.
- A hop onto an open battlefield puts the arrival off to the next beat, and the unit stays
  on its source subtile until then.
- A created unit is placed centre rows first, on a usable subtile, preferring one nobody
  holds.
- A subtile route is walked exactly as drawn, at quarter-hop pace. The road bonus doesn't
  apply, and a river edge costs the ford.
- A route reaching past the 3×3 block, over the step limit, or with a gap, is refused. A
  route validated against what the player can see never tells them about a hidden wall.
- A blocked step waits and retries once per step time, and the route stays open.
- On an open battlefield, the route advances one subtile a turn.
- A battle opens with everyone where they stood.
- A unit whose ground changes is popped to the nearest open subtile by the rules above,
  deterministically, never onto an enemy; a full tile moves it to the nearest tile with
  room. Peace separates units that share a subtile.
- Save and restore mid-route; twin runs hash equal.

## Update 2026-09-29 — review with the dev, before Phase 1

The first version of this doc had arrivals take an arrival subtile back to front at once.
A review against the code (`MoveArrivalEvent`, `GroupArrivalEvent`, `TileCapacity`,
`Battlefields`) and the user's answers changed it:

- **Arrivals walk in (the user).** "Units never just spawn on a tile. They must actually
  move to that subtile", so they can't teleport past a line of enemies: they try to reach
  the back, get stopped at the front line and start combat. This replaced instant placement
  and fixed two holes at once:
  - a hostile arrival could land behind a defending line or in a castle courtyard;
  - a world hop that lands on the far side, followed by a one-step subtile route, would
    have crossed a tile for 8 ticks instead of 30. With the edge landing and a paid walk it
    can't. (The review's fix, charging a full hop for crossing an edge, was not needed.)
- **Only enemies share a subtile (the user).** Occupying a subtile together is the battle
  signal. This replaced "one unit per side per subtile", which the code split two ways: the
  board keyed occupancy by owner, the cap by owner-and-allies. It brings two consequences:
  the cap counts non-hostile neutrals too, and peace mid-duel needs a separating rule.
- **Entry lanes and groups.** Walking in makes the four edge lanes the bottleneck, and a
  group's arrival was atomic. Both are new rules above (waiting on the source tile, landing
  in files).
- **Held arrivals.** The M41 "outside lane" is not a subtile. A hop onto an open board is
  now put off to the beat instead.
- **Blocked steps (the user).** "Keep the route open until there is no more. The player can
  see this and manage it themselves by cancelling and redrawing." So nothing drops a route.
- **Footprint changes (the user).** Pop to the nearest open subtile, with a tie-break, and
  make sure it has no weird consequences. The seven guards above are that check.
- **Shared tile entry (the user).** Hops and subtile steps that cross a tile edge use one
  method extracted from `MoveArrivalEvent`, so reveal, `EnteredFrom`, camps, the scout log,
  docks, the cap, fortification blocks, the crossing rule and the combat trigger can't
  diverge.
- **Dropped:** the concern that the cap counts an unreachable pocket. Nobody is trapped (a
  unit leaves the way it came in), so it stays as a future tidy-up.
- **Not decided here, settled while building:** whether a subtile step across an edge
  credits road wear (starting position: no), and the route step limit.

## Update 2026-09-29 (later) — all movement on subtiles; world-tile movement retires

After building M42, the user: "everything has gotten pretty complicated with movement … all
unit movement should be subtile based and roads appear on subtiles. No more world-tile
movement … world tiles still exist for structures and battlefield rules, but movement is
completely subtile based."

**Why:** the complexity M42 met all sits at the seam between the two resolutions.
- A hop lands on an entry lane, then walks in as a second movement.
- Edge lanes are a bottleneck: a unit holds its lane until its next hop, units with no lane
  wait and retry, and groups land in files.
- A hop onto an open board is put off to the beat.
- Crossing masks guess from the entry edge alone.
- There are two cost models (a hop against quarter-hop steps) and two road models.

One resolution replaces all of it with one pathfinder, one step event and one cost.

**Decided:**
1. **World-tile movement and pooled combat retire together.** Subtile movement becomes
   the game's only movement, and grid combat its only combat. This work becomes the
   default flip (M43), instead of a grid-only feature beside the old game. The price is
   rewriting the movement, hauling, road and AI timing tests once.
2. **Moving friends pass through each other outside battles.** Only a unit standing still
   takes up its subtile. On a battlefield the collision rules apply as now. Otherwise a
   busy haul road jams.
3. **Roads live on subtiles.** A road is a chain of worn links between neighbouring
   subtiles: traffic wears the steps it actually takes, and a worn step is cheaper. It
   runs straight or bends at 90°. Trails form where people really walk, right up to a
   building's entrance.
   - This replaces the M34 model: arcs between tile centres, and the rule "roads only
     between world tiles".
   - Today's roads are converted by following them through each tile: straight across
     where a road goes straight, turning 90° inside the tile where it bends from one edge
     to another.
4. **A tile order** (the strategy view's "go to that tile") ends on the free subtile nearest
   the tile's centre. This replaces back to front.

**Still true:**
- World tiles still decide structures and footprints, battlefields (a contested tile opens
  a board), the per-side cap and fog.
- Drawn subtile routes stay, for exact placement, with the 3×3-tile limit.

**Before anything is torn out,** measure subtile pathfinding on a real-size map (the
default is 128×128 tiles, 262,144 subtiles). Compare a search over the whole subtile grid
with one confined to the tile-level route's corridor (the combat prototype's
`allowed_tiles`). Plan: `docs/m43-status.md`.

## Update 2026-09-29 (M43 step 2) — the user's answers on passing, ending and the cap

1. **Friends never block a walk; only stopping needs room.** The user: moving units passing through
   each other is what roads with many units need. So a walking unit steps onto any subtile it may
   enter, whoever is on it (a friend, standing or moving; an enemy opens a board, as before). Only a
   unit that STOPS needs a subtile of its own. This replaces the M42 sidestep and blocked-step wait
   (a friend on the next subtile no longer blocks anything). It supersedes the plan's question 1.
2. **A walk that ends where a friend now stands** is re-aimed at the nearest free subtile:
   nearest first by steps, then N, E, S, W. (The user: "choose one".)
3. **Siege rounds stay** as the structure-damage round when a board closes with only attackers;
   only the unit-vs-unit pooled rounds retire. (The user: yes.)
4. **The per-side cap counts units standing, not units passing.** (The user didn't know what this
   meant, so it was decided for them:) the cap exists so a side never has more units standing on a
   tile than the tile has subtiles for it. A unit passing through was counting against it and could
   be held up by a full tile. Now a moving unit never waits on the cap, and a unit that would
   stop on a tile with no free subtile is re-aimed (2 above), possibly into the next tile. So the
   invariant is: standing non-hostile units never share a subtile, moving ones may.

## Update 2026-10-01 — no friend ever comes to rest on a friend

**The crash.** A live game died at the landing with `Owner 1 has two units on (1,0)`
from `BoardState`. (1,0) is the castle's gate gap. Two of one side stood on one subtile
when a board read the tile. The rules above forbid that, but two holes allowed it:

1. **A walk that ends mid-pass.** Friends pass through each other, so a walker can be on a
   friend's subtile at any moment. `Walk.Halt` (for example "no room to stop" on a full
   tile) stopped the unit wherever it was, which could be on that friend.
2. **A board freezing a passer.** When a fight opens, walkers on the tile are taken where
   they stand. On a tile already filled to the cap (a garrisoned castle, at 14), a friend
   passing through is one too many. `Placement.SeparateOn` can't find it a free subtile,
   and `Pop`'s fallback left it where it was.

**The rule, restated by the user:** friends may share a subtile only while they move. A unit
never comes to rest on a friend's subtile.

**The fix:**
- `Walk.Halt` ends with `Walk.Settle`. A unit that stopped on a friend walks to the
  nearest free subtile, across a tile edge if its own tile is full.
- `Battlefields.Enroll` refuses a unit whose subtile a non-hostile unit on the board
  already holds. It stays off the board (it can neither act nor be hit, the same as a unit
  with no subtile). If it was walking it walks on; otherwise it settles.
- `Battlefields.RunTurn` drops any second unit of one owner on one subtile before building
  the board, and settles it. This is the last line of defence, so one bad placement can't
  take the server down again. `PlanPreview` (a pure read for the view) filters the same way.

Tests: `NoFriendlyOverlapTests`, three cases that failed before the fix (two of them with
the crash's exact exception).

**Not fixed here:** `Placement.Pop`'s fallback still keeps a unit on a shared subtile when
its tile has no free one (for example after `Reseat` on a full tile). The board guards
above make that harmless in a fight. A cleaner `Pop` would need a `Simulation` to start a
settle walk, since `Placement` only has the world.
