# Salvage: the haul queue carries home what your scouts found

## The decision

A **salvage job** is a third kind of haul job. It carries **everything** from a place
that is nobody's (a loot cache, a ruin's spill, a burned bandit camp's hoard, any ground
pile) to one of the player's buildings.

- The player may name a source only if their **chart** says something is there (a scout
  came home and reported it), or they can **see** it right now.
- The queue keeps a small **crew** of haulers on the job (2 from the client; 1–6
  allowed). Each hauler takes a full, mixed load of whatever is there.
- The job ends when a hauler **arrives and finds nothing left**, the same moment that
  hauler's eyes strike the chart entry.

In the prod client, the chart is the handle:
- **Drag** from a charted tile (or a cache or pile in sight) to your storehouse.
- **Or** select the tile, choose "Salvage — bring it all home…", and click the building.
- In the world, the **fog glows gold** over a charted find (ember red over a camp).
- In the Tab view, where the fog is off, a **chip** marks the find instead.

## Why

- **The user's call (2026-09-24):** "may the haul queue carry from things you don't
  own?" Yes. It is the vision's first law of automation applied: **choosing** to go and
  get it (allocation) stays the player's; **carrying** it (maintenance) is the machine's.
  Before this, looting a far cache was one order per resource, 5 at a time for a scout:
  a 40-ore cache was 8 round trips by hand.
- **Knowledge comes from presence.** The source is gated on what the player knows (a
  known chart entry, or live sight), never on what is really there. The driver **never
  reads the source**: salvage has no "need", only a crew. Nothing about the source's
  contents or fate reaches the player until a hauler stands on it, so the job cannot
  leak a far cache's state. The honest cost: after a cache is emptied, up to one crew's
  worth of haulers may walk out and come back empty. That walk is how they found out.
- **Nobody's only.** A player's own buildings use ordinary jobs; another faction's
  buildings are theirs (stealing is a raid, not logistics). Salvage accepts a cache,
  rubble with a spill on it, or bare ground with a pile.

### Alternatives considered

- **A salvage job that knows the contents and stops exactly when empty.** Lost: it
  leaks hidden state to the player (a job vanishing tells you a far cache was
  emptied), and it sends a precise number of haulers the player could never have
  worked out.
- **Ending the job when the cache is removed (whoever emptied it).** Lost for the same
  reason. The job ends on arrival.
- **A per-resource salvage ("bring the ore").** Lost: the player does not know what is
  there (the chart's hint is faint on purpose). "Everything" is the only honest order.
- **A permanent pin in the world for charted tiles.** Kept as a fallback, but the user
  preferred the fog shining. A pin is a UI object; a glow is the fog remembering. The
  cost was measured first: one texture read per pixel at the fog's half resolution
  (about 0.01 ms), skipped entirely while nothing is charted.

## Shape

- **Sim.Core:**
  - `HaulJobKind.Salvage` (3); `HaulingConstants.MaxSalvageCrew` (6).
  - `Salvage.Blocker` (the source rule) and `Salvage.TakeAll` (a mixed load from the
    cache, then the pile).
  - `SetHaulJobIntent` validates salvage.
  - `HaulIntent` lets a salvage trip name no resource and skips the "is there a source"
    check (the hauler goes to look).
  - `HaulPickupEvent` takes everything, or ends the job on an empty arrival.
  - `HaulDepositEvent` delivers the whole mixed load.
- **Sim.Server:** `HaulingDriver` keeps up to the crew at work (nearest free hauler to
  the source, one per turn, re-queued like any job); its report counts haulers, not
  amounts.
- **Prod client:**
  - `SalvageRules.CanSalvage` (the same rule, answered from the player's view).
  - `JobPlanner.Salvaging`: a drag from a salvage source sends at once on release.
  - `OrderIssuer.SetSalvageJob`.
  - "Salvage — bring it all home…" on the wheel of a tile, a cache, or a pile.
  - The tile bubble shows the chart entry: its hint, when it was seen or found gone.
  - Tab-view chart chips; Haul page and bubble rows.
  - `ChartGlow` plus the fog shader's gold/ember tint.

## Acceptance tests

`SalvageJobTests`:
- an uncharted, unseen cache is refused; a charted or seen one is accepted;
- salvage names no resource, stays within its crew, and never takes a building;
- a charted cache is carried home whole, then the job ends and the chart is struck;
- a pile in plain sight is carried home;
- a cache someone else emptied ends the job when the haulers get there;
- the queue keeps exactly the crew at work.

`IntentJsonTests.M40_TheClientsSalvagePayload_Parses` pins the client's bytes.

## Future expansion

- **Salvage from a razed kingdom's vault** needs nothing new (a pile on rubble), and
  makes charting ruins the next step.
- **A crew size choice** on the gesture: today it is fixed at 2 client-side.
- **Carts and roads** already make far salvage cheaper (50 cargo; up to 66% faster). A
  forward stockpile near a far find is the player's own logistics answer.
- **Escorts:** salvage haulers walk alone into bandit country; a route with soldiers
  (M36) is the escorted version.
