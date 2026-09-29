namespace Sim.Core.Battlefields;

// M41 — one battlefield's turn, on the beat (docs/battlefield-grid.md §5).
// Every board resolves on ticks divisible by RoundIntervalTicks; each board
// has its own event so a board that opens, closes or suspends touches only
// its own anchor.
//
// Fencing: the event carries the tile; it runs only if the board is still
// open, not suspended, and its anchor (NextTurnTick, NextTurnSeq) is this
// event. Anything else is a stale event from a board that closed or was
// rescheduled, and no-ops.
public sealed class BattlefieldTurnEvent : ScheduledEvent
{
    public TileCoord Tile { get; }

    public BattlefieldTurnEvent(TileCoord tile) { Tile = tile; }

    public override void Apply(Simulation sim)
    {
        if (!sim.World.Battlefields.TryGetValue(Tile, out var bf))
        {
            Outcome = IntentOutcome.Reject($"no battlefield on tile {Tile}");
            return;
        }
        if (bf.Suspended || bf.NextTurnTick != At || bf.NextTurnSeq != Seq)
        {
            Outcome = IntentOutcome.Reject($"stale battlefield turn for {Tile}");
            return;
        }
        Battlefields.RunTurn(sim, bf);
    }

    public override string Describe() => $"BattlefieldTurn(@ {Tile.X},{Tile.Y})";
}
