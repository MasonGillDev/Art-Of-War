using Sim.Core;
using Sim.Core.Diplomacy;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Ai;
using Sim.Server.Ai.Rungs;
using Sim.Server.Wire;

namespace Sim.Tests;

// M43 step 6 (docs/m43-status.md, "AI and drivers"): the brains learn the board's per-side
// unit cap. The AI stays view-only, so it holds the rule as config (CastleAttackerSlots) and
// this file pins that config to the rule it mirrors, then the three places it shows: the
// campaign's assault power, the rally that fills, and the civilian recall to a full castle.
public class AiBoardAwarenessTests
{
    private static StructDto Struct(int x, int y, StructureKind kind, int owner) =>
        new() { X = x, Y = y, Kind = (int)kind, OwnerId = owner };

    private static UnitDto Unit(int id, int x, int y, UnitRole role, int owner, int power = 3) =>
        new() { Id = id, X = x, Y = y, Role = (int)role, OwnerId = owner, Power = power,
                Activity = (int)Activity.Idle, Age = 20, DestX = -1, DestY = -1 };

    private static ViewDto View(StructDto[] structures, UnitDto[] units, params RelationshipDto[] rels) => new()
    {
        PlayerId = 0, Width = 64, Height = 64, Population = 48,
        Structures = structures, Units = units,
        Factions = [new FactionDto { Id = 0 }, new FactionDto { Id = 1 }],
        Relationships = rels,
    };

    [Fact]
    public void CastleAttackerSlots_MirrorsTheBoardsCap()
    {
        var build = WorldFactory.Build(new ServerOptions { MapWidth = 64, MapHeight = 64, MapSeed = 7, AiPlayers = 1 });
        var world = new Sim.Core.Engine.Simulation(build.Spec, seed: 1).World;
        var castle = world.Structures.Values.OfType<Castle>().First(c => c.OwnerId == 0);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);

        Assert.Equal(new AiConfig().CastleAttackerSlots, TileCapacity.For(world, castle.At, owner: 1));
        Assert.Equal(Sim.Core.Battlefields.Subtile.Count - 2, TileCapacity.For(world, castle.At, owner: 0));   // the keep's two subtiles are nobody's
    }

    [Fact]
    public void AssaultPower_CountsTheStrongestSlotsInFull_AndTheRestAsReserve()
    {
        var ctx = ThinkContext.Build(View([], []), new AiConfig(), new AiMemory(), now: 0);
        var cfg = ctx.Cfg;
        // A force that fits is worth its plain sum.
        Assert.Equal(3 * cfg.CastleAttackerSlots,
            EnemyIntel.AssaultPower(ctx, Enumerable.Repeat(3, cfg.CastleAttackerSlots), cfg.CastleAttackerSlots));
        // Past the slots the strongest fight in full, the rest at ReserveWeightPercent.
        var powers = Enumerable.Repeat(3, cfg.CastleAttackerSlots).Concat([10, 10]).ToList();
        var expected = 10 + 10 + 3 * (cfg.CastleAttackerSlots - 2)
            + (3 * 2) * cfg.ReserveWeightPercent / 100;
        Assert.Equal(expected, EnemyIntel.AssaultPower(ctx, powers, cfg.CastleAttackerSlots));
    }

    [Fact]
    public void Conquer_WontLaunch_OnPowerTheGateCantBring()
    {
        // 10 soldiers, pop 48 claims 6. Their plain sum (18pw) clears the GO gate against
        // the assumed 12pw garrison (18 x100 >= 12 x150) but one more than the slots is a
        // reserve worth half (5 x 3 + 1 x 3 / 2 = 16pw): 1600 < 1800, so nobody marches.
        var cfg = new AiConfig { CastleAttackerSlots = 5 };   // the reserve arithmetic below is pinned at five slots
        StructDto[] structures = [Struct(5, 5, StructureKind.Castle, 0), Struct(6, 5, StructureKind.Barracks, 0)];
        var soldiers = Enumerable.Range(0, 10).Select(i => Unit(100 + i, 6, 5, UnitRole.Soldier, 0)).ToArray();
        var war = new RelationshipDto { LoId = 0, HiId = 1, State = (int)RelationshipState.Enemy };
        var mem = new AiMemory { WarTarget = 1 };
        mem.KnownEnemyCastles[1] = ((40, 40), 100L);

        var d = new ConquerRung().TryClaim(ThinkContext.Build(View(structures, soldiers, war), cfg, mem, now: 200));

        Assert.Null(d);
        Assert.False(mem.CampaignLaunched);
    }

    [Fact]
    public void Conquer_AFullRally_DoesNotMarchTheSpilledBackEveryThink()
    {
        // 16 already stand on the rally; the rest were re-aimed onto the tile beside it. They
        // count as assembled, so no order sends them back to be re-aimed again.
        var cfg = new AiConfig();
        StructDto[] structures = [Struct(5, 5, StructureKind.Castle, 0), Struct(6, 5, StructureKind.Barracks, 0)];
        var soldiers = Enumerable.Range(0, 16).Select(i => Unit(100 + i, 6, 5, UnitRole.Soldier, 0))
            .Concat(Enumerable.Range(0, 4).Select(i => Unit(200 + i, 7, 5, UnitRole.Soldier, 0))).ToArray();
        var view = View(structures, soldiers, new RelationshipDto { LoId = 0, HiId = 1, State = (int)RelationshipState.Neutral, PendingEffectiveTick = 9999 });
        view.Population = 200;
        var mem = new AiMemory { WarTarget = 1 };
        mem.KnownEnemyCastles[1] = ((40, 40), 100L);

        var d = new ConquerRung().TryClaim(ThinkContext.Build(view, cfg, mem, now: 100));

        Assert.Null(d);   // assembled; the telegraph is still running, so nobody marches
    }

    [Fact]
    public void Defend_RecallsCivilians_OnlyAsFarAsTheCastleHasRoom()
    {
        var cfg = new AiConfig();
        var castleTile = new TileCoord(5, 5);
        StructDto[] structures = [Struct(5, 5, StructureKind.Castle, 0)];
        UnitDto[] Crowd(int atCastle) => Enumerable.Range(0, atCastle)
            .Select(i => Unit(100 + i, 5, 5, UnitRole.Builder, 0))
            .Concat(Enumerable.Range(0, 3).Select(i => Unit(300 + i, 7, 5, UnitRole.Builder, 0)))
            .Append(Unit(900, 8, 5, UnitRole.Bandit, Sim.Core.Bandits.BanditConstants.OwnerId))
            .ToArray();

        // The castle is full: nobody is sent to be re-aimed outside it.
        var full = new DefendRung().TryClaim(ThinkContext.Build(View(structures, Crowd(16)), cfg, new AiMemory(), now: 10));
        Assert.Null(full);

        // Two places left: two of the three are sent.
        var room = new DefendRung().TryClaim(ThinkContext.Build(View(structures, Crowd(14)), cfg, new AiMemory(), now: 10));
        Assert.NotNull(room);
        var moves = room!.Intents.OfType<MoveIntent>().ToList();
        Assert.Equal(2, moves.Count);
        Assert.All(moves, m => Assert.Equal(castleTile, m.Destination));
    }
}
