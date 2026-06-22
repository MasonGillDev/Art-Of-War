using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Sieges;

// M24 — fires the tick a player's Castle is razed (CombatRoundEvent ->
// SiegeDamage.RazeStructure). Marks the owner as defeated so the
// IntentEvent gate rejects every further intent from them, and — if at
// most one non-bandit / non-cache player remains undefeated — schedules
// a GameOverEvent on the same tick.
//
// Why an EVENT and not an inline call from RazeStructure: the
// PlayerDefeated transition is observable state the host wants to know
// about, and putting it in the resolved log lets replays / audit /
// scouting features pin "the tick the player fell" without scraping the
// CombatStates map. Same justification as the FamineCheckEvent /
// BirthEvent / DeathByAgeEvent pattern.
//
// Idempotent fence: a second PlayerDefeatedEvent for an already-defeated
// player rejects cleanly (defends against a future "all castles razed
// same tick" edge case where two events both fire for the same owner).
//
// Recovery-clean: this event is scheduled at sim.Now and fires this same
// tick, so the queue never carries it across a snapshot. No anchor.
public sealed class PlayerDefeatedEvent : ScheduledEvent
{
    public int OwnerId { get; }
    public TileCoord CastleAt { get; }

    public PlayerDefeatedEvent(int ownerId, TileCoord castleAt)
    {
        OwnerId = ownerId;
        CastleAt = castleAt;
    }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Players.TryGetValue(OwnerId, out var player))
        {
            Outcome = IntentOutcome.Reject($"player {OwnerId} not in world");
            return;
        }
        if (player.Defeated)
        {
            Outcome = IntentOutcome.Reject($"player {OwnerId} already defeated");
            return;
        }

        player.Defeated = true;

        // Game-over check. The "living players" set is the snapshot of
        // every NON-negative owner id (bandits -1 / caches -2 / rubble -3
        // never count) whose Defeated flag is false. <= 1 → fire
        // GameOverEvent; winner is the last surviving id (or null if a
        // simultaneous mutual-razing edge case zeroes the set).
        var liveCount = 0;
        int? winner = null;
        foreach (var (id, p) in world.Players)
        {
            if (id < 0) continue;
            if (p.Defeated) continue;
            liveCount++;
            winner = id;
        }
        if (liveCount <= 1)
            sim.Schedule(sim.Now, new GameOverEvent(liveCount == 1 ? winner : null));
    }

    public override string Describe() =>
        $"PlayerDefeated(owner={OwnerId} @ castle {CastleAt.X},{CastleAt.Y})";
}
