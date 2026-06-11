namespace Sim.Server;

// Command-line configuration for the host. Seed is fixed (not an arg) so a given
// --mapseed always reproduces the same game.
public sealed record ServerOptions
{
    public int Port { get; init; } = 8080;
    public double TicksPerSecond { get; init; } = 20.0;  // wall-clock seconds -> sim ticks
    public int MapSeed { get; init; } = 1151;
    public int MapWidth { get; init; } = 256;
    public int MapHeight { get; init; } = 256;
    public ulong Seed { get; init; } = 0xC0FFEE;          // sim RNG seed

    public static ServerOptions Parse(string[] args)
    {
        int port = 8080, mapSeed = 1151, mapWidth = 256, mapHeight = 256;
        var tps = 20.0;
        for (var i = 0; i + 1 < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":    int.TryParse(args[i + 1], out port); break;
                case "--tps":     double.TryParse(args[i + 1], out tps); break;
                case "--mapseed": int.TryParse(args[i + 1], out mapSeed); break;
                case "--width":   int.TryParse(args[i + 1], out mapWidth); break;
                case "--height":  int.TryParse(args[i + 1], out mapHeight); break;
            }
        }
        return new ServerOptions
        {
            Port = port,
            TicksPerSecond = tps,
            MapSeed = mapSeed,
            MapWidth = mapWidth,
            MapHeight = mapHeight,
        };
    }
}
