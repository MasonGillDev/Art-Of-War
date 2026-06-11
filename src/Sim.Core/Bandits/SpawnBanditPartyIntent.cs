using Sim.Core.Intents;
using Sim.Core.World;

namespace Sim.Core.Bandits;

// M16 — a bandit party materializes out of the fog. SERVER-INTERNAL:
// only the in-process bandit driver may submit this (the wire rejects
// the intent type and the bandit PlayerId — see GameHost); it still
// flows through the normal intent pipeline so the durable log replays
// the spawn deterministically.
//
// Darkness is validated AT RESOLVE TIME, not submit time — the driver
// proposes a site from a slightly stale read, and if a scout walked
// into view during the intent's flight, the spawn dies here. The world
// only ever conjures bandits where no player could have seen it happen.
//
// Preconditions:
//   * PlayerId == BanditConstants.OwnerId (defense in depth below the wire guard).
//   * 1 <= Size <= MaxPartySize.
//   * Tile in bounds; derived biome walkable (no Water/None spawns).
//   * Tile invisible to every non-bandit faction (BanditRules.IsSeenByAnyPlayer).
//   * Chebyshev >= MinSpawnDistance from any player unit or structure.
//
// Units are created through Population.OnUnitAdded (the canonical
// runtime-add hook — its docstring always anticipated "mob spawns") with
// the lifespan roll SKIPPED: bandits die by sword, not calendar, and
// consuming RNG here would shift every later demographic roll.
public sealed class SpawnBanditPartyIntent : Intent
{
    public TileCoord At { get; }
    public int Size { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SpawnBanditPartyIntent(TileCoord at, int size)
    {
        At = at;
        Size = size;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (PlayerId != BanditConstants.OwnerId)
            return IntentOutcome.Reject(
                $"only the bandit driver may spawn bandits (PlayerId={PlayerId})");
        if (Size < 1 || Size > BanditConstants.MaxPartySize)
            return IntentOutcome.Reject(
                $"party size {Size} outside 1..{BanditConstants.MaxPartySize}");
        if (At.X < 0 || At.Y < 0 || At.X >= world.Grid.Width || At.Y >= world.Grid.Height)
            return IntentOutcome.Reject($"spawn tile {At} out of bounds");

        var biome = Sim.Core.Biomes.BiomeDegradation.BiomeAt(
            world, At, sim.Now, world.BiomeDegradationConfig);
        if (biome is Biome.Water or Biome.None)
            return IntentOutcome.Reject($"spawn tile {At} is not walkable ({biome})");

        if (BanditRules.IsSeenByAnyPlayer(world, At))
            return IntentOutcome.Reject($"spawn tile {At} is visible to a player");

        var dist = BanditRules.ChebyshevToNearestPlayerPresence(world, At);
        if (dist < BanditConstants.MinSpawnDistance)
            return IntentOutcome.Reject(
                $"spawn tile {At} is {dist} tiles from player presence " +
                $"(min {BanditConstants.MinSpawnDistance})");

        for (var i = 0; i < Size; i++)
        {
            var id = world.NextUnitId;
            world.NextUnitId++;
            Sim.Core.Population.Population.OnUnitAdded(sim, new Unit(id, At)
            {
                Role = UnitRole.Bandit,
                OwnerId = BanditConstants.OwnerId,
                BornTick = sim.Now,
            });
            // No ScheduleLifespan — age-exempt by design.
        }
        return IntentOutcome.Applied;
    }

    public override string Describe() => $"SpawnBanditParty(at={At.X},{At.Y} size={Size})";
}
