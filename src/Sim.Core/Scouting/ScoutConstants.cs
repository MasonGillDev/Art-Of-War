namespace Sim.Core.Scouting;

// M20 Phase 3 — dispatch knobs. Sim balance constants (a scout's leash), so
// tests derive from them per the standing convention.
public static class ScoutConstants
{
    // A single dispatch may not name more than this many waypoints — a guard
    // rail against an unbounded patrol path bloating the snapshot, not a
    // gameplay cap players will feel in normal use.
    public const int MaxWaypoints = 16;
}
