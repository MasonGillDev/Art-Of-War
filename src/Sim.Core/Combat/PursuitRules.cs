using Sim.Core.Movement;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Core.Combat;

// M29 — the chase (docs/patrols.md).
//
// ONE HOP AT A TIME. Step() commits a single tile of movement toward the
// target's CURRENT position and returns; the arrival handler calls it again.
// That is what makes it a pursuit rather than a walk to where the target used
// to be — and it costs one A* per hop per pursuer, which is affordable because
// only an engaged patrol pays it.
//
// Committing one hop also means every pursuit arrival is a FINAL arrival, so
// the re-entry point is MoveArrivalEvent's existing DispatchOnFinalArrival —
// no new event type, no second scheduling path.
//
// FAIL CLEAN ON EVERY EXIT PATH. Release() is the only way this anchor ever
// clears, and every terminating condition routes through it. The M28/M29 haul
// lesson (docs/automation-substrate.md): an anchor left set on a unit that has
// stopped moving is a permanent brick — every consumer that asks "is this unit
// free?" reads the anchor, not the activity flag, so a stranded Pursuit would
// retire the body for the rest of the game.
public static class PursuitRules
{
    // Take one step of the chase, or end it. Safe to call on any unit; a unit
    // with no Pursuit anchor is a no-op.
    public static void Step(Simulation sim, Unit pursuer)
    {
        if (pursuer.Pursuit is not { } chase) return;
        var world = sim.World;

        // ---- 1. Target gone (killed, despawned, captured) ----
        if (!world.Units.TryGetValue(chase.TargetUnitId, out var target))
        {
            Release(pursuer);
            return;
        }

        // ---- 2. Target aboard a boat ----
        // Embarked units are off-tile (Position frozen at the dock) and cannot
        // be fought — chasing one would walk to a phantom.
        if (target.IsEmbarked)
        {
            Release(pursuer);
            return;
        }

        // ---- 3. CAUGHT ----
        // Co-location IS contact: MoveArrivalEvent already ran the combat
        // trigger for this tile before dispatching here, so the fight is
        // scheduled and the pin has landed. The chase's job is done.
        if (pursuer.Position == target.Position)
        {
            Release(pursuer);
            return;
        }

        // ---- 4. PINNED IN A FIGHT ----
        // The pursuer walked onto a contested tile — the engagement pin has
        // just cancelled its movement precisely so a force cannot walk THROUGH
        // a hostile one. Stepping again here would march it straight back out
        // of the fight and defeat the pin.
        //
        // Being pinned ENDS the chase rather than suspending it. Suspending
        // would leave a live anchor on a unit with no arrival scheduled — the
        // permanent-brick shape (docs/automation-substrate.md) — because
        // nothing would re-enter Step once the fight resolved. Ending it is
        // also the honest division of labour: the sim owns the fight, and the
        // patrol driver re-evaluates posture on its next think.
        if (world.CombatStates.ContainsKey(pursuer.Position))
        {
            Release(pursuer);
            return;
        }

        // ---- 5. Leash ----
        // Measured from the ROUTE anchor to the TARGET: a runner that breaks
        // the leash is gone, and a patrol can never be walked further than
        // LeashRadius from the line the player drew. 0 = no leash.
        if (chase.LeashRadius > 0
            && Chebyshev(target.Position, chase.LeashTile) > chase.LeashRadius)
        {
            Release(pursuer);
            return;
        }

        // ---- 6. Lost from sight ----
        // THE FOG CONTRACT: you cannot chase what you cannot see. Checked
        // against the PURSUER's own eyes rather than the owner's whole vision
        // network, so a patrol can't be guided by a watchtower on the far side
        // of the kingdom — a chase is a local, physical act.
        if (Chebyshev(pursuer.Position, target.Position) > Sight.RadiusFor(pursuer.Role))
        {
            Release(pursuer);
            return;
        }

        // ---- 7. Step toward them ----
        // BeginMove plans the whole route, then we truncate to its first hop
        // so the next arrival re-plans against wherever the target has moved
        // to. Truncating AFTER planning (rather than pathing to an adjacent
        // tile) keeps the step on a real route — around lakes and walls — so
        // the pursuer doesn't walk face-first into an obstacle every hop.
        // ACTIVITY BEFORE THE MOVE. TrySetActivity bumps AssignmentEpoch on a
        // real change, and ScheduleNextHop stamps the arrival event with the
        // epoch as it schedules — so flipping the flag afterwards fences the
        // very event just created and the chase freezes on its first step
        // (found by the catch test: the pursuer never left its tile).
        pursuer.TrySetActivity(Activity.Moving);
        MoveIntent.BeginMove(sim, pursuer, target.Position);

        if (pursuer.PathRemaining is null || pursuer.PathRemaining.Count == 0)
        {
            // No route (walled off, another island). Not a failure state —
            // just an uncatchable target. Fail clean.
            Release(pursuer);
            return;
        }

        // Keep only the first hop so the next arrival re-plans against wherever
        // the target has moved to. PathFinalDest must match, or the arrival
        // reads as mid-path and continues without consulting the chase.
        var firstHop = pursuer.PathRemaining[0];
        pursuer.PathRemaining = new List<TileCoord> { firstHop };
        pursuer.PathFinalDest = firstHop;
    }

    // THE ONLY exit. Clears the anchor and the movement it owned, and parks
    // the unit Idle so selectors and the automation driver see a free body
    // again.
    public static void Release(Unit pursuer)
    {
        pursuer.Pursuit = null;
        pursuer.PathRemaining = null;
        pursuer.PathFinalDest = null;
        pursuer.NextArrivalTick = null;
        pursuer.NextArrivalSeq = null;
        pursuer.TrySetActivity(Activity.Idle);
    }

    public static int Chebyshev(TileCoord a, TileCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
