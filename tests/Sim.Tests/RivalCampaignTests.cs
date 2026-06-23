using Sim.Core;
using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Core.WorldGen;
using Sim.Server;
using Sim.Server.Ai;
using Sim.Server.Ai.Rungs;
using Sim.Server.Wire;
using Xunit.Abstractions;

namespace Sim.Tests;

// M25 Phase 4 — the offensive war machine (docs/m25-rival-spec.md): muster a
// field army, march it on the target by strike doctrine, siege. Fast unit pins
// on the RivalRung army logic, plus a real-sim integration test that a Warlord
// actually wages a war and razes an enemy structure.
public class RivalCampaignTests
{
    private readonly ITestOutputHelper _output;
    public RivalCampaignTests(ITestOutputHelper output) { _output = output; }

    private const long Now = 5000;
    private static readonly AiConfig Warlord = new() { Personality = AiPersonality.Warlord };

    private static StructDto S(int x, int y, StructureKind kind, int owner) =>
        new() { X = x, Y = y, Kind = (int)kind, OwnerId = owner };

    private static UnitDto Soldier(int id, int x, int y, int owner) =>
        new()
        {
            Id = id, X = x, Y = y, Role = (int)UnitRole.Soldier, OwnerId = owner,
            Activity = (int)Activity.Idle, Power = 3, DestX = -1, DestY = -1,
        };

    private static ViewDto View(StructDto[] structs, UnitDto[] units) => new()
    {
        PlayerId = 0, Width = 200, Height = 200, Tick = Now,
        Population = 20, CastleFood = 2000, FoodRunwayTicks = 100_000_000,
        Structures = structs.Prepend(S(10, 10, StructureKind.Castle, 0)).ToArray(),
        Units = units,
        Factions = new[] { new FactionDto { Id = 0 }, new FactionDto { Id = 1 } },
    };

    // STRIKE DOCTRINE — military before economy before the keep, even when the
    // military structure is the farthest of the three.
    [Fact]
    public void Objective_Prioritizes_Military_Then_Economy_Then_Castle()
    {
        var structs = new[]
        {
            S(35, 10, StructureKind.Farm, 1),       // economy (priority 1), nearest
            S(40, 10, StructureKind.Castle, 1),     // keep (priority 2)
            S(50, 10, StructureKind.Barracks, 1),   // military (priority 0), farthest
        };
        var mem = new AiMemory { CampaignTarget = 1, CampaignReason = "conquest" };
        var ctx = ThinkContext.Build(View(structs, Array.Empty<UnitDto>()), Warlord, mem, Now);
        new RivalRung().Perceive(ctx);

        Assert.Equal(new TileCoord(50, 10), mem.CampaignObjective);   // the Barracks
    }

    // COMMIT GATE — hold below campaign strength, then march the whole column on.
    [Fact]
    public void Army_Marches_OnlyOnceAtStrength()
    {
        var structs = new[] { S(40, 10, StructureKind.LumberCamp, 1) };
        var obj = new TileCoord(40, 10);
        // strength = min(CampaignArmySize 8, pop/WarPopPerSoldier 20/4 = 5) = 5.
        var three = Enumerable.Range(1, 3).Select(i => Soldier(i, 10, 10, 0)).ToArray();
        var ctxFew = ThinkContext.Build(View(structs, three),
            Warlord, new AiMemory { CampaignTarget = 1, CampaignObjective = obj }, Now);
        Assert.Null(new RivalRung().TryClaim(ctxFew));   // still mustering

        var five = Enumerable.Range(1, 5).Select(i => Soldier(i, 10, 10, 0)).ToArray();
        var ctxReady = ThinkContext.Build(View(structs, five),
            Warlord, new AiMemory { CampaignTarget = 1, CampaignObjective = obj }, Now);
        var d = new RivalRung().TryClaim(ctxReady);
        Assert.NotNull(d);
        Assert.Equal("rival", d!.Rung);
        Assert.Equal(5, d.Intents.OfType<MoveIntent>().Count(m => m.Destination == obj));
    }

    // REINFORCEMENT — once any blade is committed, fresh soldiers follow even
    // below the muster strength (we don't recall a war to re-muster).
    [Fact]
    public void Army_Reinforces_OnceCommitted()
    {
        var structs = new[] { S(40, 10, StructureKind.LumberCamp, 1) };
        var obj = new TileCoord(40, 10);
        var units = new[]
        {
            Soldier(1, 40, 10, 0),   // already on the objective (sieging) → committed
            Soldier(2, 10, 10, 0),   // fresh at home
        };
        var ctx = ThinkContext.Build(View(structs, units),
            Warlord, new AiMemory { CampaignTarget = 1, CampaignObjective = obj }, Now);
        var d = new RivalRung().TryClaim(ctx);

        Assert.NotNull(d);
        var move = Assert.Single(d!.Intents.OfType<MoveIntent>());
        Assert.Equal(2, move.UnitId);            // the reinforcement marches
        Assert.Equal(obj, move.Destination);
    }

    // No objective located yet (nothing of the target's in sight) → the army
    // holds and the think yields to the economy.
    [Fact]
    public void Army_Holds_WithNoObjective()
    {
        var five = Enumerable.Range(1, 5).Select(i => Soldier(i, 10, 10, 0)).ToArray();
        var ctx = ThinkContext.Build(View(Array.Empty<StructDto>(), five),
            Warlord, new AiMemory { CampaignTarget = 1, CampaignObjective = null }, Now);
        Assert.Null(new RivalRung().TryClaim(ctx));
    }

    // ---- THE INTEGRATION PROOF -------------------------------------------

    // A controlled war theatre on a REAL generated map (so the projector and its
    // fog are the genuine article): two factions in contact, the attacker handed
    // a ready field army a few tiles from the defender's school. We drive the
    // RivalRung's war machine directly against the live sim — Perceive +
    // TryClaim, the exact code the brain runs — and watch the M24 siege turn an
    // enemy structure to Rubble. (The economy ladder feeding this army is the
    // headline scenario's job; here we isolate march-and-siege end-to-end.)
    [Fact]
    public void Campaign_Musters_Marches_AndRazesEnemyStructure()
    {
        var (build, defenderSchool) = MakeWarTheater();
        var sim = new Simulation(build.Spec, seed: 0xA117);
        var projector = new ViewProjector(build);

        // Open the war and let the (1-tick) telegraph elapse so contact is hostile.
        sim.SubmitIntent(0, new Sim.Core.Diplomacy.DeclareWarIntent(0, 1));
        sim.Run(until: 3);
        Assert.True(sim.World.Diplomacy.AreHostile(0, 1));

        var rival = new RivalRung();
        var mem = new AiMemory();
        var razed = false;
        for (long t = sim.Now; t <= 30 * Time.Day; t += Time.Hour)
        {
            sim.Run(until: t);
            var view = projector.Project(sim, t, playerId: 0, reveal: false);
            var ctx = ThinkContext.Build(view, Warlord, mem, t);
            if (ctx.Castle is null) break;

            var intents = new List<Sim.Core.Intents.Intent>(rival.Perceive(ctx));
            if (rival.TryClaim(ctx) is { } d) intents.AddRange(d.Intents);
            foreach (var i in intents) sim.SubmitIntent(t, i);

            if (sim.World.Structures.Values.Any(s => s.OwnerId == Sim.Core.Sieges.SiegeConstants.RubbleOwnerId))
            {
                razed = true;
                _output.WriteLine($"razed by day {(t - 3) / Time.Day}; campaign={mem.CampaignTarget} " +
                    $"objective={mem.CampaignObjective} reason={mem.CampaignReason}");
                break;
            }
        }

        Assert.Equal(1, mem.CampaignTarget);                 // adopted the war
        Assert.False(sim.World.Structures.ContainsKey(defenderSchool)
            && sim.World.Structures[defenderSchool].Kind == StructureKind.School,
            "the defender's school still stands as a school");
        Assert.True(razed, "no enemy structure was razed by the campaign");
    }

    // CONQUEST — a Warlord doesn't stop at the economy: it razes the Castle, and
    // razing a Castle defeats the player (M24). A larger army marches on a
    // defender whose only structure is its keep; when it falls, faction 1 is
    // Defeated and the Warlord's campaign stands down (the war is won).
    [Fact]
    public void Warlord_RazesCastle_AndDefeatsThePlayer()
    {
        var (build, _) = MakeWarTheater(soldiers: 24, withSchool: false);
        var sim = new Simulation(build.Spec, seed: 0xA117);
        var projector = new ViewProjector(build);
        sim.SubmitIntent(0, new Sim.Core.Diplomacy.DeclareWarIntent(0, 1));
        sim.Run(until: 3);

        var rival = new RivalRung();
        var mem = new AiMemory();
        for (long t = sim.Now; t <= 90 * Time.Day; t += Time.Hour)
        {
            sim.Run(until: t);
            var view = projector.Project(sim, t, playerId: 0, reveal: false);
            var ctx = ThinkContext.Build(view, Warlord, mem, t);
            if (ctx.Castle is null) break;
            var intents = new List<Sim.Core.Intents.Intent>(rival.Perceive(ctx));
            if (rival.TryClaim(ctx) is { } d) intents.AddRange(d.Intents);
            foreach (var i in intents) sim.SubmitIntent(t, i);
            if (sim.World.Players[1].Defeated) { _output.WriteLine($"defeated by day {(t - 3) / Time.Day}"); break; }
        }

        Assert.True(sim.World.Players[1].Defeated, "the defender's castle never fell");
        // The war is won → the campaign is stood down (Bookkeep clears it once
        // the target is Defeated).
        var post = projector.Project(sim, sim.Now, 0, reveal: false);
        new RivalRung().Perceive(ThinkContext.Build(post, Warlord, mem, sim.Now));
        Assert.Null(mem.CampaignTarget);
    }

    // Build the theatre: a real map (for the projector) + a custom spec placing
    // the two factions in contact, with faction 0's army staged near the
    // defender. Returns the build and the primary objective tile.
    private static (WorldBuild build, TileCoord objective) MakeWarTheater(
        int soldiers = 6, bool withSchool = true)
    {
        var generated = WorldFactory.Build(
            new ServerOptions { MapWidth = 64, MapHeight = 64, MapSeed = 7, AiPlayers = 0 });
        var map = generated.Map;

        TileCoord Land(int cx, int cy)
        {
            for (var r = 0; r <= 25; r++)
                for (var dy = -r; dy <= r; dy++)
                    for (var dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || x >= map.Width || y < 0 || y >= map.Height) continue;
                        if (map.Grid[x, y] is Biome.Water or Biome.None) continue;
                        return new TileCoord(x, y);
                    }
            return new TileCoord(Math.Clamp(cx, 0, map.Width - 1), Math.Clamp(cy, 0, map.Height - 1));
        }

        var c0 = map.Start;                       // attacker's keep
        var c1 = Land(c0.X + 16, c0.Y);           // defender's keep, in encroachment range
        var defSchool = Land(c1.X - 3, c1.Y);     // defender's school (the soft objective)
        var objective = withSchool ? defSchool : c1;

        // Attacker's army: a block of soldiers staged ~3 tiles short of the
        // objective so it's in sight from the start (a real campaign reaches it
        // by scouting; here we isolate the march-and-siege).
        var spawns0 = new List<UnitSpawn>();
        for (var i = 0; i < soldiers; i++)
            spawns0.Add(new UnitSpawn(100 + i, Land(objective.X - 4 + i % 4, objective.Y + i / 4 - 2),
                UnitRole.Soldier, OwnerId: 0, StartingAgeYears: 25));

        var f0 = new FactionStartSpec
        {
            OwnerId = 0,
            CastlePosition = c0,
            CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Food] = 4000 },
            UnitSpawns = spawns0.ToArray(),
        };
        var f1 = new FactionStartSpec
        {
            OwnerId = 1,
            CastlePosition = c1,
            CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Food] = 500 },
            SchoolPosition = withSchool ? defSchool : null,
            UnitSpawns = new[] { new UnitSpawn(200, c1, UnitRole.Builder, OwnerId: 1, StartingAgeYears: 30) },
        };

        var spec = new GenesisSpec
        {
            Width = map.Width,
            Height = map.Height,
            Biomes = MapGenerator.ToBiomeOverrides(map),
            FactionStarts = new[] { f0, f1 },
            Diplomacy = new Sim.Core.Diplomacy.DiplomacyConfig(Delay: 1, ProposalExpiryTicks: 200),
        };
        return (new WorldBuild(spec, generated.Map, generated.Elevation, generated.Config),
            withSchool ? defSchool : c1);
    }
}
