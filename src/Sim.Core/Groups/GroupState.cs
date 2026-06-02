namespace Sim.Core.Groups;

// Append-only enum (serialized into snapshots).
//   Forming — the group exists but members are walking to the rendezvous
//             tile. Cannot accept MoveGroupIntent; can be Disbanded.
//   Idle    — all members present at the group's tile; movable.
//   Moving  — group-arrival chain in flight to PathFinalDest.
public enum GroupState : byte
{
    Forming = 1,
    Idle    = 2,
    Moving  = 3,
}
