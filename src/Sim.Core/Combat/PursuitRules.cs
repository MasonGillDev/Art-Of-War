using Sim.Core.Movement;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Core.Combat;

// M29 — the chase (docs/patrols.md).
//
// ONE TILE AT A TIME. Step() commits the walk up to the next tile toward the
// target's CURRENT position and returns; the end of that walk calls it again.
// That is what makes it a pursuit rather than a walk to where the target used
// to be — and it costs one path search per tile per pursuer, which is affordable
// because only an engaged patrol pays it.
//
// Committing one tile also means every pursuit arrival is a FINAL arrival, so
// the re-entry point is the walk's existing Walk.DispatchOnArrival — no new
// event type, no second scheduling path.
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
        // Co-location IS contact: the tile entry already ran the combat
        // trigger for this tile before dispatching here, so the fight is
        // scheduled and the pin has landed. The chase's job is done.
        if (pursuer.Position == target.Position)
        {
            Release(pursuer);
            return;
        }

        // ---- 4. PINNED IN A FIGHT ----
        // The pursuer stands on a contested tile — the engagement pin has
        // cancelled its movement precisely so a force cannot walk THROUGH a
        // hostile one. Stepping again here would march it straight back out
        // of the fight and defeat the pin.
        //
        // The target is NOT here (rule 3 above took the catch), so this is a
        // BYSTANDER fight: a third party stood on the hop. The chase is
        // SUSPENDED, not ended — the anchor stays, no leg is issued, and
        // CombatRules.ResumeInterrupted re-enters Step the tick this tile's
        // combat ends, taking the next hop toward wherever the target is by
        // then (or releasing on leash / target gone, by the rules below).
        // This used to release, on the reasoning that nothing would re-enter
        // Step once the fight resolved; the un-pin is exactly that re-entry
        // (docs/combat-pin-strands-hauls.md). Every fight ends — by a death
        // or the no-progress guard — so the anchor can never brick.
        if (pursuer.Board is not null || world.CombatStates.ContainsKey(pursuer.Position))
            return;

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
        // BeginMove plans the whole walk, then we cut it at the first tile it enters so
        // the next arrival re-plans against wherever the target has moved to. Cutting
        // AFTER planning (rather than pathing to an adjacent tile) keeps the leg on a real
        // route, around lakes and walls, so the pursuer doesn't walk face-first into an
        // obstacle every leg. (The activity is set first, as it always was.)
        pursuer.TrySetActivity(Activity.Moving);
        MoveIntent.BeginMove(sim, pursuer, target.Position);

        if (!pursuer.IsWalking)
        {
            // No route (walled off, another island). Not a failure state —
            // just an uncatchable target. Fail clean.
            Release(pursuer);
            return;
        }

        // The leg ends on entering the next tile, and its PathFinalDest is that tile: the
        // walk's end then reads as a final arrival and comes back here (Walk.DispatchOnArrival).
        Sim.Core.Movement.Walk.EndAtNextTile(pursuer);
    }

    // THE ONLY exit. Clears the anchor and the movement it owned, and parks
    // the unit Idle so selectors and the automation driver see a free body
    // again.
    public static void Release(Unit pursuer)
    {
        pursuer.Pursuit = null;
        Sim.Core.Movement.Walk.Stop(pursuer);
        pursuer.TrySetActivity(Activity.Idle);
    }

    public static int Chebyshev(TileCoord a, TileCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
