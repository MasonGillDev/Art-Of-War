namespace Sim.Core.Diplomacy;

// Per-pair diplomatic state. Owns the current state plus a pending-hostile
// transition anchor (effective tick + Seq) when a war has been declared but
// hasn't yet taken effect — same M4 anchor pattern as Unit.NextArrivalTick /
// Extractor.NextProductionTickSeq. RegenerateQueue.From reconstructs the
// WarBecomesEffectiveEvent from this anchor on snapshot restore.
//
// MUTATION POLICY: written only by Diplomacy's internal API (called from
// intents/events). PlayerView and other read paths must NEVER mutate.
public sealed class Relationship
{
    public FactionPair Pair { get; }
    public RelationshipState State { get; internal set; } = RelationshipState.Neutral;

    // Set when a DeclareWarIntent commits a pending hostile transition; cleared
    // when the WarBecomesEffectiveEvent fires (or when peace overrides it via
    // an accepted proposal). Both fields move together.
    public long? PendingEffectiveTick { get; internal set; }
    public long? PendingSeq           { get; internal set; }

    public bool HasPendingWar => PendingEffectiveTick is not null;

    public Relationship(FactionPair pair) { Pair = pair; }
}
