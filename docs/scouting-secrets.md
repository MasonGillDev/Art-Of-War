# Scouting secrets: scouts chart what they find, and must come home to tell it

## The decision

The world holds **secrets**: sites the server knows and the player does not (loot caches,
rumoured ruins, and a new kind, the **idol**). Anyone can **see** a secret while it is in
their live sight, and it vanishes when the fog comes back. Only a **scout on a mission**
**charts** one: the scout writes down the exact tile and a faint idea of what it saw, and
the entry reaches the player's map only if the scout **returns alive**. A charted secret
is a permanent map marker. Going out to retrieve or investigate it is the player's next
move.

Scouts are no longer a starting unit. They are **trained at the Lodge**, which can be
built at any time for now (when it should become available is a later progression call).

**Idols** are statues in the fog. A unit that reaches one can **activate** it: for a set
time the activator sees a random circle of the world with no fog. Different idols have
different durations (and radii).

## Why

### What the user asked for (2026-09-23)

- Scouts, like soldiers, are not a unit you start with: they are trained at a lodge.
- The world fills with secrets, and scouts are how the player uncovers them.
- On a mission, a scout's sight records each secret's location and a faint idea of what it
  is. The findings count only if the scout makes it back alive; then the locations are
  marked on the map.
- Anyone can see a secret with their own eyes, but when fog returns it stops rendering,
  as everything else in fog does. Only the scout's chart keeps it.
- After the report, retrieving or investigating is up to the player.
- User answers: Lodge buildable whenever (timing later); **mission scouts only** chart;
  the chart marker is the **exact tile**; first secrets are **caches, rumoured caches and
  idols**.

### What already exists (M20) and is reused

- The **Lodge** (buildable, 60 wood + 20 stone) already gates `DispatchScoutIntent`.
- **Scout missions**: waypoints, a return rule (waypoints done / time budget / hostile
  seen), an in-sim runner, and an **observation log** captured from the scout's live
  sight on every arrival, snapshotted. The log is final when the mission is `Returned`.
  The server turns it into a report (claims + optional LLM prose) only then.
- **Live-sight-only projection**: the server sends structures and piles only while in
  current sight, and the prod client rebuilds from each view. A secret already vanishes
  in fog; nothing new is needed for "they stop rendering".

So the new work is: the secret kinds, the **chart** (what the returned mission delivers),
the idol, scouts moving to the Lodge, and the prod client surfaces.

### Choices and what lost

- **Only mission scouts chart.** Lost: any scout, or any unit, charting what it sees.
  That would make the mission, and getting home, meaningless.
- **The chart is delivered at the mission's `Returned` transition, in the sim.** Lost:
  delivering it from the server's report store. The chart is game state (it decides what
  the player can act on), so it is saved, hashed and replayed like the log it comes from.
  A scout who dies never reaches `Returned`, so its findings are lost with no special case.
- **Exact tile, faint "what".** The marker is exact; the vagueness is in the description.
  The wire carries a **hint category** (`Glint`, `StoneFigure`, ...), never the secret's
  kind or contents, so a modded client learns nothing a player would not.
- **Charts go stale.** An entry records what was seen and when ("seen day 41"). If a bandit
  empties the cache, the marker does not know until the player looks again. Recommended:
  when the owner's own live sight covers a charted tile and the secret is gone, the entry
  is struck ("gone"). Re-scouting has value; the map never silently lies.
- **Secrets are structures with a sentinel owner**, like caches today. Lost: a separate
  secret layer with its own rendering and fog rules. Structures already get live-sight
  projection, the renderer, bubbles and the loot verb.
- **Idol vision is real sight.** While active, the circle is part of the activator's
  `View.VisibleTiles`: units, structures and secrets in it render and it grows the
  explored map. `BanditRules.IsSeenByAnyPlayer` must include it too (the pinned
  equivalence rule), so bandits never spawn under an idol's eye. Secrets seen through an
  idol are **seen, not charted**: the idol shows you where to send a scout.
  - Lost: a one-time snapshot image of the circle. Live sight is what the user described
    ("see a random circle with no fog for a few days") and reuses the fog machinery.
- **The idol's circle is picked with the sim's hidden RNG**, as is the rumour's search
  area from now on. Lost: a public hash, which a modded client can invert (found
  2026-09-23 for the rumour circle). The draw happens in the sim's event order, so it is
  deterministic and replayable; it only shifts later rolls relative to a world where the
  draw never happened.

## Shape

- `UnitRole.Scout` trains at the **Lodge** (`RoleTrainerCatalog`); the human seat's start
  roster has no scouts.
- **Secrets**: `SecretKind` (Cache, RumourCache, Idol, ...) on sentinel-owned structures;
  a server-side `Hint` per kind for the wire.
- **Mission capture**: `ScoutObservation.Capture` already logs structures in sight; the
  mission keeps the secrets it saw (tile, kind, tick).
- **Chart**: `GameWorld.Charts[playerId]` → entries (tile, hint, seen tick, state
  Known/Gone), written when the mission returns and when the owner's own sight finds a
  charted secret gone. Snapshotted.
- **Idol**: an `Idol` structure (`IdolKind` → duration, radius, cooldown),
  `ActivateIdolIntent` (a unit standing on it), a `VisionGrant` (owner, centre, radius,
  ends at) read by `View.VisibleTiles` and `BanditRules.IsSeenByAnyPlayer`, and an expiry
  event. Idols are scattered into the genesis fog like caches.
- **Wire**: the owner's chart entries; the owner's active vision grants; idols and
  caches as ordinary structures while in sight.
- **Prod client**: train scouts at the Lodge; a scout-mission gesture on the patrol
  template (select a scout, click waypoints, pick a return rule, send); chart markers on
  the map (exact tile, glyph by hint, "seen day N", struck when gone); the idol's
  activate verb; the grant circle on the map while it lasts.

## Acceptance tests

- A scout trains at a Lodge and not at a School; the human start has no scouts.
- A mission scout that sights a cache and returns adds one chart entry with its exact
  tile and hint; a scout killed before returning adds nothing; a non-mission unit that
  sees the same cache adds nothing.
- The wire carries the hint and tile of a charted secret, never its kind or contents;
  an uncharted secret out of sight is not on the wire.
- The owner's sight over a charted, emptied tile strikes the entry.
- An activated idol makes its circle visible to the activator only, for exactly its
  duration; bandits cannot spawn inside it; it grows the explored set; it cannot be
  re-activated inside its cooldown.
- Twin-run and snapshot/restore mid-mission and mid-grant land on the same hash.

## Future expansion

- **More secrets** plug in as `SecretKind` rows with a hint and an interaction: bandit
  camps (progression's deferred seam), rich ore veins, shrines, wrecks, abandoned
  villages. "Investigate" becomes a verb for the kinds that need it.
- **When the Lodge becomes available** is a progression Unlock row later.
- **Recurring patrol scouting** (the M20 deferred standing order) can re-dispatch a
  mission on a loop without changing charting.
- **The M20 report prose** can narrate the finds; the chart never depends on it.
- **Multiplayer:** charts are per player; secrets are shared world objects, first come,
  first served (the user's call for the rumoured ruin).

## Update 2026-09-23 — built, and the user's answers

- **The AI keeps its starting scouts**; it asks for a replacement only if it owns a Lodge.
  A human seat starts with two untrained citizens in the scout slots (same headcount).
- **Idols are single use and crumble**, the circle lands on **unexplored land**, **20 per
  map** (every 4th a Greater idol: 5 days, radius 10; Lesser: 2 days, radius 6). Idols
  and their knobs are behind `--idols N` (host default 20; a bare `ServerOptions`, as the
  AI labs build it, places none, so their genesis RNG is untouched).
- **Strike-through: yes.** Implemented as "the owner's own sight over the tile": on every
  sim-side reveal (`Sight.AfterReveal`) and at the moment a secret leaves the world while
  the owner is watching (`View.Sees`).
- **Waking an idol can be sent from afar**: the unit walks there under a goal
  (`GoalKind.ActivateIdol`) and wakes it on arrival, the loot-errand pattern. First come,
  first served: an idol woken by someone else dissolves the errand.
- **Art:** caches `SM_Gen_Prop_Chest_01`, idols `SM_Gen_Prop_Statue_05`, as tile recipes
  in the prod client's dressing system: one prop, no fence, path or yard tint, nature
  scatter left alone.

## Update 2026-09-24 — the chart in the world: the fog shines gold

- **The user's call:** rather than a pin, the fog over a charted find **shines slightly
  gold** (ember red over a bandit camp). The per-tile glow map (`ChartGlow`) is read once
  per pixel at the end of the fog march (measured first: about 0.01 ms at the fog's
  half resolution, skipped while nothing is charted). Where the tile is in live sight
  there is no fog, so no glow, and the real thing is drawn.
- **The Tab view** switches the fog off, so there a **chip** with the scout's hint marks
  each find; clicking it selects the tile.
- **The chart is the salvage handle** (`docs/salvage.md`): select the tile, or drag from
  it to your storehouse, and the queue's haulers bring everything home.
