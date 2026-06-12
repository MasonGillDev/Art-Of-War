namespace Sim.Server;

// Command-line configuration for the host. Seed is fixed (not an arg) so a given
// --mapseed always reproduces the same game.
public sealed record ServerOptions
{
    public int Port { get; init; } = 8080;
    public double TicksPerSecond { get; init; } = 20.0;  // wall-clock seconds -> sim ticks
    public int MapSeed { get; init; } = 1151;
    public int MapWidth { get; init; } = 128;
    public int MapHeight { get; init; } = 128;
    public ulong Seed { get; init; } = 0xC0FFEE;          // sim RNG seed
    public bool Bandits { get; init; } = true;            // M16 — --bandits 0 to disable the driver
    public int AiPlayers { get; init; } = 1;              // M17 — --ai N full AI factions (0 = none)
    public bool AiTrace { get; init; } = false;           // M17 — --ai-trace 1 prints each brain decision

    public static ServerOptions Parse(string[] args)
    {
        int port = 8080, mapSeed = 1351, mapWidth = 128, mapHeight = 128;
        var tps = 20.0;
        var bandits = 1;
        int ai = 1, aiTrace = 0;
        for (var i = 0; i + 1 < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":    int.TryParse(args[i + 1], out port); break;
                case "--tps":     double.TryParse(args[i + 1], out tps); break;
                case "--mapseed": int.TryParse(args[i + 1], out mapSeed); break;
                case "--width":   int.TryParse(args[i + 1], out mapWidth); break;
                case "--height":  int.TryParse(args[i + 1], out mapHeight); break;
                case "--bandits": int.TryParse(args[i + 1], out bandits); break;
                case "--ai":      int.TryParse(args[i + 1], out ai); break;
                case "--ai-trace": int.TryParse(args[i + 1], out aiTrace); break;
            }
        }
        return new ServerOptions
        {
            Port = port,
            TicksPerSecond = tps,
            MapSeed = mapSeed,
            MapWidth = mapWidth,
            MapHeight = mapHeight,
            Bandits = bandits != 0,
            AiPlayers = Math.Max(0, ai),
            AiTrace = aiTrace != 0,
        };
    }
}
