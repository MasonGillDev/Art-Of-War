namespace Sim.Core.Battlefields;

// M41 — a unit's place on an open battlefield (Unit.Board). Exactly one of:
//   on the board  — At is a subtile 0..3;
//   waiting       — At is just outside an edge, in the lane it will come on
//                   by (it arrived while the board was open; it steps on at
//                   the next beat, docs/battlefield-grid.md §4.3);
//   sheltered     — more of its side on the tile than the board holds (build
//                   decision D1): off the board, safe, unable to act; it
//                   comes on into a free subtile when one frees.
public sealed class BoardSlot
{
    public TileCoord Tile { get; }
    public Subtile At { get; internal set; }
    public Subtile? CameFrom { get; internal set; }
    public BattleOrder? Order { get; internal set; }
    public bool Sheltered { get; internal set; }

    // Why its last planned step didn't happen (StepNote.None if it did, or it
    // had none). Shown to the owner (§10).
    public StepNote LastNote { get; internal set; }

    public BoardSlot(TileCoord tile, Subtile at, bool sheltered = false)
    {
        Tile = tile;
        At = at;
        Sheltered = sheltered;
    }

    public bool OnBoard => !Sheltered && At.IsOnBoard;
    public bool Waiting => !Sheltered && !At.IsOnBoard;
}

// M41 — one open battlefield (GameWorld.Battlefields). The turn anchor is the
// M4 fencing token: BattlefieldTurnEvent fences on (NextTurnTick,
// NextTurnSeq); RegenerateQueue rebuilds the event from it. A Suspended board
// has no event queued (§5 "Idle battlefields"); an order or an arrival wakes it.
public sealed class Battlefield
{
    public TileCoord Tile { get; }
    public long OpenedTick { get; }
    public int TurnNumber { get; internal set; }
    public long NextTurnTick { get; internal set; }
    public long NextTurnSeq { get; internal set; }
    public bool Suspended { get; internal set; }

    // The turn just resolved, for the view's playback (§10). Presentation
    // only: NOT snapshotted and not part of the sim's state (a restored world
    // simply has nothing to replay until its next beat).
    public BattleTurnRecord? LastTurn { get; internal set; }

    public Battlefield(TileCoord tile, long openedTick)
    {
        Tile = tile;
        OpenedTick = openedTick;
    }
}

// What one turn did, kept for the view. Owners maps every unit named in the
// result (including the dead and the departed) to its owner.
public sealed record BattleTurnRecord(long Tick, int TurnNumber, TurnResult Result, IReadOnlyDictionary<int, int> Owners);
