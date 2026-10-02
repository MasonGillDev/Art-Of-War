namespace Sim.Core.Battlefields;

// M41 — a unit's part in an open battlefield (Unit.Board). M42 phase 4: where it
// stands is Unit.Subtile, the same as at world scale, so the slot holds only what
// belongs to the fight: the standing order, the subtile it last moved from (no
// passing through), and why its last planned step didn't happen.
public sealed class BoardSlot
{
    public TileCoord Tile { get; }
    public Subtile? CameFrom { get; internal set; }
    public BattleOrder? Order { get; internal set; }

    // Why its last planned step didn't happen (StepNote.None if it did, or it
    // had none). Shown to the owner (§10).
    public StepNote LastNote { get; internal set; }

    public BoardSlot(TileCoord tile) { Tile = tile; }
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
