using System.Reflection;
using Sim.Core;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.World;
using Sim.Server;
using Sim.Server.Ai;
using Xunit.Abstractions;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M17 — AI players (docs/m17-ai-players-spec.md). The brain sees only
// the projected view; the headline tests double as the BALANCE LAB:
// they are the "is the opening winnable?" question, frozen as CI.
public class AiPlayerTests
{
    private readonly ITestOutputHelper _output;
    public AiPlayerTests(ITestOutputHelper output) { _output = output; }

    // A small generated continent with the human slot (0) + one AI
    // faction (1), identical starts. Tests drive BOTH factions with
    // Homesteader brains — AI vs AI is the balance lab.
    // LAB_MAPSEED overrides the continent for ad-hoc seed sweeps
    // (lab.ps1 -Seed N); unset, the seed is fixed for deterministic CI.
    private static (Simulation sim, ViewProjector projector, WorldBuild build) MakeMatch(
        int aiPlayers = 1, int mapSeed = 7, int size = 96)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("LAB_MAPSEED"), out var envSeed)
            && envSeed != 0)
            mapSeed = envSeed;
        if (int.TryParse(Environment.GetEnvironmentVariable("LAB_MAPSIZE"), out var envSize)
            && envSize > 0)
            size = envSize;
        var opts = new ServerOptions
        {
            MapWidth = size, MapHeight = size, MapSeed = mapSeed, AiPlayers = aiPlayers,
        };
        var build = WorldFactory.Build(opts);
        var sim = new Simulation(build.Spec, seed: 0xA117);
        return (sim, new ViewProjector(build), build);
    }

    private static void RunMatch(Simulation sim, ViewProjector projector,
        IReadOnlyList<AiPlayerDriver> drivers, long until, long step)
    {
        for (var t = sim.Now; t <= until; t += step)
        {
            sim.Run(until: t);
            foreach (var dr in drivers) dr.Think(sim, projector, t);
        }
        // Settle: resolve the final think's submissions so the world (and
        // its Seq counter) reflects every logged intent — the replay
        // headline depends on no submission being left in flight.
        sim.Run(until: until + step);
    }

    private static Castle CastleOf(Simulation sim, int ownerId) =>
        sim.World.Structures.Values.OfType<Castle>().Single(c => c.OwnerId == ownerId);

    // ---- genesis plumbing -------------------------------------------------

    [Fact]
    public void Genesis_AiFactions_IdenticalStarts_Separated()
    {
        var (_, _, build) = MakeMatch(aiPlayers: 2);
        var starts = build.Spec.FactionStarts;
        Assert.True(starts.Count >= 2, "expected at least one AI faction placed");

        var human = starts.Single(f => f.OwnerId == 0);
        foreach (var ai in starts.Where(f => f.OwnerId != 0))
        {
            // Fairness includes the opening: identical loadout, roster size.
            Assert.Equal(human.CastleHoldings, ai.CastleHoldings);
            Assert.Equal(human.UnitSpawns.Count, ai.UnitSpawns.Count);
        }
        // Castles pairwise separated.
        var castles = starts.Select(f => f.CastlePosition).ToList();
        for (var i = 0; i < castles.Count; i++)
            for (var j = i + 1; j < castles.Count; j++)
                Assert.True(Math.Max(Math.Abs(castles[i].X - castles[j].X),
                        Math.Abs(castles[i].Y - castles[j].Y)) >= 24,
                    $"castles {i} and {j} too close");
    }

    // ---- the fairness pin ---------------------------------------------------

    [Fact]
    public void Brain_TouchesOnlyTheView()
    {
        // The brain may never receive the world or the sim — fairness is
        // structural. (The DRIVER shell holds the sim for view-building
        // and submission; the brain's inputs are the view + clock + its
        // own droppable memory.)
        var forbidden = new[] { typeof(GameWorld), typeof(Simulation) };
        foreach (var m in typeof(HomesteaderBrain).GetMethods(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            foreach (var p in m.GetParameters())
                Assert.DoesNotContain(p.ParameterType, forbidden);
    }

    // ---- the arbitration debugger ------------------------------------------

    [Fact]
    public void DecisionTrace_RecordsEveryThink()
    {
        var (sim, projector, _) = MakeMatch();
        var cfg = new AiConfig();
        var driver = new AiPlayerDriver(1, cfg);
        RunMatch(sim, projector, new[] { driver }, until: 10 * cfg.ThinkPeriodTicks,
            step: cfg.ThinkPeriodTicks);

        var entries = driver.Trace.Entries();
        Assert.True(entries.Count >= 10, $"expected >=10 trace entries, got {entries.Count}");
        // Every entry names a rung and carries the tick it fired at.
        Assert.All(entries, e => Assert.False(string.IsNullOrEmpty(e.Rung)));
        for (var i = 1; i < entries.Count; i++)
            Assert.True(entries[i].Tick > entries[i - 1].Tick, "trace must be chronological");
    }

    // ---- the bootstrap race -------------------------------------------------

    [Fact]
    public void Homesteader_BuildsAFarm_BeforeTheRunwayEnds()
    {
        // The same race the human plays: 200 food ≈ 3.6 game-days for 14
        // mouths; a farm must be up and DELIVERING before it ends.
        var (sim, projector, _) = MakeMatch();
        var cfg = new AiConfig();
        var driver = new AiPlayerDriver(1, cfg);
        RunMatch(sim, projector, new[] { driver }, until: 6 * Time.Day,
            step: cfg.ThinkPeriodTicks);

        var farm = sim.World.Structures.Values.OfType<Extractor>()
            .FirstOrDefault(e => e.OwnerId == 1 && e.Kind == StructureKind.Farm);
        Assert.True(farm is not null,
            "no farm after 6 game-days — trace:\n" + driver.Trace.Dump());
        var castle = CastleOf(sim, 1);
        Assert.True(castle.FamineStartTick is null && castle.FoodDebt == 0,
            $"famine during bootstrap (debt={castle.FoodDebt}) — trace:\n" + driver.Trace.Dump());
    }

    // ---- THE HEADLINE: the balance lab -------------------------------------

    [Fact]
    public void Homesteader_Survives100GameDays_NoStarvationDeath()
    {
        // Two Homesteaders (the human slot is driven too — AI vs AI), no
        // bandits, 100 game-days. If a config retune breaks the opening
        // or the steady state, THIS fails — not your evening.
        var (sim, projector, _) = MakeMatch();
        var cfg = new AiConfig();
        var drivers = new[] { new AiPlayerDriver(0, cfg), new AiPlayerDriver(1, cfg) };
        RunMatch(sim, projector, drivers, until: 100 * Time.Day, step: cfg.ThinkPeriodTicks);

        foreach (var ownerId in new[] { 0, 1 })
        {
            var castle = CastleOf(sim, ownerId);
            var pop = sim.World.Players[ownerId].PopulationCount;
            var trace = drivers.Single(d => d.PlayerId == ownerId).Trace;
            Assert.True(castle.FoodDebt == 0 && castle.FamineStartTick is null,
                $"faction {ownerId} in famine at day 100 (debt={castle.FoodDebt}, " +
                $"pop={pop}) — trace tail:\n{trace.Dump()}");
            Assert.True(pop >= 14,
                $"faction {ownerId} shrank to {pop} — trace tail:\n{trace.Dump()}");
            // No starvation death ever fired for this castle (the kill
            // path leaves Outcome unset; reject/fence paths set it).
            Assert.DoesNotContain(sim.ResolvedLog.OfType<Sim.Core.Food.StarvationDeathEvent>(),
                e => e.CastleAt == castle.At && e.Outcome is null);
        }
    }

    [Fact(Skip = "expected to fail until Defender (M17 phase 2) — the Homesteader " +
                 "doesn't fight back; tracked in docs/m17-ai-players-spec.md")]
    public void AiVsBandits_EconomySurvivesRaids()
    {
        // Re-enable when the Defender rung lands: Homesteader + default
        // bandit pressure for 50 game-days; population may dip, never zero.
    }

    // ---- THE BALANCE LAB REPORT: the long-match curve ------------------------

    // The lab's match length — tune freely (runtime is roughly linear,
    // ~2-4s per 100 game-days). Mind two things on long runs: the
    // assertions below encode "thriving at the end" and will rightly fail
    // when a world's land or labor genuinely runs out (that's a finding,
    // not a bug), and the LAB_MAPSEED env var (lab.ps1 -Seed) still works.
    private const long LabDays = 300;

    [Fact]
    public void BalanceLab_160Days_SprawlsUnderPressure_AndRecovers()
    {
        // The long match: demographic surge, farm rotation (claims exhaust
        // at ~day 104), at least one food crunch survived, and a growing
        // population at the end. The per-decade curve prints as test
        // output — this is the report the lab exists to produce.
        var (sim, projector, _) = MakeMatch();
        var cfg = new AiConfig();
        var drivers = new[] { new AiPlayerDriver(0, cfg), new AiPlayerDriver(1, cfg) };
        for (long t = 0; t <= LabDays * Time.Day; t += cfg.ThinkPeriodTicks)
        {
            sim.Run(until: t);
            foreach (var dr in drivers) dr.Think(sim, projector, t);
            if (t % (10 * Time.Day) == 0)
            {
                var c = CastleOf(sim, 0);
                var farms = sim.World.Structures.Values.OfType<Extractor>()
                    .Count(e => e.OwnerId == 0 && e.Kind == StructureKind.Farm);
                _output.WriteLine($"d{t / Time.Day,3}: pop={sim.World.Players[0].PopulationCount,2} " +
                    $"food={Sim.Core.Food.FoodConsumption.CurrentLevel(c, sim, t),5} " +
                    $"wood={c.AmountOf(Resource.Wood),4} farms={farms}");
            }
        }
        sim.Run(until: LabDays * Time.Day + cfg.ThinkPeriodTicks);

        foreach (var id in new[] { 0, 1 })
        {
            var castle = CastleOf(sim, id);
            var pop = sim.World.Players[id].PopulationCount;
            // No starvation death ever (kill path leaves Outcome unset).
            Assert.DoesNotContain(sim.ResolvedLog.OfType<Sim.Core.Food.StarvationDeathEvent>(),
                e => e.CastleAt == castle.At && e.Outcome is null);
            Assert.True(castle.FoodDebt == 0 && castle.FamineStartTick is null,
                $"faction {id} ends day 160 in famine (debt={castle.FoodDebt})");
            // GREW through the founder die-off: more people than genesis.
            Assert.True(pop > 14, $"faction {id} ended at pop {pop} — no demographic takeoff");
            // ROTATED: more farms than the demand-count alone would build.
            var farms = sim.World.Structures.Values.OfType<Extractor>()
                .Count(e => e.OwnerId == id && e.Kind == StructureKind.Farm);
            Assert.True(farms >= 3, $"faction {id} built only {farms} farms — no rotation under pressure");
        }
    }

    // ---- determinism: same proof as M16, for this driver --------------------

    [Fact]
    public void Ai_ReplayFromIntentLog_HashesMatch()
    {
        Simulation Fresh() => new(MakeMatch().build.Spec, seed: 0xA117);

        var (live, projector, _) = MakeMatch();
        var cfg = new AiConfig();
        var driver = new AiPlayerDriver(1, cfg);
        RunMatch(live, projector, new[] { driver }, until: 15 * Time.Day,
            step: cfg.ThinkPeriodTicks);
        var endTick = live.Now;

        // Driverless replay of the durable log, chronologically interleaved:
        // run to each think tick, then submit that tick's WHOLE BATCH before
        // running further. Live, the driver queues its full batch before any
        // of it resolves — replaying one-at-a-time would let intent #1's
        // follow-up events take Seq numbers ahead of intent #2, reordering
        // same-tick execution.
        var replay = Fresh();
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
