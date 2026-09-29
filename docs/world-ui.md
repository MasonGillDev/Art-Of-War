# World UI: the kingdom is the interface

*Decided 2026-09-17 in conversation with the owner. W0 of the roadmap below was built
the same day: `Art Of War(prod)/Assets/Scripts/Client/WorldUi/` (Glyphs, Verb,
VerbCatalogue, Bubble, ActionWheel, WorldUi) plus `Resources/UI/WorldUi.uss`, an
additive Follow/PlayerMoved API on `CameraRig`, and a `WorldUi` slot on
`GameBootstrap`. Compile-checked headlessly; not yet run in the editor.*

## The decision

The production client's interface is **diegetic first, radial and in place**. The world
carries state. Chrome exists only for what the world cannot show: numbers, names and
verbs. Click anything and it speaks for itself where it stands, in a small dark glass
bubble anchored beside it, with an **action wheel** of glyph verbs around the rim.

**There is no fixed chrome.** No realm strip, clock, hotbar, minimap, dock or toast
log. The only things ever drawn over the world are the bubble beside the selected thing
and, while a mode is running, one slim pill at top centre naming the mode. The bet is
that the game is playable at this minimum; anything added later has to earn its place
one element at a time.

It is built as a **separate layer** beside the corner HUD in `docs/hud-layout.md`. That
HUD keeps working untouched, because it is how the game is played while this is
designed. The two are swapped by which host component sits on the scene object.

The aesthetic is quiet: slim, dark, semi-transparent, flat glyphs, one warm accent. A
thin layer of glass over the kingdom, never a frame around it.

## The test for every element

> Could the world itself have shown this?

If yes, it is cut from the UI and put in the world. If no, it becomes a glanceable glyph
at the point of relevance. Only where a glyph fails does a word appear (names). The
player should feel like they are touching the kingdom, not operating software about a
kingdom.

## The action model

Every action is one loop: **select → verb → parameters → confirm**. The world is the
form you fill in.

1. **Select.** Click a unit, structure or tile. The camera flies to it and **tracks
   it**: a walking unit stays in frame with its bubble stuck beside it. Selection is
   inspection; nothing has happened yet. Foreign things get the same fly-and-track and a
   bubble showing only what the wire reveals, with no wheel.
2. **Verb.** Click a glyph on the rim. Two kinds:
   - **Instant** (recall crew, unload, disband, form army, put to work, start
     building): no parameters, fires on click, the bubble confirms with a brief state
     change.
   - **Targeted** (move, haul, assign, build, train, equip, loot): needs the world as
     input, so clicking enters a **mode**.
3. **Mode: the world is the picker.** The camera adjusts to the decision's natural scale
   (out to choose a tile, in to choose among a building's contents). The pill names the
   step ("Haul — choose source"). Valid targets lift and glow; everything else dims.
   The world is never painted with error: invalid is quiet, valid is bright, and red is
   only a small slash at the cursor over something illegal. Where routing is implied, a
   ghost ribbon draws live from source to hovered target with a time estimate.
4. **Confirm fires one goal.** The last click submits one complete goal-shaped intent
   with every participant bound. The client's "nearest idle" sugar binds explicit units;
   the sim never chooses. The mode ends, the camera **zooms out to a comfortable
   distance from where it is** (no return to origin), and the intent becomes visible
   geometry: route on the land, glyph over the destination, pip on the walker.
5. **The intent stays visible until done.** In-flight goals are world annotation.
   Stalled goals announce themselves with an amber chip at the waiting thing
   ("waiting: builder", "waiting: food"). Dissolved goals get a line.

### Camera input is the abort

Any pan, orbit, zoom or key by the player is read as "I've stopped looking at that":

- With a bubble open and no mode: the bubble fades, tracking stops, the selection
  clears.
- Inside a mode: it aborts the **current step**, back to the previous step, not out of
  the mode. Zoomed into a chosen structure to pick cargo and you move the camera, you
  are back to choosing a structure.
- Escape aborts the whole mode. Nothing partial reaches the server.

### Right-click stays

Right-click on the ground moves the selection. It is a shortcut for the Move verb and
the same intent, kept because it is the RTS reflex. It is the only shortcut; every
other targeted verb starts from the wheel.

### The bubble's interior is dynamic

By default: portrait slot, name line, vitals. A verb that needs a choice either
**replaces the interior** with the choices (a few) or **grows a second ring outside**
(many). The threshold is a tunable, not a law; the first guess is four.

### Unit vitals

From the wire, in this order: **age**, **cargo** as glyph and count, then one
**activity glyph**: Zs idle, arrow moving, sack hauling, pickaxe working, hammer
building, hourglass waiting. Health as a small bar. All six activity states are on the
wire today; the world-state track (below) will later take activity and cargo off the
glass once figures show them.

### Structure vitals

Workers over capacity, holdings as glyph and count, build progress and builders present
for a site, residents and pantry for a house. Read-only.

## Why

- **The world already renders truth.** The client's prime directive is that
  presentation amplifies truth and never invents it. Every unit, building, cargo and
  combat on the wire is drawn. A panel repeating what is drawn two metres away is a
  second, worse rendering of the same fact.
- **A verb next to the thing is a verb you find.** The corner HUD learned that
  description and verbs must be one object. The bubble puts that object beside the
  thing, so the eye never leaves the world.
- **Glyphs scale; sentences don't.** A dozen sentences is a wall; a dozen glyphs on a
  rim is a glance, with the sentence on hover for the one you are unsure of.
- **The screen belongs to the camera.** `docs/art-direction.md` puts everything into
  the oblique diorama shot. Chrome that frames the shot competes with the light and
  mist that make it.
- **Minimal first.** Starting from nothing and adding with justification is the only
  way to find out what is truly needed. Starting from the current HUD and removing
  would keep every fixture that nobody argued against.

### Alternatives ruled out

- **Reskinning the corner HUD.** The skin is not the problem; the layout is. The
  previous reskin's verdict was "essentially the same".
- **uGUI world-space canvases per entity.** A second UI stack, one canvas per bubble,
  text that shrinks with distance. The bubble is a screen-space UI Toolkit element that
  follows a projected world point each frame. Crisp at every zoom, one draw path, one
  stylesheet.
- **3D in-world widgets for numbers.** Bars and digits as meshes read badly at the
  oblique band and fight the fog. 3D is kept for what is genuinely in the world:
  selection ring, drawn orders, route ribbons, glanceable markers.
- **Icons drawn in code** (as `HudIcons` does with Painter2D). That is brute-forcing art
  with code, which this build stops doing. Glyphs are **assets or nothing**: a verb
  whose glyph is missing is not drawn, and the console lists the missing names.
- **A verb layer beside `OrderIssuer`.** Every Can/Do pair stays there; the wheel is a
  new presentation of the same verbs.

### Trade-offs accepted

- **Two hosts for a while.** The corner HUD and the world UI coexist until the wheel
  covers every verb the card offers. Then the HUD is deleted.
- **Verb rules ported, not shared.** The wheel's catalogue re-expresses what the context
  card holds inline. Refactoring the card we are retiring would be waste.
- **The Deliver chain stays for now.** The client fakes Deliver as a Move followed by a
  client-tracked Unload. It breaks "one goal, one intent" and the sim never knows a
  delivery is happening. Kept until the server has a goal-shaped Deliver intent;
  recorded here as a known gap, not an exception to the rule.

## Wire gaps this design exposes (server decisions, not client fabrications)

| The mock shows | The wire has | Until then |
|---|---|---|
| "Edda", "Riverside Farm" | no names on units or structures | title by role or kind |
| a slider choosing 16 of 24 | haul carries a resource, no quantity | holdings read-only |
| a Deliver that the sim carries | Move + client-side Unload | the chain, flagged |
| route time estimate | no client pathfinder | client estimate over the known world, labelled as an estimate |

## The asset contract (the artist's half)

Art, not code. Loaded by name; the code runs, visibly unfinished, without them.

| Asset | Spec |
|---|---|
| Glyph set | flat, single weight, white on transparent, 24-unit grid, one file per name; the list of names is the code's `Glyphs.Names` and the console reports what is missing |
| Portraits | one per role, square; per-person portraits wait on names on the wire |
| Fonts | current stand-ins are machine-licensed; open-licence swap before shipping |
| Glass | translucent dark fill is a stylesheet token; real blur behind glass is a URP render feature, deferred until the layout has settled |
| Accent | one warm colour; nothing else saturated |

When a step needs an artist, the code stops at the contract and says so.

## Roadmap

| Phase | What |
|---|---|
| W0 | Host, world anchor, bubble with vitals, action wheel, verb catalogue over `OrderIssuer`, glyph contract, stylesheet. Fly-to and track on select; camera input aborts. Switchable beside the corner HUD. |
| W1 | Modes: the pill, glow-and-dim targets, cursor slash, step-back on camera input, zoom-out on completion. Haul and build first. |
| W2 | Visible intents: route ribbons, destination glyphs, walker pips, amber stall chips. |
| W3 | Interior choices and outer rings: train, equip, craft, loot, family. Retire the corner HUD when every verb is on a wheel. |
| W4 | World-state track: cargo on figures, activity posture, bustle; remove the glass rows the world now shows. |
| W5 | Art pass: glyphs, portraits, glass, fonts, accent. No code changes by design. |

## Acceptance

- With nothing selected, nothing is drawn over the world.
- Selecting a person flies the camera to them, follows them as they walk, and shows a
  bubble beside them with age, cargo, activity and every applicable verb on its rim.
- Any camera input fades the bubble and clears the selection; inside a mode it steps
  back one step.
- No glyph is drawn that is not an asset on disk; the console lists the missing ones.
- Every number on the glass is a wire field.

## Update 2026-09-17

The glyph set and body font from the asset contract now exist,
ahead of W0 code. Sources live in `art/ui/` (see its README); `build-glyphs.js` is the
single source of truth for every shape and writes the PNGs into the production client's
`Resources/UI/Glyphs`. Body and BodyBold are Inter 4.1 (OFL)
in place of the Segoe UI copies; Title is still Palatino and still cannot ship. Portraits
were tried in the glyph stroke language and rejected; the slot stays empty until the art
pass (W5) paints real ones.

## Update 2026-09-18 — W1 begins with Haul

Haul is the first full mode (`Client/WorldUi/HaulMode.cs`, `MarkerLayer.cs`). Source and
destination steps let go of the camera and pull out to survey distance; every valid
target is ringed and wears a chip (`MarkerLayer`) saying what it holds; camera input
in those steps is how you look, not an abort. Choosing cargo flies to the source, moves
the bubble onto it with its holdings inside and the cargo glyphs on the rim; camera
input there steps back. Completion (`HaulPlanner.Complete`, distinct from `Cancel`)
settles the camera at survey distance where it is; a cancel hands it back to the
hauler. Invalid hover = the `invalid` glyph beside the cursor, the only red allowed.

Not yet: the world dimming behind lit targets (needs a render feature, not a UI
overlay, or the lit targets dim too), the route ribbon with a time estimate (W2, needs a
client-side cost estimate), and the same treatment for Move and Build.


## Update 2026-09-20 — received-but-unused wire fields go into the world, not cards

The P1 package (`docs/prod-client-gap-discovery.md`) lists eight view fields the client
receives and never shows. They were specified for the corner HUD's cards; the corner HUD is
being phased out, so each was placed in the world UI instead. Decided row by row:

| Field | Home in the world | Status |
|---|---|---|
| `buildDurationTicks` | Caption of each kind in the Build ring: "Build a farm · 20 wood · 3 days". | done |
| `minTrainAge` | The Train verb dims with "too young — trains at N". Rule lives in `OrderIssuer.TrainObstacle`; the wheel and the corner HUD both read it. A mixed selection is refused only when nobody is old enough. Behaviour change: a click that used to fail silently is now a dimmed verb with a reason. | done |
| `gestationTicks` | Caption of the Family verb: "Begin a family… · a child in 2 days". | done |
| `buildEtaTicks` | The site bubble's progress bar caption reads "done in N days" while building; when not building the bar dims and the planned stall chip says what is short. | done |
| `foodPeriodTicks` | **The castle is the realm.** Realm-level facts (food, net per period, runway) move onto the castle's bubble; the period is the unit on the net row. This is the phase-out path for the realm strip, one row at a time. Castle bubble now carries people, food with rate per period, and runway or "starving in N". | done |
| `claimFertility` | **Gradient on the claim outline.** While one of your extractors is selected, each claimed tile's glowing edge runs green→amber→red by fertility, graded between the sim's forest and desert thresholds (now on the wire as `WorldDto.fertility`; a server without the block leaves the plain glow). Grade and colours live in `WorldUi/SoilGrade.cs`; `SelectionRenderer` reads them. The bubble shows one soil row, worst→best, amber when the worst field is near the latch. Needs a `soil` glyph asset. | done |
| `ProposalDto.expiryTick` | Deferred to the **admin screen**: a full-screen, paged view opened with Esc for stats, diplomacy and messages from other players. Not part of the world UI; not designed yet. | deferred |
| `OrderDto.currentStep` | Skipped. Automation gets its own redesign in the world. | skipped |

Formatting of every duration goes through `Client/Hud/Days.cs` (`Of`, `Ago`, `In`), the one
voice for "how long".


## Update 2026-09-21 — the first targeted verb with a parameter: Engage

Engage is the first verb to walk the whole action model (select → verb → parameters →
confirm) with a numeric parameter. Decided:

- **The world picks what it can.** The quarry is a hostile clicked on the map; the leash
  centre is a tile clicked during the leash step (default: the lead chaser's feet). While a
  quarry is being chosen, the pointer over anything but an enemy wears the red slash.
- **The radius is a slider inside the bubble**, the design's "sliders beside the thing". It is
  built once when the step opens and never rebuilt by the tick, so a drag is never interrupted.
  The planner's `Changed` event is silent on radius drags for the same reason.
- **Right-click on an enemy stays** as the shortcut with the default leash (8).
- **Not yet:** a preview ring at the leash centre during composition. The renderer draws
  pursuits from the view only; the preview is a small renderer-seam request recorded in
  `docs/p3-status.md`.
- **Battles are read off the ground.** A battle tile's bubble is the battle: sides, power,
  round. A siege tile is the fortification's bubble, which already carries the siege readout.


## Update 2026-09-21 — automation in the world: a supply line is a haul you pin

The corner HUD's Machine page (a form of eight blanks) is being retired in favour of
automation composed and read in the world. Decided with the user, supply lines first:

- **One gesture, two endings.** The Haul gesture (source → cargo → destination, world as
  picker, camera flying between) gains a Confirm step on the destination. The rim offers
  *once* (the errand, as before) and *keep it running* (a standing supply line whose named
  crew is the hauler the plan started with). Automation is one click past the errand the
  player already knows.
- **Ask only what the world cannot answer.** A source holding one kind of thing skips the
  cargo step. The level to keep defaults to the corner form's rule (60% of the store,
  capped at 300), the priority to "after what already runs". Nobody is asked about priority.
- **The line is a thing on the BOARD.** Revised the same day: the user did not want a chip hovering in the diorama all the time, so threads and chips draw only while the Tab strategic view (`LegibilityView`) is up — the diorama shows people and buildings, the board shows what runs them. (The flat chart at the zoom ceiling was considered and set aside; Tab keeps the 3D camera and the projection the chips already use.) While Tab is up, every supply line wears a chip at its midpoint
  (cargo glyph + the sim's own state word, amber when short of hands, blocked or stopped).
  Clicking the chip selects the line — a new `SelectionKind.Order` — and the camera frames
  both ends. Its bubble: cargo and level, from → to, hands by portrait (or "anyone within
  R"), state, and the sim's reason when stalled. Wheel: end the line. The thread itself is
  still `OrderOverlay`'s dashes.
- **Rejected:** hold-and-drag from source to destination (the hold fights the camera when
  the ends are far apart; the two-click rubber band keeps the camera free) and a
  wheel-first form on the destination (front-loads five questions the world can answer).
- **Caravans** need no new concept: more names on the crew. Soldiers on a supply line
  protect by presence — combat is same-tile co-location — and the line never chases
  (engage radius stays 0). Patrols are a separate order for the roads. Deferred: crew
  editing on the line's bubble, the pull-radius slider curved along the rim, adjusting the
  level, and the other order kinds (staff on the extractor, train on the school, breed on
  the house, patrol as a drawn route).
- **Sim wrinkle recorded:** every civilian carries 5 cargo, so soldiers on a caravan load
  too; harmless for a non-chasing line, but their load spills as loot on death. Server
  decision, not taken here.

Needs one glyph asset: `repeat` (keep it running).


## Update 2026-09-21 — patrols, picking a crowd, and honest routes

- **Patrol = crew as selected + stops as clicks.** Select soldiers, Patrol on the wheel, click
  the stops (numbered marks appear where clicked), right-click or Backspace takes one back,
  Enter or the confirm glyph sends. Engage 3 / leash 4 go out by default (the corner form's
  military defaults); the patrol's bubble has an *Adjust* verb that opens two sliders in the
  interior (leash never below engage), applied by the rim's confirm — Clear + Set underneath,
  so the patrol returns under a new id. A formed army cannot be crew; a routine needs a
  building of yours to anchor to (castle preferred); both are said on the dimmed verb.
  A patroller's bubble offers *Open their patrol*. Rejected: "Patrol this line" from a supply
  line's bubble (user: the two orders stay separate). The patrol's chip sits at the loop's
  centre on the Tab board, like a line's.
- **Picking a crowd.** In a many-person bubble each trade row is a button: click it to keep
  only that trade. Double-click a person to select everyone of their trade on screen. Supply
  lines still take a single hauler as crew (user chose not to extend crew-as-selected there).
- **Routes are walked trails, not straight dashes.** `Automation/RouteTrails.cs` remembers,
  per standing order, the tiles its lead hand was seen standing on; `OrderOverlay` draws pads
  at the places and short dashes along the trail only. A new order shows its places and no
  road until the crew has walked. Honest by construction and client-only; the sim's committed
  path (`Unit.PathRemaining`) is not on the wire, and putting it there for own units would be
  a projector-side addition — parked by the user's call.

Needs glyph assets: `patrol`, `tune` — both made 2026-09-23 in `art/ui/build-glyphs.js`.

## Update 2026-09-23 — sightings: foreign units coming into sight

A foreign unit entering live sight is announced on a card in a top-left stack in the world
UI layer: yellow for a neutral kingdom, red for a kingdom at war with you (or with a war
pending) and for bandits. Allies are silent. One card per unit, by the user's choice over
one-per-kingdom or one-per-cluster. Clicking selects the unit (fly and track as usual), or
its last-seen tile if it has left sight. Red pulses until clicked, yellow fades. Sound slots
exist for a warning and an alarm, one per tick at most and rate-limited.

**The camera never jumps on its own.** Auto-focusing on an enemy was considered and
rejected: it fights a player busy around their own kingdom. "Only when idle" was the
middle option and was also declined. The card is the interrupt; looking is the player's
choice.

This is the second fixed element on the glass after the mode pill. It earns its place
under "add elements one at a time with a reason": threat arrival is the one event that
must not wait for the player to look. Client-only: the view already carries exactly the
units in live sight, so no wire change was needed. Engine detail in the prod client's
`docs/world-ui.md` §11.
