# Citizens haul from the queue

## The decision

Untrained citizens (`UnitRole.None`) of **any age** take trips from the haul queue as an **overflow tier**. They are dispatched
only when no Hauler is free, and they carry their own 5 instead of a
Hauler's 25. Decided by the user on 2026-10-01.

Code: `HaulingDriver.Pool`. Tests: the citizen-tier tests in
`HaulQueueTests`.

## Why

The human opening (`docs/human-opening.md`) starts the player with eleven
untrained citizens and no School. Before this change those citizens could do
nothing: the queue took only Haulers, and every other job needs a trained
role. Idle mouths beside a stalled queue is a bottleneck the player can't
act on until a School stands.

- **Overflow, not equal.** If citizens and Haulers shared one nearest-first
  pool, a citizen standing nearer would win trips a Hauler should take, at a
  fifth of the load. Putting Haulers first keeps training a Hauler worth it.
  The citizen tier only absorbs work the Haulers can't get to.
- **No age gate.** Hauling is what a child does until it's old enough to
  train. Even a one-year-old can carry 5, and once a citizen reaches
  `MinTrainAge` the player should train them. *Rejected:* gating on
  `MinTrainAge`. It was the first cut, and the user turned it down the same
  day because it left exactly the bodies this exists for with nothing to do.
- **Capacity unchanged.** `UnitCargoCatalog` already gave every non-Hauler 5
  ("civilians can lug a small load when asked"). Citizens just get asked now.
- **Rejected: a dedicated "labourer" role or citizen-only jobs.** More rules
  for the same result.

Side effects, accepted:

- A citizen on a trip is not Idle, so training or breeding it is refused
  until the trip lands, as it is for any busy unit. Trips are short. If this
  turns out to bite, the fix is to stop citizen dispatch while a goal is
  pending, not to cancel the trip.
- AI factions never use the haul queue (their logistics run through the
  substrate), so this changes nothing for them or the balance labs.
- The HUD's free-hauler count still counts Haulers only. Citizens are the
  overflow, not the crew, and showing them would need a new wire field.

## Future expansion

- **A wire field for free citizens**, if the HUD should say "no haulers, 3
  citizens helping".
- **A per-job "haulers only" flag**, if some lines (say, ore to a smelter)
  should never take a 5-load body.
- **Other overflow work**, such as helping build or salvage, would follow
  the same two-tier pattern with trained roles first.
