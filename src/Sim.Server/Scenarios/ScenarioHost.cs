using System.Net;
using System.Text.Json;
using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Server.Scenarios;

// M41 — the battle test bed's host (docs/m41-status.md Phase 4; the client
// half is the BattleTestbed scene). `--scenario NAME|DIR` runs this instead of
// a full world: it builds a SMALL REAL WORLD per scenario (the game's own
// worldgen, a Grid combat world, no bandits, no AI brains, no caches, no
// landing), places the scenario's people, and serves the /v2/dev/* routes:
//
//   GET  /v2/dev/scenarios      the list, and which is loaded
//   POST /v2/dev/scenario       {"name": "…"} — load it (swaps the world;
//                               the client reloads its scene)
//   POST /v2/dev/reset          reload the current one
//   POST /v2/dev/clock          {"mode": "step" | "resume" | "pause" | "hold" | "free"}
//   GET  /v2/dev/state          scenario, tick, paused, holding at beats
//
// Both seats are playable from one client: the host has no per-seat
// authentication, so the test bed's director mode posts each unit's orders as
// its owner. A DEV TOOL — scenario mode must never be a production default.
public sealed class ScenarioHost : IDisposable
{
    public const string DefaultDirectory = "scenarios/battle";
    private const int FirstScenarioUnitId = 5000;

    private readonly string _dir;
    public GameHost Current { get; private set; } = null!;
    public ScenarioFile Scenario { get; private set; } = null!;
    public HttpApi? Api { get; set; }

    public ScenarioHost(string nameOrDir)
    {
        if (Directory.Exists(nameOrDir))
        {
            _dir = nameOrDir;
            Load(List().First().Name);
        }
        else
        {
            _dir = Directory.Exists(DefaultDirectory) ? DefaultDirectory : Path.GetDirectoryName(nameOrDir) ?? ".";
            Load(Path.GetFileNameWithoutExtension(nameOrDir));
        }
    }

    public IReadOnlyList<ScenarioFile> List() =>
        Directory.GetFiles(_dir, "*.scenario").OrderBy(p => p, StringComparer.Ordinal).Select(ScenarioFile.Load).ToList();

    public GameHost Load(string name)
    {
        var file = Path.Combine(_dir, name + ".scenario");
        if (!File.Exists(file)) throw new FileNotFoundException($"no scenario '{name}' in {_dir}");
        var scenario = ScenarioFile.Load(file);
        var host = Build(scenario);
        host.Start();
        var old = Current;
        Current = host;
        Scenario = scenario;
        Api?.SwapHost(host);
        old?.Stop();
        Console.WriteLine($"Scenario '{scenario.Name}' loaded: {scenario.Title} ({scenario.Units.Count} units, {scenario.Size}x{scenario.Size} map).");
        return host;
    }

    public static GameHost Build(ScenarioFile s)
    {
        var (build, setup, seed) = Prepare(s);
        var host = new GameHost(build, seed, s.TicksPerSecond,
            new Bandits.BanditConfig { Enabled = false },
            new Ai.AiConfig { Enabled = false },
            setup: setup);
        host.PreludeTicksPerSecond = s.TicksPerSecond;
        host.HoldAtBeats = true;
        return host;
    }

    // The scenario's world and its before-tick-0 setup, without a host: what Build
    // runs, and what the scenario-library tests run headless.
    public static (WorldBuild Build, Action<Simulation> Setup, ulong Seed) Prepare(ScenarioFile s)
    {
        var opts = new ServerOptions
        {
            MapSeed = s.MapSeed,
            MapWidth = s.Size,
            MapHeight = s.Size,
            AiPlayers = 1,           // Red's seat: a castle, no brain
            Bandits = false,
            CacheCount = 0,
            IdolCount = 0,
            LandingDay = 0,
            Progression = false,
            TicksPerSecond = s.TicksPerSecond,
            Combat = "grid",
        };
        var build = WorldFactory.Build(opts);
        var starts = build.Spec.FactionStarts.ToList();
        if (starts.All(f => f.OwnerId != 1))
            starts.Add(new FactionStartSpec
            {
                OwnerId = 1,
                CastlePosition = FarLand(build, starts[0].CastlePosition),
                CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Food] = 1000 },
            });
        if (!s.KeepFounders)
            starts = starts.Select(f => f with { UnitSpawns = Array.Empty<UnitSpawn>(), KingUnitId = null }).ToList();
        var spec = build.Spec with
        {
            FactionStarts = starts,
            Combat = new CombatConfig(s.TurnTicks, CombatModel.Grid),
            StartingWars = s.Wars,
        };
        build = build with { Spec = spec };
        return (build, sim => Setup(sim, s), opts.Seed);
    }

    // What a GenesisSpec can't say: the scenario's people, their gear, health
    // and doctrine, and their opening world orders. Deterministic, before tick 0.
    private static void Setup(Simulation sim, ScenarioFile s)
    {
        var world = sim.World;
        var anchor = world.Structures.Values.OfType<Castle>().Where(c => c.OwnerId == 0)
            .OrderBy(c => c.At.Y).ThenBy(c => c.At.X).First().At;
        world.Structures[anchor].Facing = s.CastleFacing;
        var ids = new List<(int Id, int Owner)>();
        var id = FirstScenarioUnitId;
        foreach (var su in s.Units)
        {
            var at = NearestWalkable(world, new TileCoord(anchor.X + su.Dx, anchor.Y + su.Dy));
            // The per-side cap: a scenario that stands more of a side on a tile than
            // it can hold puts the rest on the nearest tile with room.
            at = Sim.Core.Movement.TileCapacity.RoomNear(world, at, su.Side);
            var unit = world.AddUnit(new Unit(id, at) { Role = su.Role, OwnerId = su.Side });
            foreach (var item in su.Items)
            {
                if (!EquipmentCatalog.TryGetSpec(item, out var spec) || !spec.AllowedRoles.Contains(su.Role)) continue;
                unit.Buffs.Add(new Buff(spec.BuffKind, spec.PowerModifier, spec.HealthModifier, ExpiresAt: null,
                    CargoModifier: spec.CargoModifier, MoveCostPercent: spec.MoveCostPercent));
                unit.Health += spec.HealthModifier;
            }
            if (su.Hp is { } hp) unit.Health = Math.Max(1, hp);
            if (su.Doctrine is { } d)
                new SetBattleDoctrineIntent(id, (byte)d.Behaviour, d.WithdrawBelow) { PlayerId = su.Side }.Resolve(sim);
            ids.Add((id, su.Side));
            id++;
        }
        foreach (var m in s.Moves)
        {
            var (unitId, owner) = ids[m.Unit - 1];
            var to = NearestWalkable(world, new TileCoord(anchor.X + m.Dx, anchor.Y + m.Dy));
            sim.SubmitIntent(m.At, new MoveIntent(unitId, to) { PlayerId = owner });
        }
        // People who start on the same tile as an enemy are already fighting.
        foreach (var tile in world.Units.Values.Select(u => u.Position).Distinct()
                     .OrderBy(t => t.Y).ThenBy(t => t.X).ToList())
            CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
    }

    private static bool Walkable(GameWorld world, TileCoord t) =>
        world.Grid.InBounds(t) && Sim.Core.World.Biomes.MoveCost(world.Grid.BiomeAt(t)) < Sim.Core.World.Biomes.Impassable;

    // The nearest walkable tile to `t` (rings outward, row-major within a ring).
    private static TileCoord NearestWalkable(GameWorld world, TileCoord t)
    {
        for (var r = 0; r < 40; r++)
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var c = new TileCoord(t.X + dx, t.Y + dy);
                    if (Walkable(world, c)) return c;
                }
        return t;
    }

    // A land tile far from Blue for Red's castle, when worldgen found no AI start.
    private static TileCoord FarLand(WorldBuild build, TileCoord blue)
    {
        var best = blue;
        var bestD = -1;
        for (var y = 2; y < build.Map.Height - 2; y++)
            for (var x = 2; x < build.Map.Width - 2; x++)
            {
                var b = build.Map.Grid[x, y];
                if (Sim.Core.World.Biomes.MoveCost(b) >= Sim.Core.World.Biomes.Impassable) continue;
                var d = Math.Abs(x - blue.X) + Math.Abs(y - blue.Y);
                if (d > bestD) { bestD = d; best = new TileCoord(x, y); }
            }
        return best;
    }

    // ---- the dev routes ----------------------------------------------------------

    public bool Handle(HttpListenerContext ctx, string method, string path, string body)
    {
        switch (method, path)
        {
            case ("GET", "/v2/dev/scenarios"):
                HttpApi.WriteJson(ctx, 200, JsonSerializer.Serialize(new
                {
                    current = Scenario.Name,
                    scenarios = List().Select(f => new { name = f.Name, title = f.Title, note = f.Note }).ToArray(),
                }, ServerJson.Options));
                return true;
            case ("POST", "/v2/dev/scenario"):
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString() ?? "";
                Load(name);
                WriteState(ctx);
                return true;
            case ("POST", "/v2/dev/reset"):
                Load(Scenario.Name);
                WriteState(ctx);
                return true;
            case ("POST", "/v2/dev/clock"):
                var mode = JsonDocument.Parse(body).RootElement.GetProperty("mode").GetString();
                switch (mode)
                {
                    case "step": Current.StepTurn(); break;
                    case "resume": Current.Resume(); break;
                    case "pause": Current.SetPace(true, null); break;
                    case "hold": Current.HoldAtBeats = true; break;
                    case "free": Current.HoldAtBeats = false; Current.Resume(); break;
                }
                WriteState(ctx);
                return true;
            case ("GET", "/v2/dev/state"):
                WriteState(ctx);
                return true;
        }
        return false;
    }

    private void WriteState(HttpListenerContext ctx) =>
        HttpApi.WriteJson(ctx, 200, JsonSerializer.Serialize(new
        {
            scenario = Scenario.Name,
            title = Scenario.Title,
            note = Scenario.Note,
            tick = Current.VirtualTick,
            paused = Current.IsPaused,
            holdAtBeats = Current.HoldAtBeats,
        }, ServerJson.Options));

    public void Dispose() => Current?.Stop();
}
