namespace Sim.Server;

// Command-line configuration for the host. Seed is fixed (not an arg) so a given
// --mapseed always reproduces the same game.
public sealed record ServerOptions
{
    public int Port { get; init; } = 8080;
    // Wall-clock seconds -> sim ticks. 4 is the INTENDED pace (user, 2026-08-04):
    // 1 game-day = 6 real minutes; a night's sleep = ~80 game-days. The game is
    // async at this pace by design — docs/automation-progression.md. Labs and
    // attended debugging can crank it via --tps; balance never reads this value.
    public double TicksPerSecond { get; init; } = 4.0;
    public int MapSeed { get; init; } = 1151;
    public int MapWidth { get; init; } = 128;
    public int MapHeight { get; init; } = 128;
    public ulong Seed { get; init; } = 0xC0FFEE;          // sim RNG seed
    public bool Bandits { get; init; } = true;            // M16 — --bandits 0 to disable the driver
    public int BanditGraceDays { get; init; } = 7;        // M16 — --bandit-grace N: game-days before the first party spawns
    public int AiPlayers { get; init; } = 8;              // M17 — --ai N full AI factions (0 = none)
    public int Rivals { get; init; } = 0;                 // M25 — --rivals K of the AI factions play to conquer (0 = all peaceful)
    public bool AiTrace { get; init; } = false;           // M17 — --ai-trace 1 prints each brain decision
    public int CacheCount { get; init; } = 30;            // M23 — loot caches scattered in the fog (--caches N, 0 = none)
    // C3 — ticks per day of LIGHT (--light-cycle N). Presentation-only: the sky's
    // clock, not the sim's. Default one Time.Week, ~42 real minutes at 4 tps.
    public long LightCycleTicks { get; init; } = Sim.Core.Time.Week;
    // M35 — the environmental fertility gradient played on this server:
    // "flat" (off, the identity), "mild" or "strong" — the rungs of the
    // docs/m35-status.md sweep (--fertility NAME). Applied by WorldFactory.
    public string FertilityGradient { get; init; } = "flat";

    public static ServerOptions Parse(string[] args)
    {
        int port = 8080, mapSeed = 21, mapWidth = 252, mapHeight = 252;
        var tps = 4.0;
        var bandits = 1;
        long lightCycle = Sim.Core.Time.Week;
        int ai = 15, rivals = 0, aiTrace = 0, caches = 30, banditGrace = 7;
        var fertility = "flat";
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
                case "--bandit-grace": int.TryParse(args[i + 1], out banditGrace); break;
                case "--ai":      int.TryParse(args[i + 1], out ai); break;
                case "--rivals":  int.TryParse(args[i + 1], out rivals); break;
                case "--ai-trace": int.TryParse(args[i + 1], out aiTrace); break;
                case "--caches":  int.TryParse(args[i + 1], out caches); break;
                case "--light-cycle": long.TryParse(args[i + 1], out lightCycle); break;
                case "--fertility": fertility = args[i + 1]; break;
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
            BanditGraceDays = Math.Max(0, banditGrace),
            AiPlayers = Math.Max(0, ai),
            Rivals = Math.Clamp(rivals, 0, Math.Max(0, ai)),
            AiTrace = aiTrace != 0,
            CacheCount = Math.Max(0, caches),
            LightCycleTicks = lightCycle > 0 ? lightCycle : Sim.Core.Time.Week,
            FertilityGradient = fertility,
        };
    }
}
