# Combat Engagement Pin

## Decision

When `CombatTrigger.MaybeBeginCombatOnTile` detects a hostile pair on a tile,
it **cancels the committed movement of every belligerent on the tile** — solo
path anchors, group path anchors, and the corresponding fence epoch — before
returning. A force lands on a contested tile and stays there to fight; it does
not march through. Pinning runs on combat START *and* on every later arrival
into the same active fight, so reinforcements that walk in also stop.

Neutrals (units whose owner has no hostile counterpart present) are **not**
pinned and pass through normally.

## Why

### Why a pin at all

Without it, a unit landing on a contested tile triggers combat in
`MoveArrivalEvent.Apply`, and then immediately schedules the next hop. If the
next hop's cost is less than one `RoundIntervalTicks`, the unit walks off the
tile *before round 1 fires* and takes zero damage. On a high-condition road —
which the design explicitly makes public so anyone, including a raider, can use
it (§8.6, `docs/movement-cost.md`) — that's the normal case. A "blockade" can
be walked through for free. Supply-line cutting (`persistent-rts-design.md`
§8.6: "cutting a high-traffic supply line compounds") has no teeth.

The pin closes this. It is the **engagement** half of the trigger that was
already there — co-location was always meant to be a fight, not a pass-through
with a damage opportunity.

### Why fence via epoch bump, not by editing the queued event

`Unit.AssignmentEpoch` and `Group.MovementEpoch` already exist for exactly
this shape: a queued `MoveArrivalEvent` / `GroupArrivalEvent` carries the epoch
it was scheduled with and no-ops on fire if the unit/group's current epoch has
moved on. The pin clears the in-flight path anchors *and* bumps the epoch, so
the already-queued next hop fences cleanly without the pin having to reach
into the priority queue. Same primitive as `MoveIntent.Resolve` retasking a
busy unit, just driven by combat rather than by the player.

### Why pin only belligerents, not everyone on the tile

A unit is pinned only if *its* owner has a hostile counterpart on the tile.
Neutrals and allies passing through a fight remain free. This matches §10
(neutral = paths can cross without auto-combat) and avoids the cascade where a
single hostile pair locks every passer-by. With N owners on a tile and
asymmetric hostility (0–1 enemies, 2–3 enemies, no cross), both pairs pin
independently and both fights compose into one `CombatRoundEvent` round (it
already sums damage from hostile counterparts only).

### Why also pin on reinforcement (not just combat start)

If `MaybeBeginCombatOnTile` early-returned on an already-contested tile (the
prior shape), a reinforcement that marched in would inherit the bug the pin
was introduced to fix — it would join the fight just long enough to walk
through. The trigger now does the owners-scan and the pin **before** the
contested-tile short-circuit, so reinforcement gets pinned, then composes into
the next round's re-gather (the established M7 reinforcement story).

### Side fixes that came along for free

- The owners-scan in `MaybeBeginCombatOnTile` now filters embarked passengers
  (`!u.IsEmbarked`), matching `CombatRules.GatherForcesOnTile`. Without this,
  a hostile boat docking with embarked passengers from another faction would
  write a phantom `CombatStates` entry that the next round would immediately
  clear.
- A no-progress guard in `CombatRoundEvent.Apply` ends combat when no
  belligerent can deal positive damage. The pin now reliably co-locates
  power-0 forces (e.g. two empty boats — `Boat.BasePower = 0` per
  `UnitCombatCatalog.cs`), which otherwise would loop forever rescheduling
  zero-damage rounds. Combat with a positive-power side proceeds unchanged.

## Rejected alternatives

- **Owner-aware execution cost (enemy tiles cost more to traverse).** Already
  considered and rejected in `docs/movement-cost.md` ("Owner-aware execution
  costs would mean enemies pass through your formations for free, which
  doesn't match the spatial intuition"). Slowing a traversal is not stopping
  it; the blockade requirement is binary, not graduated.
- **Trigger combat only on full-stop arrivals.** Would mean a player has to
  manually stop a hauler on a contested tile to fight. The trigger is
  presence-gated, not intention-gated, by §9.1; this preserves that.
- **Pin everyone on the tile, including neutrals.** Cleaner code, but violates
  the diplomacy contract (neutrals cross without auto-engagement).

## Known limitations

- **Winner stops too.** A force that wins still has its committed path
  cancelled. After combat the player must re-issue a move. Conventional RTS
  behavior; an "auto-resume surviving path" pass is a clean follow-up — likely
  a stash of `PathFinalDest` on the `CombatState` per-owner-survivor at pin
  time, re-armed in the `CombatStates.Remove` path of `CombatRoundEvent`.
  Deferred until play surfaces a need.
- **Pinned Forming-group walker leaves the group stuck.** If a `Forming`
  group's member is pinned mid-route to the rendezvous, it never decrements
  the group's `PendingArrivals` (only
  `MoveArrivalEvent.DispatchOnFinalArrival` on real arrival at the rendezvous
  does that). The group stays `Forming` forever; the player must
  `DisbandGroupIntent`. This shape pre-exists in the death pipeline
  (`CombatRules.OnUnitDeath` has the same gap) and should be addressed there
  too as a single fix.
- **Haulers keep their `HaulPlan`.** A pinned hauler retains its plan but has
  no path; pending pickup/deposit events fence on the bumped epoch. The
  hauler sits with cargo until the player re-tasks. Functional, not pretty.

## Future expansion

The pin is the prerequisite for everything that wants combat to actually
intercept movement:

- **Ranged-from-adjacent (§9.4).** When `GatherForcesOnTile` becomes
  `GatherForcesNearTile`, the pin's owners-scan can grow the same neighborhood
  step. Pinning a unit one tile off from a ranged attacker is the natural
  formalization of "you cannot run past archers on the next tile."
- **Gates and walls.** A friendly gate on the contested tile would pin only
  enemies (Gate ownership says "your forces cross freely"), implemented by
  adding a per-belligerent allow-through check inside `PinBelligerents`.
- **Capture-on-pin.** A wounded enemy hauler pinned on your tile could
  surrender rather than fight. The pin gives that mechanic a hook (the moment
  of forced stop) without needing a separate event.
- **Auto-resume surviving path.** As above — one extra field on `CombatState`;
  zero changes to the round event.
