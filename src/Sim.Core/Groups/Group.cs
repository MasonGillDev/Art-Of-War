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
//   MusterGroupIntent → Forming: members finish what must finish and walk to their
//   places around the anchor (GroupMuster); the last one in makes it Idle.
//   MoveGroupIntent → Moving: the group marches (GroupMarch); Idle on arrival.
//   DismissGroupIntent → Dismissed again: everyone back to their saved tasks.
//   DeleteGroupIntent (any state) → dismissed, members go solo, group removed.
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

    // ---- Forming integrity state (the muster) ----
    // Non-null only while State == Forming; nulled out on transition to Idle.
    public TileCoord? RendezvousTile { get; set; }
    // M46 Phase D — while Forming: the members the muster still waits for (walking
    // to their places, or finishing a job first). Empty → Idle. GroupMuster.
    public SortedSet<int> Awaiting { get; } = new();
    // Each member's place in the muster's block (FormationLayout), kept until the
    // group is dismissed so a member that finishes its job late knows where to go.
    // A member with no entry had no room within reach.
    public SortedDictionary<int, Sim.Core.Battlefields.WorldSubtile> MusterPlaces { get; } = new();

    // ---- M47 route groups (docs/m47-route-groups-spec.md) ----
    // A group crewing a haul route (the route's RouteCrew.GroupId names it) runs the
    // route as its daily task: the hauling driver marches it from stop to stop. A
    // player's muster or move SUSPENDS the route; dismiss lifts the suspension and
    // the driver picks the route up at the crew's stop.
    public bool RouteSuspended { get; set; }
    // A muster that reached a route group mid-leg: it finishes the leg (walks to its
    // stop and serves it) and then answers. Applied by ServeRouteStopIntent.
    public TileCoord? PendingMuster { get; set; }

    // ---- M49 stance (docs/m49-group-stance-spec.md) ----
    // How the group fights: the player's one choice. GroupStances defines what each means.
    public GroupStance Stance { get; set; } = GroupStance.Defensive;
    // Where the group goes back to once a fight it went to (aid, a charge) is over. Null
    // when it isn't away helping or charging. GroupStances.
    public TileCoord? ReturnTo { get; set; }
    // Members whose walk for the group's current order (the rendezvous while Forming,
    // the destination while Moving) hasn't ended. GroupRules.OneLessPending takes a
    // member off: its walk finished, was halted, or it died on the way.
    public int PendingArrivals { get; set; }

    // ---- Movement (M46 Phase C: the group walks) ----
    // A Moving group marches in two stages (GroupMarch):
    //   1. THE COLUMN — MarchPath is the lead's committed path (the owner's view at
    //      order time; stored, never recomputed on restore). MarchLead is the lead's
    //      index on it. One GroupStepEvent (anchor NextStepTick/Seq) moves every
    //      member a step toward its slot in the column; the lead advances only when
    //      all of them stand on their slots.
    //   2. CLOSING — MarchPath is null; each member walks its own short walk to its
    //      place around the destination (FormationLayout), and PendingArrivals counts
    //      them down to Idle.
    // PathFinalDest is the destination tile through both stages.
    public TileCoord? PathFinalDest { get; set; }
    public List<Sim.Core.Battlefields.WorldSubtile>? MarchPath { get; set; }
    public int MarchLead { get; set; }
    public long? NextStepTick { get; set; }
    public long? NextStepSeq { get; set; }
    // Members that fell out of the column (no way to their slot) and walk on alone to
    // the destination; they rejoin the block at closing.
    public SortedSet<int> Stragglers { get; } = new();

    // Monotonic counter bumped on every MoveGroupIntent.Resolve, on a halt by a battle,
    // and on removal. Same fencing-token pattern as Unit.AssignmentEpoch.
    public byte MovementEpoch { get; private set; }

    public Group(int id) { Id = id; }

    internal void BumpEpoch() { unchecked { MovementEpoch++; } }

    // Restore-only. Used by Snapshot.Restore to rebuild the epoch without
    // running through BumpEpoch's increment logic.
    internal void RestoreMovementEpoch(byte epoch) => MovementEpoch = epoch;
}

// Append-only enum (serialized, on the wire). How a group fights (M49).
public enum GroupStance : byte
{
    Passive    = 1,
    Defensive  = 2,
    Aggressive = 3,
}

// Append-only enum (serialized). What a group holds.
public enum GroupKind : byte
{
    Units  = 1,
    Groups = 2,
}
