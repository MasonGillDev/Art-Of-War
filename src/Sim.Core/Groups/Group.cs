namespace Sim.Core.Groups;

// M5 — A group of units that moves as one at the pace of the slowest
// member. First-class entity on GameWorld.Groups; members are still Unit
// instances with Unit.GroupId set.
//
// The group is the orchestrator (it owns the path and the next-event
// anchor); the members are what actually exist on tiles. When a group
// moves, all members' positions update synchronously in a single
// GroupArrivalEvent — they enter the next tile together.
//
// Lifecycle:
//   FormGroupIntent → State = Forming, members walk to RendezvousTile.
//   Each member's MoveArrivalEvent at the rendezvous decrements
//   PendingArrivals. When zero, State → Idle.
//   MoveGroupIntent on Idle → State = Moving; on Moving → epoch bumps,
//   new path takes over.
//   DisbandGroupIntent (any state) → members go solo, group removed.
//
// In-flight anchors mirror Unit's M4 anchors so Snapshot+RegenerateQueue
// reconstruct the queue from state alone. See docs/architecture.md §2.8.
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

    // ---- M4 in-flight movement anchor (mirror of Unit's) ----
    public List<TileCoord>? PathRemaining { get; set; }
    public TileCoord? PathFinalDest { get; set; }
    public long? NextArrivalTick { get; set; }
    public long? NextArrivalSeq  { get; set; }

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
