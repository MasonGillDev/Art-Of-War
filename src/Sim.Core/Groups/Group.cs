namespace Sim.Core.Groups;

// A group: a named, lasting body of units (docs/groups-first-class.md, M46). First-class
// entity on GameWorld.Groups; members are still Unit instances with Unit.GroupId set.
//
// A group holds UNITS (a leaf) or other GROUPS (a parent: an army holding its
// companies), never both (Kind). Groups form a tree at most GroupConstants.MaxDepth
// levels deep; a unit is in at most one leaf. A group outlives its members: one whose
// members all die is kept, empty, until DeleteGroupIntent removes it.
//
// The group orchestrates (it owns the destination and the count of members still
// walking); the members are what actually exist on tiles, and each walks its own
// subtile route (M43; M46 Phase C makes the group the walker).
//
// Lifecycle (State):
//   CreateGroupIntent → Dismissed: members are free and do their own work.
//   FormGroupIntent → Forming, members walk to RendezvousTile. Each member's walk
//   ending takes it off PendingArrivals; at zero, Idle.
//   MoveGroupIntent on Idle → Moving; on Moving → epoch bumps, new walks take over.
//   DeleteGroupIntent / DisbandGroupIntent (any state) → members go solo, group removed.
public sealed class Group
{
    public int Id { get; }
    public int OwnerId { get; init; } = 0;

    // The player's name for the group ("" = unnamed; the client shows the number).
    // Set by CreateGroupIntent and RenameGroupIntent only. GroupRules.CleanName.
    public string Name { get; set; } = "";

    // Units, or groups. Fixed at creation: an empty group still knows which it holds.
    public GroupKind Kind { get; init; } = GroupKind.Units;

    // The group this one sits under (null = top level). Written only with the
    // parent's Children, by GroupRules.SetParent and GroupRules.Remove.
    public int? ParentId { get; set; }

    // Sorted sets keep snapshot canonicalization order-stable. Members is used by a
    // Units group, Children by a Groups group; the other stays empty.
    public SortedSet<int> Members { get; } = new();
    public SortedSet<int> Children { get; } = new();

    // Where the group is "at" right now. While Moving, updates per arrival.
    // While Forming, holds the rendezvous tile (where members are walking to).
    public TileCoord Position { get; set; }

    public GroupState State { get; set; } = GroupState.Idle;

    // ---- Forming integrity state ----
    // Non-null only while State == Forming; nulled out on transition to Idle.
    public TileCoord? RendezvousTile { get; set; }
    // Members whose walk for the group's current order (the rendezvous while Forming,
    // the destination while Moving) hasn't ended. GroupRules.OneLessPending takes a
    // member off: its walk finished, was halted, or it died on the way.
    public int PendingArrivals { get; set; }

    // ---- Movement (M43) ----
    // A group moves as its members' walks (each member walks its own subtile route
    // to the ordered tile). PathFinalDest is the ordered tile while Moving; the
    // group is Idle again when the last walking member finishes (PendingArrivals).
    public TileCoord? PathFinalDest { get; set; }

    // Monotonic counter bumped on every MoveGroupIntent.Resolve, on a halt by a battle,
    // and on removal. Same fencing-token pattern as Unit.AssignmentEpoch.
    public byte MovementEpoch { get; private set; }

    public Group(int id) { Id = id; }

    internal void BumpEpoch() { unchecked { MovementEpoch++; } }

    // Restore-only. Used by Snapshot.Restore to rebuild the epoch without
    // running through BumpEpoch's increment logic.
    internal void RestoreMovementEpoch(byte epoch) => MovementEpoch = epoch;
}

// Append-only enum (serialized). What a group holds.
public enum GroupKind : byte
{
    Units  = 1,
    Groups = 2,
}
