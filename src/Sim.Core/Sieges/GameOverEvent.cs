using Sim.Core.Engine;
using Sim.Core.Intents;

namespace Sim.Core.Sieges;

// M24 — fires once the world resolves to a winner (PlayerDefeatedEvent
// detected <= 1 surviving player). A pure marker: the event has no side
// effects on world state, it just lands in ResolvedLog so the host /
// replay / audit can observe "this is the tick the game ended" without
// scanning Player.Defeated across the whole roster every tick.
//
// WinnerId is the surviving player id, or null if the final defeat was
// mutual (no one left standing — the all-castles-razed-same-tick edge).
// The sim DOES NOT halt: events continue to fire, snapshots continue to
// round-trip, the host decides what to do with the post-game state.
// Defeated players cannot issue new intents (IntentEvent gate).
//
// Recovery-clean: scheduled at sim.Now and consumed this same tick — no
// anchor; the queue never carries it across a snapshot. Player.Defeated
// IS persisted (snapshot v19), so the game-over CONDITION survives
// restore even if the event itself doesn't.
public sealed class GameOverEvent : ScheduledEvent
{
    public int? WinnerId { get; }

    public GameOverEvent(int? winnerId) { WinnerId = winnerId; }

    public override void Apply(Simulation sim)
    {
        // No state mutation. The point of the event is to be readable
        // from sim.ResolvedLog — `world.Players` already tells the
        // story for any code that needs to act on it.
        Outcome = IntentOutcome.Applied;
    }

    public override string Describe() =>
        WinnerId is int w ? $"GameOver(winner={w})" : "GameOver(no winner)";
}
