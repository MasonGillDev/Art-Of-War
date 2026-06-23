using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server.Ai;
using Sim.Server.Ai.Rungs;
using Sim.Server.Wire;

namespace Sim.Tests;

// M25 Phase 2 — the defender guards its border against rival FACTIONS, not just
// bandits (docs/m25-rival-spec.md). The same M17 doctrine (threat memory →
// force-parity → sortie) now keys off the view's PUBLIC diplomacy, so even a
// peaceful Homesteader repels an invading enemy army, and the enemy's strength
// is read from its visible ROLE (its true Power is fogged like a human's).
public class RivalDefenseTests
{
    private static UnitDto Unit(int id, int x, int y, UnitRole role, int owner, int power = -1) => new()
    {
        Id = id, X = x, Y = y, Role = (int)role, OwnerId = owner,
        Activity = owner == 0 ? (int)Activity.Idle : -1,   // enemy activity is hidden, like the wire
        Power = power, DestX = -1, DestY = -1,
    };

    // Faction 0 (the viewer) holds a castle at (10,10) with one garrison
    // soldier; faction 1 has marched a soldier to (13,10), three tiles off the
    // keep and in sight. `state` sets the pair's relationship.
    private static ViewDto ViewWithInvader(RelationshipState state) => new()
    {
        PlayerId = 0, Width = 40, Height = 40, Tick = 1000,
        Population = 14, CastleFood = 1000,
        Structures = new[]
        {
            new StructDto { X = 10, Y = 10, Kind = (int)StructureKind.Castle, OwnerId = 0 },
        },
        Units = new[]
        {
            Unit(1, 10, 10, UnitRole.Soldier, owner: 0, power: 3),  // garrison (own power on wire)
            Unit(2, 13, 10, UnitRole.Soldier, owner: 1),            // invader (power hidden)
        },
        Visible = new[] { new TileDto { X = 13, Y = 10, Biome = (int)Biome.Grassland } },
        Relationships = new[] { new RelationshipDto { LoId = 0, HiId = 1, State = (int)state } },
        Factions = new[] { new FactionDto { Id = 0 }, new FactionDto { Id = 1 } },
    };

    [Fact]
    public void Defender_PerceivesEnemyFaction_AndSorties()
    {
        var mem = new AiMemory();
        var ctx = ThinkContext.Build(ViewWithInvader(RelationshipState.Enemy), new AiConfig(), mem, now: 1000);
        var decision = new DefendRung().TryClaim(ctx);

        // The enemy soldier is banked in the threat memory, priced from its
        // ROLE (its Power was hidden in the fair view, exactly as a human sees).
        Assert.True(mem.SightedHostiles.ContainsKey((13, 10)));
        Assert.Equal(UnitCombatCatalog.Spec(UnitRole.Soldier).BasePower,
            mem.SightedHostiles[(13, 10)].Power);

        // Garrison power (3) >= invader power (3): the garrison sorties — the
        // soldier is ordered onto the invader's tile (combat is automatic there).
        Assert.NotNull(decision);
        Assert.Equal("defend", decision!.Rung);
        var move = decision.Intents.OfType<MoveIntent>().FirstOrDefault(m => m.UnitId == 1);
        Assert.NotNull(move);
        Assert.Equal(new TileCoord(13, 10), move!.Destination);
    }

    [Fact]
    public void NeutralFaction_IsNotAThreat()
    {
        // Identical invader, but the factions are at peace: not hostile, so no
        // threat is recorded and the garrison stands easy at home.
        var mem = new AiMemory();
        var ctx = ThinkContext.Build(ViewWithInvader(RelationshipState.Neutral), new AiConfig(), mem, now: 1000);
        var decision = new DefendRung().TryClaim(ctx);

        Assert.Empty(mem.SightedHostiles);
        Assert.Null(decision);   // garrison already home on the keep — nothing to do
    }
}
