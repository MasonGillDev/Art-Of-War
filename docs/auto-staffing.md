# Auto-staffing: every building carries the number of workers it wants

**Status: decided 2026-10-02. No code yet; the spec is
`docs/m48-auto-staffing-spec.md`.**

## The decision

Every structure that takes workers carries a **desired-worker count**, set
when the site is placed (default: the worker cap) and editable in place.
The kingdom keeps the building staffed to that number by itself. A
construction site always wants exactly its required builder count.

The mechanism is the existing `Staff` recipe of the automation substrate
(`docs/automation-substrate.md`), made **implicit**: placing a site installs
one staffing order for that tile, which lives and dies with the building.
The player never authors a Staff order and never sends a worker to a
building by hand unless they want to. The per-player order count was
removed the same day so that these orders can never be capped
(`docs/automation-layers.md`, update 2026-10-02).

The user's words for the goal: a dead farmer must not take the kingdom
down twenty minutes later because nobody noticed the farm went quiet.

## Why

### What exists already

- `Staff` (`SubstrateDriver.RunErrand`): trigger `workers < target`, pull a
  same-role dormant unit, walk it to the extractor, `AssignWorkersIntent`.
  `Staff_RefillsTheFieldWhenAWorkerDies` pins the death case.
- Death retires the slot (`CombatRules.OnUnitDeath` step 2b through
  `WorkAssignment.Release`), so the trigger sees the vacancy without any
  death event.
- The Claims Ledger, journal, "Short of hands" dashboard state, fog
  fairness and the replay headline all come with the recipe.

So the gap was not a missing system. It was that a Staff order had to be
authored per building on the Machine page, and nobody does that for every
farm.

### The alternative that lost: react on death

"When a unit dies, find an idle unit of the same role and send it to the
dead unit's building; if none, train one." It lost on three grounds:

1. **It is a global vacancy-watcher**, which `docs/automation-substrate.md`
   already ruled out as a "mini-Homesteader": cross-cutting, illegible,
   judgment-bearing. Replacement must be a property each order carries.
2. **Edge-triggered misses most vacancies.** Workers leave slots by manual
   unassign, retask-on-assign, muster into a group, starvation on shift and
   re-homing, not only by combat death. A level trigger (`workers <
   target`) catches every cause with one rule.
3. **There is no death event to hang it on.** Death removes the unit from
   `world.Units`; the server finds out by diffing (`GraveTracker`). Adding
   a Core death hook that picks participants would also break the rule
   that the sim never chooses who does a job (`BuildIntent.cs`,
   `GoalPlan.cs`, `docs/goal-shaped-intents.md`: "auto-replacement of a
   dead participant is the automation-tier upgrade").

### Why a desired count on the building, not "fill every slot"

Filling every slot is simpler but takes a decision away: a player who wants
one miner on a two-slot mine, or nobody on a farm they are about to
demolish, has no way to say so. A number per building keeps the control
and costs one stepper at placement. Zero means "never auto-staff this".
The staffing order only ever pulls **below** the target; a player who
manually puts more workers on a building than the target keeps them.

### Why one implicit order per tile, created at placement

Three shapes were considered:

- **Auto-create a real `Order` at build completion** (chosen, with one
  change: create it at placement). Reuses the ledger, journal, wire and
  dashboard verbatim. The building's context card already lists the
  orders that touch it. Creating it at placement rather than completion
  lets the same order cover the build itself: while a site stands on the
  tile the order runs a **Build** errand (target = required builders, role
  = Builder); once the extractor stands, it runs **Staff** (target =
  desired workers). One record for the building's whole life, no handover.
- **Derive the behaviour from a `DesiredWorkers` field with no record.**
  Cleaner state, but the Claims Ledger, the journal and `HeldUnits` are all
  keyed by order id, so staffing would need a second identity scheme for
  claims and a second status channel to the client. Rejected as a parallel
  system.
- **A `DesiredWorkers` field plus a record.** Two sources of truth for one
  number. Rejected; the number lives on the order, and the building reads
  it through the order.

### Why training stays a separate (implicit) quota

The substrate's law 3 is "compose through the world, never order-to-order",
and `Staff` deliberately does not train a substitute. The user wants the
kingdom to *try* to train when no worker of the trade is free. The answer
that keeps the law is a second implicit order per role: a Train quota whose
target is the **sum of desired workers** over the player's buildings that
prefer that role. It is arithmetic, not judgment; it pulls only no-role
adults (never retrains a soldier, per the user's original note in
`auttomation.txt`); and it needs a school, which the player still has to
build. Staff and Train still do not know about each other.

### Why not groups yet

Refilling a group is the `Reinforce` recipe, blocked on an add-member
intent that M46 (`docs/m46-groups-spec.md`) introduces. It is the same
thermostat and will be a later row. One interaction is already settled:
a unit mustered out of a slot keeps the slot held (M46), so it still counts
in `Workers` and the staffing order will not double-fill it.

## Future expansion

- **Reinforce for groups** once M46 lands: a per-role crew spec, same
  errand shape.
- **Towers and other manned buildings** (`docs/manned-towers.md`) get a
  worker cap and inherit staffing with no new code.
- **Progression** can gate the default: before a Guildhall (or whatever the
  unlock is) the default desired count could be 0 so the player staffs by
  hand and learns the verb, then earns automation
  (`docs/automation-as-core-game.md`, "Earning the tools").
- **Conscription** (`ConscriptOptIn`) stays deferred: taking a working unit
  needs a staged unassign nothing else needs.
- **The AI** keeps its own staffing (`ThinkContext.StaffExtractor`) for now
  so the Homesteader lab is untouched; moving it onto implicit orders is a
  later cleanup.

## Acceptance tests (pinned in the spec)

- A farmer dies on shift; a dormant farmer is walked in and working within
  one think, with no player intent.
- A site placed with a desired count of 1 on a two-slot mine ends with one
  worker and the order Resting.
- Desired count 0: nobody is ever sent.
- No farmer free: the order reports Short of hands; with a school and an
  untrained adult, the implicit Train quota trains one and Staff then
  places them, with no order knowing the other exists.
- Razing, demolishing, clearing rubble or completing a build never leaves
  an orphan order or an orphan claim.
- Driver on vs. intent-log replay with the driver off: equal
  `Snapshot.Hash`.
