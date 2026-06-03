using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M6 Phase B: per-pair relationship state model.
//   * FactionPair canonicalizes (a, b) → (min, max) so AreHostile is symmetric.
//   * Default is Neutral for every pair.
//   * AreHostile is true only for Enemy.
//   * Relationships round-trip through Snapshot at FormatVersion 3.
public class DiplomacyRelationshipTests
{
    private static GameWorld MakeTwoFactionWorld(DiplomacyConfig? config = null)
    {
        var spec = new GenesisSpec
        {
            Width = 30, Height = 30,
            Diplomacy = config ?? new DiplomacyConfig(),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(2, 2) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(25, 25) },
            },
        };
        return Genesis.Build(spec);
    }

    [Fact]
    public void FactionPair_Canonicalizes()
    {
        Assert.Equal(FactionPair.Of(1, 3), FactionPair.Of(3, 1));
        Assert.Equal(1, FactionPair.Of(3, 1).Lo);
        Assert.Equal(3, FactionPair.Of(3, 1).Hi);
    }

    [Fact]
    public void FactionPair_RejectsSelfPair()
    {
        Assert.Throws<InvalidOperationException>(() => FactionPair.Of(5, 5));
    }

    [Fact]
    public void FactionPair_OtherReturnsTheOtherSide()
    {
        var p = FactionPair.Of(1, 4);
        Assert.Equal(4, p.Other(1));
        Assert.Equal(1, p.Other(4));
    }

    [Fact]
    public void Default_AllPairsNeutral()
    {
        var world = MakeTwoFactionWorld();
        Assert.Equal(RelationshipState.Neutral, world.Diplomacy.RelationshipBetween(0, 1));
        Assert.Equal(RelationshipState.Neutral, world.Diplomacy.RelationshipBetween(1, 0));
        Assert.False(world.Diplomacy.AreHostile(0, 1));
    }

    [Fact]
    public void RelationshipBetween_IsSymmetric()
    {
        var world = MakeTwoFactionWorld();
        // Force a non-default state via the internal API.
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        Assert.Equal(RelationshipState.Enemy, world.Diplomacy.RelationshipBetween(0, 1));
        Assert.Equal(RelationshipState.Enemy, world.Diplomacy.RelationshipBetween(1, 0));
    }

    [Fact]
    public void AreHostile_OnlyEnemy()
    {
        var world = MakeTwoFactionWorld();
        var d = world.Diplomacy;
        var pair = FactionPair.Of(0, 1);

        d.SetState(pair, RelationshipState.Neutral);
        Assert.False(d.AreHostile(0, 1));

        d.SetState(pair, RelationshipState.Ally);
        Assert.False(d.AreHostile(0, 1));

        d.SetState(pair, RelationshipState.Enemy);
        Assert.True(d.AreHostile(0, 1));
    }

    [Fact]
    public void Snapshot_RoundTrips_Relationships()
    {
        var world = MakeTwoFactionWorld(new DiplomacyConfig(Delay: 77, ProposalExpiryTicks: 555));
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        // Set a pending-war anchor too so the per-pair anchor round-trips.
        world.Diplomacy.BeginPendingWar(FactionPair.Of(0, 1), effectiveTick: 200, seq: 42);

        var sim = new Simulation(world, seed: 0xD1A);
        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 0xD1A);

        Assert.Equal(77L, restored.World.Diplomacy.Config.Delay);
        Assert.Equal(555L, restored.World.Diplomacy.Config.ProposalExpiryTicks);
        Assert.Equal(RelationshipState.Enemy, restored.World.Diplomacy.RelationshipBetween(0, 1));
        var rel = restored.World.Diplomacy.Relationships[FactionPair.Of(0, 1)];
        Assert.Equal(200L, rel.PendingEffectiveTick);
        Assert.Equal(42L, rel.PendingSeq);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }
}
