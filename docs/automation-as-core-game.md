# Automation as the Core Game — the vision

**Status: living vision doc, agreed 2026-08-04 (direction, not mechanics).**
This is the *what* and the *why it's fun*. It sits above the design base
(`docs/persistent-rts-design.md`) and steers everything below it. It does
**not** lock specific automation mechanics — those need more thought (see
"Still open"). It exists so that every downstream decision can be checked
against one question: *does this make the automation game deeper?*

Timing numbers here (tps, telegraph windows, siege durations, food rates)
are illustrative. Getting them right is turning a knob; this doc is about the
shape they're tuned toward, not the values.

## The thesis

**Automation is the game on top of the game.** The sim — food, land,
people, distance, war — is the physics. The *game* is the machine you build
on top of that physics to survive and grow it without touching it by hand.
The reference point is Factorio: the rocket and the biters are just bones;
the real fun is *how you solve the problem with the tools you're given*. An
inserter, a belt, a train — here they are a hauler, a road, a boat, a supply
line. You are not playing a kingdom. You are building the thing that plays
the kingdom, and then tending it.

The old failure this replaces: the game demanded your attention for *chores*
(hauling food, emptying buffers, walking breeding pairs) and gave you none
for *stakes* (armies, walls, war). The vision inverts that completely.
**Peace runs itself. War demands you. Attention follows stakes, never
chores.** The rhythm of the game is long automated exhales punctuated by
sharp manual inhales — which is the rhythm of real war, and the reason the
game breathes.

## The rhythm: async by design

At the intended pace a game-day is minutes and a night's sleep is dozens of
game-days. No one can babysit a farm buffer across that gap, so the game
*must* run itself while you're gone — and the whole point of building the
automation machine is to earn the right to walk away. You come back not to
play the kingdom move-by-move but to **read what your machine did** and
adjust its trajectory: re-aim a supply line, raise a food floor, mark a
threat. Five minutes of tending can bend the next hundred game-days.

This is the twist that makes it *not* Factorio: you never get to watch the
belt back up in real time. **You design for your own absence.** That forces
two things the genre rarely has — throughput you can predict at design time
(so you can balance a network you won't be watching) and a legible report
you read on return (so the variance between predicted and actual *is* the
news). Reading the state of your own machine is a primary verb of this game.

## The laws of automation

These are the load-bearing agreements. Break one and it's a different game.

1. **Automation covers maintenance, never allocation.** Hauling, restocking,
   breeding, training, crafting, equipping, routine scouting — the repeatable
   verbs — can all be automated. *Where* to build, *what* to build next, when
   to go to war, who to ally: those stay human forever. They are the game;
   automation is what frees you to play them.

2. **Automation is one layer deep.** Rules act on the world. Rules never act
   on other rules. There is no meta-automation, no policy that reconfigures
   policies, no fire-drill that rewrites your logistics when war comes. That
   is one level too deep and it becomes a chore of its own ("automating the
   automation"). When the situation changes, *you* step in and change the
   machine. The wrench stays a wrench.

3. **Automation composes through the world, not through itself.** Orders
   compose by sharing physical stock at physical structures — one line fills
   a stockpile, another drains it, a craft order eats from the barracks a
   supply line feeds. No order ever references another order. The
   **warehouse is the interface.** This keeps every composition visible on
   the map, debuggable by walking to it, and attackable by an enemy. The
   moment orders wire to orders, you've built invisible spaghetti and lost
   the game's physicality.

4. **Automation is embodied in mortal crews.** Every order is run by specific
   named people, not an anonymous pool. Those people age, eat, and die. Kill
   the crew and the order goes dark — logistics is broken by
   *systematically killing the people who run it*, which is more real and
   more satisfying than blowing up one magic building. Your machine is made
   of flesh, and the enemy can bleed it.

5. **Bread is electricity.** Every automated crew burns food. Automation is
   never free once built — it has a wage bill forever. This is the law that
   fuses the two halves of the game: more automation needs more food, more
   food needs more land, more land is always farther away (the growth curve
   is logarithmic — three farms are fast, the twelfth is a decision), farther
   land means contact, contact means war. **The automation game and the war
   game are one game, connected through bread.** The game doesn't need an
   artificial rocket goal; the loop closes on its own.

6. **Nothing teleports — except your command.** Food, armies, intel, goods:
   everything physical must be carried across real distance. The one thing
   that moves at the speed of thought is the player's own command — you issue
   orders instantly anywhere. (Command-travel — couriers, signal towers, a
   command radius — was considered and **rejected as one layer too deep**:
   it makes running the machine itself a logistics chore, which violates law
   2. The physics stays hardcore; the interface to it does not.)

## Earning the tools: progression is the tutorial

A new player is not dropped into the full toolset and wished luck. The tools
are **earned by performing the verb they automate.** The game directs you —
build a farm; haul food home; build a house; breed — you do it once by hand,
and completing it permanently unlocks the automation of exactly that verb.
Doing is the tutorial, the unlock is the reward, and the fiction (haul food
to learn hauling automation) is diegetic — no abstract tech tree. Not
everyone playing the same handed-out steward brain; everyone assembling their
own machine from primitives they earned and understand.

The opening is deliberately a little bit of a chore — but a *rewarded* one,
each rung paying out a capability, until the player has the full toolset and
the kingdom can survive the night. From there the game opens: maintaining the
land, war, diplomacy, ships, canals, expansion. The game unfolds in front of
you as you play it, rather than dumping you into a running sim and saying
good luck.

**The exact rungs, triggers, and what hosts a capability once earned are
open** (knowledge vs. razeable capacity structures — see "Still open").
What's agreed is the shape: teach by doing, reward with automation, open
into strategy.

## The async fairness doctrine

Because the game is played across your absence, offline can never mean
deleted:

- **Presence buys finesse, never survival.** Auto-resolve is the floor;
  showing up is the ceiling. When you're not there, the sim resolves things
  with proportional, swingless math — you lose the *edge*, never your
  existence. A coin-flip that wipes a kingdom while its owner sleeps is the
  single most unfair thing this game can do, and it never happens.
- **Reserves are player-chosen insurance.** Automation keeps food *flowing*;
  storage keeps it *banked*. How many days of buffer to hold against a bad
  night is a real strategic choice with a real cost. The player sets how safe
  their kingdom is while they sleep.
- **Losing capacity degrades the surplus, never the lifeline.** However
  automation capacity is hosted, there is always an unkillable minimum that
  keeps the core alive; an enemy can cripple your *margins* overnight, never
  your heartbeat.
- **Aggression spans absence windows.** War telegraphs and civic sieges are
  anchored to wall-clock absence, not to how they feel when you're staring at
  the screen — so a defender always gets at least one waking session to
  react. The counterplay to being attacked while away is *preparation* —
  walls, garrisons, reserves, allies — which is the skill the whole game is
  about anyway.

## War: the wrench

War is the most interesting thing in the game because it is where automation
**erodes**, and that's by design, not a gap. It erodes as a gradient by
proximity to the enemy:

- **Deep home** — fully automated. Farms, breeding, crafting hum.
- **The border** — automation under threat. Escorted supply lines, armed
  patrols: still automated, but made of crews the enemy can kill (law 4).
  This is the frontier your kingdom grows a hard edge along.
- **Contact** — fully manual. No rule can express what to do when the lines
  meet, because contact is never routine. This is a design law, not a missing
  atom.
- **Aftermath** — automation creeps back. Salvage crews walk the field,
  dead orders get re-crewed, the machine heals.

**The battle itself is where the player becomes a general.** Up to this point
you've been a logistician. Then two armies converge — you both see it coming
days out through the telegraph and your scouts' fog-broken glimpses — and it
becomes a tactical fight you play by hand over a game-day, closer to chess
than to RTS click-storms: split your force, race to be the massed one when
the lines touch (big stacks march slow, divided ones fast), fight from the
walls and chokepoints you built in peacetime, feed reserves round by round.
And the whole time the home front is still running behind you and may need
*you* to uproot it mid-battle — pull haulers into the fight, levy farmers,
re-aim a supply line from ore to bread to keep the army fed. Two boards, one
clock. That is the scenario the whole game is built to produce.

Time itself is an instrument here. Because wall-clock pace never touches the
sim's internal proportions, the server can let quiet months flow past
overnight and slow the world when lines meet — so a battle-day becomes an
evening of real thinking time. Time dilation as **dramaturgy**, not just a
load valve.

## The scenarios (the heart)

Six moments, each leaning on a different system, all showing the same thread:
automation runs the kingdom, the player runs the exceptions, war is the
biggest exception.

### 1. The First Night — *food + the first automation handoff*
You start with a castle, fourteen people, and a food bar already ticking
down. The game says: build a farm. You do — but the food piles up *at the
farm*, so it says: haul it home. You walk a hauler out, load 25, walk it
back, deposit — and the moment it lands you're handed your first tool: a
supply line. Keep the castle above 300 food, from that farm, on that hauler.
You watch it do by itself what you just did by hand, and it clicks: that loop
runs whether you're looking or not. You bank the castle toward its ceiling,
your reserve goes fifteen game-days deep, and for the first time you look at
the clock differently. You close the laptop. Eighty game-days pass while you
sleep, and you wake up to a kingdom that's still alive — bigger, even. The
whole promise of the game, delivered once, small.

### 2. The Wandering Fields — *land degradation + rotation/irrigation + the distance curve*
A farm doesn't last: it strips its own soil to permanent desert after about a
hundred game-days, and your oldest, nearest field dies on the ground it
killed — with your first supply line still pointing at it. Two interesting
answers: *rotate* onto fresh grass a little farther out (and "farther out" is
the trap — the good near land is spent, the next farm is always deeper), or
*irrigate* — dig a canal into the belt so the same land recovers faster and
feeds more, a big up-front project that buys years of not moving. Either way
your supply lines now chase a moving target and every trip runs longer. The
kingdom isn't harder to *run* — it's harder to run *well*, because distance is
eating your throughput. Three farms were fast; the twelfth is a decision.
That's the log curve.

### 3. The Stone Problem — *the multi-tier transport puzzle*
You need stone; the nearest mountains are across a lake or forty tiles of
forest. Three tools, your pick: lay a **road** through the forest (fast
artery, but it decays if you stop feeding it), build a **dock and boat** to
haul 100 at a time at five times march pace (huge write-down, pays off
forever), or **dig a canal** to bring the water to you. Whichever you choose,
you wrap it in automation — quarry→dock supply line, boat freight route, home
dock→stockpile line — and now you've built a three-stage machine moving stone
across a lake without you. You watch the first loads flow and check whether
the stages balance or whether stone piles up at the far dock because the boat
can't keep up. Then you add a second boat. Solving the map with the tools is
the game.

### 4. The Border Bleed — *bandits + crew-death breaking orders + walls*
Your best ore runs through dark ground, which means bandits. You find out the
morning a hauler is just *gone* — dead on a road, cargo taken — and because
that hauler *was* the crew of a supply line, the order stopped. No one
stepped in, and you didn't notice for two game-days because you were asleep.
The border teaches you that automation out here is made of people, and people
at the edge get killed, and every killed person is an order going dark. So
you re-crew the line, wall-and-gate the pinch point the raiders use, and set
soldiers to patrol it — armed automation, a crew whose job is to stay alive
at the dangerous part of the map. Your kingdom now has a frontier: soft
automated interior, hard watched edge. The bandits taught you where your
kingdom ends.

### 5. The Day the Lines Met — *diplomacy + scouting + manual tactical war*
A rival declares war; you have two game-days, and in async time that's an
appointment, not an ambush — you see the declaration, then your scouts feed
you glimpses of their army forming and marching straight at you. There's
nothing to automate about what happens when it arrives, so you take the
wheel. You pull your soldiers off their standing orders, read the ground you
built (the bandit wall is now a battle line, the gate a chokepoint you own),
split your force to catch their column before it concentrates, and feed
reserves as the rounds resolve. And the whole time your food floor is
dropping because you've pulled haulers into the fight — so mid-battle you
have to decide whether to yank a supply line off ore and onto bread to keep
the army fed. Two boards, one clock. Then it's over, the salvage crews walk
out on their own, and the machine takes back over. This is the scenario the
game is for.

### 6. The Morning Report — *demography + reading your machine*
You've been away a day; ninety game-days went by. You didn't lose — but you
sit down to a kingdom that lived without you, and you read what it did. It's
not all fine: your population is up but your food *margin* is down (the town
outgrew the farms), an old field quietly desertified and its supply line has
been pointed at dead dirt for thirty days, a standing scout brought back a
neighbor's castle that's suddenly much bigger, and a birth drought — a cohort
of founders aging out of fertility together — left a notch in your population
curve that'll be a notch in your army twenty days from now. None of it's a
crisis; all of it's a decision. You re-aim the dead farm's line, raise the
grow policy's food floor, mark the neighbor for a real scouting mission. Five
minutes, nothing touched by hand, the next hundred game-days bent. The async
game at rest: not playing the kingdom, but tending the machine that plays it.

## What the sim becomes

If automation is the game, the sim's job description changes: it stops being
the game and becomes the **physics engine the game is played against** —
which is how it was built. Its pressure generators — land degradation,
bandits, rivals, demography, distance — are this game's biters. Their tuning
target is not "is this realistic" but **"does this force interesting
redesign?"** A pressure that makes the player rip up an old part of their
machine and build a better one is working. A pressure that just nags is not.

## Still open (needs more thought)

Agreed: the vision above. **Not** agreed — deliberately, per the steer that
specific automation mechanics need more thought:

- **The automation vocabulary.** Which primitives exist beyond today's supply
  line / route / standing craft — breeding (grow), training (pipeline),
  soldiers (muster), escorts/convoys, and how far each parameterizes. Paper-
  play a full kingdom and find where "I can't say what I want" first hits;
  those gaps, in the order they hurt, are the real roadmap.
- **Where an earned capability lives** — permanent knowledge, razeable
  capacity structures, or both — and how that reconciles with the async
  fairness doctrine (the unkillable minimum).
- **Frictionless redesign.** In-place editing and re-crewing (a dead crew's
  order accepts a replacement) — core verbs, not deferred niceties, if
  ripping up your own machine is a main activity.
- **The order editor as the primary UI**, map-native, with design-time
  throughput prediction and the morning report. The single biggest build,
  mostly client.
- **The tactical war layer** — disengage/retreat, target priority, stances,
  and tps-as-dramaturgy — spec'd against the Rival, who already musters,
  marches, and sieges, so the chess layer is testable in single player.
- **All timing.** Every window named here is a knob to be tuned, not a
  decision.

## References

- `docs/persistent-rts-design.md` — the design base this steers.
- `docs/automation-layers.md` — the shipped engine (standing orders + driver)
  these ideas extend.
- `docs/m18-automation-engine-spec.md` — the atoms/templates that exist today.
- `docs/time-and-scale.md` — the band model; async is its consequence.
- `docs/m25-rival-spec.md`, `docs/m17-defender-spec.md` — the AI whose war
  doctrines the tactical layer is proven against.
- `docs/sieges-and-conquest.md`, `docs/walls-and-gates.md` — the war physics
  the wrench erodes into.
