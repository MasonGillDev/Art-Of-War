using Sim.Core.Diplomacy;
using Sim.Core.World;
using Sim.Server.Ai;
using Sim.Server.Ai.Rungs;
using Sim.Server.Wire;

namespace Sim.Tests;

// M25 Phase 3 — the casus belli (docs/m25-rival-spec.md). The Rival's perception
// pass decides WHEN to go to war, gated by personality. These pin each of the
// four triggers, the Homesteader's pacifism, and the no-double-declare guard —
// all at the RivalRung.Perceive seam (a hand-built view, the fair channel).
public class RivalDeclarationTests
{
    private const long Now = 5000;

    private static StructDto S(int x, int y, StructureKind kind, int owner) =>
        new() { X = x, Y = y, Kind = (int)kind, OwnerId = owner };

    private static UnitDto U(int id, int x, int y, UnitRole role, int owner, int power = -1) =>
        new()
        {
            Id = id, X = x, Y = y, Role = (int)role, OwnerId = owner,
            Activity = owner == 0 ? (int)Activity.Idle : -1, Power = power, DestX = -1, DestY = -1,
        };

    // Viewer = faction 0, castle at (10,10), well-fed, with whatever rival
    // structures/units the test adds. Factions {0,1} both standing.
    private static ViewDto View(StructDto[] structs, UnitDto[] units) => new()
    {
        PlayerId = 0, Width = 200, Height = 200, Tick = Now,
        Population = 20, CastleFood = 2000, FoodRunwayTicks = 100_000_000,
        Structures = structs.Prepend(S(10, 10, StructureKind.Castle, 0)).ToArray(),
        Units = units,
        Factions = new[] { new FactionDto { Id = 0 }, new FactionDto { Id = 1 } },
    };

    private static (AiMemory mem, List<Sim.Core.Intents.Intent> intents) Run(
        ViewDto view, AiPersonality personality)
    {
        var mem = new AiMemory();
        var ctx = ThinkContext.Build(view, new AiConfig { Personality = personality }, mem, Now);
        return (mem, new RivalRung().Perceive(ctx));
    }

    // ENCROACHMENT — a rival structure inside my border opens a war.
    [Fact]
    public void Opportunist_DeclaresWar_OnEncroachingRival()
    {
        // Rival lumber camp 20 tiles east of the keep — inside EncroachmentRadius (28).
        var view = View(new[] { S(30, 10, StructureKind.LumberCamp, 1) }, Array.Empty<UnitDto>());
        var (mem, intents) = Run(view, AiPersonality.Opportunist);

        Assert.Equal(1, mem.CampaignTarget);
        Assert.Equal("encroachment", mem.CampaignReason);
        var war = Assert.Single(intents.OfType<DeclareWarIntent>());
        Assert.Equal(0, war.DeclarerId);
        Assert.Equal(1, war.TargetId);
        Assert.Equal(0, war.PlayerId);   // authorized as the declarer
    }

    // The Homesteader never initiates — even with a rival camped in its yard.
    [Fact]
    public void Homesteader_NeverDeclaresWar()
    {
        var view = View(new[] { S(30, 10, StructureKind.LumberCamp, 1) }, Array.Empty<UnitDto>());
        var (mem, intents) = Run(view, AiPersonality.Homesteader);

        Assert.Null(mem.CampaignTarget);
        Assert.Empty(intents);
    }

    // A distant rival (beyond the encroachment radius) provokes no Opportunist,
    // but a Warlord manufactures a war anyway.
    [Fact]
    public void Warlord_ManufacturesWar_WhereOpportunistHoldsBack()
    {
        // 70 tiles off: no trespass, but reachable (CampaignReachTiles 120).
        var camp = new[] { S(80, 10, StructureKind.LumberCamp, 1) };

        var (opp, oppIntents) = Run(View(camp, Array.Empty<UnitDto>()), AiPersonality.Opportunist);
        Assert.Null(opp.CampaignTarget);
        Assert.Empty(oppIntents);

        var (war, warIntents) = Run(View(camp, Array.Empty<UnitDto>()), AiPersonality.Warlord);
        Assert.Equal(1, war.CampaignTarget);
        Assert.Equal("conquest", war.CampaignReason);
        Assert.Single(warIntents.OfType<DeclareWarIntent>());
    }

    // OPPORTUNISM — a standing army that overwhelms a weakly-guarded objective.
    [Fact]
    public void Opportunist_Strikes_WhenItOutweighsTheGuard()
    {
        // Rival barracks 50 tiles off (no trespass), guarded by a single soldier.
        // My five soldiers (15 power) dwarf their guard (3 power).
        var structs = new[] { S(60, 10, StructureKind.Barracks, 1) };
        var units = Enumerable.Range(1, 5)
            .Select(i => U(i, 10, 10, UnitRole.Soldier, owner: 0, power: 3))
            .Append(U(100, 61, 10, UnitRole.Soldier, owner: 1))   // lone guard, power hidden
            .ToArray();
        var (mem, intents) = Run(View(structs, units), AiPersonality.Opportunist);

        Assert.Equal(1, mem.CampaignTarget);
        Assert.Equal("opportunism", mem.CampaignReason);
        Assert.Single(intents.OfType<DeclareWarIntent>());
    }

    // RETALIATION — an incoming declaration turns a war-capable AI offensive,
    // but NOT with a fresh declaration (the war is already on its way).
    [Fact]
    public void Retaliation_AdoptsCampaign_ButDoesNotRedeclare()
    {
        var view = View(Array.Empty<StructDto>(), Array.Empty<UnitDto>());
        view.PendingWars = new[] { new PendingWarDto { LoId = 0, HiId = 1, EffectiveTick = 6000 } };
        view.Relationships = new[]
            { new RelationshipDto { LoId = 0, HiId = 1, State = (int)RelationshipState.Neutral, PendingEffectiveTick = 6000 } };

        var (opp, oppIntents) = Run(view, AiPersonality.Opportunist);
        Assert.Equal(1, opp.CampaignTarget);
        Assert.Equal("retaliation", opp.CampaignReason);
        Assert.Empty(oppIntents.OfType<DeclareWarIntent>());

        // A Homesteader does NOT counter-invade — it leaves the answer to Defend.
        var (home, homeIntents) = Run(view, AiPersonality.Homesteader);
        Assert.Null(home.CampaignTarget);
        Assert.Empty(homeIntents);
    }

    // Idempotent: with the war already pending, the perception pass re-runs but
    // never re-declares (which would only earn a rejection + a notice).
    [Fact]
    public void DoesNotRedeclare_WhenWarAlreadyPending()
    {
        var view = View(new[] { S(30, 10, StructureKind.LumberCamp, 1) }, Array.Empty<UnitDto>());
        view.PendingWars = new[] { new PendingWarDto { LoId = 0, HiId = 1, EffectiveTick = 6000 } };
        var mem = new AiMemory { CampaignTarget = 1, CampaignReason = "encroachment" };
        var ctx = ThinkContext.Build(view, new AiConfig { Personality = AiPersonality.Warlord }, mem, Now);

        Assert.Empty(new RivalRung().Perceive(ctx).OfType<DeclareWarIntent>());
    }

    // Survival outranks conquest: a hungry colony does not open a war.
    [Fact]
    public void Famine_BlocksProactiveWar()
    {
        var view = View(new[] { S(30, 10, StructureKind.LumberCamp, 1) }, Array.Empty<UnitDto>());
        view.InFamine = true;
        var (mem, intents) = Run(view, AiPersonality.Warlord);

        Assert.Null(mem.CampaignTarget);
        Assert.Empty(intents);
    }

    // Wiring: the declaration ships through the full brain, not just the seam.
    [Fact]
    public void Brain_EmitsDeclaration_ThroughTheLadder()
    {
        var view = View(new[] { S(30, 10, StructureKind.LumberCamp, 1) }, Array.Empty<UnitDto>());
        var mem = new AiMemory();
        var brain = new HomesteaderBrain(new AiConfig { Personality = AiPersonality.Opportunist });
        var decision = brain.Think(view, Now, mem);

        Assert.Contains(decision.Intents, i => i is DeclareWarIntent { TargetId: 1 });
        Assert.Equal(1, mem.CampaignTarget);
    }

    // A defeated rival is no casus belli — the Warlord doesn't march on a corpse.
    [Fact]
    public void DefeatedRival_IsNotATarget()
    {
        var view = View(new[] { S(30, 10, StructureKind.LumberCamp, 1) }, Array.Empty<UnitDto>());
        view.Factions = new[] { new FactionDto { Id = 0 }, new FactionDto { Id = 1, Defeated = true } };
        var (mem, intents) = Run(view, AiPersonality.Warlord);

        Assert.Null(mem.CampaignTarget);
        Assert.Empty(intents);
    }
}
