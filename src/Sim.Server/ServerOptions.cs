namespace Sim.Server;

// Command-line configuration for the host. Seed is fixed (not an arg) so a given
// --mapseed always reproduces the same game.
public sealed record ServerOptions
{
    public int Port { get; init; } = 8080;
    // TWO-ACT PACING (docs/two-act-pacing.md, user 2026-09-24). Wall-clock
    // seconds -> sim ticks, in two acts:
    //   * PreludeTicksPerSecond (--prelude-tps, 4) until the landing: a
    //     game-day is 6 real minutes, so day 30 lands about 3 hours in;
    //   * TicksPerSecond (--tps, 1) from the landing on, and throughout a world
    //     with no landing: a game-day is 24 real minutes, a night's sleep about
    //     20 game-days.
    // Balance never reads either value; the sim knows only WHEN the landing is.
    public double TicksPerSecond { get; init; } = 1.0;
    public double PreludeTicksPerSecond { get; init; } = 4.0;
    // The game-day the landing comes (--landing-day N; 0 = a one-act world).
    // ON for the host (Parse defaults to 30) but OFF for a bare ServerOptions,
    // so the AI labs that build one keep their one-act worlds and their hashes
    // (the Progression / IdolCount precedent below).
    public int LandingDay { get; init; } = 0;
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
    // M38 — idols scattered in the fog (--idols N). ON for the host (Parse
    // defaults to 20) but 0 for a bare ServerOptions, so the AI labs' genesis
    // Rng sequence is untouched (docs/scouting-secrets.md).
    public int IdolCount { get; init; } = 0;
    // C3 — ticks per day of LIGHT (--light-cycle N). Presentation-only: the sky's
    // clock, not the sim's. Default one Time.Week, ~42 real minutes at 4 tps.
    public long LightCycleTicks { get; init; } = Sim.Core.Time.Week;
    // M35 — the environmental fertility gradient played on this server:
    // "flat" (off, the identity), "mild" or "strong" — the rungs of the
    // docs/m35-status.md sweep (--fertility NAME). Applied by WorldFactory.
    public string FertilityGradient { get; init; } = "flat";
    // God mode (docs/god-mode.md) — --god 1 lets player 0 place any buildable
    // structure for free, standing instantly. A test-harness switch: AI
    // factions never get it, and a god run's balance readouts mean nothing.
    public bool GodMode { get; init; } = false;
    // M37 — progression for the human seat (docs/progression.md): hidden
    // milestones, omens, arrivals. AI factions never enrol. ON for the host
    // (Parse defaults it to 1; --progression 0 turns it off) but OFF for a
    // bare ServerOptions, because the labs that build one drive player 0 with
    // an AI brain, and a reprisal raid would muddy every balance readout.
    public bool Progression { get; init; } = false;
    // The battle sandbox (--sandbox [DIR], docs/battle-sandbox.md): the host builds
    // small worlds from compositions (saved as JSON in DIR, default `sandbox/`)
    // instead of a full game, and serves the /v2/dev/* routes. A dev tool; never a
    // production default. Null = an ordinary game.
    public string? Sandbox { get; init; }

    public static ServerOptions Parse(string[] args)
    {
        int port = 8080, mapSeed = 3301431, mapWidth = 252, mapHeight = 252;
        var tps = 1.0;
        var preludeTps = 4.0;
        var landingDay = 30;
        var bandits = 1;
        long lightCycle = Sim.Core.Time.Week;
        int ai = 15, rivals = 0, aiTrace = 0, caches = 30, banditGrace = 7;
        var idols = 20;
        var fertility = "flat";
        var god = 0;
        var progression = 1;
        string? sandbox = null;
        // `--sandbox` may stand last with no folder: the default one.
        if (args.Length > 0 && args[^1] == "--sandbox") sandbox = Sim.Server.Sandbox.SandboxHost.DefaultDirectory;
        for (var i = 0; i + 1 < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":    int.TryParse(args[i + 1], out port); break;
                case "--tps":     double.TryParse(args[i + 1], out tps); break;
                case "--prelude-tps": double.TryParse(args[i + 1], out preludeTps); break;
                case "--landing-day": int.TryParse(args[i + 1], out landingDay); break;
                case "--mapseed": int.TryParse(args[i + 1], out mapSeed); break;
                case "--width":   int.TryParse(args[i + 1], out mapWidth); break;
                case "--height":  int.TryParse(args[i + 1], out mapHeight); break;
                case "--bandits": int.TryParse(args[i + 1], out bandits); break;
                case "--bandit-grace": int.TryParse(args[i + 1], out banditGrace); break;
                case "--ai":      int.TryParse(args[i + 1], out ai); break;
                case "--rivals":  int.TryParse(args[i + 1], out rivals); break;
                case "--ai-trace": int.TryParse(args[i + 1], out aiTrace); break;
                case "--caches":  int.TryParse(args[i + 1], out caches); break;
                case "--idols":   int.TryParse(args[i + 1], out idols); break;
                case "--light-cycle": long.TryParse(args[i + 1], out lightCycle); break;
                case "--fertility": fertility = args[i + 1]; break;
                case "--god":     int.TryParse(args[i + 1], out god); break;
                case "--progression": int.TryParse(args[i + 1], out progression); break;
                case "--sandbox": sandbox = args[i + 1].StartsWith("--") ? Sim.Server.Sandbox.SandboxHost.DefaultDirectory : args[i + 1]; break;
            }
        }
        return new ServerOptions
        {
            Port = port,
            TicksPerSecond = tps,
            PreludeTicksPerSecond = preludeTps,
            LandingDay = Math.Max(0, landingDay),
            MapSeed = mapSeed,
            
            MapWidth = mapWidth,
            MapHeight = mapHeight,
            Bandits = bandits != 0,
            BanditGraceDays = Math.Max(0, banditGrace),
            AiPlayers = Math.Max(0, ai),
            Rivals = Math.Clamp(rivals, 0, Math.Max(0, ai)),
            AiTrace = aiTrace != 0,
            CacheCount = Math.Max(0, caches),
            IdolCount = Math.Max(0, idols),
            LightCycleTicks = lightCycle > 0 ? lightCycle : Sim.Core.Time.Week,
            FertilityGradient = fertility,
            GodMode = god != 0,
            Progression = progression != 0,
            Sandbox = sandbox,
        };
    }
}
