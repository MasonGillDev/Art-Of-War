# Secrets and progression: what hides in the shadows, and the shape of a game

**Status:** proposal, 2026-09-24. Nothing here is decided. It is a design pass over the
systems that exist, measured against their real numbers, with recommendations. When the
user picks, each pick becomes its own decision doc and milestone, per CLAUDE.md.

**Method.** Every number below is read from the code (catalogs, configs, constants) or
measured: a probe built the default host world (252x252, 15 AI, 30 caches, 20 idols) on
three map seeds, and a small script replayed the combat rule (`CombatRoundEvent`: each
hour, each side deals its enemies' summed power, lowest health first) on the real stats.

## 0. The recommendation in one page

1. **Keep the rule the user set: only a returning scout marks a secret.** Deepen it
   instead of widening it. A scout close enough (inside a third of its sight, 2 tiles)
   brings back a sharper hint, and a standing "survey" order keeps charts fresh (routine
   scouting is maintenance, which the vision doc says automation may do).
2. **Build bandit camps next.** They are the designed follow-up to M16 and deepen five
   systems at once: the bandit driver, sieges, piles, patrols and scouting. They also give
   progression a threat with an address. Details in section 4.3; sizing in section 6.
3. **Let the haul queue carry loot home.** A once-only haul job may name a charted cache
   or pile as its source. Choosing to go and get it stays the player's call; the
   carrying becomes automation, as the vision doc's first law says.
4. **Put the value where the trip is.** A hauler 80 tiles out eats about 1.07 food per
   unit it brings home, so distant food and wood are worthless. Tier caches by distance
   from the nearest castle: bulk near home, ore, iron, gear and people far away.
5. **Give progression a long tail.** All four M37 milestones fire in the first few real
   hours. At 4 ticks per second a night's sleep is 80 game-days, so later milestones
   should be spaced tens to hundreds of game-days apart and keyed to things that take
   real days: population, generations, the explored share of the world, iron smelted.
6. **Fix what the dig turned up** (section 8). Bandit pressure is pinned at its maximum
   on every map because caches and idols count as "player structures". Archers are a
   trap inside a mixed stack. Plus a few smaller things.

## 1. The real numbers

### 1.1 The clock at 4 ticks per second

| Game time | Real time | What it means |
|---|---|---|
| 1 hour (60 ticks) | 15 s | a combat round; 2 tiles of grassland walking |
| 1 day (1,440) | 6 min | 4 food per person |
| 1 week | 42 min | the reprisal's warning; the bandit grace period |
| 1 demographic year (3 days) | 18 min | a child is trainable at 6 years = 18 days (1.8 h) |
| lifespan 65–95 years | 19.5–28.5 h | the founding king (age 18) dies on day 141–231 |
| a night's sleep (8 h) | = 80 game-days | the async window everything must survive |

Walking cost per tile, in ticks: grassland 30, hills 75, forest 90, desert 120, mountain
135, water (wading) 750; roads cut up to 66%. On mixed ground (about 60 ticks a tile) a
trip of 40 tiles out and back takes 3.3 days (20 real minutes).

Build times are small next to walking (Lodge 50 minutes of game time = 12.5 s real; Farm
10 h = 2.5 min; House 30 h = 7.5 min; the Dock, at 10 days, is the only long one).
**Distance, not construction, is the game's clock.** That is why secrets far away matter.

### 1.2 The economy (a full crew, full fertility)

| Extractor | Per specialist per day | A full crew (3) | Notes |
|---|---|---|---|
| Farm | 24 food | 72 food/day = 18 mouths | settled workers +1 each: 108/day |
| Lumber camp | 96 wood | 288 wood/day | the buffer (30) and hauling are the real cap |
| Quarry | 4 stone | 12 stone/day | **stone paces every build ladder** |
| Mine | 2 ore | 6 ore/day | |
| Smelter (2 workers) | 2 iron | 4 iron/day | eats 8 ore + 40 wood per day |

- **People:** 4 food a day each; a birth costs 20 food and 2.25 days of pregnancy.
- **Starting stock:** 14 people, 200 food (3.6 days), 70 wood, 50 stone.
- **Gear:**
  - Sword: 3 iron + 2 wood (6 ore + 32 wood), +3 power.
  - Bow: 10 wood, +4 power.
  - Shield: 5 wood + 5 stone, +10 health.
  - Cart: 20 wood + 10 stone, +25 carrying, +50% walking time.
- **Carrying capacity:** anyone 5, hauler 25, hauler with a cart 50, bandit 15, boat 100.

What a unit of each good is worth, in worker-days (one person's day of labour, which
costs 4 food):

| Good | Worker-days per unit | 40 units are worth |
|---|---|---|
| wood | 0.01 | 0.4 |
| food | 0.04 | 1.7 |
| stone | 0.25 | 10 |
| ore | 0.5 | 20 |
| iron | about 1.6 | 64 |
| sword | about 4.8 each | — |
| an adult person | 6 years of upbringing (18 days, 72 food, a birth) before they can train | — |

**Ranking of rewards: people, then gear, then iron and ore, then stone, then food, then
wood.** This is why the user's favourite rewards, arrivals, are also the strongest ones.

### 1.3 Combat (real stats, real rule)

| Unit | Health | Power |
|---|---|---|
| citizen / scout / king | 10 | 1 |
| soldier | 30 | 3 |
| archer | 15 | 5 |
| bandit | 25 | 3 |

Buffs: settled +1 power, king's aura +1 within 3 tiles.

| Fight | Result |
|---|---|
| 6 bare soldiers vs 6 bandits (the reprisal) | win in 13 h, **4 soldiers lost** |
| 6 housed soldiers (+1) vs 6 | win in 8 h, 2 lost |
| 6 soldiers with shields vs 6 | win in 11 h, 2 lost |
| 6 soldiers with sword + shield vs 6 | win in 5 h, **1 lost** |
| 6 bare soldiers + 8 levied citizens vs 6 | win in 7 h; the citizens soak the blows |
| 4 soldiers + 2 archers vs 6 | **lose** (the archers die first) |
| 14 citizens vs 6 bandits | lose |
| 3 farmers vs 2 bandits | lose: **any bandit party wipes a work crew** |
| 4 soldiers vs 3 bandits | win, 3 left |
| 2 soldiers vs 2 bandits | win, 1 left |
| 6 swordsmen vs 8 bandits | win, 3 left |
| 6 bare soldiers vs 10 bandits | lose |
| 12 sword + shield vs 16 bandits | win, 8 left (10 of them: 4 left) |
| 12 sword + shield vs 20 bandits | win, 5 left (10 of them lose) |

### 1.4 The world (default host, three seeds)

| Measure | Value |
|---|---|
| Land | 35–39k tiles of 63.5k (the rest is sea) |
| Factions | 16 (you + 15 AI), castles **24 tiles apart** (median nearest neighbour) |
| Your nearest AI neighbours | 24–40 tiles |
| Explored at the start | 88–91 tiles |
| Land within 20 / 40 tiles of you | about 1.1–1.7k / 5.3–6.3k tiles |
| Caches | 30; nearest 5–27 tiles, median 70–88; **3–7 within 40 tiles** |
| Idols | 20 (5 greater); nearest 14–45 tiles, median 81–87; **0–4 within 40 tiles** |
| All cache loot on a map | about 310 wood, 250 food, 230 stone, 230–320 ore, 5–8 gear |
| Islands | 4–7 other land masses of 2–445 tiles; 0–1 caches on them today |
| Bandits | at most 4 parties of 2–4 *in the whole world* |

**The "wild" is other people's back yard.** With castles 24 tiles apart, anything past
about 12 tiles from home is a neighbour's land. Secrets sit between and behind the AI
kingdoms (they ignore caches and idols; bandits do steal from caches).

### 1.5 Fetching loot home (60 ticks a tile on average)

| Distance | Carrier | Round trip | Food it eats | Food eaten per unit carried |
|---|---|---|---|---|
| 20 tiles | scout (5) | 1.7 days / 10 min | 6.7 | 1.33 |
| 20 tiles | hauler (25) | 1.7 days | 6.7 | 0.27 |
| 40 tiles | hauler (25) | 3.3 days / 20 min | 13.3 | 0.53 |
| 80 tiles | scout (5) | 6.7 days / 40 min | 26.7 | **5.33** |
| 80 tiles | hauler (25) | 6.7 days | 26.7 | **1.07** |
| 80 tiles | hauler + cart (50) | 10 days / 60 min | 40 | 0.80 |

Hauling food home from 80 tiles costs more food than it brings. Ore is worth the trip at
any distance. A sword is worth a scout's long walk. **Distance decides what a secret
should hold.**

## 2. What the numbers say

1. **Stone paces everything.** Every military and civic building past the first farm
   needs stone. The first three military buildings (Barracks, Smithy, Smelter) need 70
   stone, about 6 days of one full quarry. A near cache of stone is a real accelerant; a
   near cache of wood is a joke.
2. **Every bandit party kills a work crew.** Two bandits beat three farmers. Without a
   garrison or patrol, any raid near a farm kills its crew. This is already true today.
3. **The reprisal is a real test with a readable gradient.** Bare soldiers win but lose
   four of six; housed or shielded soldiers lose two; sword and shield lose one. The
   reprisal tells the player to arm and house their soldiers, and the war chest (10
   iron, about 3 swords) re-arms whoever survives.
4. **Archers are a trap in a stack.** Lowest-health-first targeting kills them first,
   so 4 soldiers + 2 archers lose a fight that 6 soldiers win. Health 20 (not 15), a
   shield, or a manned tower (M32, decided and unbuilt) fixes it.
5. **Progression is front-loaded.** "A good home" fires around day 5, the first
   charted find and "Far horizons" around days 8–20, the reprisal around days 20–40.
   Everything M37 has fires in the first 2–4 real hours. The async game (80 game-days a
   night) has no beats.
6. **Warnings last a session, not a night.** Seven days of warning is 42 real minutes.
   The vision's fairness rule ("presence buys finesse, never survival") therefore has to
   hold another way: any threat that can fire while the player is away must lose to a
   standing home garrison. Only fights the player starts (assaulting a camp) can be big.
7. **Idols pay little today.** A lesser idol shows about 113 random unexplored tiles
   for 12 real minutes; a greater one about 314 for 30 minutes. That is exploration
   (useful toward "Far horizons") but not treasure.
8. **Looting is a chore.** One resource per order, carried 5 at a time by a scout: a
   40-ore cache is 8 round trips.

## 3. Principles for secrets

These come from the vision doc ("knowledge comes from presence"; automation covers
maintenance, never allocation; bread is electricity) and the numbers above.

1. **Only a returning scout marks a secret** (the user's rule, M38). Seeing is not
   knowing.
2. **Value per unit carried beats the trip** (section 1.5). Far secrets hold dense
   value (people, gear, iron, ore); near ones may hold bulk.
3. **A secret deepens a system that exists.** Every entry below names the system and
   the code that already does most of the work.
4. **A secret changes while you are not looking.** Bandits strip caches, AIs scavenge
   ruins, camps grow their hoards, idols crumble for whoever wakes them first. A chart
   is a memory, and memories go stale.
5. **One-shot and escalating by variety, not by repetition** (the M37 rule): bigger
   moments are new rows with their own flavour.
6. **Anything that can hurt you while you are away loses to a home garrison.**

## 4. The catalogue: what hides in the shadows

Each entry: what it is, the system it deepens, its numbers, and roughly how much is new.

### 4.1 Caches, deepened (exists: M23 + M38)

- **Tiered by distance from the nearest castle.**
  - **Stash**, in the nearest third of the map: 20–40 food, wood or stone.
  - **Cache**, in the middle third: 30–60 stone or ore, with a 30% chance of gear.
  - **Hoard**, in the farthest third: 60–100 ore or 20–40 iron, with 60% gear (1–2
    items).
  - Keep the count at 30; move the value outward. Reuses `CacheScatter`, which already
    walks candidate tiles in a fixed order; it only needs distances to castles.
- **A sharper hint up close.** A scout that saw the cache from 2 tiles or less (inside
  the M20 report's "exact" band, `ExactCountQualityPct` 67%) charts what kind of thing it
  holds: "a glint of iron", "stacked stone", "grain sacks", "old steel". From farther
  away the hint stays faint. This is a new hint row per category and a distance check
  in `Charts.Deliver`, which already walks the log leg by leg.
- **Salvage jobs.** A once-only haul job may name a charted cache, or a ground pile, as
  its source. The M36 queue then sends the nearest free hauler, repeatedly, until the
  job's amount is met or the cache is gone. This relaxes one ownership check in
  `SetHaulJobIntent` (plus the job ending when the cache is emptied) and makes carts and
  roads matter for expeditions.
- **The wheel trap** (the same one idols had): loot is only on the cache's own wheel.
  Right-click and the unit wheel should offer it too.

### 4.2 Idols, deepened (exists: M38)

- **Lesser idols (15):** as today, a random circle of unexplored land. They are an
  exploration shortcut: about 113 tiles, about 9 scout-steps saved toward "Far
  horizons".
- **Greater idols (5):** the circle lands over an **uncharted secret** of the
  activator's (camp, hoard, ruin or wreck), offset from it with the sim's random
  generator the way the rumour circle is. You *see* it for 5 days, which is enough to
  send a scout about 120 tiles, but only a scout can chart it. This makes the idol a
  pointer to treasure rather than a lottery ticket. It is a change to
  `Idols.PickCenter` only.

### 4.3 Bandit camps (new; M16's designed follow-up)

A bandit-owned structure in the fog that sends raiders out and **keeps what they steal**.

- **Deepens:**
  - **Bandit driver:** raiders flee to the camp instead of despawning, and their loot
    joins the camp's hoard. The M16 doc anticipated this exact swap.
  - **Sieges:** raze the camp.
  - **Piles:** the hoard spills when it falls, through `SiegeDamage.RazeStructure`.
  - **Patrols:** a patrol is the answer while you are away.
  - **Scouting:** find it via its rumour circle, or by following raiders home.
  - **Progression:** a threat with an address.
- **Numbers (sized in section 6):**
  - Garrison: 6 bandits.
  - Structure health: 300. That is 17 h of uncontested siege by 6 bare soldiers, 8 h by
    6 swordsmen.
  - Raids: one party of 2–3 every 7 days (42 real minutes), only while the camp stands.
  - Hoard: starts at 30 iron + 20 ore, plus everything its raiders steal. Each raid can
    carry off up to 45 units.
- **Placement:** one per human seat, raised by a progression row (4.3a below) as a
  rumour with a search circle, in the wildest bearing 20–40 tiles out. World-wide camps
  that replace today's global "prosperity" spawning can come later.
- **Chart hint:** "smoke".

### 4.4 Ruins of fallen kingdoms (emergent; deepens M24 and dead kingdoms)

When a kingdom dies today, its castle becomes rubble and its vault spills as a ground
pile. A castle holds up to 5,000, so that pile can be large. Make that rubble a secret
("burned walls") so scouts chart it and salvage jobs can empty it. The AI's
`ScavengeRung` already strips ruins, so this is a race with them. With 15 AI kingdoms,
some of which die, this is the late game's treasure that nobody placed. It needs only a
hint row and a pile source for salvage jobs.

### 4.5 Island wrecks (new placement; deepens boats and docks)

Every map has 4–7 other land masses (2–445 tiles). Today nothing is on them, so a Dock
(200 wood, 50 stone, 10 days to build, one boat every 10 days) has no reason to exist
on a one-continent map. Put a **wreck hoard** on each island of 20 tiles or more (0–3
per map) holding about 80 ore, 3 swords and 2 shields (about 57 worker-days). Its
rumour comes from a late progression row ("The wide world"). This is placement only;
boats already carry cargo.

### 4.6 Old battlefields (new placement; deepens graves, salvage and equipment)

A few genesis tiles with 2–4 swords or shields lying on the ground and graves beside them.

- **Hint:** "bones".
- **Value:** 3 swords is about 14 worker-days, and a real shortcut to arming for the
  reprisal. It is an alternative path to the iron economy, not a replacement.
- **New work:** piles already exist and can be salvaged and hauled. What is new is that
  charts must record ground piles, not only structures.

### 4.7 A lost hamlet (new; deepens population and arrivals)

A hut in the far fog (3–5 per map) where 2–3 people live.

- **How they join:** when one of your units reaches it, they follow it home, reusing
  `Omens.ArriveRefugees` for the walk.
- **Hint:** "a thin line of smoke".
- **Value:** people are the top reward. An adult saves 18 days of upbringing and can
  farm 24 food a day for the 4 they eat. Keep the count small.

### 4.8 Later, noted so nothing is lost

- **A claimant.** An exiled royal who can take an empty throne (deepens M31 succession).
- **A ruined watchtower.** Claim it for a forward eye (deepens towers and manned towers,
  M32).
- **An old road.** Paved arcs at genesis leading somewhere (deepens roads, M34).
- **A natural ford.** Depends on bridges and fords (M33, decided, unbuilt).

## 5. The shape of a game

Real time at 4 ticks per second. Days are estimates for an engaged player (the AI lab
reached 79 people on day 160).

| Act | Game days | Real time | What happens | Milestones |
|---|---|---|---|---|
| **I. The first night** | 0–7 | 0–40 min | food wall, first farm and houses, the Lodge, first scouts; bandit grace ends on day 7 | ✓ A good home (3 houses → 2 settlers, about day 5) |
| **II. The reach** | 7–30 | 40 min–3 h | missions 20–40 tiles out chart 3–7 caches and 0–4 idols; bandit parties test crews | NEW First find (first charted secret → a wanderer joins); ✓ Far horizons (1,500 explored → ruin rumour, about days 10–20); NEW Smoke on the horizon (population 25 → a camp rumour; raids start 10 days later) |
| **III. The sword** | 20–80 | 2–8 h | stone, then smelter and smithy; arm, raze the camp | ✓ Reprisal (6 trained → raid of 6 in 7 days); ✓ Word spreads (4 refugees); NEW The camp burns (camp razed → hoard + counter); NEW Ironworks (100 iron smelted → a smith arrives with 2 swords) |
| **IV. The realm** | 80–300+ | overnight into the next days | neighbours 24–40 tiles away, generations, the sea | NEW The warlord (camp razed and 12 trained → a host of 16 in 10 days → hoard + 6 refugees); NEW The wide world (6,000 explored, about 17% of land → a wreck rumour); NEW A market town (population 60 → 6 settlers); NEW Long live the king (first succession, day 141–231 → 3 loyal retainers, soldiers) |

Why these triggers:

- **They are things you do, and none punishes growth.** The camp is keyed to population
  (a town worth robbing), not to building a Barracks, which would teach players not to
  arm.
- **The later rows sit where the async game lives.** Population 60, 6,000 explored,
  12 trained and the first succession all take real days.
- **Every threat row has a matching reward row** (Reprisal → Word spreads; Smoke → The
  camp burns → The warlord → its hoard), and arrivals, the strongest reward, are spread
  across all four acts.

## 6. Threat sizing (combat math, section 1.3)

| Threat | Size | Beaten by | What a win costs | Reward |
|---|---|---|---|---|
| Wandering party (today) | 2–4 | 4 soldiers on patrol (3 left vs 3, 2 left vs 4) | 1–2 | — |
| Reprisal (M37) | 6 | 6 soldiers | bare 4, housed 2, sword + shield 1 | 10 iron + 4 people |
| Camp raid (new) | 2–3 every 7 days | a 4-soldier patrol, even unattended | 0–1 | the hoard stops growing |
| Camp assault (new, you start it) | 6 garrison (+3 if a raid is home) | 6 swordsmen (5 left) or 8 bare (6 left) | 1–2 | hoard (30 iron + 20 ore + stolen goods) |
| The warlord (new) | 16 in 10 days | 12 sword + shield (8 left); 10 scrape through (4 left) | 4–6 | hoard (40 iron + 4 swords) + 6 people |

**The async rule, stated as a number:** anything that can reach you while you sleep (a
camp raid, a wandering party) is at most 3 bandits, which lose to a 4-soldier patrol.
The big fights (the reprisal, which you trigger by arming; the camp assault; the warlord)
either wait for your move or are raised by your own actions while you are playing.

## 7. Build order

| Slice | Contents | Deepens | Size |
|---|---|---|---|
| 1. The camp | bandit camp structure, flee-to-camp and hoard, raid cadence, raze and spill, Smoke / The camp burns rows, rumour circle; plus the bandit-pressure fix | bandits, sieges, piles, patrols, scouting, progression | medium |
| 2. Salvage | once-only haul jobs from charted caches and piles, caches tiered by distance, sharper hints up close, the cache wheel fix | hauling, caches, scouting | small–medium |
| 3. The long tail | First find, Ironworks, The warlord, The wide world, A market town, Long live the king; greater idols aim at secrets | progression, idols, boats, dynasty | medium |
| 4. Emergent secrets | ruins charted, island wrecks, old battlefields, lost hamlets | dead kingdoms, boats, graves, population | medium |
| Later | claimant, watchtower ruins, old roads, fords | dynasty, towers, roads, rivers | — |

## 8. Findings to fix whatever is chosen

1. **Bandit pressure is pinned at maximum.** `BanditDriver.MaybeSpawn` counts every
   structure not owned by bandits: the 30 caches, the 20 idols, rubble and all 15 AI
   kingdoms. So the live-party target sits at its cap of 4 from day 7 on every map, and
   M38's idols added 20 more to the count. Excluding owners below zero is the minimum
   fix; measuring pressure per faction is better.
2. **Archers die first in a mixed stack** (section 2.4). Options: archer base health
   15 → 20 (the 4 + 2 stack then matches 6 soldiers); "shield your archers" as a lesson;
   or build manned towers (M32) so archers get their niche.
3. **The cache wheel trap.** Loot is only on the cache's own wheel, which needs a
   selection that clicking the cache replaces. Same fix as idols: right-click and the
   unit wheel.
4. **Scout waypoints disagree.** The client's `ScoutPlanner.MaxWaypoints` is 8; the
   server's `ScoutConstants.MaxWaypoints` is 16.
5. **Stale comments in `StructureCatalog`:**
   - The Dock says "one boat per 5 game-hours"; the period is 10 days.
   - The Lumber camp says "6 claimed forest tiles"; `ClaimCount` is 8.
   - The Farm says "4 claimed grassland tiles"; `ClaimCount` is 15.
6. **The record-struct defaults trap.** `new CacheConfig()` is all zeros, not its
   defaults (the trap `IdolConfig` hit in M38). It is harmless today only because the
   Count-0 case is the only use. Give it an explicit parameterless constructor like
   `RoyaltyConfig`.

## 9. Questions for the user

1. **Camp first?** It is the richest single addition and the M16 follow-up.
2. **Salvage by haul job:** may the queue carry from things you do not own (charted
   caches, piles, ruins)? It is the automation law applied (you choose, it carries), but
   it is a rule change to M36's "only your own buildings".
3. **Cache tiers:** move the value outward (bulk near, iron and gear far)?
4. **Archers:** health 20, or leave it to shields and future towers?
5. **The long tail:** which rows, and are the triggers right (population 25 and 60, 100
   iron, 6,000 explored, the first succession)?
6. **The Lodge:** still "whenever"? If it should be earned, the natural moment is "A
   good home" (about day 5): a settled realm starts looking outward.

## Update 2026-09-24 — the user's answers, and two new measurements

1. **Camps: yes, next.**
2. **Salvage by haul job: yes.** The UI question: the charted cache sits in remembered
   fog, where the structure is not drawn. The answer is to select **the chart entry, not
   the structure**. See "Salvage UI" below.
3. **Cache tiers: yes.** The user asked whether "far from you" is close to someone
   else. Measured (default host, three seeds), taking the distance from each land tile
   to the nearest castle of **any** faction:
   - A third of the land is within 16–17 tiles of a castle and half within 21–24.
   - The outer third is 26–31+ tiles from every castle (9.5k–14k tiles), up to 75–90 at
     the rim.
   - The 16 castles cluster in the middle of the continent (the start picker places them
     centre-out), so **there is real wilderness at the rim, far from everyone**. This
     corrects section 1.4's "the wild is other people's back yard", which is true only
     of the middle ring.
   - Tier by the nearest castle of anyone: **stash under 17 tiles, cache 17–30, hoard 31
     and over.** No one ever starts beside a hoard, and in multiplayer every seat is
     equally far from the rim.
   - Today about a third of the 30 caches already lie 31 or more from any castle.
4. **Archers: "they can't be hit until enemies get to them"**, in one of two ways:
   (a) they shoot from an adjacent tile, or (b) they share the tile but take no damage
   while any non-archer on their side stands. Measured with rule (b):

   | Fight | Today | Rule (b) |
   |---|---|---|
   | 6 soldiers vs 6 | win, 2 left | win, 2 left (unchanged) |
   | 4 soldiers + 2 archers vs 6 | **lose** | **win in 9 h, 3 left (both archers)** |
   | 3 soldiers + 3 archers vs 6 | lose | win in 8 h, 4 left |
   | 2 soldiers + 4 archers vs 6 | lose | win in 7 h, 4 left |
   | 6 archers alone vs 6 | lose | lose (no line, no protection) |
   | 6 soldiers + 6 bow archers vs 16 | lose | win in 7 h, 6 left |

   (b) makes mixed forces beat pure ones, which is the combined-arms game, and costs one
   ordering rule in the round's damage step. (a) is the natural rule for **archers on
   walls and towers** (manned towers, M32, already decided that archers add power to
   fights near their tower and take no damage). Recommended: (b) now, (a) with
   fortifications. Balance note: under (b) a bow (10 wood, +4 power) becomes the
   cheapest damage in the game. Watch it, and price it up (for example 20 wood + 1
   iron) if bow archers crowd everything else out.
5. **The long tail: yes, and expandable.** Add a **travelling merchant** that teaches
   trade before trade exists. It arrives as an omen and sets up a temporary stall (a
   nobody's structure) beside the castle for a few days, with posted barter rates. You
   haul goods in and haul the returns home. That is the design doc's asynchronous trade
   post (section 11.2) in miniature, taught with the haul queue the player already
   uses. Suggested trigger: a road of yours reaches the Paved stage (merchants travel on
   roads).
6. **"A good home"** is the M37 milestone that fires when you finish your third House: 2
   settlers are announced two days ahead and walk in from your settled side. The open
   question is whether the Lodge should unlock then (about day 5, around 30 real
   minutes in) or stay buildable from the start.

### Salvage UI

- **The handle is the chart entry.** The chart (tile, hint, seen day, struck or not) is
  on the wire. Remembered-fog tiles are already clickable: a ground click selects the
  tile. When the tile holds a chart entry, the bubble shows the entry: "a glint of
  something man-made, seen day 41", or struck, "gone since day 58". It offers:
  - **Salvage — bring it home:** pick your storehouse or castle.
  - **Send someone to take it** (the loot errand, for one item).
  - **Scout it again:** a one-waypoint mission.
  - **Wake it** (idols).
- **The gesture, as for M36 jobs:** press on the charted tile, drag to your storehouse,
  release. A salvage job needs no cargo or amount question because you do not know what
  is inside. It takes **everything**, in mixed loads (M36 mixed cargo), until the
  source is empty or gone.
- **In the world:** a small ground pin on the charted tile while it is out of live sight.
  When the tile comes into view the real chest or statue draws and the pin steps aside.
  Also on the map (the diamonds) and in the Tab view with its hint.
- **Server rule:** a salvage job may name a source only if the player has a known chart
  entry there, or the secret is in their live sight. Knowledge still comes from presence.
  The job ends when the source empties or vanishes, and the hauler who finds it gone
  strikes the chart entry.
