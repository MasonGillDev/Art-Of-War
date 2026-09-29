namespace Sim.Core.Hauling;

// Append-only enum (serialized).
public enum HaulJobKind : byte
{
    // Keep the destination's stock of Resource at Target, forever.
    Standing = 1,
    // Deliver Target units in total, then the job is done and removed.
    Once     = 2,
    // M40 — carry EVERYTHING from a charted or seen secret or pile that is
    // nobody's (docs/salvage.md). Resource is None; Target is how many haulers
    // may work it at once. Ends when a hauler arrives and finds nothing left.
    Salvage  = 3,
}

// One job in a player's haul queue (docs/hauling-queue-and-routes.md).
//
// THE QUEUE IS NOT A LIST. It is every job of one owner sorted by
// (QueueStamp, JobId). "To the back" is a fresh stamp from
// GameWorld.NextHaulStamp, so moving a job is one field write and the order
// survives a snapshot with nothing else to keep in step.
//
// Sim.Core never evaluates a job. The server-side HaulingDriver reads the
// queue, picks haulers and submits ordinary HaulIntents; this record only
// holds what the player asked for and where the job sits in line.
//
// Mutated ONLY by SetHaulJobIntent / ClearHaulJobIntent (definition),
// RequeueHaulJobIntent (QueueStamp, QueuedAtTick) and HaulDepositEvent
// (Delivered, and removal of a finished Once job).
public sealed class HaulJob
{
    public int JobId { get; init; }
    public int OwnerId { get; init; }
    public TileCoord Source { get; init; }
    public TileCoord Dest { get; init; }
    public Resource Resource { get; init; }
    public HaulJobKind Kind { get; init; }
    // Standing: the stock level to keep at Dest. Once: the total to deliver.
    public int Target { get; init; }

    // Once only — delivered so far. The job is removed when this reaches Target.
    public int Delivered { get; set; }

    // Place in the owner's queue: lower goes first.
    public long QueueStamp { get; set; }
    // When the job last joined the back of the queue. The HUD's "longest
    // wait" reads it; nothing in the sim does.
    public long QueuedAtTick { get; set; }
}
