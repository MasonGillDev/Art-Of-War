# Fix combat: battlefield entry and the bandit brain (M43)

> Supersedes the June draft in `fix combat` (engagement pin + no-progress guard), which targeted the
> old tile-hop movement M43 replaced.

## The decision

Walking onto a tile with an open battlefield needs room there. A walker that finds none **waits at the
edge** and retries on the board's beats; it never steps in and gets popped to another tile. A pop never
changes a unit's tile. The bandit brain leaves a fight to the board (doctrine, not marches) and never sends
an order that didn't take twice.

**Decided (2026-09-30):** core A + B + E, brain 1 + 2 + 3. **Not doing D** (battlefields as no-go for
through-traffic): too hard a rule while doctrine and AI are still being built. Anything that avoids battles
is opt-in per order or per AI, never global.

## What was wrong

1. **Walk in, pop out, teleport.** "Friends never block a step" let a walker step onto a board's tile, then
   `Placement.SeparateOn` popped it (to another tile if none was free) and the client drew a jump.
2. **Bandits always marched.** `BanditDriver` re-ordered every idle, not-walking bandit each think; a unit on
   a board is never "walking", so fighting bandits were re-ordered and their doctrine overridden.
3. **Order spam.** An order with no room or no path silently did nothing and was sent again each think.

## What was built

- **A. Guard** (`SubtileRoutes.Step`, `Battlefields.Leave`): a step across an edge onto a tile with an open
  board is blocked if a standing non-hostile unit is on the arrival subtile or the unit's side is at the cap
  (`TileCapacity.HasRoom`). A hostile unit on the arrival subtile is a duel, not a block. No board: friends
  still pass through. A unit leaving one board for a neighbour that is also a board meets the same guard and
  stays (keeping its order) until there is room.
- **B. Wait at the edge.** `Unit.WaitingToEnter` + `WaitingSince` (snapshot v44, inside the route block,
  cleared by `SubtileRoutes.Cancel`). The same step is rescheduled at `Battlefields.NextBeat` on the route's
  existing anchor (so `RegenerateQueue` needs nothing new). After `BattleConfig.MaxEntryWaitBeats` (6, a
  constant beside `SteadyMorale`) `Walk.Halt` ends the walk: "no room on the battlefield". A waiting unit is
  not on the board. Wire: `BattlefieldProjection` lists it under the board with `Waiting = true` in the lane
  outside the edge it will come in by; the client already draws that state.
- **E. A pop never changes a tile** (`Placement.Pop`): no free subtile means the best one left (shared with
  an enemy: better a duel than a lost unit), else the unit keeps its own. `RoomNear` stays for things that are
  created (births, refugees, landings).
- **Bandit brain** (`BanditDriver`): no `MoveIntent` for a unit with `Board`; `SetBattleDoctrine` once per
  unit and again only on a change (raiders and landing bands Advance, a camp garrison Hold, a fleeing party
  Withdraw); one `Go` for every march that spots an order that didn't take (same place, still idle, not walking)
  and checks `Walk.CanReach` and `TileCapacity.HasRoom` before repeating it, blocking the place for
  `BlockedCooldownTicks` and picking another (a prize: next `FindTarget`; a seat or staging: `RoomNear`); a
  party joins a fight open at its prize only at `JoinPowerRatioPercent` (80) of the power fighting there,
  otherwise it holds and looks again; a fleeing party skips dark tiles whose straight line of tiles crosses an
  open board.

## Two changes the spec didn't name, and why

- **`Walk.Halt` releases a chase.** A walk halted with `Unit.Pursuit` set left the anchor on a unit that had
  stopped moving (`PursuitRules` calls that a permanent brick). Found by `PatrolTests`, since waiting made
  the case common.
- **Withdraw never backs out into an enemy** (`TurnPlanner.WithdrawEdge`, fed by
  `BoardSurroundings.OwnersBeyond`). With walkers now waiting at an edge, a fleeing King ended up between two
  tiles that each held a Hold soldier and bounced between them for ever, opening a board each time, which
  stalled the scavenge lab for 35,000 ticks. An edge whose neighbour holds a hostile unit is no way out; if
  all of them are, the unit stays and the board carries on.

## Deviations from the draft

- `WithdrawBelow` is a **head count** of the unit's own side left on the board (`BattleDoctrine`), not a
  percentage of max HP. The brain's knob is `WithdrawBelowPercent` (50) of the party size, turned into that
  count.
- The "flee avoids a board" test is a straight line of tiles, not the pathfinder's real route. An opt-in
  avoid-set on the order was the alternative; it was left out to keep D's "nothing global" rule simple.

## Future expansion

A real avoid-set for the pathfinder (per order), a doctrine field for "on contact" (join, hold at the edge,
avoid) replacing the hard-coded group halt, and a mid-tile waiting lane if two waiters ever contend for the
same lane.

## Tests

`BattlefieldEntryTests` (friend on the arrival subtile waits and never leaves its tile; full board waits then
ends; enemy on the arrival subtile is a duel; no board still passes through; a pop never changes a tile; leave
onto a neighbour board is guarded; a withdrawing unit avoids an enemy-held edge; twin run and mid-wait
restore), `BanditBrainBoardTests` (no orders on a board; doctrine once; bad odds skipped and fair odds
joined; no repeated order when the place is full). `SubtilePlacementTests` (the old pop-to-another-tile
test now pins the new rule).
