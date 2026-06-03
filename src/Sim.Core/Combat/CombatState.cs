using Sim.Core.World;

namespace Sim.Core.Combat;

// M7 — per-tile contested state. One row in world.CombatStates per
// active combat. The next-round anchor (NextRoundTick + NextRoundSeq)
// is the M4-pattern fencing token: RegenerateQueue reconstructs a
// CombatRoundEvent from this anchor on snapshot restore, and stale
// round events (e.g. a round scheduled then cleared by combat ending
// early) fence on Seq mismatch.
//
// MUTATION POLICY: written only by CombatTrigger, CombatRoundEvent,
// and Snapshot.Restore. Views read but never write.
public sealed class CombatState
{
    public TileCoord Tile { get; }
    public long NextRoundTick { get; internal set; }
    public long NextRoundSeq { get; internal set; }
    public byte RoundNumber { get; internal set; }

    public CombatState(TileCoord tile) { Tile = tile; }
}
