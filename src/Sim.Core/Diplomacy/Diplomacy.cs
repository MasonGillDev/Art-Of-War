namespace Sim.Core.Diplomacy;

// M6 aggregate: per-pair relationships + open proposals + world-level
// diplomacy config. Lives on GameWorld.
//
// PURE-READ WALL: mutation goes through the internal API
// (SetState, BeginPendingWar, ClearPending, AddProposal, RemoveProposal).
// Intents/events call those; views do not. The mutation surface stays
// inside Sim.Core so InternalsVisibleTo doesn't leak it to hosts.
public sealed class Diplomacy
{
    public DiplomacyConfig Config { get; private set; }

    // Sorted by canonical pair key so snapshot serialization is order-stable.
    // Sparse: a row exists only after a transition is requested (no row =
    // Neutral, the default).
    public SortedDictionary<FactionPair, Relationship> Relationships { get; } = new();

    // Open proposals (Phase D). Sparse by id; iteration in id order is
    // deterministic for the snapshot.
    public SortedDictionary<int, Proposal> Proposals { get; } = new();
    public int NextProposalId { get; internal set; } = 1;

    public Diplomacy(DiplomacyConfig config) { Config = config; }

    // ---- Reads ----------------------------------------------------------

    public RelationshipState RelationshipBetween(int a, int b)
    {
        if (a == b) return RelationshipState.Neutral; // self → trivially non-hostile
        return Relationships.TryGetValue(FactionPair.Of(a, b), out var rel)
            ? rel.State
            : RelationshipState.Neutral;
    }

    // The combat gate. Combat (M7) calls this every time two forces might
    // engage. True iff the pair's state is Enemy — except the bandit
    // faction (M16), which is hostile to EVERYONE, always: no relationship
    // rows, no war telegraph, no peace. The reserved id never appears in
    // Relationships (diplomacy intents reject it).
    public bool AreHostile(int a, int b)
    {
        if (a == b) return false;
        if (a == Bandits.BanditConstants.OwnerId || b == Bandits.BanditConstants.OwnerId)
            return true;
        return RelationshipBetween(a, b) == RelationshipState.Enemy;
    }

    // ---- Internal mutation API -----------------------------------------

    // Get-or-create a relationship row. Used by intent paths that need a
    // mutable row even if the pair has never transitioned.
    internal Relationship GetOrCreate(FactionPair pair)
    {
        if (!Relationships.TryGetValue(pair, out var rel))
        {
            rel = new Relationship(pair);
            Relationships[pair] = rel;
        }
        return rel;
    }

    internal void SetState(FactionPair pair, RelationshipState state)
    {
        var rel = GetOrCreate(pair);
        rel.State = state;
    }

    internal void BeginPendingWar(FactionPair pair, long effectiveTick, long seq)
    {
        var rel = GetOrCreate(pair);
        rel.PendingEffectiveTick = effectiveTick;
        rel.PendingSeq = seq;
    }

    internal void ClearPending(FactionPair pair)
    {
        if (!Relationships.TryGetValue(pair, out var rel)) return;
        rel.PendingEffectiveTick = null;
        rel.PendingSeq = null;
    }

    internal int AddProposal(Proposal p)
    {
        Proposals[p.Id] = p;
        return p.Id;
    }

    internal void RemoveProposal(int id) => Proposals.Remove(id);

    // Restore-only — used by Snapshot.Restore to rebuild without going
    // through the normal mutation path.
    internal void RestoreNextProposalId(int next) => NextProposalId = next;
    internal void RestoreConfig(DiplomacyConfig config) => Config = config;
}

// Proposal — Phase D will fill in the body. Declared here so Diplomacy can
// hold the collection.
public sealed class Proposal
{
    public int Id { get; init; }
    public int ProposerId { get; init; }
    public int TargetId { get; init; }
    public RelationshipState DesiredState { get; init; }
    public long ExpiryTick { get; init; }
}
