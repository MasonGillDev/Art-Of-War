# Combat — Mechanics Spec (Battlefield Grid)

**Status:** LOCKED ruleset, ready for development. Validate numbers in the standalone prototype (`combat-prototype-spec.md`) first. **Amended 2026-09-25:** battles are fought in **simultaneous turns** (§5); units move freely even in contact (nothing locks); every unit has a **morale** value (§5, "Morale"); any unit may **leave a battlefield by any edge** (§4); orders include **traced routes** (§3, §5); starting values come from the game (§5). See the updates at the bottom.
**Supersedes:**
- the stat-pool combat model (all of an owner's units on a tile pooled, proportional damage, lowest-health-first casualties) — replaced by per-duel resolution;
- the open questions and "variable clock" sections of the earlier tactical-battle-layer doc.
**Still rejected:** real-time free-movement combat (determinism and fairness reasons unchanged).

Marks: **⚠ VERIFY** = this doc assumes something about current code that must be checked before building.

---

## 1. Core idea

When hostile units share a world tile, that tile **opens as a battlefield**: a 4×4 grid of subtiles. Units take positions on it. Soldiers fight enemies on their own subtile; archers shoot enemies next to them. A unit may step out of a fight at any time; a fight lasts while both units stay. Battles are fought in **simultaneous turns**: both sides give orders without seeing the other's, then every order resolves at once, so each turn is a guess about what the other side will do. Lines, flanks, support fire, morale, and chokepoints decide battles — not raw totals.

Two scales, one game:
- **The grid** rewards holding lines.
- **The world map** rewards stretching them: attacking from several neighboring tiles opens several fronts. Flanking is decided by *which neighbors you attack from*.

Every unit is a **cohort/battalion** (existing fiction). Up to 16 per side per battlefield reads as a real battle.

---

## 2. Battlefield lifecycle

### Opening
A world tile opens a battlefield when it becomes **contested**: units of two owners who are hostile (`AreHostile`) are both present. ⚠ VERIFY: reuse the existing trigger point where combat currently starts on arrival (`MaybeBeginCombatOnTile` or equivalent).

On opening, every unit on the tile gets a subtile position:
- **Arriving units** deploy onto the **edge row facing the world tile they came from.**
- **Units already present** (defenders, workers) deploy by their standing **doctrine formation** (§7), default: center rows. *Decided 2026-09-25:* centre rows for now. The player must be able to set a formation before the tile becomes a battlefield; that comes later, through the same placement step.
- **Overflow** *(decided 2026-09-25)*: past 16 units of one side, the rest shelter off the board (in the castle, on a castle tile), safe and unable to act. They come on like arrivals when a lane frees.
- Placement is deterministic: fixed fill order within the row (index 0→3), processed by unit ID. If a unit cannot be placed (row full of its own side), it deploys to the next row inward, same order.
- Placement is immediate. The first turn resolves on the first beat at least half a turn later (§5).

### While open
- Subtile positions exist only while the battlefield is open.
- The battlefield resolves a **turn on every beat** (§5), a beat shared by every battlefield in the world: a self-rescheduling event keyed to the tile — the existing combat-state/round-event pattern, reused.
- Units on the tile follow grid rules (§3–§4). Units elsewhere move at world scale as normal.

### Closing
When the tile is no longer contested (one side gone: dead or withdrawn), the battlefield closes:
- Subtile positions are dropped. Surviving units return to world scale on that tile.
- Units that had move orders **continue their route** into the next world tile.
- The turn event is cancelled (anchor cleared, epoch fence).

---

## 3. Grid rules

| Rule | Detail |
|---|---|
| **Occupancy** | At most **one unit per side per subtile**. A subtile holding one unit of each side is a **duel**. |
| **Movement** | Orthogonal only, **one subtile per turn**, resolved on the beat (§5). No diagonals, no slipping past. |
| **Entering** | A move may target any orthogonally adjacent subtile that isn't blocked terrain. Whether it arrives is decided by the collision rules (§5): a unit ends a turn in a subtile only if no other unit of its side ends there. Ending in a subtile with an enemy is a **duel**. |
| **In contact** | Units in a duel take the same orders as any other unit: move, swap, withdraw. Nothing is ever locked. |
| **No passing through** | A unit leaving a duel may not move onto the subtile its opponent came from, nor carry straight on in the direction it entered the duel's subtile. It can back off or slide sideways. This is "no slipping past" for units that are free to move in contact. |
| **Duel ends** | When one or both units reach 0 HP, or when either moves out and the other doesn't follow. Damage lands on the duels that stand after the turn's moves (§5). |
| **Swap** | One order for two adjacent allies: they trade subtiles on the beat, in contact or not (allies may pass each other; enemies can't). It replaces both units' orders for that turn. A swap into a duel's subtile hands that duel to the incoming ally (**relief**: a fresh unit takes over, the wounded one steps back), unless the enemy's own move changes the outcome under the collision rules. |
| **Pathing** | A **move** order names a destination. Each turn the unit takes the next step on the shortest orthogonal path (blocked terrain avoided, units ignored, ties broken N, E, S, W). A **route** order is a traced list of subtiles, followed exactly and never re-planned. A unit knocked off its route (by a swap, say) walks back to the route's next subtile and carries on. A step that fails under the collision rules (§5) is tried again next turn. |
| **After a win** | The survivor resumes its order (re-path from its current subtile). |

### Turn pace
A unit moves at most **one subtile per turn**, whatever the terrain, so it crosses the board in 4 turns. Board movement no longer follows world travel speed: `SubtileStepTicks` and its ⚠ VERIFY are dropped (2026-09-25). Terrain that costs extra turns (forest, say) is a later option.

Off the battlefields, units keep world pace (a grass tile in 30 ticks). Crossing a world tile on a battlefield takes 4 turns (240 ticks), so going round a battle is about 8 times quicker than going through it (§11, question 12).

---

## 4. World-tile rules

1. **Leaving (amended 2026-09-25).** Any unit, in a duel or not, may leave a battlefield **across any edge**. This replaces hold-until-clear and withdraw-only-toward-`EnteredFrom`.
   - Leaving resolves first on the beat, so a leaving unit is off the board before anyone moves.
   - It then makes a normal world hop (full `ExecutionCost`) onto the neighbour, and can do nothing else until it lands. So leaving is never free, and a battlefield never speeds a unit's travel up.
   - A move or route whose next step crosses the edge leaves this way and keeps its order.
   - This replaces the old engagement pin (units stopped on arrival). ⚠ VERIFY `PinBelligerents` / epoch-bump logic: retire the pin, and let the leaving hop finish.
2. **Withdraw.** A standing order to leave back the way the unit came. `EnteredFrom` is stored on each boundary crossing. The unit walks to that edge row, a subtile a turn, and leaves on the next beat. A unit that never crossed in (it was there when the battle opened, or was born, trained or spawned on the tile) withdraws by the nearest edge.
3. **Entering an open battlefield.** A unit whose world move would end on an open battlefield is **held on the tile it came from** until the next beat. It then enters that neighbor's edge row as that turn's move. **Entry subtile:** the player may pick the lane (a traced route picks it exactly); default is the first free lane in index order. If the lane ends the turn holding an own-side unit, the entry fails and the unit waits for the next beat. If the lane ends the turn with an enemy there, the unit duels on arrival.
4. **Several fronts.** Each neighboring tile feeds a different edge. Attacking from multiple neighbors is how a 4-wide defensive line is beaten.

Consequence (intended): 4 units on an edge row completely block a head-on attack from that neighbor. The attacker must win duels one lane at a time, or open another edge.

---

## 5. Turns and damage

Battles are fought in **simultaneous turns** (amended 2026-09-25). During a turn, each side gives its orders without seeing the other side's. At the end of the turn, on the **beat**, every order resolves at once. Both sides then see the result and plan the next turn.

- **The beat:** every `RoundTicks` (start: 60 ticks, one game-hour), on ticks divisible by `RoundTicks`. Every battlefield in the world resolves on the same beat. A turn is one real minute at 1 tps (after day X) and 15 seconds at 4 tps (the prelude).
- **The first turn** of a new battlefield resolves on the first beat at least half a turn after it opens, so both sides get time to plan.
- **Orders:** Hold, Move to (a subtile), Route (a traced list of subtiles, §3), Swap (with an adjacent ally), Withdraw, Stop (drop the order and return to doctrine), and the entry lane for a unit waiting to come on (§4). An order stands until it is done or replaced. A unit with no order takes its turn from its doctrine (§7), worked out from the positions at the start of the turn. An order counts for a turn if it arrives before that turn's resolution runs.
- **Hidden orders:** a side never sees the other side's orders, only what happens on the beat.

### Resolution, on the beat

1. **Leaving:** withdrawals, and moves whose next step crosses the edge, leave the board (§4).
2. **Moves** resolve simultaneously, by the collision rules below. Units waiting to come on enter here.
3. **Duels** stand wherever a subtile holds one unit of each side.
4. **Morale** is computed from the new positions (see "Morale").
5. **Damage** is collected before any is applied:
   - Every unit in a duel deals its damage to its opponent.
   - Every unit in a **clash** (it charged an enemy that charged it back, and it is not in a duel after the moves) deals its damage to that enemy. A duel comes first: a unit in a duel fights its duel, not the clash. A clashing archer fights the clash and doesn't shoot.
   - Every **archer not in a duel** picks one target among enemies in the 4 orthogonally adjacent subtiles **of its own battlefield** (a unit on the neighbouring tile isn't on the board), excluding enemies in Forest. Target: **lowest current HP**, ties broken **N, E, S, W**. Archers can fire into a duel (support fire).
   - Archers in a duel deal their bow damage to their opponent. They have less HP and die quickly to soldiers, but they can move back out of reach.
   - Modifiers go through the existing `Unit.Buffs` seam (king's trait, future equipment/training), then morale scales the result.

   Then all damage is applied simultaneously.
6. **The dead** are removed via the existing death primitive (§8). Survivors carry on with their orders; nothing is ever locked.
7. If the tile is no longer contested, the battlefield **closes** (§2).

A duel formed by this turn's moves fights this turn. A unit that moves out of a duel escapes this turn's damage, unless its enemy followed it; then they fight where they meet.

### Collision rules

Moves are resolved by rule, never by who ordered first.

| Case | Result |
|---|---|
| Two enemies move into the same empty subtile | Both arrive: a duel. |
| Two enemies try to swap subtiles (each charges the other) | Neither moves: enemies can't pass through each other. They **clash**: each deals its duel damage to the other this turn, like two charges meeting in the middle. |
| A unit moves into a subtile an enemy is leaving | It arrives; the enemy is gone. |
| A unit moves into a subtile where an enemy stays | It arrives: a duel. |
| Two friends move into the same subtile | The lower unit ID arrives; the other stays where it was. |
| A unit moves into a subtile where a friend stays | It stays where it was. |
| Friends move into subtiles other friends are leaving (a swap, a column, a wheel) | All arrive, as long as each unit ahead arrives. |
| A unit that fails to move stays where it was | Moves into its subtile are checked again. Failures can chain; resolution repeats until nothing changes. |

**Idle battlefields:** if a turn would have no duels, no archer with a target, no unit ordered (or sent by doctrine) to move or withdraw, and no one waiting to come on, the battlefield **suspends** instead of resolving empty turns. An order or an arrival wakes it, and it resolves on the next beat. This also closes the old "zero-damage round reschedules forever" bug class.

### Starting values (tune in prototype)

The game's own numbers, as the prototype plays them (amended 2026-09-25; the 100/20 placeholders are retired):

| Role | HP | Damage/turn | Attacks | Source |
|---|---|---|---|---|
| Soldier | 30 | 3 | same subtile | `UnitCombatCatalog` |
| Archer | 15 | 5 | same subtile, or one adjacent enemy | " |
| Bandit | 25 | 3 | same subtile | " |
| Other roles (workers, haulers, builders, scouts; king, heir) | 10 | 1 | same subtile only | " |
| + Sword (soldier) | | +3 | | `EquipmentCatalog` |
| + Bow (archer) | | +4 | | " |
| + Shield (soldier or archer) | +10 | | | " |

A kingdom's fielded units are a **soldier with sword and shield (40 HP, 6 a turn)** and an **archer with a bow (15 HP, 9 a turn)**. Two equal soldiers still kill each other in the same turn (7 turns each way).

Non-combat roles occupy subtiles and fight weakly. A hauler caught on a battlefield is a real loss (cargo drops, §8).

### Morale (added 2026-09-25)

Every unit has a **morale** value, a percentage: **100 is steady**. It scales the damage the unit deals.

v1 has one source and one effect:

| | Rule | Starting value (tune in prototype) |
|---|---|---|
| **Source: the line** | +`LineSupport` for each **friendly unit in an orthogonally adjacent subtile of the same battlefield** (the same 4 neighbours an archer reaches; a friend on the neighbouring tile isn't on the board). Friends in duels count. | `LineSupport` = 25 |
| **Effect: damage** | Damage dealt = base damage × morale ÷ 100, integer, rounded down. Applies to duels and archer fire alike. | — |

- **What the numbers give:**
  - A unit alone is at 100.
  - In a 4-wide line, the ends are at 125 and the middle at 150.
  - A middle unit in a two-deep block is at 175, and a unit with friends on all four sides at 200.
  - Example: a sword-and-shield soldier (6) in the middle of a line deals 9 to the lone attacker in front of it, which deals 6 back.
- **When it is computed:** each turn, from positions after the moves, before damage is collected (resolution step 4). In v1 morale is **derived, not stored**, so there is nothing to snapshot.
- **What it does to play:** the shape of a formation matters, not just the count.
  - The ends of a line are its weak points and the middle its strong point.
  - Killing one unit lowers both its neighbours' morale from the next turn, so a breakthrough spreads.
  - A wide line covers more lanes; a compact block fights harder.
  - Coming round the end of a line (from another edge) thins it without any free hit.
- **Built to grow.** Morale is where later effects plug in, each one a new source or a new effect. Candidates (none in v1):
  - *sources:* the king nearby (his aura could become morale), a fed home, a friend dying next to the unit, stepping out of a fight, winning a duel, low health, being outnumbered on the tile, fighting on home ground;
  - *effects:* damage taken, breaking (below a threshold the unit falls back on its own), holding a lane under archer fire.

  A source with memory (a death nearby, a retreat) makes morale stored per-unit state. That adds a snapshot field and a FormatVersion bump at that point.

---

## 6. Terrain on the battlefield

**v1 (decided 2026-09-25): the board stays open.** Every subtile is a standard tile of the tile's biome, with no effect on movement or combat. The model keeps a per-subtile layer, so the terrain below, and other occupants such as structures, can fill subtiles later.

Later, each subtile gets terrain, derived **deterministically** from the world tile (biome, river crossing, district) and the tile seed.

| Terrain | Movement | Combat | Source |
|---|---|---|---|
| Open | passable | — | default |
| Blocked | impassable | — | river crossing, palisade/wall |
| Forest | passable | cannot be targeted by archers | forest biome tiles (pattern of subtiles, not necessarily all) |

- **River tiles:** the river becomes a blocked row/column with one **ford or bridge subtile**. A bridge is a one-lane chokepoint.
- **Walled districts:** palisade becomes blocked border subtiles with one **gate subtile**. Walls finally have a combat meaning.
- **Later (not v1):** high ground (archers reach 2 subtiles), structures as subtile objects and combat targets (sieges).

Build order, after v1: Blocked first (most value), then Forest. Everything else after balance.

---

## 7. Presence, absence, and doctrine

- **Present player:** gives orders during the turn — move, swap, withdraw, choose entry lane — in contact or not. They resolve on the beat (§5).
- **Absent player:** a unit without an order takes its turn from its **doctrine**, a standing per-unit (or per-group) battle behavior, worked out deterministically from the positions at the start of each turn:

| Doctrine | Behavior |
|---|---|
| **Hold** (default) | Stay on current subtile. Fight whatever enters. |
| **Advance** | Move toward the nearest enemy. |
| **Support** (archers) | Move to a subtile adjacent to the nearest friendly unit in a duel; else hold. |
| **Withdraw at threshold** | If own side's unit count on this tile drops below X, units withdraw (from a duel too). |

- Doctrine is inert state set by intents (same pattern as standing orders); it never picks participants.
- A **Hold** order overrides doctrine; **Stop** drops the unit's order and returns it to doctrine.
- An AI (bandits, rivals, the Day-X host) orders a battlefield's units once a turn, from the positions just after the beat (or when the battlefield opens), never mid-turn. A present player plans from the same information.
- Default doctrine on deployment: soldiers Hold on the front row, archers Support.
- Raiders, bandits, and the Day-X host use the **same rules and doctrines**. No special combat code for AI factions.
- **Doctrine is predictable,** so a present opponent can learn it and out-guess it turn after turn (§11).

---

## 8. Integration with existing systems

| System | Behavior |
|---|---|
| **Death** | Via the existing membership/death primitive. Laden units drop cargo to ground resources. Group attrition-disband applies. Breeding: only the **gestating parent's** death aborts a gestation. ⚠ VERIFY death path still clears in-flight obligations. |
| **Groups** | On battlefield open, group members deploy and act as individual units. Group orders resume at world scale on close. ⚠ VERIFY group movement anchors are fenced correctly on open/close. |
| **Health** | Persists after battle. ⚠ Decide whether HP recovers over time or requires rest in a friendly district. |
| **King** | A combat unit on the grid. His Royal Trait (Warrior) buffs friendly units within **Manhattan distance R subtiles** (start R = 1). Heir's minor buff likewise. Losing him mid-battle is real. |
| **Fog** | Battlefield state is visible only to players with vision on the tile. Enemy orders are never visible, only their results. Scouts can report battles in progress. |
| **Telegraphed war / raids / Day X** | Unchanged. Their arrivals simply open battlefields. Day X opens many at once, and they all resolve on the same beat. |
| **Loss condition (castle)** | ⚠ Out of scope here — sieges spec decides how a castle tile falls. Dependency noted. |

---

## 9. Determinism and persistence

- Integer-only. No randomness in combat.
- Turn resolution is order-independent: all of a turn's orders resolve together by the collision rules (§5). Ties between friends go to the lower unit ID, never to the earlier intent.
- Damage collected then applied simultaneously → order-independent.
- **Snapshot state grows:** per open battlefield: turn anchor (next beat tick + Seq, suspended flag), subtile terrain is derived (not stored); per unit: subtile position, standing order (hold, destination, swap, withdraw; entry lane), the committed path and whether it is a traced route, a held arrival waiting to come on, `EnteredFrom`, the subtile it last moved from (no passing through), a busy-until tick while it travels after leaving, doctrine. No step anchors: nothing moves between beats. (No locked flag: nothing locks. Morale is derived each turn in v1, not stored.) FormatVersion bump. ⚠ VERIFY RegenerateQueue rebuilds the turn events from these anchors.

---

## 10. Presentation (client)

- Subtile grid appears only on open battlefields (faint 4×4 overlay on the tile).
- Units render as **battalion formations** occupying their subtile; banners for identity.
- Duels: both formations engaged in one subtile. No locked indicator: either side can step out. Show each unit's morale (for example, banner height).
- Each turn's resolution plays as a short **animated sequence** (moves, clash, arrows flying, casualties), never as teleporting state changes.
- Archer fire: arcs from shooter to target each turn.
- Radial actions on a unit: Move (subtile), Swap, Withdraw, Set Doctrine, Choose Entry Lane, also while it is in a duel. They are orders for the next beat.
- **Planning:** your orders drawn as arrows, ghost markers for where your units will end up, and one countdown to the beat for all your battles. The enemy's orders are never shown, nor are the paths of the enemy's units.
- **Routes are traced** by dragging across subtiles. The unit follows the route exactly; a group follows it side by side, each unit shifted by its place in the formation.
- For each of your units, show the move it will try on the next beat and, if its last move failed, why ("ally in the way", "enemies can't pass each other", "cannot pass its opponent").
- Named battalions in the side panel (e.g. "Bridge Infantry") for doctrine and chronicler text.

---

## 11. Open questions

1. **Battle timing after Day X.** The world clock slows sharply after Day X. What is `RoundTicks` in real time then? Battles should resolve within a sitting when players are present. The earlier "consensual acceleration" idea conflicts with a single shared world clock — treat it as dropped unless a per-battlefield time scale can be made deterministic and fair. **Answered 2026-09-25:** a turn is one combat round (`RoundTicks` = 60), one real minute after day X. The beat never comes early for players who are ready, because the clock is shared.
2. Non-combat role combat values.
3. HP recovery rule after battle.
4. King's buff radius and magnitude (balance lab).
5. Entry lane default: first free lane vs. nearest to the unit's destination. **Partly answered 2026-09-25:** a traced route picks the lane exactly, so the default only matters for plain moves.
6. Castle tile / siege resolution (separate spec).
7. **Leaving contact is free (2026-09-25).** Watch for units stepping out just before every round tick, so fights never land. If it happens, the natural cost is morale (stepping out of a fight lowers it), not a free hit. **Resolved by simultaneous turns (same day):** stepping out is now a guess, because the enemy may follow in the same turn.
8. **Morale numbers (2026-09-25):** the size of `LineSupport`, whether all four neighbours count the same (a friend behind versus beside), and whether there is a cap. **Prototype finding:** depth only counts on the board. A second rank waiting on the neighbouring tile adds nothing, so a one-deep wall trades 1 for 1 against 8 attackers queued in two ranks. A two-deep block on the tile itself still fights at 150–175%.
9. **Predictable doctrine (2026-09-25).** A present player can out-guess a sleeping player's doctrine every turn. Is the defender's prepared line (terrain, morale) worth enough to cover that, or does doctrine need more variety?
10. **Prelude turns (2026-09-25).** At 4 tps a turn is 15 seconds: enough for small fights left to doctrine, tight for anything big. Does the prelude need a longer turn? **Playtest data:** in the stockpile raid (waves from three edges) the user found 15 s too short to think out a plan, while allowing it may be skill. A longer prelude turn (for example `RoundTicks` 120, 30 s at 4 tps) keeps the number of turns per fight and doubles the game time, but widens the gap in question 12.
11. **Archer aim (2026-09-25).** v1 is automatic (the weakest adjacent enemy after the moves). Later option: order an archer to fire into a subtile, hitting whoever is there after the moves, which makes archers a guessing game too.
12. **Round a battle vs through it (2026-09-25).** Off a battlefield a unit crosses a grass tile in 30 ticks; on one it needs 4 turns (240 ticks). Reinforcements and flankers can therefore run round a battle about 8 times faster than anyone can cross it (16 times with 120-tick turns). Keep it (a battle is a slog, the map is for manoeuvre), or slow world movement next to an open battlefield? **Answered 2026-09-25 (the user): keep it.** "Battlefields are muddy and unpredictable. It should be slow movement." A battle is a slog; manoeuvre happens on the map.
13. **Face-to-face attacks (2026-09-25).** Two free melee units facing each other, both ordered to attack, never meet, because enemies can't trade subtiles. For players that's a guess (attack, or hold and let them come). Watch whether human play turns it into a stand-off where nobody commits. If it does, one fix is that both meet in one of the two subtiles. **Answered 2026-09-25 (the user): they clash.** Neither moves, and each deals its duel damage to the other (§5). Attack against hold is a duel on the holder's subtile, attack against attack is a clash, and hold against hold is nothing.
14. **Reopening windows (2026-09-25).** A battlefield closes the moment one side is gone, and the next arrival reopens it with a fresh half-turn before its first turn. At a one-lane chokepoint every attacker in the queue hands the defender a new planning window. Intended, or should a tile stay open for a turn after it empties? **Answered for now (2026-09-25, the user):** fine as it is. Revisit if playtests show defenders farming the windows.

---

## 12. Tests to write

- Twin-run determinism on full battles (hold vs advance, doctrine vs doctrine, present vs absent).
- Order-independence: the same orders submitted in any order resolve identically; simultaneous damage.
- Collision rules, one test per row: enemies swapping both stay and clash (both take duel damage; a unit in a duel fights the duel instead; a clashing archer doesn't shoot); two enemies into one empty subtile duel; two friends into one subtile, the lower ID arrives; a failed move chains back through a column; a column or a wheel of friends all arrive.
- The beat: every battlefield resolves on ticks divisible by `RoundTicks`; one opened less than half a turn before a beat first resolves on the one after.
- Hidden orders: the enemy's orders never appear in a player's view.
- Wall: 4 defenders on an edge vs 8 attackers from one neighbor → at most 4 duels.
- Flank: same, attackers split across two neighbors → wall bypassed.
- Support fire decides an even duel.
- Leaving: any unit leaves by any edge on the beat, then travels a world hop before it can act; withdraw goes back toward `EnteredFrom` (the nearest edge for a unit that never entered), including from a duel; a move across the edge keeps its order.
- No passing through: a unit leaving a duel can't step onto its opponent's previous subtile or carry straight on.
- Routes: a traced route is followed exactly, one subtile a turn; a unit knocked off it rejoins it; a blocked waypoint is refused.
- Reach: archers and morale count only units on the same battlefield.
- Arrivals: units and waves come onto an open battlefield only on a beat.
- AI planning: an AI orders a battlefield's units at most once a turn.
- Stepping out: a unit moves out of a duel while its enemy holds, and neither takes duel damage that turn. If the enemy follows, they duel where they meet.
- Relief: a swap into a duel hands the fight to the fresh unit that same turn. If the enemy moves into the relieving unit's subtile at the same time, both moves fail and the wounded unit stays in the duel.
- Morale: a unit with two adjacent friends deals 150% damage; killing the middle of a line lowers both neighbours' damage from the next turn.
- Battlefield open/close fences group and movement anchors correctly.
- Idle suspend/resume: no empty turns; an order or an arrival wakes the battlefield on the next beat.
- Snapshot/restore mid-battle (duels, standing orders, held arrivals, suspended turns) reproduces identically.
- Terrain: bridge subtile forces single-lane crossing; forest blocks archer targeting.

---

## Update 2026-09-24 — notes added when the spec was saved

The spec above is the user's locked text, saved unchanged. These notes come from the design
conversation and from a check of the ⚠ VERIFY marks against the code. Nothing is built yet: the
stat-pool model in `docs/combat-model.md` still runs.

### Why, and what lost

- **The need.** Day X (`docs/two-act-pacing.md`, "the landing" in code) slows the clock so the
  battle is fought "at a speed where decisions matter". Under the stat-pool model nothing is
  decided inside a fight. `docs/persistent-rts-design.md` §9.3 already warned that with pure
  headcount "the bigger force always wins predictably". The board is the answer.
- **Options that lost in the design conversation:**
  - *Flanking with free hits* (a flanked unit cannot strike its flankers until the fight in front
    of it resolves). Claude proposed it; the user rejected it. Flanking is a manoeuvre to get
    round a line, and nobody gets free hits.
  - *A small stack per subtile* (2–3 a side, fighting as ranks). It only existed so two units
    could gang up on one. Without free hits, one unit per side per subtile fits every other rule,
    and archers' support fire is how a side concentrates force.
  - *One step per round* (simultaneous chess-like moves). The spec moves units step by step on
    the event queue instead, and keeps damage on round ticks.
  - *Archers on a neighbouring world tile reaching the edge row* (§9.4 of the design base, at board
    scale). Not in v1: archers reach the 4 orthogonal subtiles only.
- **It reverses one earlier decision:** §9.3's "no frontage cap needed". Each edge admits 4 lanes,
  and a side holds at most 16 subtiles. The reason is new (positioning, not stopping doomstacks).
  The reversal is recorded in the design base's own update.
- **It replaces the archer line rule** built the same day (`docs/combat-model.md`, "archers
  fight behind the line"). With one unit per side per subtile, "behind the line" is a place: the
  subtile behind.

### Checked against the code before building

**Holds:**
- `CombatTrigger.MaybeBeginCombatOnTile` runs from both `MoveArrivalEvent` and
  `GroupArrivalEvent`. `PinBelligerents` and its epoch fences exist, as does
  `Diplomacy.AreHostile`. `CombatState` keeps the round anchor that `RegenerateQueue` rebuilds.
- World pathfinding is a 4-neighbourhood A* (`Pathfinding.cs`), so a unit always enters a tile
  through exactly one edge. The edge-row rule has no diagonal case.
- Every death converges on `Population.OnUnitRemoved`, which handles food, beds, the crown,
  omens, camps and goals. `CombatRules.OnUnitDeath` drops cargo to a pile and disbands an
  emptied group.
- Zero-damage rounds are already guarded: the fight ends. On the board, suspending is right,
  because a quiet board is not a finished one.

**Does not match the game as built:**
1. **Sieges are live** (`docs/sieges-and-conquest.md`, M24).
   - Razing a castle defeats its player.
   - Rival conquest, bandit-camp razing (M39) and dead kingdoms depend on it.
   - Walls and gates are besieged from the next tile (`FortSiege`).

   Suggested v1 rule: the board decides who holds the tile. When it closes with only attackers
   left, today's siege rounds run unchanged.
2. **Rivers run along tile edges** (a per-tile edge mask), not through tiles.
   - There are no fords or bridges yet; a crossing costs 2 game-hours, and `River.cs` keeps the
     seam for them.
   - Suggested mapping: a river on an edge narrows that side's entry to one lane.
3. **Walls and gates are whole tiles** (`docs/walls-and-gates.md`, M26), and they already block
   movement and take sieges. "District" is a client-only idea; the sim has none. §6's palisade
   rule needs a different mapping.
4. **A unit is a person, not a battalion.** In the sim a unit ages, breeds and can be king. The
   prod client draws one figure per unit; the cohort of figures was dropped on 2026-09-17. Decide
   the fiction before building §10.
5. **Equipment already exists.**
   - A sword gives +3 power, a bow +4, a shield +10 health, and a fed House +1 (`EquipmentCatalog`,
     `Housing`). On the 100/20 scale these are negligible and need rescaling.
   - §5's table also needs Bandit, King and Heir rows. Today a bandit is 25 health / 3 power, and
     a king or heir 10 / 1.
6. **Breeding:** today either parent's death ends a gestation, so "only the gestating parent" is a
   change, not a check.
7. **The king** has no trait system: he gives a flat `RoyaltyConfig.AuraPowerBonus` within
   `AuraRadius` world tiles (`CombatRules.KingAuraBonus`).
8. **Nothing heals today.** Fights are to the death and HP persists, so §8's health question must
   be answered before a day-X lab.
9. **`PinBelligerents` clears the route.** "Continue their route on close" needs the route kept
   through the battle. That is snapshot state §9 does not list.

**Holes in the rules:**
1. **Overflow:** a castle tile may hold 25–30 units of one side when the board opens, and §2's
   placement runs out at 16. Suggested: the rest shelter in the castle, off the board, until a
   subtile frees.
2. **§2 vs §4.3:** a group arriving as the board opens puts its extras "next row inward"; one tick
   later they "wait outside". Suggested: every arrival uses §4.3.
3. **No `EnteredFrom`** for units born, trained or spawned on the tile, so they can never withdraw.
4. **The "doctrine formation" is undefined.** Nothing says where units already present stand: §7
   lists behaviours, not positions. And "soldiers hold the front row" cannot be resolved before
   the enemy's entry edge is known. A per-tile layout the player sets would fill both gaps.
5. **Even duels are double kills.** Two 100/20 soldiers both reach 0 on round 5, so 4 against 4
   head-on is 8 dead. Support fire breaks the tie; keep this only if it is intended.
6. **Integer steps:** a grass hop is 30 ticks, so a step is 7.5 ticks and needs a rounding rule.
   The spec should also say when a resumed round fires.
7. **Day X depends on the raid splitting.** With 4 lanes an edge, 12 raiders on one edge lose badly
   to 4 soldiers with archers behind them. "No special combat code for AI" holds. But choosing
   several approach tiles is world-scale behaviour, and neither the bandit driver nor the AI rungs
   have it yet.
8. **Water:** boats and water tiles are not mentioned. Suggested: "no battlefields on water in v1".

**Open question 1, answered by today's numbers:** `CombatConfig.RoundIntervalTicks` is 60 (one
game-hour). After day X (1 tps):
- a round is 1 real minute;
- a grass step is about 7.5 s;
- a soldier duel is 5 minutes;
- a 12-raider fight through 4 lanes is roughly 15–20 minutes.

That fits within a sitting, with no change. In the prelude (4 tps) a round is 15 s.

**Not in the repo:** `combat-prototype-spec.md` and the earlier tactical-battle-layer doc this
spec cites.

## Update 2026-09-25 — free movement in contact, and morale

**The decision (the user's).** Two amendments, written into the rules above:
1. **Units move freely even in contact.**
   - The lock is gone: a unit in a duel can step out, swap, or withdraw at any time.
   - A duel ends when one side dies or either steps out. Damage still lands only on whoever is in the duel at the round tick.
   - A swap into a duel's subtile is **relief**: the fresh unit takes over the fight.
   - This is not the rejected "real-time free-movement combat". Movement is still subtile steps on the event queue, deterministic; only the lock is removed.
2. **Every unit has morale,** a percentage where 100 is steady.
   - v1 has one source, the line: +25 for each friend in an orthogonally adjacent subtile.
   - v1 has one effect, damage dealt: × morale ÷ 100.
   - It is designed to grow: later sources and effects plug into the same value.

**Why.** The first standalone demo (a 16×16 open grid, 8 units against 8) "played well but was quite stale", and a small program could work out every battle. The diagnosis:
- **Contact ended the decisions.** Once two units locked, the rest was arithmetic.
- **Units were independent.** Damage just added up, so the shape of a formation didn't matter.

The user picked free movement as "the single best win for now": every round stays a decision (step out, relieve, reposition, re-aim). Morale makes units depend on each other. The worth of a position now depends on its neighbours, as it does for pieces in chess. It also softens the "even duels are double kills" hole above: two equal soldiers still trade evenly, but the shape around them now breaks the tie.

**What lost:**
- **Keeping the lock ("to the death").** Simple, but the outcome could be worked out at contact, which was the problem.
- **Relief only** (a locked unit may leave only when a friend swaps in). Narrower than free movement, which includes it.
- **A flat +25% damage per neighbour, with no stat.** The same v1 behaviour, but nothing to build on. A morale value gives later effects one home.
- **Other levers from the same review, not taken now:**
  - simultaneous orders per round;
  - fog on the board;
  - counters from gear.

  They are still open. Simultaneous orders would change the movement model, so that is the one to decide early.

**What to watch:**
- **Evasion.** Leaving contact is free, so a unit can step out before every round tick. If fights stop landing, cost the retreat in morale, not with a free hit (§11, question 7).
- **Numbers.** `LineSupport` 25 gives the middle of a line 150% damage. Tune it in the prototype with everything else (§11, question 8).

**Build note:** the lock and its snapshot flag are gone from §3 and §9. Nothing else in the build order changes.

**Future expansion:** morale's candidate sources and effects are listed in §5, "Morale".

## Update 2026-09-25 — simultaneous turns

**The decision (the user's).** Battles are fought in simultaneous turns. It is written into the rules above (§1–§5, §7–§12).
- During a turn, each side gives orders without seeing the other's. On the beat, every order resolves at once, in this order:
  1. withdrawals;
  2. moves, by the collision rules;
  3. duels, morale and damage.

  Both sides see the result and plan the next turn.
- One turn is one combat round (`RoundTicks`, 60 ticks), and every battlefield resolves on the same beat. After day X (1 tps) a turn is one real minute; in the prelude (4 tps) it is 15 seconds.
- A unit moves at most one subtile per turn. An order stands until it is done or replaced; a unit without one follows its doctrine. Enemy orders are never shown.

**Why.** The user: "you have to anticipate your opponents next moves and it gives players more time to plan." After the stale demo, the aim is combat that a small program can't simply work out.
- **No single right answer.** The best order depends on what the other side orders, and you can't see it.
- **It settles the evasion question** from the update above. Stepping out of a fight is now a guess, because the enemy may follow in the same turn.
- **Presence buys planning, not reaction speed.** Inside a fixed window, being there pays off as better planning. That suits an async game better than step-by-step movement.
- **It is simpler to keep deterministic.** Each turn resolves once, by fixed rules, instead of step events racing each other ("lower Seq enters first", "wait and retry").

**What lost:**
- **Step-by-step movement on the event queue** (the spec as saved). It matched world travel speed. But units reacted continuously, stepping out just before a round cost nothing, and ties went to event order.
- **Fight, then move.** Each turn's fights would be fully known while planning, so only the moves were a guess. A unit could also never be pulled out of the fight it was already in. Move-then-fight gives more to anticipate.
- **A clock per battlefield.** With one beat for every battle, a player has one countdown however many fights they are in, and the AI thinks on the beat.
- **Several steps per turn, or steps timed by terrain.** One step keeps the board readable, as in chess. Faster units or slow terrain can come later.
- **Ending a turn early when both sides are ready.** Impossible on a shared world clock, so the beat is fixed.

**What to watch:**
- **Doctrine is predictable.** A present player can out-guess a sleeping player's doctrine every turn (§11, question 9).
- **Prelude turns are 15 seconds long** (§11, question 10).
- **The collision rules carry the whole mechanic.** The prototype should exercise every row of the table.
- **The client needs a planning screen:** order arrows, ghost positions, one countdown, and a replay of each resolution.

**Build note:** this replaces step movement.
- There are no step anchors and no step events. Each battlefield has one turn event, on the beat.
- The snapshot gains, per unit, a standing order and a held arrival waiting to come on.
- Enemy orders stay off the wire.
- `SubtileStepTicks` and its ⚠ VERIFY are gone.

**Future expansion:**
- archers that aim at a subtile rather than a unit (§11, question 11);
- faster units that take two steps a turn;
- terrain that costs extra turns;
- a doctrine with more variety.

## Update 2026-09-25 — one subtile a turn confirmed, and three carry-overs

**The decision (the user's).**
- **One subtile a turn.** Two turn models were written up the same day in two sessions. The user chose the "simultaneous turns" update above: one subtile a turn, whatever the terrain, resolved on the beat by the collision table.
- **The other model lost:** units walking at world pace through the turn with only the orders committed on the beat. That text has been removed from this doc; its reasoning is kept below.
- **Three carry-overs** from the losing model fill gaps in the chosen one. They are the user's call, on a recommendation.
- **Built:** all of it, in the standalone prototype (`C:\Users\ghgma\combat-prototype`).

**The carry-overs:**
1. **Withdrawing is not a free teleport.** §4.2 has a withdrawing unit "gone before anyone moves". It leaves the board on the beat, then makes a normal world hop (full `ExecutionCost`) to the tile it withdrew into, and can do nothing else until it lands. Without this, the withdrawal step would be an instant escape to a tile a whole hop away.
2. **No passing through the opponent.** The collision table stops two enemies trading subtiles, but not a unit in a duel stepping on past its opponent into the empty subtile behind it. The rule: a unit leaving a duel may not move onto the subtile its opponent came from, nor carry straight on in the direction it entered. It can back off or slide sideways. This is §3's "no slipping past", made concrete for units that are free to move in contact. It needs one more stored field per unit, the subtile it last moved from.
3. **Hops into an open battlefield don't pay crowding bands.** Today a hop into a tile holding 16 or more units pays +150, which would take about three turns to reinforce a big battle. The four lanes per edge already ration entry, and it is not a travel shortcut, because nothing passes through a contested tile.

**Why one subtile a turn won:**
- **Readable, like chess.** Every order is one step, and both players can see the whole of the next turn's possibilities. At world pace a grass unit takes about 7 steps a turn, so one order plays out 7 steps before anyone can react.
- **Fair.** At world pace, clashes in the middle of a turn go to event order, in practice the lower id. That's invisible and uncontrollable, and it becomes a hidden bias once orders are secret. The collision table settles every clash by a published rule instead.
- **It still meets the speed requirement.** One subtile per 60 ticks is far below world pace (8 ticks a subtile on grass). With the withdrawal hop (carry-over 1), a battlefield can only ever slow a unit down. Matching world speed exactly was never needed, only never exceeding it.

**What lost, with the losing model:**
- **Per-terrain step timing** inside battles. Terrain returns later as "terrain that costs extra turns" (future expansion, above).
- **A slower step out of a fight.** That was a 200% step in the losing model. It isn't needed: stepping out is a guess now, because the enemy can follow in the same turn (§11 question 7).

**What the prototype measured** (`predict.py`, 150 random AI-vs-AI battles, against "total HP × total damage"):
- **Decided battles:** the calculator names the winner 89% of the time with morale on, 91% off.
- **Close battles** (strengths within 20%): 64% either way, down from 75% under the world-pace model. Positioning decides more of the close fights.
- **The AI caps what this shows.** It doesn't build lines, so the real measure needs human replays.

**Found while building:**
- **Face-to-face attacks never meet.** Two free melee units facing each other, both ordered to attack, fail every turn under "enemies can't trade subtiles". For players that's a real guess: attack, or hold and let them come. An AI needs a tie-break, though, or two AIs stall for ever. The prototype's: after such a failed turn, the lower id attacks and the higher holds.
- **Depth only counts on the board.** Morale counts only friends on the same battlefield. A second rank waiting on the neighbouring tile adds nothing, so a 4-wide wall trades 1 for 1 against 8 attackers queued in two ranks. A two-deep block on the tile itself still fights at 150–175%. This bears on §11 question 8.
- **A chokepoint gives the defender time.** Each time a battlefield empties of one side it closes, and the next arrival reopens it half a turn before its first turn. At a one-lane bridge, every attacker in the queue gives the defender a fresh planning window.

**Build notes:**
- A withdrawn unit carries a "busy until" tick (the world hop), and the subtile it last moved from. Both are snapshot fields.
- `PinBelligerents` must not cancel a withdrawal hop.
- The AI plans each battlefield once a turn, just after the beat or when the battlefield opens (`claim_plan` in the prototype). It never plans mid-turn.

## Update 2026-09-25 — synced with the standalone demo

The demo (`C:\Users\ghgma\combat-prototype`, pygame, 108 tests) plays the beat model above at the game's own numbers. This update brings the spec in line with what the demo showed and with two calls the user made while playing it. The rules sections above now carry each change.

**Decisions (the user's):**
1. **Any unit may leave a battlefield by any edge.** The user: "the units should be able to move out of a battle field from any edge."
   - The trigger: an archer that started the battle on its tile had no `EnteredFrom`, so under withdraw-only-toward-`EnteredFrom` it could never leave (hole 3 of the 2026-09-24 notes).
   - Leaving still costs a beat and a world hop of travel (carry-over 1 above), so it is never a shortcut.
   - Replaced in §4: hold-until-clear and withdraw-only-toward-`EnteredFrom`. **What lost:** pinning attackers inside a battlefield until it clears. It was simple, but it trapped units with no `EnteredFrom`, and a plain move toward safety looked like a bug ("holding: tile contested").
   - Withdraw stays as an order, meaning "back the way I came", or the nearest edge for a unit that never entered.
2. **Traced routes.** The user found shortest-path moves unworkable for navigating a tile ("I want to be able to trace out the unit's route"). Ties in the shortest path (N, E, S, W) often pick a lane nobody meant. A route order is a list of subtiles, followed exactly. Plain moves stay for "just get there".

**Corrections from the demo** (the spec said otherwise or said nothing):
- **Starting values** (§5): the game's catalog numbers replace the 100/20 placeholders. That answers note 5 of 2026-09-24.
- **Reach** (§5): archers target, and morale counts, only units on the same battlefield. A unit on the neighbouring tile has no subtile. The user checked this in play ("does that blue A do any damage from the non-battlefield tile?"): it doesn't.
- **No passing through** is now a row of §3's grid rules, not just a carry-over.
- **Orders** (§5, §7): Stop, and the AI planning once a turn.
- **Snapshot** (§9): the committed path and route flag, the subtile last moved from, and the busy-until tick.
- **Presentation** (§10): route tracing; showing each unit's next move and why its last one failed; enemy paths hidden.
- **Open questions** (§11):
  - Q8 gains the depth finding, Q10 the playtest data, and Q5 is partly answered by routes.
  - Q12 (round a battle vs through it), Q13 (face-to-face attacks) and Q14 (reopening windows) are new.

**What the demo does not exercise** (so none of it is validated yet): deployment placement on opening (§2; the demo keeps positions), overflow past 16 a side, idle-battlefield suspend (§5; the demo resolves empty turns, which is harmless), terrain derived from world tiles, sieges, groups, fog, and the king.

**Playtest verdict:** "it was actually interesting and made me think". This was the stockpile raid with waves from several edges. The open items are pacing (Q10, Q12) and the client's planning UX, not the rules.

## Update 2026-09-25 — clashes

**The decision (the user's).** Two enemies that charge each other, each ordered into the other's subtile, **clash**. Neither moves, as before (enemies can't pass through each other). But each now deals its duel damage (morale-scaled) to the other that turn, like two charges meeting in the middle. It is written into §5 and closes §11 question 13.

| Blue | Red | Result |
|---|---|---|
| charges | holds | a duel on Red's subtile |
| holds | charges | a duel on Blue's subtile |
| charges | charges | a clash: both hurt, neither gains ground |
| holds | holds | nothing |

A unit that is in a duel after the moves fights that duel instead of the clash. A clashing archer fights the clash and doesn't shoot.

**Why.** Before this, charge against charge did nothing at all.
- **Two players who both wanted the fight bounced** off each other every turn.
- **The demo's AI stalled for ever** in random battles for the same reason, and needed an AI-only tie-break (the lower id attacks, the higher holds). That tie-break is gone now: clashes resolve on their own.
- **A clash keeps the guess and removes the free stand-off.** Holding still wins you your own ground (your archers, your morale neighbours, your forest). Charging into a charge costs both sides.

**What lost:**
- **Doing nothing** (the rule before). It gave a free stand-off.
- **Meeting in one of the two subtiles.** Any fixed choice of which subtile favours one side, and a tie-break by id is hidden and arbitrary.
- **An AI-only tie-break.** It fixed the AI but not people.

**Measured** (`predict.py`, 150 random AI-vs-AI battles, no tie-break): the calculator names the winner of 92% of decided battles with morale on (95% off), and of 67% of close ones (72% off). 4–5 of 150 are left unfinished, the same as with the tie-break.

**Future expansion:** a clash is a place for charge-specific rules later. For example, a unit that charged from further back could hit harder, or cavalry could win clashes. Neither is in v1.

## Update 2026-09-25 — build decisions (M41 Phase 0)

**The decisions (the user's).** The ten open build questions were settled before the build (`docs/m41-status.md`, Phase 0). Every default was taken, with two changes.
1. **Overflow:** past 16 of one side, the rest shelter off the board and come on like arrivals when a lane frees (§2).
2. **Units already on the tile** stand in the centre rows for now. The user: "the player needs to be able to form formations before the tile becomes a battle field."
   - That is a confirmed requirement for a later milestone.
   - Opening placement reads from a formation source, so a player-set formation plugs in without touching the rules (§2).
3. **The board stays open in v1.** The user: "keep the battle field open for now but build it so tiles can be occupied by different things, but for now it's all just a standard tile of the biome."
   - Every subtile is plain; §6's terrain, and the Forest exception in archer targeting (§5), wait for later.
   - The per-subtile layer is built now.
4. **Sieges:** the board decides who holds the tile, then today's siege rounds run (M24). Adjacency sieges are untouched.
5. **Visibility:** on a board you can see, enemy positions, HP and morale are visible; their orders and paths never are.
6. **Default doctrines:**
   - soldiers Hold, archers Support;
   - bandits Advance, and withdraw at the party's flee threshold;
   - non-combatants Withdraw.
7. **Looting:** none on a tile while a board is open there, as today. Structures on the board are a later question.
8. **The king:** today's world-tile aura, through the modifier seam. The "R subtiles" aura is later.
9. **Pursuit:** it ends when the quarry is on an open board, and the pursuer enters with doctrine Advance.
10. **On screen:** one figure per unit. Battalion formations are later.

**Why.** Each is decided against the world as built, not against the standalone demo. The demo found the rules; its scenarios and conveniences are not the world's.

## Update 2026-09-25 — built (M41)

**The rules above are built:**
- the sim (`src/Sim.Core/Battlefields/`), behind `--combat grid`, with Pooled still the
  default;
- the host's projection;
- the prod client's battle presentation;
- a battle test-bed scene for playing it.

Status, and every call made while building: `docs/m41-status.md`, "What was built". Three of
those calls touch this spec:

- **Arrivals wait off the board, on the tile.** §4.3 says a unit is "held on the tile it came
  from" until the beat. As built, its world hop completes (it is on the battle's tile) and it
  waits just outside its lane, off the board, unable to fight or be hit, until the beat. The
  outcome is the same; it keeps every arrival's bookkeeping on the one arrival path.
- **Doctrine paths round friends.** §3 says pathing ignores units. It still does for orders.
  Doctrine now goes round the unit's own friends when a way round exists, because in the first
  live run a supporting archer kept stepping into the back of its own line.
- **Groups halt as a body and do not resume on close** (§8 asked for resuming). Their members
  fight as individuals, and the player re-orders the group after.

## Update 2026-09-28 — structures on the board: walls first

Direction agreed with the user: **structures will occupy subtiles on the board**, and
units cannot stand on a building's subtiles except in special cases. It fills the slot
D3 left, `SubtileLayer` (`Battlefields.LayerFor` returns an all-open layer today). The
footprints, placement, scale and special cases are still being designed.

Decided so far, for walls (full text in `docs/walls-and-gates.md`, Update 2026-09-28):
- a wall is a line of obstacle subtiles along one of the middle rows, turned to follow
  the wall;
- gates are passable for the owner and allies, and blocked for everyone else;
- a tower subtile in the wall carries archers who shoot over it;
- units next to a wall damage it even while defenders are on the board;
- one HP pool for the whole wall, at today's values;
- other buildings keep the whole-tile siege rule, shielded by defenders.

This supersedes D4 for fortifications only: a defended wall is fought on the board, not
from the four tiles beside it.

Continued in `docs/structure-footprints.md`, which covers:
- the subtile types: Open, Blocked, Wall, Tower, Gate, Cover;
- rotation;
- the castle ring;
- the edge rule for world movement;
- the per-side unit cap.

## Update 2026-09-29 — units always stand on a subtile (M42)

Decided with the user: every unit always stands on a subtile, not only while a battle is
open. Movement comes at two resolutions:
- **world moves** as today (tile pathfinding, the road bonus between tiles);
- **subtile routes** the player draws exactly, walked at a quarter of the world hop per
  step, within the 3×3 block of world tiles around the unit.

On a battlefield the same routes are walked one subtile per turn. A world move loads each
tile back to front from the edge it entered by.

This replaces seating at battle open and the overflow shelter (build decision D1), and
settles pre-battle formations: your formation is where your units stand. See
`docs/subtile-movement.md` and `docs/m42-status.md`.
