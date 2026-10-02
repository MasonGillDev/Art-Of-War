namespace Sim.Core.Groups;

// Append-only enum (serialized into snapshots).
//   Forming   — members are walking to the rendezvous tile. Cannot accept
//               MoveGroupIntent; can be Disbanded.
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
