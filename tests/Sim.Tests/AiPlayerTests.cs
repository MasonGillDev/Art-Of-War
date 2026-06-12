using System.Reflection;
using Sim.Core;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Movement;
using Sim.Core.Population;
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
        // The circular-lock fix (user decision 2026-06-12): every faction
        // is BORN with its trainer — losing the last Builder can never
        // again lock a colony out of construction forever.
        Assert.All(starts, f => Assert.NotNull(f.SchoolPosition));
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
        // own droppable memory.) Since the Phase-0 decomposition the
        // brain is a NAMESPACE, not a class — the sweep covers every Ai
        // type (rungs, ThinkContext, memory), public and private alike;
        // only the driver shell legitimately holds the sim.
        var forbidden = new[] { typeof(GameWorld), typeof(Simulation) };
        var brainTypes = typeof(HomesteaderBrain).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Sim.Server.Ai") == true
                && t != typeof(AiPlayerDriver)
                && !t.Name.Contains('<'))   // compiler-generated closures
            .ToList();
        Assert.Contains(typeof(HomesteaderBrain), brainTypes);
        Assert.Contains(typeof(ThinkContext), brainTypes);
        foreach (var type in brainTypes)
            foreach (var m in type.GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic
                         | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                foreach (var p in m.GetParameters())
                    Assert.True(!forbidden.Contains(p.ParameterType),
                        $"{type.Name}.{m.Name} takes {p.ParameterType.Name} — " +
                        "brain code may only see the view");
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

    // THE PHASE-2 GATE, un-skipped (docs/m17-defender-spec.md): two
    // Homesteaders + default bandit pressure for 50 game-days. The
    // population may dip; it never hits zero. Doubles as the PURSUIT
    // LEASH pin: militia positions are sampled every think and must
    // stay inside PursuitLeashTiles of their own castle — Defend only
    // ever orders moves to in-leash tiles, so an excursion means the
    // leash filter broke.
    [Fact]
    public void AiVsBandits_EconomySurvivesRaids()
    {
        var (sim, projector, _) = MakeMatch();
        var aiCfg = new AiConfig();
        var bandits = new Sim.Server.Bandits.BanditDriver(new Sim.Server.Bandits.BanditConfig());
        var drivers = new[] { new AiPlayerDriver(0, aiCfg), new AiPlayerDriver(1, aiCfg) };
        var castles = new Dictionary<int, TileCoord>
        {
            [0] = CastleOf(sim, 0).At, [1] = CastleOf(sim, 1).At,
        };
        var maxExcursion = 0;
        for (var t = sim.Now; t <= 50 * Time.Day; t += aiCfg.ThinkPeriodTicks)
        {
            sim.Run(until: t);
            bandits.Think(sim, t);
            foreach (var dr in drivers) dr.Think(sim, projector, t);
            foreach (var u in sim.World.Units.Values)
                if (u.Role == UnitRole.Soldier && castles.TryGetValue(u.OwnerId, out var home))
                    maxExcursion = Math.Max(maxExcursion, Math.Max(
                        Math.Abs(u.Position.X - home.X), Math.Abs(u.Position.Y - home.Y)));
        }
        sim.Run(until: 50 * Time.Day + aiCfg.ThinkPeriodTicks);

        foreach (var id in new[] { 0, 1 })
        {
            var pop = sim.World.Players[id].PopulationCount;
            var trace = drivers.Single(d => d.PlayerId == id).Trace;
            Assert.True(pop > 0,
                $"faction {id} wiped out under default bandit pressure — " +
                $"trace tail:\n{trace.Dump()}");
        }
        Assert.True(maxExcursion <= aiCfg.PursuitLeashTiles,
            $"militia ranged {maxExcursion} tiles from home — leash is {aiCfg.PursuitLeashTiles}");
    }

    // THE DOCTRINE A/B (docs/m17-defender-spec.md, user-locked: run
    // both, let the data pin the default): same world, same bandit
    // seed, recall ON vs OFF. The first run of this report PICKED the
    // default — recall OFF got faction 1 wiped by day 50 (bandits
    // steal from worked posts and blades fall on field hands); recall
    // ON kept both colonies alive. The asserts now PIN that decision:
    // the shipped doctrine keeps everyone alive AND does at least as
    // well as the alternative — if OFF ever beats ON under future
    // tuning, this fails and the default deserves re-examination.
    [Fact]
    public void Doctrine_RecallVsWorkThrough_LabReport()
    {
        var endPops = new Dictionary<bool, int[]>();
        foreach (var recall in new[] { true, false })
        {
            var (sim, projector, _) = MakeMatch();
            var aiCfg = new AiConfig { RecallCiviliansUnderRaid = recall };
            var bandits = new Sim.Server.Bandits.BanditDriver(new Sim.Server.Bandits.BanditConfig());
            var drivers = new[] { new AiPlayerDriver(0, aiCfg), new AiPlayerDriver(1, aiCfg) };
            _output.WriteLine($"--- RecallCiviliansUnderRaid = {recall} ---");
            for (var t = sim.Now; t <= 50 * Time.Day; t += aiCfg.ThinkPeriodTicks)
            {
                sim.Run(until: t);
                bandits.Think(sim, t);
                foreach (var dr in drivers) dr.Think(sim, projector, t);
                if (t % (10 * Time.Day) == 0)
                {
                    var c = CastleOf(sim, 0);
                    var soldiers = sim.World.Units.Values.Count(u =>
                        u.OwnerId == 0 && u.Role == UnitRole.Soldier);
                    var banditCount = sim.World.Units.Values.Count(u =>
                        u.OwnerId == Sim.Core.Bandits.BanditConstants.OwnerId);
                    _output.WriteLine(
                        $"d{t / Time.Day,3}: pop={sim.World.Players[0].PopulationCount,3} " +
                        $"food={Sim.Core.Food.FoodConsumption.CurrentLevel(c, sim, t),5} " +
                        $"soldiers={soldiers} banditsLive={banditCount}");
                }
            }
            sim.Run(until: 50 * Time.Day + aiCfg.ThinkPeriodTicks);
            endPops[recall] = new[] { 0, 1 }
                .Select(id => sim.World.Players[id].PopulationCount).ToArray();
            _output.WriteLine($"end: faction0={endPops[recall][0]} faction1={endPops[recall][1]}");
        }
        // The pinned doctrine (the AiConfig default) keeps every colony alive…
        Assert.True(new AiConfig().RecallCiviliansUnderRaid,
            "default doctrine changed — re-run this A/B and re-pin");
        Assert.All(endPops[true], pop => Assert.True(pop > 0,
            "a colony died under the PINNED doctrine (recall=true)"));
        // …and does at least as well as the alternative.
        Assert.True(endPops[true].Sum() >= endPops[false].Sum(),
            $"recall=false outperformed the pinned default " +
            $"({endPops[false].Sum()} vs {endPops[true].Sum()} total pop) — re-examine");
    }

    // ---- M17 Phase 2: role floors (the builder-extinction fix) ---------------

    [Fact]
    public void Train_RestoresBuilderFloor_AfterRoleLoss()
    {
        // The siege lab found the colony's hard dependency: only
        // Builder-role hands may raise a site (engine rule), so losing
        // every Builder freezes construction FOREVER unless the brain
        // restores the role. Stage the loss LEGALLY: once the School
        // stands, walk both of faction 1's Builders onto it and retrain
        // them to Farmer (intents any player could send) — then assert
        // the Train rung's role floor brings Builders back.
        var (sim, projector, _) = MakeMatch();
        var cfg = new AiConfig { TraceCapacity = 8192 };   // keep the whole match for the causation pin
        var drivers = new[] { new AiPlayerDriver(0, cfg), new AiPlayerDriver(1, cfg) };
        RunMatch(sim, projector, drivers, until: 12 * Time.Day, step: cfg.ThinkPeriodTicks);

        var school = sim.World.Structures.Values
            .First(s => s.OwnerId == 1 && s.Kind == StructureKind.School);
        var builders = sim.World.Units.Values
            .Where(u => u.OwnerId == 1 && u.Role == UnitRole.Builder)
            .Select(u => u.Id).ToList();
        Assert.True(builders.Count > 0, "test premise: faction 1 has builders at day 12");

        // March each builder to the school and retrain — re-issuing the
        // move every step until they arrive (the brain may re-task them
        // between our steps; submitting AFTER its think wins the tick).
        // The drivers keep thinking throughout: an un-driven colony
        // famines in days, and dead builders prove nothing.
        foreach (var id in builders)
        {
            var deadline = sim.Now + 4 * Time.Day;
            while (sim.Now < deadline)
            {
                var t = sim.Now + cfg.ThinkPeriodTicks;
                sim.Run(until: t);
                foreach (var dr in drivers) dr.Think(sim, projector, t);
                if (!sim.World.Units.TryGetValue(id, out var u)) break;   // died — also extinction
                if (u.Position == school.At && u.Activity == Activity.Idle)
                {
                    sim.SubmitIntent(t, new TrainUnitIntent(id, UnitRole.Farmer) { PlayerId = 1 });
                    sim.Run(until: t + 1);
                    break;
                }
                if (u.Position != school.At)
                    sim.SubmitIntent(t, new MoveIntent(id, school.At) { PlayerId = 1 });
            }
        }
        // No "count == 0" assert here, deliberately: the drivers keep
        // thinking through the dance, so the floor often graduates a
        // REPLACEMENT before we finish retraining the second original —
        // the homeostat racing the extinction-stager is the feature
        // working. Causation is pinned via the trace below instead.

        // Builder-less colony + a standing School: the floor must refill.
        var resume = sim.Now;
        for (var t = resume; t <= resume + 15 * Time.Day; t += cfg.ThinkPeriodTicks)
        {
            sim.Run(until: t);
            foreach (var dr in drivers) dr.Think(sim, projector, t);
        }
        sim.Run(until: resume + 15 * Time.Day + cfg.ThinkPeriodTicks);

        var rebuilt = sim.World.Units.Values.Count(u =>
            u.OwnerId == 1 && u.Role == UnitRole.Builder);
        Assert.True(rebuilt >= Math.Min(cfg.BuilderFloor, 2),
            $"builder floor not restored: {rebuilt} of {cfg.BuilderFloor} after 15 days — " +
            $"trace tail:\n{drivers[1].Trace.Dump()}");
        // Causation: the Train rung itself graduated a Builder.
        Assert.Contains(drivers[1].Trace.Entries(),
            e => e.Rung == "train" && e.Why.Contains("graduating a Builder"));
    }

    // ---- M17 Phase 2: the standing army --------------------------------------

    [Fact]
    public void Muster_ReachesQuota_StandingArmyWithoutFamine()
    {
        // The Muster rung fills the soldier quota in peacetime (no
        // bandits here — this is the defense-budget test, not the war
        // test): a Barracks goes up once the larder clears the growth
        // floor, recruits walk in and swear in, and the colony pays the
        // standing-army food tax WITHOUT slipping into famine. 60 days
        // is generous — the larder clears the floor around day 8-12 and
        // each recruit is a designate-walk-train cycle of a few hours.
        var (sim, projector, _) = MakeMatch();
        var cfg = new AiConfig();
        var drivers = new[] { new AiPlayerDriver(0, cfg), new AiPlayerDriver(1, cfg) };
        RunMatch(sim, projector, drivers, until: 60 * Time.Day, step: cfg.ThinkPeriodTicks);

        foreach (var id in new[] { 0, 1 })
        {
            var trace = drivers.Single(d => d.PlayerId == id).Trace;
            var soldiers = sim.World.Units.Values.Count(u =>
                u.OwnerId == id && u.Role == UnitRole.Soldier);
            // Config-derived: recompute the brain's own quota formula
            // from the END state (population/structures move during the
            // run; the target moves with them). One recruit of slack —
            // a freshly-risen quota may have a designee mid-walk.
            var structures = sim.World.Structures.Values.Count(s => s.OwnerId == id);
            var pop = sim.World.Players[id].PopulationCount;
            var quota = Math.Min(
                cfg.SoldierQuotaFloor + structures / Math.Max(1, cfg.SoldiersPerStructures),
                pop / Math.Max(1, cfg.PopulationPerSoldier));
            Assert.True(soldiers >= 1 && soldiers + 1 >= quota,
                $"faction {id} mustered {soldiers} of quota {quota} " +
                $"(pop {pop}, {structures} structures) — trace tail:\n{trace.Dump()}");
            var barracks = sim.World.Structures.Values
                .Count(s => s.OwnerId == id && s.Kind == StructureKind.Barracks);
            Assert.True(barracks >= 1, $"faction {id} built no barracks");
            // The army is a tax the economy must carry, not a famine.
            var castle = CastleOf(sim, id);
            Assert.True(castle.FoodDebt == 0 && castle.FamineStartTick is null,
                $"faction {id} in famine at day 60 carrying {soldiers} soldiers " +
                $"(debt={castle.FoodDebt}) — trace tail:\n{trace.Dump()}");
        }
    }

    // ---- THE BALANCE LAB REPORT: the long-match curve ------------------------

    // The lab's match length — tune freely (runtime is roughly linear,
    // ~2-4s per 100 game-days). Mind two things on long runs: the
    // assertions below encode "thriving at the end" and will rightly fail
    // when a world's land or labor genuinely runs out (that's a finding,
    // not a bug), and the LAB_MAPSEED env var (lab.ps1 -Seed) still works.
    private const long LabDays = 160;

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
