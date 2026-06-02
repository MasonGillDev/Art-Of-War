namespace Sim.Core.World;

// Minimal player identity for M3. Owns entities (Units, Structures) and
// has an explored-tile memory. No factions, no economies, no diplomacy
// yet — those land with combat. Genesis seeds player 0; multi-player
// scenarios add more.
public sealed class Player
{
    public int Id { get; }
    public Player(int id) { Id = id; }
}
