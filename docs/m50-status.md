# M50 status: march modes and saved formations

Spec: `docs/m50-march-formations-spec.md`. Built 2026-10-02 in one phase.

## What was built

- **March modes** (`SetGroupMarchModeIntent`):
  - **Column** is the default. Members hold their place in the formation (saved, or four abreast):
    each row is anchored on the trail at its depth, and the facing is the path's dominant direction
    over 4 tiles, so it doesn't flip on staircases.
  - **Single file:** everyone on the leader's exact trail.
  - Putting a group on a route switches it to single file. Setting an army sets every company.
- **The beat** lands, advances, then plans. The leader waits only for a member more than 2 subtiles
  from its spot (the elastic gate). The beat lasts the slowest step being taken. Each member's
  planned step goes on the wire as its step in flight, **so the client glides marching members
  like solo walkers.** This was the main source of the jitter: column members used to snap.
- **Saved formations:**
  - `ArrangeGroupMemberIntent`: in the world, within 4 tiles; onto a member's spot, the two swap.
  - `SaveGroupFormationIntent`: a leader, a front side, and offsets in a front-is-north frame.
  - `ClearGroupFormationIntent` goes back to the default.
- **Slot filling** (`Formations.Assign`): own unit, then same role, then anyone; gaps close from the
  back; newcomers fall in at the rear.
- **A muster forms up in the saved formation** (leader on the anchor). A Column march **stops
  centred on the destination**, facing the way it came.
- **Wire:** `GroupDto.MarchMode`, `FormationFront`, `FormationSlots`, and the step in flight for
  marching members.
- **Snapshot v53.**

## Results (`MarchFormationTests`)

- **8 in Column over 22 tiles:** within 1.25× one soldier's solo time. At least 95% of member-beats
  on the straight are steps (no stop-start). The shape is held (every member within 1 subtile of its
  spot) in at least 90% of straight beats.
- **Single file:** within 1.2× the solo time (the gap is the caravan's own length), and at least 95%
  of member-beats are steps.
- A diagonal route turns the formation at most twice.
- A saved wedge musters in its shape and arrives turned to face its march.
- Twin run and mid-march restore give the same hash.
- `GroupWireTests.AMarchingMember_ReportsItsStepInFlight` pins the glide data.

## Notes

- **Two test-loop bugs during the build,** both fixed. The budget was measured in `sim.Now`, which
  stops advancing when the sim goes quiet, and macOS has no `timeout` command. Loop budgets now count
  iterations, and test runs use `--blame-hang-timeout`.
- **A stray test host from the first hung run** (pid 64533) was left spinning, and the user was asked
  to kill it.
