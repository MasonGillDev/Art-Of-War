namespace Sim.Core.Groups;

// M5 — A group of units that moves as one at the pace of the slowest
// member. First-class entity on GameWorld.Groups; members are still Unit
// instances with Unit.GroupId set.
//
// The group is the orchestrator (it owns the destination and the count of
// members still walking); the members are what actually exist on tiles, and each
// walks its own subtile route (M43).
//
// Lifecycle:
//   FormGroupIntent → State = Forming, members walk to RendezvousTile.
//   Each member's MoveArrivalEvent at the rendezvous decrements
//   PendingArrivals. When zero, State → Idle.
//   MoveGroupIntent on Idle → State = Moving; on Moving → epoch bumps,
//   new path takes over.
//   DisbandGroupIntent (any state) → members go solo, group removed.
//
public sealed class Group
{
    public int Id { get; }
    public int OwnerId { get; init; } = 0;

    // Sorted set keeps snapshot canonicalization order-stable.
    public SortedSet<int> Members { get; } = new();

    // Where the group is "at" right now. While Moving, updates per arrival.
    // While Forming, holds the rendezvous tile (where members are walking to).
    public TileCoord Position { get; set; }

    public GroupState State { get; set; } = GroupState.Idle;

    // ---- Forming integrity state ----
    // Non-null only while State == Forming; nulled out on transition to Idle.
    public TileCoord? RendezvousTile { get; set; }
    // Members who haven't yet arrived at the rendezvous. Decrements as each
    // off-rendezvous member's MoveArrivalEvent reaches RendezvousTile.
    public int PendingArrivals { get; set; }

    // ---- Movement (M43) ----
    // A group moves as its members' walks (each member walks its own subtile route
    // to the ordered tile). PathFinalDest is the ordered tile while Moving; the
    // group is Idle again when the last walking member finishes (PendingArrivals).
    public TileCoord? PathFinalDest { get; set; }

    // Monotonic counter bumped on every MoveGroupIntent.Resolve and on
    // DisbandGroupIntent.Resolve. Live GroupArrivalEvents carry the epoch
    // they were scheduled with and fence on mismatch — same M2 pattern as
    // Unit.AssignmentEpoch.
    public byte MovementEpoch { get; private set; }

    public Group(int id) { Id = id; }

    internal void BumpEpoch() { unchecked { MovementEpoch++; } }

    // Restore-only. Used by Snapshot.Restore to rebuild the epoch without
    // running through BumpEpoch's increment logic.
    internal void RestoreMovementEpoch(byte epoch) => MovementEpoch = epoch;
}
