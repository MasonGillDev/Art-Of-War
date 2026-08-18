namespace Sim.Core.World;

// M29 — the on-unit PURSUIT anchor (docs/patrols.md).
//
// Same shape and discipline as HaulPlan: durable state on the unit that says
// "this body is chasing someone", from which the arrival handler derives the
// next hop. Two reasons it lives here rather than in the automation driver:
//
//   1. RESOLUTION. The driver thinks once per game-hour; a chase re-evaluated
//      hourly is not a chase. Pursuit re-paths on every tile arrival, which
//      only the sim can do.
//   2. RECOVERY. A chase in progress at snapshot time must survive a restart,
//      and a driverless replay of the intent log must reproduce it exactly.
//      One EngageUnitIntent in the log is the whole chase; everything after
//      unfolds from this anchor.
//
// PURE PURSUIT: the pursuer steps toward the target's CURRENT tile, never a
// predicted one. Interception is tactical AI (docs/automation-as-core-game.md)
// and is deliberately absent — which is why an equal-speed runner escapes.
//
// THE LEASH IS ANCHORED TO THE ROUTE. LeashTile is the patrol's current stop,
// not the tile the chase started on, so a target that hovers at the boundary
// cannot drag the party across the map in leash-sized increments. A patrol can
// never be pulled further than LeashRadius from the line the player drew.
public sealed class Pursuit
{
    public int TargetUnitId { get; init; }

    // The tile the leash measures FROM — the patrol's current stop. For a
    // hand-issued chase this is wherever the player anchored it.
    public TileCoord LeashTile { get; init; }

    // Chebyshev tiles from LeashTile the TARGET may reach before the chase is
    // called off. 0 = no leash (chase while visible) — legal, and the shape a
    // player-issued "run them down" uses.
    public int LeashRadius { get; init; }

    public Pursuit(int targetUnitId, TileCoord leashTile, int leashRadius)
    {
        TargetUnitId = targetUnitId;
        LeashTile = leashTile;
        LeashRadius = leashRadius;
    }
}
