using Sim.Server;

// Sim.Server — a thin HTTP wrapper around the authoritative Sim.Core engine so the
// dumb Unity client can drive the REAL deterministic simulation instead of a mock.
// It touches NOTHING in Sim.Core: it only calls public APIs. The pieces:
//   ServerOptions  — CLI args (--port / --tps / --mapseed / --width / --height).
//   WorldFactory   — builds the generated world + heightmap (WorldBuild).
//   GameHost       — owns the Simulation, the lock, and the virtual clock.
//   ViewProjector  — maps Sim.Core state -> fog-filtered wire DTOs (Wire/).
//   HttpApi        — the HttpListener loop + routing.
// Endpoints: POST /intent, GET /view/{playerId}.

var options = ServerOptions.Parse(args);

// The battle sandbox: small worlds built from compositions, and the /v2/dev/*
// routes, instead of a full game (Sandbox/SandboxHost, docs/battle-sandbox.md).
if (options.Sandbox is { } sandboxDir)
{
    using var sandbox = new Sim.Server.Sandbox.SandboxHost(sandboxDir);
    using var devApi = new HttpApi(sandbox.Current, options.Port) { Dev = sandbox };
    sandbox.Api = devApi;
    Console.WriteLine($"Sim.Server BATTLE SANDBOX on http://localhost:{options.Port}/  (compositions in '{sandboxDir}')");
    foreach (var name in sandbox.Saved())
        Console.WriteLine($"  {name}");
    Console.WriteLine("  GET /v2/dev/sandbox · PUT /v2/dev/composition · POST /v2/dev/play · POST /v2/dev/edit");
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; devApi.Stop(); sandbox.Current.Stop(); };
    devApi.Run();
    return;
}

var build = WorldFactory.Build(options);

using var host = new GameHost(build, options.Seed, options.TicksPerSecond,
    new Sim.Server.Bandits.BanditConfig
    {
        Enabled = options.Bandits,
        SpawnGraceTicks = options.BanditGraceDays * Sim.Core.Time.Day,
    },
    new Sim.Server.Ai.AiConfig { Enabled = options.AiPlayers > 0, TracePrint = options.AiTrace, RivalCount = options.Rivals });
host.LightCycle = Sim.Server.Atmosphere.LightCycleConfig.ForCycle(options.LightCycleTicks);
// Two-act pacing (docs/two-act-pacing.md): fast until the landing, then options.TicksPerSecond.
host.PreludeTicksPerSecond = options.PreludeTicksPerSecond;
host.Start();

using var api = new HttpApi(host, options.Port);

var pace = host.Schedule.HasLanding
    ? $"prelude {host.Schedule.PreludeTicksPerSecond} tps until the landing on day {options.LandingDay}, then {host.Schedule.TicksPerSecond} tps"
    : $"tps={host.Schedule.TicksPerSecond}, no landing";
Console.WriteLine($"Sim.Server listening on http://localhost:{options.Port}/  ({pace}, seed=0x{options.Seed:X}, bandits={(options.Bandits ? "on" : "off")}, ai={options.AiPlayers}, rivals={options.Rivals}{(options.GodMode ? ", GOD MODE on player 0" : "")})");
// M25 personalities — the who's-who, so a playtest can tell WHICH rival
// is stalking it (Homesteaders are all identical; only rivals are named).
foreach (var d in host.AiDrivers)
    if (d.Kind == Sim.Server.Ai.BrainKind.Rival)
        Console.WriteLine($"  rival: faction {d.PlayerId} ({d.Personality})");
Console.WriteLine("  GET  /view/{playerId}");
Console.WriteLine("  POST /intent");
Console.WriteLine("Ctrl+C to stop.");

Console.CancelKeyPress += (_, e) => { e.Cancel = true; api.Stop(); host.Stop(); };

api.Run();   // blocks until the listener is stopped
