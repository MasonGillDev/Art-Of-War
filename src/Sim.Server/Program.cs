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
var build = WorldFactory.Build(options);

using var host = new GameHost(build, options.Seed, options.TicksPerSecond,
    new Sim.Server.Bandits.BanditConfig { Enabled = options.Bandits });
host.Start();

using var api = new HttpApi(host, options.Port);

Console.WriteLine($"Sim.Server listening on http://localhost:{options.Port}/  (tps={options.TicksPerSecond}, seed=0x{options.Seed:X}, bandits={(options.Bandits ? "on" : "off")})");
Console.WriteLine("  GET  /view/{playerId}");
Console.WriteLine("  POST /intent");
Console.WriteLine("Ctrl+C to stop.");

Console.CancelKeyPress += (_, e) => { e.Cancel = true; api.Stop(); host.Stop(); };

api.Run();   // blocks until the listener is stopped
