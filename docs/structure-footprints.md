# Structure footprints: buildings on the battlefield

**Status:** designed with the user, 2026-09-28. Slice 1 (subtile types and movement on the
board) is built; see the update at the bottom. It applies to grid combat (`--combat grid`,
`docs/battlefield-grid.md`).

## The decision

A structure takes up space on its world tile. Each structure kind has a fixed **footprint**:
a 4×4 pattern that gives every subtile of the tile a **subtile type**. The type decides who
can enter the subtile, from which sides, who can stand on it, and what fighting from it
does. The player chooses one of four **rotations** when placing a structure, which turns the
pattern.

The same pattern also decides **world movement**: which edges of the tile can be crossed,
and by whom. A castle wall that nobody walks through in battle is also a wall nobody walks
through on an errand. Units and roads therefore visibly enter through the gate.

**The number of units a side may have on a tile** is the number of subtiles that side may
stand on. It applies all the time, not only in battle.

## Subtile types

| Type | Who can enter | From which sides | Who can stand on it | In a fight |
|---|---|---|---|---|
| **Open** | anyone | all | anyone | normal |
| **Blocked** | nobody | none | nobody | nothing |
| **Wall** | owner and allies | three sides: the defended side, and along the line. The outer side is closed. | owner and allies | only archers deal damage from it |
| **Tower** | owner's and allies' archers | as Wall | owner's and allies' archers | reach: the subtile in front and the two beside that one |
| **Gate** | owner and allies | all (a way through the wall) | owner and allies | normal |
| **Cover** | anyone | all | anyone | archers cannot hit a unit on it |

**Nobody passes through a Wall or Tower subtile.** You can step on from inside and walk
along the line from wall to tower and back, but the outer side is closed both ways. The
only way through a wall is a Gate, for the owner and allies, or the castle's open gap, for
anyone.

Behind the names, a subtile is stored as properties: who may enter, an entry-side mask,
which roles may stand, and a combat modifier. The named types are presets over those
properties. A new type is a new combination, not a new code path, and the tile's
world-movement edges are worked out from the same data.

**Deferred types** (named so the property model leaves room for them):
- **Rough:** costs a turn to leave (a ford, mud, scaffolding). It bends the one-subtile-a-turn
  rule, so it needs its own spec decision.
- **Water:** blocked on foot unless bridged.
- **High ground:** archers reach further or ignore cover. The tower is the owner-only case of it.
- **Burning:** damages whoever ends a turn on it.

## Walls, gates, towers and the castle

- **Walls** are Wall subtiles. The owner and allies stand on them, stepping on from the
  defended side and moving along the line, but they never cross the outer side. Enemies
  cannot enter them. Only archers deal damage from them. The defended side comes from the
  rotation.
  - A standalone wall is a line along one of the tile's two middle rows, turned to follow the
    wall. Which middle row is still open.
  - It has one HP pool per tile at today's values (Wall 500).
  - Enemies next to it damage it even while defenders are on the board (Update 2026-09-28
    in `docs/walls-and-gates.md`).
  - A **Gate** (the existing `Gate` kind) is the way through a standalone wall, for the
    owner and allies only.
- **A tower in a wall** is a Tower subtile in the wall line, rotated by the player to face
  the side the wall is defended against. It has the Wall's entry rule, so archers reach it
  from the inside or along the line. From it they hit the subtile in front (on the far
  side) and the two subtiles beside that one.
- **The castle** is a ring of 12 outer subtiles around an open 2×2 courtyard:
  - 11 ring subtiles are Wall subtiles, closed on their outer side (two outer sides at a
    corner).
  - One ring subtile is Open: the gate, a gap anyone may use. The rotation chooses which
    side it faces.
  - Castle walls **cannot be damaged**. The castle falls by the existing siege rule: once no
    defenders are on the tile, the attackers' summed power takes its HP (1,000). Razing it
    defeats the owner.
  - Attackers can only come in through the gap, from the tile facing it, one subtile at a
    time. That is by design: taking a castle is hard, and a kingdom is meant to be beaten by
    cutting its supply lines.
- **Other buildings** keep the simple siege rule: the attackers' summed power, blocked while
  the owner's units are on the tile. Their footprints are mostly Blocked subtiles. A
  proposed table (not confirmed): 1×1 for House, Stockpile, Lodge, Smithy, Tower, Idol,
  Cache and Dock; 1×2 for Barracks, School, Workshop and Smelter; a 1×1 hut plus Cover land
  for extractors; Cover over the old footprint for Rubble and construction sites.

## World movement: the edge rule

A tile's edge can be crossed by a side if the footprint has a subtile on that edge the side
may enter.
- **Castle:** only the gate edge is crossable, by anyone including the owner. The ring
  has no way through.
- **Standalone wall tile:** nobody crosses it, as today. A **gate tile** is crossable by the
  owner and allies only (`AlliedPassage`, as today). Standing on the wall, and an
  attacker's foothold on its near half, are board matters; world movement keeps treating
  the tile as whole.
- **The mechanism is already there.** Rivers store a per-tile edge mask, and pathfinding
  already costs each (from, to) crossing. A blocked crossing is a crossing that is refused.
  Rotating an existing structure changes its edges, so it has to fence any path in flight.
- **Roads** (M34) are laid down by wear from traffic, so they only form on edges that can be
  crossed. A road on an edge that becomes blocked falls out of use and fades.

**Client:** a single route inside each tile, found over the footprint from the entering edge
through passable subtiles, is used both by walking figures and by the road painter. People
enter through the gate subtile and walk round houses, and roads go where people walk. It is
presentation only; outside battles the sim does not track position inside a tile.

## The unit cap

- **Per side** (a player and their allies), not a total. Enemies must always be able to
  step in and fight, or a full tile could never be attacked.
- **Cap = the subtiles that side may stand on.**
  - Castle: the owner gets 16 (the ring and courtyard); attackers get 5 (the gap and the
    courtyard).
  - Wall tile: the owner gets all 16; enemies get only the Open rows.
- **It applies all the time.** A unit moving into a full tile stops and waits on the tile
  before it, reusing the existing blocked-hop path.
- **Everything that creates units respects it:** births, training, refugees and settlers,
  landing from boats, bandit spawns, scenario setup. A new unit on a full tile appears on
  the nearest tile with room.
- **What it replaces:**
  - the board's overflow shelter (M41 build decision D1);
  - probably the banded crowding cost on hops (`MovementConstants.BandedCrowdingCost`).
- **Knock-on:** the AI's raid recall (`AiConfig.RecallCiviliansUnderRaid`, `DefendRung`)
  pulls every civilian to the castle, and has to respect the cap.

## Why

- **Why patterns, not free placement.** A fixed pattern per kind and rotation is
  deterministic and costs almost nothing to save (the kind plus a rotation byte). It gives
  the player one meaningful choice, which way the building faces, without a layout editor.
  Free placement inside the tile was considered and deferred; it belongs with pre-battle
  formations.
- **Why the castle is a ring, not a 2×2 keep.** A 2×2 keep in the middle of an open board
  (the first proposal) keeps the board open but gives no chokepoint. The ring makes the gate
  the whole fight.
- **Why boards stay separate.** Joining neighbouring open boards into one grid was
  proposed, so attackers outside the castle could reach its wall. It was rejected: to fight
  the gate you send a unit onto the tile, and losing your archers' support there is the
  cost of attacking a castle. Fronts across several tiles come from separate battles on
  neighbouring tiles.
- **Why walls can be stood on, but not crossed.** Defenders need to stand on the line to
  shoot from it and to move along it between towers. Letting the owner step through it
  would make the gate pointless. A version where the owner crossed freely was proposed and
  corrected by the user the same day. Archers-only damage stops the wall from being a free
  soldier platform.
- **Why a per-side cap and not the shelter.** With every unit on a subtile, battles open
  with no special cases. A total cap was rejected because it makes a full tile untouchable.
- **Why the structure layout also rules world movement.** If walls only existed in battle,
  units on errands would walk through them. The edge rule makes the world and the board
  agree, and roads follow on their own.

## Future expansion

- The deferred subtile types above.
- Player-chosen placement inside the tile, alongside pre-battle formations.
- Siege engines as damage multipliers: rams against gates, trebuchets at range, siege towers
  over walls.
- A structure-damage knob per role.
- Breaches per subtile.
- Several structures on one tile. Today the world stores one structure per tile, keyed by
  tile; subtile footprints make several possible, and that is a larger sim change.

## Open questions

1. Which middle row a standalone wall uses.
2. (Settled: the `Gate` kind stays as the only way through a standalone wall.)
3. How a standalone `Tower` (today: vision 7, HP 150, no wall) relates to a Tower subtile in
   a wall.
4. **Castle ring archers have nothing in front of them.** Boards don't join, so an archer
   on the ring only reaches subtiles on the castle's own board: the gap and the courtyard.
   Their fire lands on attackers who are already inside.
5. The footprint table for the other buildings, and whether farm land is Cover or Open.
6. Can soldiers stand on a wall? The rule says yes; they are safe there from melee but deal
   no damage.

## References

- `docs/battlefield-grid.md`: the board, turns and collision rules; its `SubtileLayer`
  (build decision D3) is the slot this fills.
- `docs/walls-and-gates.md`: M26 walls, and the Update 2026-09-28 wall-on-board rules.
- `docs/sieges-and-conquest.md`: the whole-tile siege rule kept for buildings.
- `docs/roads-on-edges.md`, `docs/rivers.md`: the edge-mask mechanism.

## Update 2026-09-28 — slice 1 built: subtile types and movement on the board

**What was built:**
- **Sim** (`src/Sim.Core/Battlefields/`):
  - `SubtileLayer`: the kinds Open, Blocked, Wall, Tower, Gate and Cover, closed sides per
    subtile, and the layer's owner.
  - `BoardMover`: friendly (owner or ally, per `Fortification.IsOwnOrAllied`), and archer.
  - The rules: `CanStand`, `CanStep` (closed sides both ways) and `TowerFront`.
  - `Footprints.For(structure)`: the Castle ring, and the Wall, Gate and Tower lines, turned
    by `Structure.Facing`.
  - `Battlefields.LayerFor` now reads the tile's structure.
- **Everything that moves or seats a unit obeys the layer:**
  - pathing, which searches the steps this unit may take;
  - the planner: coming on, orders, swaps, advancing only on foes it can reach, and
    withdrawing only by an edge it can get out of;
  - the resolver: moves checked, a closed exit refused;
  - board seating: open ground before walls, arrivals only where they could walk from their
    edge, otherwise waiting outside their lane;
  - order validation.
- **Tower archers** reach the two subtiles beside the one in front.
- **`Structure.Facing`:** snapshot v42, North by default. Scenarios gain `castle-facing:`;
  the castle raid faces south, where its attackers arrive.
- **Wire:** `BattlefieldDto.Kinds`, `Closed`, `HasLayerOwner` and `LayerOwner`. The prod
  client draws walls, towers, gates, blocked ground, cover, and a bright bar on each closed
  side (`BattleOverlay.Footprint`).
- **Tests:** `StructureFootprintTests` (21). `ScenarioLibraryTests` now also requires the
  two sides to stand on a board together within six turns of a battle opening.

**Choices made while building** (open questions resolved by default; say if any is wrong):
- **Standalone wall row:** the line sits on the middle row nearer its outer side. Attackers
  keep one row as a foothold; defenders keep two.
- **The castle gap** is the second subtile along its facing edge, (1, 0) when facing north.
- **Gate and Tower tiles** are a wall line whose two middle subtiles are Gate or Tower.
- **Soldiers may stand on walls;** towers take archers only.
- **Only Castle, Wall, Gate and Tower** have footprints. Every other kind stays plain ground
  until its footprint table is confirmed.
- **Seating on reopen:** a unit that walked onto the tile is seated only where it could have
  reached from the edge it came in by. Found in testing: when attackers are all waiting
  outside, the board closes (waiting units don't count as fighting) and reopens at once, and
  without this rule the reopened board seated them in the courtyard.

**Known gaps, until the next slices:**
- **World movement doesn't know the footprint yet.** A unit can still walk onto a castle
  tile across a walled edge. On the board it then waits outside for good, and the board
  closes and reopens each beat. The edge rule (slice 2) stops it arriving that way.
- **Rotation isn't chosen at placement yet.** Everything faces north except scenario
  castles.
- **The per-side cap isn't built.** Overflow still shelters: 16 of the owner's units fit on
  a castle board.
- **Nothing damages a wall on the board yet** (the adjacency rule for standalone walls).

## Update 2026-09-28 (later) — wall lines join their neighbours; corners turn 90°

User: a wall at a corner is not a straight line. It turns a right angle. A wall, gate or
tower tile's line is now shaped by the neighbours it **joins**:
- **What it joins:** a standing wall, gate, tower or castle of its own owner on each of its
  four sides. This is the same rule the client's `FortificationDresser` draws by, so the
  board matches the picture.
- **Where the line runs:** to the edge of every joined neighbour, one row in from its
  outer side, so it meets the neighbour's line at the shared edge and an enclosure stays
  sealed across tiles.

| Joins | Shape |
|---|---|
| none | straight across its facing |
| one side, or two opposite sides | straight across the tile |
| two sides at right angles | a corner: a 90° turn |
| three or four sides | a T or a cross |

**Which way a corner turns, from Facing:**
- **Facing away from the joined neighbours** gives the **outer corner** of an enclosure.
  The line runs near the two outside edges, and attackers keep an L-shaped strip of the
  tile.
- **Facing toward a joined neighbour** gives the **inner corner**. The line runs near the
  joined edges, and attackers keep a single subtile.

**Other rules:**
- **Straight runs:** Facing is the outer side of a run across it. For a run along it, the
  outer side is a quarter turn anticlockwise (N → W, S → E, E → N, W → S).
- **Closed sides:** each wall subtile is closed on its run's outer side, except where the
  line itself carries on that way, which is the inside of a turn.
- **Gates and towers** are the two middle subtiles of a straight run, or the corner subtile
  of a turn.
- **Castles** don't change shape by neighbours. Their ring is already closed, and a wall
  ends at the castle's edge.
- **The line only holds if every tile of an enclosure faces outward.** The player sets
  Facing at placement (a later slice). Until then everything faces north, so vertical runs
  and corners only line up in scenarios and tests that set Facing.
- **Open:** T-junctions and crosses split a tile into three or four parts. Which parts count
  as "outside" is decided only by each run's own facing. Revisit once walls with branches
  are played.
- **Code:** `Footprints.JoinsOf` and `Footprints.Line`. **Tests:** outer corner, inner
  corner, a tower at a corner, and a three-tile L in the world whose lines meet at the
  shared edges.

## Update 2026-09-28 (later) — the raider camp, as drawn by the user

The user is drawing each structure's footprint. The first is the raider camp (`BanditCamp`),
facing North:

```
      x=0   x=1   x=2   x=3
y=0    .    ███    .     .
y=1   ███    .    ███    .
y=2   ███    .     .     .
y=3    .     .     .    TWR
```

- **███ Blocked:** nobody enters.
- **TWR Tower:** only the camp's own archers stand on it, never a player's. The user first
  drew it at (0, 0), where the blocks and board edges walled it in, then moved it to
  (3, 3), where archers climb it from (2, 3) or (3, 2).
- **Everything else** is open land.
- **Rotation:** it turns with the camp's Facing, like the castle. Camps are placed by the
  game, and every camp faces North for now. Facing its target kingdom was proposed; not
  decided.
- **Tower reach:** see the next update: 2 moves.
- **Code:** `Footprints.BanditCampNorth`. **Test:**
  `TheRaiderCamp_IsAsDrawn_TheTowerTakesOnlyItsOwnArchers`.

## Update 2026-09-28 (later) — archer reach is a per-subtile value; camp towers reach 2

User: camp towers reach further, meaning **every subtile within two moves**, and **Blocked
subtiles stop arrows**.

- **Reach is a per-subtile value** in the layer (`SubtileLayer.ReachAt`, set by a
  footprint through `Build`'s `reach` argument). An archer standing on a subtile shoots
  every subtile it could walk to in that many orthogonal steps, with Blocked subtiles the
  only obstacle. Arrows fly over walls and closed sides, not through buildings. The
  default is 1: the four neighbours, the old rule unchanged.
- **The raider camp's tower has reach 2** (`Footprints.CampTowerReach`). From its corner
  that covers (3,2), (2,3), (3,1), (2,2) and (1,3).
- **Targets:** still the lowest HP. Ties go to the nearer target, then the order the search
  met them (N, E, S, W first), then the unit id (`TurnResolver.InReach`, `ArcherTarget`).
- **The wall tower's two extra subtiles** beside the one in front are still a separate
  rule. They are diagonal, and diagonals aren't moves. It could become a reach shape later.
- **Not on the wire yet:** the client doesn't know a subtile's reach, so it can't draw a
  tower's range.
- **Tests:** camp tower reach, blocked subtiles stopping arrows, the nearer target winning
  ties.

## Update 2026-09-28 (later) — Farm and School, as drawn

Drawn by the user, facing North; every other subtile is open land. Farm fields are open land,
not cover.

```
Farm                            School
      x=0  x=1  x=2  x=3              x=0  x=1  x=2  x=3
y=0    .    .    .    .        y=0    .    .    .    .
y=1    .    .   ███   .        y=1    .   ███  ███   .
y=2    .    .    .    .        y=2    .   ███  ███   .
y=3    .    .    .    .        y=3    .    .    .    .
```

- **Code:** each turns with its structure's Facing (`Footprints.Drawn`). Adding a drawn
  building is one line there.
- **Test:** `DrawnStructures_AreAsDrawn_AndTurnWithTheirFacing` checks every drawing in all
  four facings. Each new drawing adds one row to it.

## Update 2026-09-28 (later) — House, Workshop, and the Dock's water edge

Drawn by the user, facing North; every other subtile is open land. ~~~ is the new **Water**
subtile type.

```
House                           Workshop                        Dock
      x=0  x=1  x=2  x=3              x=0  x=1  x=2  x=3              x=0  x=1  x=2  x=3
y=0    .   ███  ███   .        y=0    .    .    .    .        y=0    .    .    .   ~~~
y=1    .    .    .    .        y=1    .    .   ███   .        y=1    .    .   ███  ~~~
y=2    .    .    .    .        y=2    .   ███  ███   .        y=2    .    .    .   ~~~
y=3    .    .   ███   .        y=3    .    .    .    .        y=3    .    .    .   ~~~
```

**Water, and boats on the board: decided to keep it simple for now.** The user asked
whether water on the dock is worth the complication. It was kept as terrain, with boats
left off the board.
- **Water** (`SubtileKind.Water`, wire 6) is where boats will stand. For now nobody stands
  on it. Arrows fly over it.
- **The dock's water edge isn't turned by Facing.** It always lies on the edge facing the
  dock's slip, the water tile its boats use (`Footprints.DockTurns`).
- **How boats work today** (`docs/boats.md`), which is why they aren't on the board:
  - a boat is a unit that lives only on water tiles and never enters land, and the dock is
    land;
  - passengers aren't on any tile while aboard, don't fight, and drown with the boat;
  - boarding and landing happen between the dock tile and its slip;
  - boats have 40 HP and 0 attack.
- **Putting boats on the dock's board is a naval-combat design, deferred**, with these
  decisions:
  - boats entering a land tile;
  - boarding and landing on the board (passengers stepping from a boat subtile to the
    quay);
  - a rule for fights between boats, since two 0-attack boats never finish a fight;
  - whether passengers take a subtile while aboard;
  - capturing a dock from the water (already deferred in `docs/boats.md`).
- **Client:** `BattleVocabulary.KindWater`, drawn as a blue square.
- **Tests:** House and Workshop join the drawing theory. `TheDock_HasItsWaterOnTheSlipSide`
  covers all four slip directions.

## Update 2026-09-28 (later) — Quarry, Lodge, Cache

Drawn by the user, facing North; every other subtile is open land. ▒▒▒ is **Cover** (the
user's green): anyone walks on it, and archers can't target a unit standing there.

```
Quarry                          Lodge                           Cache
      x=0  x=1  x=2  x=3              x=0  x=1  x=2  x=3              x=0  x=1  x=2  x=3
y=0    .    .    .    .        y=0    .    .    .    .        y=0    .    .    .    .
y=1    .    .   ▒▒▒   .        y=1    .   ▒▒▒   .    .        y=1    .   ███   .    .
y=2    .    .    .    .        y=2    .   ▒▒▒   .    .        y=2    .    .    .    .
y=3    .    .    .    .        y=3    .    .    .    .        y=3    .    .    .    .
```

- These are the first footprints to use Cover, which the rules have supported since M41.
  The client already draws it as a green square.
- **Test:** the drawing theory now checks Cover subtiles as well as Blocked ones.
- **Still undrawn:** see the next update.

## Update 2026-09-28 (later) — Rubble, Smelter, Smithy

Drawn by the user, facing North; every other subtile is open land.

```
Smelter                         Smithy                          Rubble
      x=0  x=1  x=2  x=3              x=0  x=1  x=2  x=3
y=0    .    .    .    .        y=0    .    .    .    .        all open land
y=1    .   ███  ▒▒▒   .        y=1    .   ███   .   ▒▒▒
y=2    .    .    .    .        y=2    .    .    .    .
y=3    .    .    .    .        y=3    .    .    .    .
```

- **Rubble is drawn all open.** This replaces the proposal that rubble be cover over the
  old building's shape: a razed building leaves no shape on the battlefield.
- **Still undrawn:** see the next update.

## Update 2026-09-28 (later) — Mine, Lumber camp, Barracks

Drawn by the user, facing North; every other subtile is open land. TWR is a Tower (the
owner's and allies' archers only).

```
Mine                            Lumber camp                     Barracks
      x=0  x=1  x=2  x=3              x=0  x=1  x=2  x=3              x=0  x=1  x=2  x=3
y=0    .    .   ███   .        y=0    .    .    .    .        y=0   TWR   .    .    .
y=1    .    .    .    .        y=1   ███   .    .    .        y=1    .    .   ███   .
y=2    .    .    .    .        y=2    .    .   ███   .        y=2    .   ███  ███   .
y=3    .    .    .    .        y=3    .    .    .    .        y=3    .    .    .    .
```

- **The barracks tower** is reached from (1,0) or (0,1). It has the normal one-move reach.
  The user didn't mark it for more, unlike the raider camp's tower (2).
- **Still undrawn:** see the next update.

## Update 2026-09-28 (later) — Stockpile, Construction site; the canal drawn, not built

Drawn by the user, facing North; every other subtile is open land.

```
Stockpile                       Construction site (whatever is being built)
      x=0  x=1  x=2  x=3              x=0  x=1  x=2  x=3
y=0    .    .    .    .        y=0    .    .    .    .
y=1    .   ███   .    .        y=1    .   ███   .    .
y=2    .   ███   .    .        y=2    .    .   ▒▒▒   .
y=3    .    .    .    .        y=3    .    .    .    .
```

**The canal is drawn, but not built yet:** a two-wide water channel down the middle columns,
with banks on either side.

```
      x=0  x=1  x=2  x=3
y=0    .   ~~~  ~~~   .
y=1    .   ~~~  ~~~   .
y=2    .   ~~~  ~~~   .
y=3    .   ~~~  ~~~   .
```

- **Today a finished canal leaves no structure.** Its tiles become `Biome.Water`
  (`docs/canals.md`).
- **The user's direction:**
  - canal tiles carry a Canal structure with this footprint;
  - bridges become the only way across a canal;
  - world pathfinding learns which edges of a tile connect to which (the crossing-mask
    proposal, below).
- **Waiting on the user's decisions and on the pathfinding slice.**
- **Still undrawn:** Idol (open ground until drawn).

## Update 2026-09-28 (later) — world movement follows the tiles' shapes; feet stay off water; canals built

**Decided with the user:**
1. Canal tiles stay `Biome.Water` (boats sail them, they irrigate), with a Canal structure
   on top.
2. **Only boats go on water.** A move aimed at water, including water hidden in the fog,
   walks to the nearest land instead of being refused. Refusing would let a player map the
   fog's water by clicking into it.
3. A canal's channel follows its route and turns at bends, like walls.
4. World pathfinding learns tile shapes, from a proposal the user brought: "the pathfinder
   asks about the shape of the ground, not who is standing where".

**Built. All of it applies only in grid-combat worlds** (`CrossingRule.Applies`), like the
rest of the battlefield work. The default game moves exactly as before until the grid
becomes the default.

- **`Crossings`:** per tile and per side (friendly, other), which entry edge can reach
  which exit edge, and whether a mover can stop there. Worked out from the footprint. It
  reads the ground's shape, never who is standing where, so a defender in the castle gap is
  a battle matter, not a pathing one. Derived data, never saved.
- **`CrossingRule`:** the question pathfinding asks. Planning follows the fog contract of
  the M26 walls: a structure the player can't see plans as open, and the mover finds out
  hop by hop. `GroundTruth` is the per-hop check.
- **`Pathfinding.FindPath`** has an overload taking the rule and the edge the mover came
  in by. The search state is (tile, entry edge), but only on tiles whose shape matters. On
  open ground it is the tile alone, so a search costs what it did. Without a rule, paths
  are identical to before.
- **Moves and group moves** plan with the rule and check each hop against the ground. A
  hop that has become impassable (a canal dug across the route) now stops the unit.
  Before, it would have scheduled the arrival an Impassable number of ticks away.
- **Castle gates face walkable land.** At genesis the gate faces north if that neighbour is
  dry, walkable land, else east, south, west. Found in testing: a castle at (0, 0) facing
  north had its gate opening off the map.
- **Feet off water:** `MovementCost.FeetKeepOffWater`. `LandNear` lists the nearest land
  tiles, nearest first (Manhattan), then north to south, west to east. Unit and group moves
  aimed at water go to the first one they can reach.
- **Canal structure:** `Canal`, owned by its digger, no health, snapshot row with no
  fields. It is placed on every dug tile when a canal finishes in a grid world. Its
  footprint is a two-wide channel (the middle four subtiles plus a two-wide arm to each
  joined neighbour: another canal tile, or open water). A dead end stops at the middle.

**Found in testing, and a decision taken: canal tiles are water to feet at world scale.**
- **The bug:** the first version let feet walk a canal's banks inside canal tiles. The
  planner then crossed the canal by zig-zagging: in on the west bank, north a tile, back
  south, out on the east bank.
- **Why:** world movement remembers only the edge a unit came in by, and each end edge of
  a canal tile touches both banks.
- **The fix:** feet follow a canal on the land beside it, and a bridge will be the way
  across. The banks exist on the battle board only.
- **The alternative, deferred:** walking the banks would need each unit, and each planned
  hop, to carry which area of the tile it is in, with a snapshot change. It isn't worth
  that yet.

**A guard against this for every future drawing:**
`EveryDrawnFootprint_HasOneAreaPerEdgePerSide` checks that no edge of any footprint leads
into two separate areas.

**Open, found by that guard: the raider camp.** Since the tower moved to (3, 3), the corner
(0, 0) is open land walled in by the blocks at (1, 0) and (0, 1), reachable only from
outside the tile. Its north and west edges each lead into two areas. The user to choose:
block (0, 0), open (1, 0) or (0, 1), or leave it. It is harmless in battle, but world
movement could treat the camp's north-west corner as a way through. The camp is excluded
from the guard until then.

**Also still open:**
- **Bridges:** the user will draw them. Until then a canal can't be crossed on foot in a
  grid world.
- **Other "can I get there?" checks don't use the rule yet:** the AI's reachability checks,
  `FormGroupIntent`'s rendezvous check, `Battlefields.Exits`, and the client's route through
  each tile. They should read the same masks.

**Tests:** `CrossingTests` covers:
- the castle's crossings;
- walking in by the gate and out by it;
- a fogged enemy stopped at the wall;
- a move aimed at water ending on the nearest land, never on water;
- a path round a lake;
- the default game still wading;
- a canal's banks on the board, with feet never entering;
- a path round a canal's end;
- a finished canal leaving canal tiles.

## Update 2026-09-28 (later) — the camp's corner is blocked

User: block (0, 0). The raider camp, facing North, is now:

```
      x=0  x=1  x=2  x=3
y=0   ███  ███   .    .
y=1   ███   .   ███   .
y=2   ███   .    .    .
y=3    .    .    .   TWR
```

That leaves ten open subtiles and the tower. The camp is back under the
one-area-per-edge guard, and passes.

## Update 2026-09-28 (later) — footprints on the wire; placement takes a facing

**Wire** (`Sim.Server`):
- **`StructDto`:**
  - `Facing`: 0 N, 1 E, 2 S, 3 W. A dock's is its slip's side.
  - `HasFootprint` and `Footprint` (`FootprintDto`: `Kinds`, `Closed`, `Reach`, 16 each,
    row-major): the resolved layout. Sent only when it isn't plain ground.
  - **Joins are this viewer's:** a wall or canal joins only neighbours in the same view,
    the structures the viewer can see. That is how the client already joins drawn walls,
    so a wall's shape never gives away a wall in the fog. Open water is terrain and always
    counts.
- **`WorldDto.Footprints`:** every kind's pattern facing North, joining nothing
  (`Footprints.Pattern`), for the build preview. Static reference data, like the build
  catalogue.
- **`BattlefieldDto.Reach`:** an archer's reach per subtile.

**One builder:** `Footprints.Build(kind, owner, facing, joins)` serves placed structures,
the per-kind patterns and battles. The dock's pattern is stored facing North (water on the
north edge; the user's drawing turned) and turns to its slip's side.

**Placement:**
- **`PlaceSiteIntent` and `BuildIntent` take `facing`:** 0..3, or -1 for "work it out".
  The construction site keeps it, and the finished building inherits it.
- **Worked out** (`Footprints.DefaultFacing`): a wall, gate or tower faces away from the
  owner's nearest castle, along the axis the tile lies further out on, east or west on a
  tie. Anything else faces North.
- **`PlaceWallIntent` always works it out**, per segment.
- **Client:** the build tool starts facing North. **F** turns it clockwise and
  **Shift+F** anticlockwise. R resets the camera's tilt and Q/E turn the camera, so
  neither was free. Walls, canals and docks don't turn.
- **Client preview:** `SelectionRenderer.DrawFootprintPreview` draws the server's pattern,
  turned, on the hovered tile, in the battle grid's colours (`SubtileColours`, now shared
  by both).

**Not yet:**
- Walls and canals preview as their tiles only, not their joined shapes.
- The client doesn't fit building models to their footprints.
- The in-tile walking and road routes aren't built.
- The AI builds everything facing North (walls away from its castle).

**Tests:**
- `FootprintWireTests`: the genesis patterns, the view's facing and layout, a facing
  kept from order to building, the worked-out facings, out-of-range refused.
- `IntentJsonTests.Footprints_ABuildOrderCarriesItsFacing_OrNone`: the client's exact
  payloads.

## Update 2026-09-28 (later) — bridges

**Decided with the user:**
- **What a bridge is:** a structure builders build on a canal tile, and the only way
  across a canal on foot. Boats still pass under it.
- **The user's drawing:** the straight canal's channel with a one-wide deck across it at
  row 1:

```
      x=0  x=1  x=2  x=3
y=0    .   ~~~  ~~~   .
y=1    .   ═══  ═══   .
y=2    .   ~~~  ~~~   .
y=3    .   ~~~  ~~~   .
```

- **Built with the scaffolding as a deck** (my recommendation, accepted): while a bridge
  is being built its site already carries the deck, so builders walk onto it from the
  banks.
- **It can be destroyed.** Razed or demolished, it goes back to plain canal, not rubble.
- **Canals only.** No bridges on natural water.
- **Numbers**, a first cut for tuning: 40 wood, 10 stone, 24 hours, 1 builder, HP 200.
- **Roads do go across a bridge.**

**Built:**
- `StructureKind.Bridge` (24); the `Bridge` structure (snapshot row with no fields); its
  catalog entry.
- **`SubtileKind.Bridge` (7, a deck):** anyone stands on it, arrows fly over it, and boats
  will pass under it.
- **Footprint** (`Footprints.Deck`): the channel with the deck as drawn. On an east–west
  canal it is turned a quarter, so the deck is at column 2. It joins the banks into one
  area, so the one-area-per-edge guard holds; the guard now checks bridges in both
  directions.
- **Placement** (`PlaceSiteIntent.PlaceBridge`): only on the player's own canal tile, and
  only a straight one (joining two opposite sides). The construction site replaces the
  Canal structure. `Bridge.IsDeck` treats the site like the bridge for its footprint, its
  joins and walking.
- **Walking:** a bridge, or one being built, is the one canal tile feet may enter. It
  walks like grassland, and the road bonus applies: `Road.EffectiveCost` gained an
  optional base cost. Roads form on it from foot traffic like anywhere.
- **Boats:** unchanged. The tile stays Water.
- **Removal:** `SiegeDamage.Teardown` puts a Canal back for a bridge or a bridge site,
  whether razed or demolished.
- **Client:**
  - `SimVocabulary.KindBridge` and its name;
  - the build tool accepts only your own canal tiles (`Fit.NeedsCanal`, "a bridge goes on
    a straight stretch of your own canal") and doesn't turn;
  - `BattleVocabulary.KindBridge` is drawn in a timber colour on the grid and in the
    preview;
  - the world draws the dock's planking as a placeholder until a bridge model exists.
- **Tests** (`CrossingTests`):
  - built on a straight canal, its scaffolding walkable;
  - the deck joins the banks;
  - bank to bank goes over it;
  - boats still pass;
  - the deck turns on an east–west canal;
  - refused off a canal, on someone else's canal, and on a dead end;
  - a razed bridge goes back to canal.

## Update 2026-09-28 (later) — the Idol; every structure now has a footprint

The user: "just one tile that's blocked, which will be the idol itself; you decide where."
It sits at (1, 1), just off centre, and the rest is open ground. Every structure kind now
has a footprint (Rubble's is all open, by drawing).

## Update 2026-09-29 — the per-side unit cap is built

The user: "only the amount of units that can stand on a tile can be on a tile." Built as
designed under "The unit cap" above, in grid-combat worlds (`TileCapacity`).

- **Capacity:** the subtiles a non-archer of the side may stand on.
  - Open ground: 16. A school: 12. A castle: 16 for its owner and allies, 5 for
    attackers.
  - A tower's subtile is left out: it is an archer's post, so a side of soldiers is never
    promised a place it can't take.
  - A structure the player can't see counts as open ground when planning.
- **The side:** a player and its allies, off boats. Enemies count against their own side,
  so a full tile can always be attacked.
- **Arrivals:** a unit, or a whole group, that would overfill its side stops on the tile
  it was leaving. This is the same yield as the old flat cap and a wall.
- **The planner** routes round tiles it can see are full for its side: its own units
  always, allies' on visible tiles.
- **New units:** a unit that is born, arrives as a refugee or settler, spawns as a bandit
  (all through `Population.OnUnitAdded`), lands from a boat, or is placed by a scenario, on
  a tile its side has filled, appears on the nearest tile with room.
  - Nearest first, then north to south, west to east. It must be ground the unit can
    stand on.
  - If nothing within 8 tiles has room, it goes where it was meant to: better over the
    cap than lost.
- **Kept as fallbacks:** the flat 50-per-tile cap applies everywhere, and battle overflow
  shelter covers what the cap can't prevent. That means allies sharing a board, and a
  building finished on a tile already holding more units than it leaves room for.
  **Nobody is evicted.**
- **Known effects:**
  - The castle raid scenario's 18 Blue units now start as 16 in the castle and 2 on the
    nearest tile with room.
  - The AI's raid recall pulls every civilian to the castle. The extras now stop on
    neighbouring tiles. Recall should learn the cap (the M42 list).
- **Tests** (`TileCapacityTests`):
  - the capacities (open ground, castle for both sides, school, barracks with its tower);
  - a 17th unit stopped while an enemy gets in;
  - a new unit on a full tile placed on the nearest tile with room;
  - the planner going round a full tile;
  - no per-side cap in the default game.
