namespace Sim.Core.World;

// Append-only enum (serialized).
public enum Activity : byte
{
    Idle = 0,
    Moving = 1,
    Working = 2,
    Building = 3,
    Hauling = 4,
    // M30 — the body is committed to a goal whose precondition hasn't been
    // met yet: standing at the target, not working, not haulable. A
    // NON-IDLE state on purpose — every solo intent gates on Idle, so a
    // waiting unit is protected from silent retasking by every existing
    // intent without touching any of them. docs/goal-shaped-intents.md.
    Waiting = 5,
}

// The activity state machine for units.
//
// Idle is the rest state. Every non-Idle activity is owned by a single intent
// chain — the intent that put the unit there is the only thing entitled to
// transition it out. The legal transitions table below encodes that:
//   * Any non-Idle state can return to Idle (the owning intent completed or
//     was rejected).
//   * From Idle, an intent can move the unit into any single activity.
//   * No direct activity-to-activity hops. To re-task a Working unit you must
//     first Unassign it (Working → Idle), then issue the new intent.
//
// Keeping this table-explicit (rather than a tangle of inline checks in every
// intent) is what catches "why is this unit stuck in Hauling forever" bugs
// before they ship.
public static class ActivityTransitions
{
    public static bool CanTransition(Activity from, Activity to)
    {
        if (from == to) return true;             // no-op is always legal
        if (to == Activity.Idle) return true;    // anything → Idle
        if (from == Activity.Idle) return true;  // Idle → anything
        return false;                            // no direct non-Idle hops
    }
}
