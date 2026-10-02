using Sim.Core.Battlefields;
using Sim.Core.Vision;

namespace Sim.Core.Movement;

// THE one place a unit steps onto a new world tile (M42, docs/subtile-movement.md:
// "one shared tile-entry method"; M43: every walk's step across a tile edge). Everything
// that must happen when a unit enters a tile happens here: the position and the subtile
// it steps onto, the edge it came in by, the dock slip it left, what it now sees, a camp's
// raid leaving, a scout's log, a boat meeting a waiting passenger, a moving group's place.
//
// What it does NOT do: check whether the unit may enter (walls and shapes: the step
// rules), react to who is there (the combat trigger) or what comes next (the walk, the
// errand). Those belong to the caller.
public static class TileEntry
{
    public static void Enter(Simulation sim, Unit unit, TileCoord left, TileCoord to, Subtile? at)
    {
        var world = sim.World;
        unit.Position = to;
        unit.Subtile = at;
        // M41 — the edge it came in by (battles deploy arrivals there; Withdraw
        // goes back out that way).
        unit.EnteredFrom = left;
        unit.EnteredTick = sim.Now;
        // Rest healing: leaving a tile breaks any rest; arriving at an own
        // shelter starts a fresh period (docs/unit-healing.md).
        Sim.Core.Healing.Rest.Interrupt(unit);
        Sim.Core.Healing.Rest.ArmIfDormant(sim, unit);
        // A moving group is "at" the tile its lowest-id member last entered.
        if (unit.GroupId is { } gid && world.Groups.TryGetValue(gid, out var group)
            && group.State == Sim.Core.Groups.GroupState.Moving && group.Members.Count > 0 && group.Members.Min == unit.Id)
            group.Position = to;
        // M12 — dock slip-clear hook: if the tile the unit just left is any
        // dock's slip, that dock re-evaluates its production.
        Sim.Core.Boats.DockArmer.OnUnitLeftTile(sim, left);
        // M3 Phase B: the arrival also reveals the unit's vision radius for its
        // owner. See Vision/Sight.cs.
        Sight.Reveal(world, unit.OwnerId, to, Sight.RadiusFor(unit.Role), sim.Now);
        Sight.AfterReveal(sim, unit.OwnerId, to, Sight.RadiusFor(unit.Role));   // M37/M38
        Sim.Core.Bandits.Camps.OnMoved(world, unit);   // M39 — a camp's raid has left
        // M20: a scouting unit appends what its fresh vision touched to the
        // observation log (THE one write site; a dict lookup for anyone else).
        Sim.Core.Scouting.ScoutObservation.Capture(sim, unit);
        // M30 sweep — a hull finishing a hop alongside a dock boards whoever is
        // standing there waiting for it. Role-gated. docs/goal-shaped-intents.md.
        if (unit.Role == UnitRole.Boat)
            Sim.Core.Boats.EmbarkGoal.OnBoatArrived(sim, unit);
    }
}
