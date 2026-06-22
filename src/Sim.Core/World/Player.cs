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
