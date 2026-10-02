namespace Sim.Core.Groups;

// Append-only enum (serialized into snapshots).
//   Forming   — mustering: members are walking to their places around the anchor
//               (or finishing a job first). A move is taken: who has arrived marches.
//   Idle      — all members present at the group's tile; movable.
//   Moving    — members walking to PathFinalDest.
//   Dismissed — (M46) the group stands down: its members are free and do their own
//               work. Not under command (GroupRules.UnderCommand).
public enum GroupState : byte
{
    Forming   = 1,
    Idle      = 2,
    Moving    = 3,
    Dismissed = 4,
}
