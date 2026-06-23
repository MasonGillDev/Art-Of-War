using Sim.Core;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.World;
using Sim.Core.WorldGen;
using Sim.Server;
using Sim.Server.Ai;
using Xunit.Abstractions;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M25 HEADLINE (docs/m25-rival-spec.md): a war-capable AI scenario — a full
// AiPlayerDriver Warlord prosecuting a war against a Homesteader — is
// deterministic. Same contract as M16/M17, now carrying war intents through the
// machine: declare → march → siege → raze.
//   1. TWIN-RUN: identical scenarios hash-match.
//   2. REPLAY-FROM-INTENT-LOG: driverless replay reproduces the live hash.
// The scenario is also asserted NON-TRIVIAL (a war was actually fought), so the
// determinism pins can't pass vacuously on an idle world.
public class RivalHeadlineTests
{
    private readonly ITestOutputHelper _output;
    public RivalHeadlineTests(ITestOutputHelper output) { _output = output; }

    private static readonly long War = 25 * Time.Day;   // 25 game-days

    // A real map (for the projector) + a custom contact spec: a Warlord (0)
    // staged with an army by a Homesteader (1)'s keep.
    private static WorldBuild Theater()
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

        var c0 = map.Start;
        var c1 = Land(c0.X + 16, c0.Y);
        var school = Land(c1.X - 3, c1.Y);

        var spawns0 = new List<UnitSpawn>();
        for (var i = 0; i < 10; i++)
            spawns0.Add(new UnitSpawn(100 + i, Land(school.X - 4 + i % 4, school.Y + i / 4 - 1),
                UnitRole.Soldier, OwnerId: 0, StartingAgeYears: 25));
        // A couple of civilians so the economy brain has hands and the colony
        // has a population beyond the levy (keeps the scenario realistic).
        spawns0.Add(new UnitSpawn(120, Land(c0.X, c0.Y), UnitRole.Builder, OwnerId: 0, StartingAgeYears: 30));
        spawns0.Add(new UnitSpawn(121, Land(c0.X + 1, c0.Y), UnitRole.Farmer, OwnerId: 0, StartingAgeYears: 30));

        var f0 = new FactionStartSpec
        {
            OwnerId = 0, CastlePosition = c0,
            CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Food] = 4000, [Resource.Wood] = 70, [Resource.Stone] = 50 },
            UnitSpawns = spawns0.ToArray(),
            SchoolPosition = Land(c0.X, c0.Y + 1),
        };
        var f1 = new FactionStartSpec
        {
            OwnerId = 1, CastlePosition = c1,
            CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Food] = 600, [Resource.Wood] = 70, [Resource.Stone] = 50 },
            SchoolPosition = school,
            UnitSpawns = new[]
            {
                new UnitSpawn(200, c1, UnitRole.Builder, OwnerId: 1, StartingAgeYears: 30),
                new UnitSpawn(201, Land(c1.X + 1, c1.Y), UnitRole.Farmer, OwnerId: 1, StartingAgeYears: 30),
            },
        };

        var spec = new GenesisSpec
        {
            Width = map.Width, Height = map.Height,
            Biomes = MapGenerator.ToBiomeOverrides(map),
            FactionStarts = new[] { f0, f1 },
            Diplomacy = new DiplomacyConfig(Delay: 1, ProposalExpiryTicks: 5000),
        };
        return new WorldBuild(spec, generated.Map, generated.Elevation, generated.Config);
    }

    // Run the war with full drivers. Returns whether a siege razed something.
    private static bool RunWar(Simulation sim, ViewProjector projector, AiPlayerDriver[] drivers, long until)
    {
        var razed = false;
        var step = new AiConfig().ThinkPeriodTicks;
        for (var t = sim.Now; t <= until; t += step)
        {
            sim.Run(until: t);
            foreach (var d in drivers) d.Think(sim, projector, t);
            if (sim.World.Structures.Values.Any(s => s.OwnerId == Sim.Core.Sieges.SiegeConstants.RubbleOwnerId))
                razed = true;
        }
        sim.Run(until: until + step);
        return razed;
    }

    private static (Simulation sim, ViewProjector projector, AiPlayerDriver[] drivers) Build()
    {
        var build = Theater();
        var sim = new Simulation(build.Spec, seed: 0xA117);
        sim.SubmitIntent(0, new DeclareWarIntent(0, 1));   // open the war
        var drivers = new[]
        {
            new AiPlayerDriver(0, new AiConfig { Personality = AiPersonality.Warlord }),
            new AiPlayerDriver(1, new AiConfig { Personality = AiPersonality.Homesteader }),
        };
        return (sim, new ViewProjector(build), drivers);
    }

    [Fact]
    public void War_TwinRun_HashesMatch()
    {
        var (s1, p1, d1) = Build();
        var razed1 = RunWar(s1, p1, d1, War);
        var (s2, p2, d2) = Build();
        var razed2 = RunWar(s2, p2, d2, War);

        // Non-trivial: the war was actually declared, became effective, and a
        // siege razed a structure — the headline can't pass on an idle world.
        Assert.True(s1.World.Diplomacy.RelationshipBetween(0, 1) == RelationshipState.Enemy
            || s1.World.Players[1].Defeated, "the war never became effective");
        Assert.True(razed1, "no siege razed anything — scenario didn't exercise the war");

        Assert.Equal(Snapshot.Hash(s1), Snapshot.Hash(s2));
        Assert.Equal(razed1, razed2);
    }

    [Fact]
    public void War_ReplayFromIntentLog_HashesMatch()
    {
        var (live, projector, drivers) = Build();
        RunWar(live, projector, drivers, War);
        var endTick = live.Now;

        // Driverless replay of the durable intent log, batched by tick in Seq
        // order (the same discipline as Ai_ReplayFromIntentLog_HashesMatch: a
        // driver queues its whole batch before any of it resolves).
        var replay = new Simulation(Theater().Spec, seed: 0xA117);
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

        _output.WriteLine($"live hash {Snapshot.Hash(live)} / replay {Snapshot.Hash(replay)}");
        Assert.Equal(Snapshot.Hash(live), Snapshot.Hash(replay));
    }
}
