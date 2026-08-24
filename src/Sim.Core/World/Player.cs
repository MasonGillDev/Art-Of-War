namespace Sim.Core.World;

// Minimal player identity. Owns entities (Units, Structures) and has an
// explored-tile memory and a population count.
public sealed class Player
{
    public int Id { get; }

    // M13 — total Units owned by this player. Maintained with single-
    // mutation discipline via IncrementPopulation / DecrementPopulation;
    // every Unit-add and Unit-remove site routes through Population.OnUnitAdded
    // / Population.OnUnitRemoved which call these. The audit
    // FoodConsumptionTests.PopulationCount_HasOneMutationPoint asserts that
    // no other writer exists.
    public int PopulationCount { get; private set; }

    // M24 — defeated when the player's Castle is razed (CombatRoundEvent
    // → SiegeDamage.RazeStructure → PlayerDefeatedEvent). A defeated
    // player's IntentEvent rejects every intent at the wrapper layer —
    // their existing Units and non-castle Structures persist as inert
    // wreckage of their civilization. Cleared only by GameWorld restore
    // (snapshot v19+). See docs/sieges-and-conquest.md.
    public bool Defeated { get; internal set; }

    // M31 — the reigning monarch's unit id; null during an interregnum and
    // after the line is extinct (docs/king-and-dynasty.md).
    //
    // SINGLE MUTATION POINT: Royalty.Succession.OnRoyalRemoved, reached from
    // Population.OnUnitRemoved, which every death path already converges on.
    // Restored directly by Snapshot (v28).
    //
    // Stored rather than derived for one concrete reason: the aura is a pure
    // read evaluated per unit inside the combat rollup, and a derived king
    // would make that check O(N) and the rollup O(N^2). One int buys O(1).
    public int? KingUnitId { get; internal set; }

    public Player(int id) { Id = id; }

    internal void IncrementPopulation() => PopulationCount++;
    internal void DecrementPopulation()
    {
        if (PopulationCount <= 0)
            throw new InvalidOperationException(
                $"Player {Id} population would go negative " +
                "(IncrementPopulation/DecrementPopulation imbalance).");
        PopulationCount--;
    }
}
