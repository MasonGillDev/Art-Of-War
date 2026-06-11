# Bandits — an NPC faction whose brain lives outside the sim

## The decision

Bandits are a **reserved faction id** (`BanditConstants.OwnerId = -1`)
hard-wired hostile to everyone, whose units are spawned/despawned only
**in tiles no player can see**, and whose behavior comes from a
**server-side driver** (`Sim.Server/Bandits/BanditDriver`) that reads
world state through pure-read walls and acts exclusively by submitting
ordinary durable intents. The sim contains zero AI; the sim contains
only the *laws* bandits obey.

## Why

### Why a faction, not a new entity type

Everything an antagonist needs already existed and fired on faction
hostility: M7 combat triggers on hostile co-location, capture-on-death
drops cargo, fog hides anyone, movement/cargo/death pipelines are
owner-agnostic. Modeling bandits as a faction bought the entire
behavioral surface for three small exemptions wired off one id:
always-hostile (an `AreHostile` special case — no relationship rows, no
war telegraph, no peace), exempt-from-civilization (no castle → famine
machinery never engages; no lifespan roll → death by sword, not
calendar), and no remembered map (`Sight.Reveal` skips the faction —
live sight only, which both keeps snapshots lean and makes bandit
knowledge honest).

The losing alternative — a bespoke "mob" entity with its own movement/
combat handling — would have duplicated every pipeline it touched and
needed its own persistence story. Ruled out on sight.

### Why the brain is OUTSIDE the sim (the load-bearing call)

Two places the AI could live:

- **Inside the sim** (a scheduled BanditBrainEvent): deterministic by
  construction, but it writes *policy* into the event queue. Every
  balance tweak to bandit behavior would change replay semantics, bloat
  the determinism audit, and couple "what bandits feel like" to the
  snapshot format.
- **Outside the sim** (chosen): a driver that reads state and submits
  intents like any player. Determinism is preserved by the
  intents-as-truth architecture (§3): the driver's *decisions* land in
  the durable log; crash recovery replays the decisions without the
  brain; the brain itself may be arbitrarily dumb, smart, or stateful
  with zero determinism obligations.

The headline test pins this:
`BanditDriverTests.Headline_ReplayFromIntentLog_HashesMatch` runs a full
raid live (driver thinking every game-hour), then replays the captured
intent log — round-tripped through the durable JSON registry — into a
fresh sim with NO driver, and the snapshots hash-equal. **This is the
architectural template for the player automation layer**: standing
orders will be exactly this shape (watch state → submit intents from
outside), and M16 proves the shape sound before that milestone starts.

One subtlety worth recording: replay must **interleave submissions
chronologically** (run to each intent's tick, then submit), because a
live driver submits at tick T *after* T's events have run — front-
loading the whole log gives intents earlier Seqs and reorders same-tick
execution. The test encodes the correct discipline.

### Why spawn-in-darkness + a distance floor

"Spawns only where no player can see" turns the design pillar
*knowledge comes from presence* into a defense mechanic: towers, patrol
routes, and scouts physically push the spawn frontier back — vision IS
safety. The Chebyshev `MinSpawnDistance` floor (10 tiles = 10 km) exists
because **economic structures cast no vision**: without it a party could
materialize on top of an unwatched lumber camp. It also guarantees every
raid is a march, never a jump-scare — and since max vision (Tower, 7) is
below the floor, the distance check currently subsumes the darkness
check; both are validated anyway because vision radii are balance knobs.

Validation happens **at resolve time**, so a scout walking into view
while the spawn intent is in flight kills the spawn. Symmetrically,
despawn-in-darkness rejects while ANY unit of the party is visible:
chasing a fleeing party with a scout literally keeps the stolen goods
recoverable. Lose sight of them and the loot leaves the world — that is
the punishment for ignoring a raid.

### Why stealing is `LoadCargoIntent`, and why it's player-usable

Stealing needed "fill cargo from the tile you stand on, no destination
leg" — the missing mirror of `UnloadCargoIntent`, so it shipped as a
general atom rather than a bandit-only verb. Source ownership is
deliberately unchecked, matching `HaulIntent`'s long-deferred stance,
now pinned as **the raiding economy by design**
(docs/intent-authorization.md, Update 2026-06-11): hostile units
looting an unguarded buffer is gameplay; standing there alive is
combat's problem. Bonus discovered in testing: loading from a dormant
extractor frees buffer space through the same Phase-D hook hauls use —
a bandit robbing your camp puts the surviving crew back to work.

### Why parties-only (camps deferred), per the user's call

Camps (destroyable spawn structures with hoards) are the richer loop —
they give exploration a payoff and the military an objective — but they
need the game's first structure-destruction mechanic. Parties-only
shipped first; the despawn valve and driver FSM were built so camps slot
in by swapping the flee destination ("nearest dark tile" → "home camp")
and the spawn site source (driver-picked → camp). Nearly nothing gets
scrapped when camps land.

## Mechanics shipped

- `UnitRole.Bandit` (25 HP / 3 power — between Soldier and citizen;
  cargo 15 — stings, can't empty a stockpile; sight 6 — raiders hunt by
  eye, and the driver targets only what a party can SEE: bandits get
  fog too, so deep dark territory is more dangerous than a well-lit
  core).
- Driver FSM: **Ambush** (sit in the fog until something walks into
  sight — the accidental-encounter spawn the user asked for; every
  `AmbusherEvery`-th spawn), **Raid** (march at the nearest stealable
  structure in sight — extractor buffers first, then any stocked
  storage, castles included; wander when blind; flee when full or when
  carrying with nothing left), **Flee** (run dark, despawn, loot gone).
- Prosperity-scaled pressure: live-party target =
  `playerStructures / StructuresPerParty`, capped at `MaxLiveParties` —
  sprawl attracts wolves. All knobs in `BanditConfig` (Sim.Server);
  sim-side law (darkness, distance floor, party-size clamp, stats) in
  Sim.Core constants/catalogs. `--bandits 0` disables the driver.
- Wire: bandit faction omitted from the diplomacy faction list (no
  doomed war/peace UI); bandit units render like any enemy when
  visible; client toasts on the rising edge of visible bandits.

## Future expansion

- **Bandit camps** — the designed follow-up: a structure in the fog
  spawning parties and hoarding stolen goods; find it, kill the
  defenders, raze it (the first scoped structure-destruction mechanic),
  recover the hoard. Slots into the existing faction + driver.
- **Structure damage** — bandits currently kill workers and steal but
  never harm buildings (a stockless structure is scenery to the FSM).
  Burning farms is a sieges-era question.
- **Notifications** — "bandits sighted" is a client-side view diff
  today; M10 push notifications can hook the same rising edge
  server-side.
- **Smarter brains** — the driver is deliberately simple (greedy
  nearest-target, random wander). Because it's outside the sim, it can
  be made arbitrarily clever — pack tactics, road ambushes, fleeing
  from soldiers — without touching determinism or the snapshot format.
- **Wildlife/other NPC factions** — additional reserved ids with their
  own drivers; the faction plumbing generalizes (a `IsNpcFaction`
  predicate the day there are two).

## References

- `docs/m16-bandits-spec.md` — the milestone spec this implements.
- `docs/intent-authorization.md` (Update 2026-06-11) — server-internal
  intents; raiding-by-design.
- `docs/combat-model.md` — the hostile-co-location trigger and
  capture-on-death pipeline bandits ride.
- `docs/architecture.md` §2 (pure-read walls), §3 (intents-as-truth —
  why out-of-sim AI is determinism-safe).
- `persistent-rts-design.md` §1 — "knowledge comes from presence," now
  also a defense mechanic.
