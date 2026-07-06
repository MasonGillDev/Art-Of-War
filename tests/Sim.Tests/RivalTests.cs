using Sim.Core;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Ai;
using Sim.Server.Ai.Rungs;
using Sim.Server.Wire;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M25 — the Rival: the offensive AI (docs/m25-rival-spec.md). Phase 1
// pins the plumbing the Rival stands on: diplomacy on the wire (the war
// telegraph reaches the human client and the AI brains through the SAME
// channel — the fairness contract) and the deterministic brain-selection
// seam (--rivals K → the highest K AI faction ids).
public class RivalTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public RivalTests(Xunit.Abstractions.ITestOutputHelper output) { _output = output; }

    // Same shape as AiPlayerTests.MakeMatch: a small generated continent,
    // human slot (0) + N AI factions, identical starts.
    private static (Simulation sim, ViewProjector projector, WorldBuild build) MakeMatch(
        int aiPlayers = 1, int mapSeed = 7, int size = 96)
    {
        var opts = new ServerOptions
        {
            MapWidth = size, MapHeight = size, MapSeed = mapSeed, AiPlayers = aiPlayers,
        };
        var build = WorldFactory.Build(opts);
        var sim = new Simulation(build.Spec, seed: 0xA117);
        return (sim, new ViewProjector(build), build);
    }

    // ---- diplomacy on the wire ----------------------------------------------

    [Fact]
    public void Wire_CarriesDiplomacy_RelationshipsAndPendingWars()
    {
        var (sim, projector, _) = MakeMatch(aiPlayers: 1);
        sim.SubmitIntent(5, new DeclareWarIntent(0, 1));
        sim.Run(until: 10);

        // Ground truth: the declaration resolved and anchored a pending war
        // at resolve-tick + the WORLD'S configured Delay (config-derived —
        // never assume the default; the constant is a user balance knob).
        var declaredAt = sim.ResolvedLog.OfType<IntentEvent>()
            .Single(ie => ie.Intent is DeclareWarIntent).At;
        var rel = sim.World.Diplomacy.Relationships[FactionPair.Of(0, 1)];
        var expectedEffective = declaredAt + sim.World.Diplomacy.Config.Delay;
        Assert.Equal(expectedEffective, rel.PendingEffectiveTick);

        // The TARGET's view carries the telegraph (public knowledge — any
        // viewer would; the target is the one whose fairness it protects).
        var view = projector.Project(sim, sim.Now, playerId: 1, reveal: false);

        Assert.Contains(view.Factions, f => f.Id == 0 && !f.Defeated);
        Assert.Contains(view.Factions, f => f.Id == 1 && !f.Defeated);
        Assert.All(view.Factions, f => Assert.True(f.Id >= 0,
            "sentinel owners (bandits/caches/rubble) must never appear as factions"));

        var row = Assert.Single(view.Relationships);
        Assert.Equal(0, row.LoId);
        Assert.Equal(1, row.HiId);
        Assert.Equal(expectedEffective, row.PendingEffectiveTick);

        var war = Assert.Single(view.PendingWars);
        Assert.Equal(expectedEffective, war.EffectiveTick);

        // Run past the telegraph: the war bites, the pending entry clears.
        sim.Run(until: expectedEffective + 1);
        Assert.True(sim.World.Diplomacy.AreHostile(0, 1));
        view = projector.Project(sim, sim.Now, playerId: 1, reveal: false);
        row = Assert.Single(view.Relationships);
        Assert.Equal((int)RelationshipState.Enemy, row.State);
        Assert.Equal(-1L, row.PendingEffectiveTick);
        Assert.Empty(view.PendingWars);
    }

    [Fact]
    public void Wire_IncomingProposals_ArePrivateToTheTarget()
    {
        var (sim, projector, _) = MakeMatch(aiPlayers: 2);
        sim.SubmitIntent(5, new ProposeRelationshipIntent(0, 1, RelationshipState.Ally));
        sim.Run(until: 10);
        Assert.NotEmpty(sim.World.Diplomacy.Proposals); // sanity: it resolved

        var target = projector.Project(sim, sim.Now, playerId: 1, reveal: false);
        var offer = Assert.Single(target.IncomingProposals);
        Assert.Equal(0, offer.ProposerId);
        Assert.Equal((int)RelationshipState.Ally, offer.DesiredState);

        // A third party sees the factions but NOT the private offer.
        var bystander = projector.Project(sim, sim.Now, playerId: 2, reveal: false);
        Assert.Empty(bystander.IncomingProposals);
    }

    [Fact]
    public void Wire_Diplomacy_IsPureRead()
    {
        var (sim, projector, _) = MakeMatch(aiPlayers: 2);
        sim.SubmitIntent(5, new DeclareWarIntent(0, 1));
        sim.SubmitIntent(5, new ProposeRelationshipIntent(0, 2, RelationshipState.Ally));
        sim.Run(until: 10);

        // Projection with live diplomacy rows must never move the sim hash —
        // same discipline as every other pure-read wall (View.BuildPlayerView,
        // Road.ConditionAt).
        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++)
        {
            projector.Project(sim, sim.Now, playerId: 0, reveal: false);
            projector.Project(sim, sim.Now, playerId: 1, reveal: false);
            projector.Project(sim, sim.Now, playerId: 2, reveal: true);
        }
        Assert.Equal(before, Snapshot.Hash(sim));
    }

    // ---- brain-selection seam -------------------------------------------------

    [Fact]
    public void RivalAssignment_HighestFactionIds_Deterministic()
    {
        // One world, three hosts: assignment is a pure function of the
        // faction ids present + RivalCount.
        var build = WorldFactory.Build(new ServerOptions
        {
            MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 3,
        });

        static List<(int Id, BrainKind Kind)> KindsOf(GameHost host) =>
            host.AiDrivers.Select(d => (d.PlayerId, d.Kind)).OrderBy(x => x.PlayerId).ToList();

        using var none = new GameHost(build, 0xA117, 20.0,
            aiConfig: new AiConfig { RivalCount = 0 });
        Assert.All(KindsOf(none), x => Assert.Equal(BrainKind.Homesteader, x.Kind));

        using var one = new GameHost(build, 0xA117, 20.0,
            aiConfig: new AiConfig { RivalCount = 1 });
        var kinds = KindsOf(one);
        Assert.True(kinds.Count >= 2, "expected at least two AI factions placed");
        // Exactly one Rival, and it holds the HIGHEST faction id — faction 1
        // (the balance lab's baseline) must stay a Homesteader.
        Assert.Equal(BrainKind.Rival, kinds[^1].Kind);
        Assert.All(kinds.Take(kinds.Count - 1),
            x => Assert.Equal(BrainKind.Homesteader, x.Kind));

        // Over-asking clamps: every AI faction can be a Rival, never more.
        using var all = new GameHost(build, 0xA117, 20.0,
            aiConfig: new AiConfig { RivalCount = 99 });
        Assert.All(KindsOf(all), x => Assert.Equal(BrainKind.Rival, x.Kind));
    }

    // ---- Phase 2: enemy intel + the threat extension ------------------------
    // These tests craft ViewDtos directly — the brain's whole world IS the
    // DTO, so a hand-built view is a complete, legal test fixture.

    private static StructDto Struct(int x, int y, StructureKind kind, int owner) =>
        new() { X = x, Y = y, Kind = (int)kind, OwnerId = owner };

    private static UnitDto Unit(int id, int x, int y, UnitRole role, int owner) =>
        new() { Id = id, X = x, Y = y, Role = (int)role, OwnerId = owner,
                Activity = (int)Activity.Idle, Age = 20 };

    private static ViewDto CraftView(int playerId,
        StructDto[] structures, UnitDto[] units,
        FactionDto[]? factions = null, RelationshipDto[]? relationships = null,
        (int X, int Y)[]? visible = null)
        => new()
        {
            PlayerId = playerId, Width = 64, Height = 64,
            Structures = structures, Units = units,
            Factions = factions ?? [],
            Relationships = relationships ?? [],
            Visible = (visible ?? []).Select(t => new TileDto { X = t.X, Y = t.Y }).ToArray(),
        };

    [Fact]
    public void EnemyIntel_RemembersStructures_ClearsOnObservedRazing()
    {
        var cfg = new AiConfig();
        var mem = new AiMemory { ProbeLeg = 7 };
        var ownCastle = Struct(5, 5, StructureKind.Castle, owner: 0);

        // Sight the enemy: castle + farm land in the memory; the castle
        // discovery refreshes the probe budget.
        var seen = CraftView(0,
            [ownCastle, Struct(30, 30, StructureKind.Castle, 2), Struct(31, 30, StructureKind.Farm, 2)],
            [], visible: [(30, 30), (31, 30)]);
        EnemyIntel.Perceive(ThinkContext.Build(seen, cfg, mem, now: 100));
        Assert.Equal(2, mem.KnownEnemyStructures.Count);
        Assert.Equal(((30, 30), 100L), mem.KnownEnemyCastles[2]);
        Assert.Equal(0, mem.ProbeLeg);

        // Fogged again: nothing visible, structures absent from the view —
        // memory HOLDS (fog is not evidence of absence).
        var fogged = CraftView(0, [ownCastle], []);
        EnemyIntel.Perceive(ThinkContext.Build(fogged, cfg, mem, now: 200));
        Assert.Equal(2, mem.KnownEnemyStructures.Count);
        Assert.True(mem.KnownEnemyCastles.ContainsKey(2));

        // Re-observed razed: the castle tile now holds Rubble, the farm
        // tile is bare — both clear, and the fallen castle refreshes the
        // probe budget again.
        mem.ProbeLeg = 5;
        var razed = CraftView(0,
            [ownCastle, Struct(30, 30, StructureKind.Rubble, -3)],
            [], visible: [(30, 30), (31, 30)]);
        EnemyIntel.Perceive(ThinkContext.Build(razed, cfg, mem, now: 300));
        Assert.Empty(mem.KnownEnemyStructures);
        Assert.Empty(mem.KnownEnemyCastles);
        Assert.Equal(0, mem.ProbeLeg);
    }

    [Fact]
    public void Defend_TreatsDeclaredWarUnitsAsHostiles()
    {
        var cfg = new AiConfig();
        var ownCastle = Struct(5, 5, StructureKind.Castle, owner: 0);
        var factions = new[] { new FactionDto { Id = 0 }, new FactionDto { Id = 1 } };
        var intruder = Unit(50, 10, 10, UnitRole.Soldier, owner: 1);

        // NEUTRAL neighbor: their soldier walking your fields is not a
        // threat — the memory stays empty.
        var mem = new AiMemory();
        var neutral = CraftView(0, [ownCastle], [intruder], factions, visible: [(10, 10)]);
        new DefendRung().TryClaim(ThinkContext.Build(neutral, cfg, mem, now: 100));
        Assert.Empty(mem.SightedHostiles);

        // PENDING war: still not hostile — the telegraph window is prep
        // time, not open season (AreHostile hasn't flipped).
        mem = new AiMemory();
        var pending = CraftView(0, [ownCastle], [intruder], factions,
            [new RelationshipDto { LoId = 0, HiId = 1,
                State = (int)RelationshipState.Neutral, PendingEffectiveTick = 5000 }],
            visible: [(10, 10)]);
        new DefendRung().TryClaim(ThinkContext.Build(pending, cfg, mem, now: 100));
        Assert.Empty(mem.SightedHostiles);

        // EFFECTIVE war: the same soldier is now a counted hostile —
        // the extension DefendRung's comment always promised.
        mem = new AiMemory();
        var atWar = CraftView(0, [ownCastle], [intruder], factions,
            [new RelationshipDto { LoId = 0, HiId = 1, State = (int)RelationshipState.Enemy }],
            visible: [(10, 10)]);
        new DefendRung().TryClaim(ThinkContext.Build(atWar, cfg, mem, now: 100));
        Assert.Equal((100L, 1), mem.SightedHostiles[(10, 10)]);
    }

    [Fact]
    public void Probe_FiresOnlyWhileBlind_AndWithinBudget()
    {
        var cfg = new AiConfig();
        var ownCastle = Struct(5, 5, StructureKind.Castle, owner: 0);
        var scout = Unit(9, 5, 6, UnitRole.Scout, owner: 0);
        var factions = new[] { new FactionDto { Id = 0 }, new FactionDto { Id = 1 } };
        var view = CraftView(0, [ownCastle], [scout], factions);

        // Blind (no castle intel on faction 1): probe fires, spends a leg.
        var mem = new AiMemory();
        var d = new ProbeRung().TryClaim(ThinkContext.Build(view, cfg, mem, now: 100));
        Assert.NotNull(d);
        Assert.Equal("probe", d!.Rung);
        Assert.Equal(1, mem.ProbeLeg);

        // Fresh intel on every living foe: the ledger can plan — no probe.
        mem = new AiMemory();
        mem.KnownEnemyCastles[1] = ((40, 40), 100L);
        Assert.Null(new ProbeRung().TryClaim(ThinkContext.Build(view, cfg, mem, now: 100)));

        // STALE intel re-opens the job…
        mem = new AiMemory();
        mem.KnownEnemyCastles[1] = ((40, 40), 100L);
        Assert.NotNull(new ProbeRung().TryClaim(
            ThinkContext.Build(view, cfg, mem, now: 100 + cfg.IntelStaleTicks + 1)));

        // …but never past the budget (ledger #9)…
        mem = new AiMemory { ProbeLeg = cfg.ProbeLegBudget };
        Assert.Null(new ProbeRung().TryClaim(ThinkContext.Build(view, cfg, mem, now: 100)));

        // …and never toward the defeated (inert wreckage isn't a foe).
        mem = new AiMemory();
        var beaten = CraftView(0, [ownCastle], [scout],
            [new FactionDto { Id = 0 }, new FactionDto { Id = 1, Defeated = true }]);
        Assert.Null(new ProbeRung().TryClaim(ThinkContext.Build(beaten, cfg, mem, now: 100)));
    }

    // ---- Phase 3: war policy + the offense budget ----------------------------

    private static UnitDto Soldier(int id, int x, int y, int owner, int power = 3)
    {
        var u = Unit(id, x, y, UnitRole.Soldier, owner);
        u.Power = power;
        return u;
    }

    // A prosperous, garrisoned colony at peace with fresh intel on
    // faction 1's castle at (40,40) — every declare gate open unless a
    // case closes one.
    private static (ViewDto view, AiMemory mem, AiConfig cfg) WarBench(
        int population = 36, int soldiers = 6, long intelAge = 0)
    {
        var cfg = new AiConfig();
        var structures = new List<StructDto> { Struct(5, 5, StructureKind.Castle, 0) };
        var units = new List<UnitDto>();
        for (var i = 0; i < soldiers; i++)
            units.Add(Soldier(100 + i, 5, 5, owner: 0));
        var view = CraftView(0, structures.ToArray(), units.ToArray(),
            [new FactionDto { Id = 0 }, new FactionDto { Id = 1 }]);
        view.Population = population;
        view.CastleFood = 1000;
        view.FoodRunwayTicks = 100 * Time.Day;
        var mem = new AiMemory();
        mem.KnownEnemyCastles[1] = ((40, 40), 1000L - intelAge);
        return (view, mem, cfg);
    }

    [Fact]
    public void Rival_DeclaresWar_OnlyAboveFloorAndAdvantage()
    {
        // All gates open: the advantage prices MOBILIZABLE power (pop 36 →
        // min(peacetime 4 + 36/6, 36/4) = 9 bare soldiers = 27pw) against
        // the assumed garrison (12pw × 150% = 18) — war is declared. The
        // standing army only gates the garrison check; the offense budget
        // is what the telegraph window will raise.
        var (view, mem, cfg) = WarBench();
        var d = new WarRung().TryClaim(ThinkContext.Build(view, cfg, mem, now: 1000));
        Assert.NotNull(d);
        var declare = Assert.IsType<DeclareWarIntent>(Assert.Single(d!.Intents));
        Assert.Equal(0, declare.DeclarerId);
        Assert.Equal(1, declare.TargetId);
        Assert.Equal(1, mem.WarTarget);

        // Below the population floor: young colonies homestead.
        (view, mem, cfg) = WarBench(population: cfg.CampaignPopulationFloor - 1);
        Assert.Null(new WarRung().TryClaim(ThinkContext.Build(view, cfg, mem, now: 1000)));

        // Garrison under its peacetime quota: the war chest never raids
        // the home guard (quota = min(4 + 1/8, 36/8) = 4).
        (view, mem, cfg) = WarBench(soldiers: 3);
        Assert.Null(new WarRung().TryClaim(ThinkContext.Build(view, cfg, mem, now: 1000)));

        // Stale intel is not a war plan.
        (view, mem, cfg) = WarBench(intelAge: cfg.IntelStaleTicks + 1);
        Assert.Null(new WarRung().TryClaim(ThinkContext.Build(view, cfg, mem, now: 1000)));

        // Visible strength at their castle kills the advantage: 8 enemy
        // soldiers (24pw) × 150% = 36 > our 27pw mobilizable ceiling.
        (view, mem, cfg) = WarBench();
        view.Units = view.Units.Concat(
            Enumerable.Range(0, 8).Select(i => Soldier(200 + i, 40, 41, owner: 1)))
            .ToArray();
        Assert.Null(new WarRung().TryClaim(ThinkContext.Build(view, cfg, mem, now: 1000)));

        // Famine closes the book outright.
        (view, mem, cfg) = WarBench();
        view.InFamine = true;
        Assert.Null(new WarRung().TryClaim(ThinkContext.Build(view, cfg, mem, now: 1000)));
    }

    [Fact]
    public void Rival_OffenseBudget_RespectsWartimeShare()
    {
        // Pop 24, mobilizing (pending war): peacetime = min(4+1/8, 3) = 3,
        // offense adds 24/6 = 4, wartime ceiling caps at 24/4 = 6.
        var cfg = new AiConfig();
        StructDto[] structures =
        [
            Struct(5, 5, StructureKind.Castle, 0),
            Struct(6, 5, StructureKind.Barracks, 0),
            Struct(5, 6, StructureKind.School, 0),
        ];
        var factions = new[] { new FactionDto { Id = 0 }, new FactionDto { Id = 1 } };
        var mobilizing = new[] { new RelationshipDto { LoId = 0, HiId = 1,
            State = (int)RelationshipState.Neutral, PendingEffectiveTick = 9999 } };

        ViewDto Bench(int soldierCount)
        {
            var units = new List<UnitDto>();
            for (var i = 0; i < soldierCount; i++)
                units.Add(Soldier(100 + i, 6, 5, owner: 0));
            units.Add(Unit(50, 5, 5, UnitRole.None, owner: 0));   // a recruitable native
            var v = CraftView(0, structures, units.ToArray(), factions, mobilizing);
            v.Population = 24;
            v.CastleFood = 1000;
            v.FoodRunwayTicks = 100 * Time.Day;
            return v;
        }

        // Below the ceiling the offense budget recruits…
        var d = new MusterRung(offense: true)
            .TryClaim(ThinkContext.Build(Bench(5), cfg, new AiMemory(), now: 100));
        Assert.NotNull(d);
        Assert.Equal("muster", d!.Rung);

        // …at the ceiling it stops (and does NOT demobilize its own army)…
        Assert.Null(new MusterRung(offense: true)
            .TryClaim(ThinkContext.Build(Bench(6), cfg, new AiMemory(), now: 100)));

        // …and the same view through a HOMESTEADER's bare Muster reads 6
        // soldiers as a levy to unwind (quota 3, no offense budget).
        var home = new MusterRung()
            .TryClaim(ThinkContext.Build(Bench(6), cfg, new AiMemory(), now: 100));
        Assert.NotNull(home);
        Assert.Contains("veteran", home!.Why);   // demob path: walking to / at the School
    }

    [Fact]
    public void Rival_SuesForPeace_WhenLosing()
    {
        // At war with 1, zero soldiers left: losing by any measure.
        var cfg = new AiConfig();
        var factions = new[] { new FactionDto { Id = 0 }, new FactionDto { Id = 1 } };
        var atWar = new[] { new RelationshipDto { LoId = 0, HiId = 1,
            State = (int)RelationshipState.Enemy } };
        var view = CraftView(0, [Struct(5, 5, StructureKind.Castle, 0)], [], factions, atWar);
        view.Population = 20;
        view.IncomingProposals =
            [new ProposalDto { Id = 7, ProposerId = 1, TargetId = 0,
                DesiredState = (int)RelationshipState.Neutral, ExpiryTick = 9999 }];

        var mem = new AiMemory();
        var d = new WarRung().TryClaim(ThinkContext.Build(view, cfg, mem, now: 500));
        Assert.NotNull(d);
        var accept = d!.Intents.OfType<RespondToProposalIntent>().Single();
        Assert.Equal(7, accept.ProposalId);
        Assert.True(accept.Accept);
        var sue = d.Intents.OfType<ProposeRelationshipIntent>().Single();
        Assert.Equal(1, sue.TargetId);
        Assert.Equal(RelationshipState.Neutral, sue.DesiredState);

        // The cooldown throttles the re-proposal (offers aren't visible
        // to their proposer — memory is the only brake)…
        view.IncomingProposals = [];
        var d2 = new WarRung().TryClaim(ThinkContext.Build(view, cfg, mem, now: 500 + Time.Hour));
        Assert.True(d2 is null || !d2.Intents.OfType<ProposeRelationshipIntent>().Any());

        // …and a WINNING Rival ignores peace entirely (lazy expiry buries
        // the offer).
        var strong = CraftView(0,
            [Struct(5, 5, StructureKind.Castle, 0)],
            Enumerable.Range(0, 8).Select(i => Soldier(100 + i, 5, 5, owner: 0)).ToArray(),
            factions, atWar);
        strong.Population = 30;
        strong.IncomingProposals =
            [new ProposalDto { Id = 8, ProposerId = 1, TargetId = 0,
                DesiredState = (int)RelationshipState.Neutral, ExpiryTick = 9999 }];
        Assert.Null(new WarRung().TryClaim(ThinkContext.Build(strong, cfg, new AiMemory(), now: 500)));
    }

    // ---- Phase 4: the campaign --------------------------------------------

    private static void RunMatch(Simulation sim, ViewProjector projector,
        IReadOnlyList<AiPlayerDriver> drivers, long until, long step)
    {
        for (var t = sim.Now; t <= until; t += step)
        {
            sim.Run(until: t);
            foreach (var dr in drivers) dr.Think(sim, projector, t);
        }
        sim.Run(until: until + step);
    }

    private static Castle CastleOf(Simulation sim, int ownerId) =>
        sim.World.Structures.Values.OfType<Castle>().Single(c => c.OwnerId == ownerId);

    [Fact]
    public void Conquer_LeavesTheGarrison_AndWaitsOutTheTelegraph()
    {
        // 10 soldiers, pop 48: peacetime = min(4 + 2/8, 6) = 4, so the
        // campaign claims exactly 6 — the garrison never marches.
        var cfg = new AiConfig();
        StructDto[] structures =
        [
            Struct(5, 5, StructureKind.Castle, 0),
            Struct(6, 5, StructureKind.Barracks, 0),
        ];
        var factions = new[] { new FactionDto { Id = 0 }, new FactionDto { Id = 1 } };
        var soldiers = Enumerable.Range(0, 10)
            .Select(i => Soldier(100 + i, 6, 5, owner: 0)).ToArray();  // all at the rally

        ViewDto Bench(RelationshipDto rel)
        {
            var v = CraftView(0, structures, soldiers, factions, [rel]);
            v.Population = 48;
            return v;
        }
        var mem = new AiMemory { WarTarget = 1 };
        mem.KnownEnemyCastles[1] = ((40, 40), 100L);

        // Telegraph running: the roster assembles but NOBODY marches —
        // no shot (not even a step toward one) before AreHostile is true.
        var pending = Bench(new RelationshipDto { LoId = 0, HiId = 1,
            State = (int)RelationshipState.Neutral, PendingEffectiveTick = 9999 });
        Assert.Null(new ConquerRung().TryClaim(ThinkContext.Build(pending, cfg, mem, now: 100)));
        Assert.Equal(6, mem.CampaignSoldiers.Count);
        Assert.False(mem.CampaignLaunched);

        // War effective: LAUNCH — six march orders onto the castle tile
        // (GO gate: 18pw ×100 ≥ assumed 12pw ×150, exactly).
        var effective = Bench(new RelationshipDto { LoId = 0, HiId = 1,
            State = (int)RelationshipState.Enemy });
        var d = new ConquerRung().TryClaim(ThinkContext.Build(effective, cfg, mem, now: 200));
        Assert.NotNull(d);
        Assert.Contains("LAUNCH", d!.Why);
        Assert.Equal(6, d.Intents.Count);
        Assert.All(d.Intents, i =>
        {
            var m = Assert.IsType<MoveIntent>(i);
            Assert.Equal(new TileCoord(40, 40), m.Destination);
        });
        Assert.True(mem.CampaignLaunched);
    }

    [Fact]
    public void Conquer_StandsDown_WhenTheTargetFalls()
    {
        // Never attack the defeated: the roster clears and walks home.
        var cfg = new AiConfig();
        var view = CraftView(0,
            [Struct(5, 5, StructureKind.Castle, 0)],
            [Soldier(100, 20, 20, owner: 0), Soldier(101, 20, 20, owner: 0)],
            [new FactionDto { Id = 0 }, new FactionDto { Id = 1, Defeated = true }]);
        var mem = new AiMemory { WarTarget = 1, CampaignLaunched = true };
        mem.CampaignSoldiers.Add(100);
        mem.CampaignSoldiers.Add(101);
        mem.KnownEnemyCastles[1] = ((40, 40), 100L);

        var d = new ConquerRung().TryClaim(ThinkContext.Build(view, cfg, mem, now: 200));
        Assert.NotNull(d);
        Assert.Contains("stands down", d!.Why);
        Assert.Equal(2, d.Intents.Count);
        Assert.All(d.Intents, i =>
            Assert.Equal(new TileCoord(5, 5), Assert.IsType<MoveIntent>(i).Destination));
        Assert.Empty(mem.CampaignSoldiers);
        Assert.False(mem.CampaignLaunched);
    }

    // ---- THE HEADLINE: full conquest, end to end ----------------------------

    [Fact]
    public void Rival_RazesUndefendedCastle_GameOverFires()
    {
        var (sim, projector, build) = MakeMatch(aiPlayers: 1);
        var c0 = build.Spec.FactionStarts.Single(f => f.OwnerId == 0).CastlePosition;
        var c1 = build.Spec.FactionStarts.Single(f => f.OwnerId == 1).CastlePosition;

        // Arm the Rival directly — the muster ECONOMY is the Phase-5
        // lab's subject; this test pins the campaign MACHINE (declare →
        // telegraph → assemble → march → siege → raze → game over).
        for (var i = 0; i < 10; i++)
            sim.World.AddUnit(new Unit(900 + i, c1) { Role = UnitRole.Soldier, OwnerId = 1 });
        // A forward observer keeps the enemy castle on the intel map.
        sim.World.AddUnit(new Unit(950, new TileCoord(c0.X + 1, c0.Y))
            { Role = UnitRole.Scout, OwnerId = 1 });
        // Feed the +11 non-producing mouths through the telegraph window.
        CastleOf(sim, 1).Holdings[Resource.Food] = 4000;
        // A short siege: HP-linear damage is SiegeDamageTests' pin — the
        // round count doesn't change the machine under test.
        CastleOf(sim, 0).Health = 5;

        // The gates are lab knobs; the machine is what's pinned here.
        var cfg = new AiConfig
        {
            CampaignPopulationFloor = 1,
            WarAdvantageRatioPercent = 100,
            AttackOvermatchPercent = 100,
        };
        var rival = new AiPlayerDriver(1, cfg, BrainKind.Rival);

        var delay = sim.World.Diplomacy.Config.Delay;   // config-derived, never assumed
        RunMatch(sim, projector, new[] { rival },
            until: delay + 12 * Time.Day, step: cfg.ThinkPeriodTicks);

        // The war was declared, and no combat fired before it became
        // effective — the telegraph IS the fairness contract.
        var declare = sim.ResolvedLog.OfType<IntentEvent>()
            .First(ie => ie.Intent is DeclareWarIntent && !ie.Outcome.IsRejected);
        var effective = declare.At + delay;
        var rounds = sim.ResolvedLog.OfType<Sim.Core.Combat.CombatRoundEvent>().ToList();
        Assert.True(rounds.Count > 0, $"no combat ever fired — trace:\n{rival.Trace.Dump()}");
        Assert.True(rounds.Min(r => r.At) >= effective,
            $"combat at {rounds.Min(r => r.At)} before the war took effect at {effective}");

        // The castle fell, the player fell, the game ended.
        Assert.IsType<Rubble>(sim.World.Structures[c0]);
        Assert.True(sim.World.Players[0].Defeated, $"player 0 not defeated — trace:\n{rival.Trace.Dump()}");
        var over = sim.ResolvedLog.OfType<Sim.Core.Sieges.GameOverEvent>().Single();
        Assert.Equal(1, over.WinnerId);
    }

    [Fact]
    public void Rival_DoesNotStarveItsOwnColony()
    {
        // The Sparta pin (ledger #11), rephrased for the Rival: at
        // DEFAULT config a young Rival colony sits below the war floor
        // and must homestead — the war rungs may cost it NOTHING. 60
        // days, both factions driven, the Rival's curve must hold.
        var (sim, projector, _) = MakeMatch(aiPlayers: 1);
        var cfg = new AiConfig();
        var drivers = new[]
        {
            new AiPlayerDriver(0, cfg),
            new AiPlayerDriver(1, cfg, BrainKind.Rival),
        };
        RunMatch(sim, projector, drivers, until: 60 * Time.Day, step: cfg.ThinkPeriodTicks);

        var castle = CastleOf(sim, 1);
        var pop = sim.World.Players[1].PopulationCount;
        var trace = drivers[1].Trace;
        Assert.True(castle.FoodDebt == 0 && castle.FamineStartTick is null,
            $"the Rival colony hit famine (debt={castle.FoodDebt}, pop={pop}) — " +
            $"trace tail:\n{trace.Dump()}");
        Assert.True(pop >= 14, $"the Rival colony shrank to {pop} — trace tail:\n{trace.Dump()}");
        Assert.DoesNotContain(sim.ResolvedLog.OfType<Sim.Core.Food.StarvationDeathEvent>(),
            e => e.HomeAt == castle.At && e.Outcome is null);
    }

    // ---- Phase 5: raids, the lab, and the replay headline --------------------

    [Fact]
    public void Raid_LootLoop_LearnsDryTargets()
    {
        // Pop 48, castle only: peacetime = min(4 + 1/8, 6) = 4; six
        // soldiers → two spare hands → a party of 2 (RaidPartySize 3
        // bounds, availability decides).
        var cfg = new AiConfig();
        var factions = new[] { new FactionDto { Id = 0 }, new FactionDto { Id = 1 } };
        var atWar = new[] { new RelationshipDto { LoId = 0, HiId = 1,
            State = (int)RelationshipState.Enemy } };
        var mem = new AiMemory { WarTarget = 1 };
        mem.KnownEnemyStructures[(30, 30)] = (100L, (int)StructureKind.Farm, 1);
        mem.KnownEnemyStructures[(32, 30)] = (100L, (int)StructureKind.Mine, 1);

        ViewDto Bench(params UnitDto[] units)
        {
            var v = CraftView(0, [Struct(5, 5, StructureKind.Castle, 0)], units, factions, atWar);
            v.Population = 48;
            return v;
        }
        UnitDto[] AtCastle(int n) =>
            Enumerable.Range(0, n).Select(i => Soldier(100 + i, 5, 5, owner: 0)).ToArray();

        // Designate + march on the NEAREST extractor (the farm).
        var d = new RaidRung().TryClaim(ThinkContext.Build(Bench(AtCastle(6)), cfg, mem, now: 200));
        Assert.NotNull(d);
        Assert.Equal(2, mem.RaidParty.Count);
        Assert.All(d!.Intents, i =>
            Assert.Equal(new TileCoord(30, 30), Assert.IsType<MoveIntent>(i).Destination));

        // Standing on it empty-handed: strike — LoadCargo names the
        // catalog's output for the KIND (Farm → Food; holdings are fogged).
        var party = mem.RaidParty.OrderBy(id => id).ToArray();
        var onFarm = AtCastle(6);
        foreach (var u in onFarm.Where(u => party.Contains(u.Id))) { u.X = 30; u.Y = 30; }
        d = new RaidRung().TryClaim(ThinkContext.Build(Bench(onFarm), cfg, mem, now: 260));
        Assert.NotNull(d);
        Assert.All(d!.Intents, i =>
            Assert.Equal(Resource.Food, Assert.IsType<LoadCargoIntent>(i).Resource));

        // Still empty-handed next think: the farm is DRY — struck from
        // this war's list; the think after, the party retargets the mine.
        Assert.Null(new RaidRung().TryClaim(ThinkContext.Build(Bench(onFarm), cfg, mem, now: 320)));
        Assert.Contains((30, 30), mem.DryRaidTargets);
        d = new RaidRung().TryClaim(ThinkContext.Build(Bench(onFarm), cfg, mem, now: 380));
        Assert.NotNull(d);
        Assert.All(d!.Intents, i =>
            Assert.Equal(new TileCoord(32, 30), Assert.IsType<MoveIntent>(i).Destination));

        // Loot walks home and is handed over.
        var haul = AtCastle(6);
        var carrierHome = haul.Single(u => u.Id == party[0]);
        carrierHome.CargoAmount = 5; carrierHome.CargoResource = (int)Resource.Food;
        var carrierAfield = haul.Single(u => u.Id == party[1]);
        carrierAfield.X = 20; carrierAfield.Y = 20;
        carrierAfield.CargoAmount = 5; carrierAfield.CargoResource = (int)Resource.Ore;
        d = new RaidRung().TryClaim(ThinkContext.Build(Bench(haul), cfg, mem, now: 440));
        Assert.NotNull(d);
        Assert.Equal(party[0], d!.Intents.OfType<UnloadCargoIntent>().Single().UnitId);
        Assert.Equal(new TileCoord(5, 5),
            d.Intents.OfType<MoveIntent>().Single(m => m.UnitId == party[1]).Destination);
    }

    [Fact]
    public void Raid_OnlyInEffectiveWar_AndOnlyWhenDoctrineSaysSo()
    {
        var cfg = new AiConfig();
        var factions = new[] { new FactionDto { Id = 0 }, new FactionDto { Id = 1 } };
        var soldiers = Enumerable.Range(0, 6)
            .Select(i => Soldier(100 + i, 5, 5, owner: 0)).ToArray();

        ViewDto Bench(RelationshipDto[] rels)
        {
            var v = CraftView(0, [Struct(5, 5, StructureKind.Castle, 0)],
                soldiers, factions, rels);
            v.Population = 48;
            return v;
        }

        // Telegraph running: NO looting before AreHostile — the pending
        // window is prep time, not open season.
        var mem = new AiMemory { WarTarget = 1 };
        mem.KnownEnemyStructures[(30, 30)] = (100L, (int)StructureKind.Farm, 1);
        Assert.Null(new RaidRung().TryClaim(ThinkContext.Build(
            Bench([new RelationshipDto { LoId = 0, HiId = 1,
                State = (int)RelationshipState.Neutral, PendingEffectiveTick = 9000 }]),
            cfg, mem, now: 200)));
        Assert.Empty(mem.RaidParty);

        // RaidPartySize 0 = the siege-rush doctrine: no party, ever.
        var atWar = new[] { new RelationshipDto { LoId = 0, HiId = 1,
            State = (int)RelationshipState.Enemy } };
        mem = new AiMemory { WarTarget = 1 };
        mem.KnownEnemyStructures[(30, 30)] = (100L, (int)StructureKind.Farm, 1);
        Assert.Null(new RaidRung().TryClaim(ThinkContext.Build(
            Bench(atWar), new AiConfig { RaidPartySize = 0 }, mem, now: 200)));
        Assert.Empty(mem.RaidParty);

        // Campaign launched: the party stands down and walks home.
        mem = new AiMemory { WarTarget = 1, CampaignLaunched = true };
        mem.RaidParty.Add(100);
        var afield = soldiers.Select(s => s).ToArray();
        afield[0].X = 20; afield[0].Y = 20;
        var d = new RaidRung().TryClaim(ThinkContext.Build(Bench(atWar), cfg, mem, now: 200));
        Assert.NotNull(d);
        Assert.Contains("stands down", d!.Why);
        Assert.Empty(mem.RaidParty);
    }

    [Fact]
    public void RivalVsHomesteader_WarEndsDecisively()
    {
        // The lab match: a Rival with an early war chest against a stock
        // Homesteader. However it swings — conquest, or a losing Rival
        // suing for the peace the Homesteader now ANSWERS — the war must
        // END. 40 game-days; a forever-war fails.
        var (sim, projector, build) = MakeMatch(aiPlayers: 1);
        sim.World.Diplomacy.RestoreConfig(
            new DiplomacyConfig(Delay: 2 * Time.Day, ProposalExpiryTicks: Time.Day));
        var c0 = build.Spec.FactionStarts.Single(f => f.OwnerId == 0).CastlePosition;
        var c1 = build.Spec.FactionStarts.Single(f => f.OwnerId == 1).CastlePosition;
        // A war chest big enough to be DECISIVE against a 14-body shield
        // wall (recall doctrine sends the defender's civilians to the
        // castle tile — they shield it while they live).
        for (var i = 0; i < 12; i++)
            sim.World.AddUnit(new Unit(900 + i, c1) { Role = UnitRole.Soldier, OwnerId = 1 });
        sim.World.AddUnit(new Unit(950, new TileCoord(c0.X + 1, c0.Y))
            { Role = UnitRole.Scout, OwnerId = 1 });
        CastleOf(sim, 1).Holdings[Resource.Food] = 3000;

        var rivalCfg = new AiConfig
        {
            CampaignPopulationFloor = 12,
            WarAdvantageRatioPercent = 100,
            AttackOvermatchPercent = 100,
            AssumedGarrisonPower = 3,
        };
        var drivers = new[]
        {
            new AiPlayerDriver(0, new AiConfig()),
            new AiPlayerDriver(1, rivalCfg, BrainKind.Rival),
        };
        RunMatch(sim, projector, drivers, until: 40 * Time.Day,
            step: rivalCfg.ThinkPeriodTicks);

        var declared = sim.ResolvedLog.OfType<IntentEvent>()
            .Any(ie => ie.Intent is DeclareWarIntent && !ie.Outcome.IsRejected);
        var d0 = sim.World.Players[0].Defeated;
        var d1 = sim.World.Players[1].Defeated;
        var stillAtWar = sim.World.Diplomacy.AreHostile(0, 1) && !d0 && !d1;
        _output.WriteLine($"war declared: {declared}; defeated: 0={d0} 1={d1}; " +
            $"at war at day 40: {stillAtWar}; pops: {sim.World.Players[0].PopulationCount}/" +
            $"{sim.World.Players[1].PopulationCount}");
        _output.WriteLine("rival trace tail:\n" + drivers[1].Trace.Dump());
        Assert.True(declared, "the armed Rival never declared war");
        Assert.False(stillAtWar, "forever-war: 40 days on and neither conquest nor peace");
    }

    [Fact]
    public void Rivals_GoToWar_Organically_LabReport()
    {
        // The live-playtest regression (2026-07-06): NO injected soldiers,
        // NO lowered gates — a Rival must reach its war through its own
        // economy: muster the garrison, probe out the neighbor, clear the
        // MOBILIZABLE-power advantage bar (the fix this test pins — the
        // first cut gated on the STANDING army, which the peacetime quota
        // caps below the bar, while the offense budget waited on the war:
        // a chicken-and-egg that kept every organic Rival at peace).
        // Two Rivals + a Homesteader, default knobs, 250 game-days.
        var (sim, projector, _) = MakeMatch(aiPlayers: 2);
        var cfg = new AiConfig();
        var drivers = new[]
        {
            new AiPlayerDriver(0, cfg),
            new AiPlayerDriver(1, cfg, BrainKind.Rival),
            new AiPlayerDriver(2, cfg, BrainKind.Rival),
        };
        RunMatch(sim, projector, drivers, until: 250 * Time.Day, step: cfg.ThinkPeriodTicks);

        var declare = sim.ResolvedLog.OfType<IntentEvent>()
            .FirstOrDefault(ie => ie.Intent is DeclareWarIntent && !ie.Outcome.IsRejected);
        var firstCombat = sim.ResolvedLog.OfType<Sim.Core.Combat.CombatRoundEvent>()
            .Select(e => (long?)e.At).FirstOrDefault();   // no bandits here — combat = the war
        _output.WriteLine($"first declaration: {(declare is null ? "never" : $"day {declare.At / Time.Day}")}");
        _output.WriteLine($"first combat: {(firstCombat is null ? "never" : $"day {firstCombat / Time.Day}")}");
        foreach (var id in new[] { 0, 1, 2 })
            _output.WriteLine($"faction {id}: pop {sim.World.Players[id].PopulationCount}, " +
                $"defeated={sim.World.Players[id].Defeated}");
        Assert.True(declare is not null,
            "no Rival declared war in 250 organic game-days — the mobilization interlock is back");
    }

    [Fact]
    public void Rival_ReplayFromIntentLog_HashesMatch()
    {
        // The M16/M17 determinism proof, re-run with all THREE driver
        // kinds interleaved: bandits + Homesteader + Rival. Chronological
        // batch replay (docs/bandits.md): all of a tick's intents submit
        // before the sim runs on, or same-tick Seq order diverges.
        var (live, projector, build) = MakeMatch(aiPlayers: 1);
        var shortDelay = new DiplomacyConfig(Delay: 2 * Time.Day, ProposalExpiryTicks: Time.Day);
        live.World.Diplomacy.RestoreConfig(shortDelay);
        var c0 = build.Spec.FactionStarts.Single(f => f.OwnerId == 0).CastlePosition;
        var c1 = build.Spec.FactionStarts.Single(f => f.OwnerId == 1).CastlePosition;
        void Arm(Simulation sim)
        {
            for (var i = 0; i < 6; i++)
                sim.World.AddUnit(new Unit(900 + i, c1) { Role = UnitRole.Soldier, OwnerId = 1 });
            sim.World.AddUnit(new Unit(950, new TileCoord(c0.X + 1, c0.Y))
                { Role = UnitRole.Scout, OwnerId = 1 });
            CastleOf(sim, 1).Holdings[Resource.Food] = 3000;
        }
        Arm(live);

        var rivalCfg = new AiConfig
        {
            CampaignPopulationFloor = 12,
            WarAdvantageRatioPercent = 100,
            AttackOvermatchPercent = 100,
            AssumedGarrisonPower = 3,
        };
        var bandits = new Sim.Server.Bandits.BanditDriver(new Sim.Server.Bandits.BanditConfig());
        var home = new AiPlayerDriver(0, new AiConfig());
        var rival = new AiPlayerDriver(1, rivalCfg, BrainKind.Rival);
        for (var t = live.Now; t <= 15 * Time.Day; t += rivalCfg.ThinkPeriodTicks)
        {
            live.Run(until: t);
            bandits.Think(live, t);
            home.Think(live, projector, t);
            rival.Think(live, projector, t);
        }
        live.Run(until: 15 * Time.Day + rivalCfg.ThinkPeriodTicks);
        var endTick = live.Now;

        var replay = new Simulation(build.Spec, seed: 0xA117);
        replay.World.Diplomacy.RestoreConfig(shortDelay);
        Arm(replay);
        foreach (var batch in live.ResolvedLog.OfType<IntentEvent>()
                     .OrderBy(e => e.Seq).GroupBy(e => e.At))
        {
            replay.Run(until: batch.Key);
            foreach (var ev in batch)
            {
                var (tn, payload) = Sim.Persistence.IntentJson.Serialize(ev.Intent);
                replay.SubmitIntent(batch.Key, Sim.Persistence.IntentJson.Deserialize(tn, payload));
            }
        }
        replay.Run(until: endTick);

        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(replay));
    }
}
