using System.Net;
using System.Text.Json;
using Sim.Core.Battlefields;
using Sim.Core.Equipment;
using Sim.Core.World;

namespace Sim.Server.Sandbox;

// The battle sandbox's host (docs/battle-sandbox.md; client: the BattleSandbox scene).
// `--sandbox [DIR]` runs this instead of a full game. It holds ONE composition and a
// real GameHost built from it:
//
//   Edit — the world is built at tick 0 and PAUSED. Every change to the composition
//          rebuilds it, so the editor shows what the game really does with it.
//   Play — the same world, un-paused, at the composition's pace. Nothing else
//          differs from a real game but the map's size and fog, which the client
//          lifts (?reveal=1, never &orders=1).
//
// Routes (dev only; the game never talks to /v2/dev/*):
//
//   GET  /v2/dev/sandbox              the state: mode, epochs, tick, the composition
//                                     and where each piece really landed
//   PUT  /v2/dev/composition          replace the composition (refused with the
//                                     reason if the game couldn't produce it)
//   POST /v2/dev/composition/new      start over from an empty composition
//   GET  /v2/dev/compositions         the saved ones
//   POST /v2/dev/composition/save     {"name": "…"}
//   POST /v2/dev/composition/open     {"name": "…"}
//   POST /v2/dev/composition/delete   {"name": "…"}
//   POST /v2/dev/play                 un-pause
//   POST /v2/dev/edit                 pause and rebuild from the composition
//   GET  /v2/dev/catalogue            placeable roles, gear by role
//
// Epochs: WorldEpoch bumps on every rebuild (the client clears its session);
// GenesisEpoch only when the genesis payload changes (map size or seed, or the hour,
// which is genesis's light cycle), when the client must reload its scene.
//
// Compositions are saved as JSON in DIR (default `sandbox/`, gitignored: the user's
// own, never committed).
public sealed class SandboxHost : IDisposable
{
    public const string DefaultDirectory = "sandbox";

    public enum SandboxMode { Edit, Play }

    private readonly string _dir;
    private readonly object _gate = new();

    public GameHost Current { get; private set; } = null!;
    public Composition Composition { get; private set; } = Composition.Empty();
    public string Name { get; private set; } = "";
    public SandboxMode Mode { get; private set; } = SandboxMode.Edit;
    public int WorldEpoch { get; private set; }
    public int GenesisEpoch { get; private set; }
    public IReadOnlyList<PlacedPiece> Placed { get; private set; } = [];
    public HttpApi? Api { get; set; }

    public SandboxHost(string? dir = null)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? DefaultDirectory : dir;
        Rebuild(Composition.Empty(), genesisChanged: true);
    }

    // ---- building ------------------------------------------------------------------

    // Build `c`'s world, paused at tick 0, and make it current. Throws only for a
    // composition that failed Problem(), which callers check first.
    private void Rebuild(Composition c, bool genesisChanged)
    {
        var placed = new List<PlacedPiece>();
        var (build, setup) = SandboxWorld.Prepare(c, placed);
        var host = new GameHost(build, SandboxWorld.Seed(c), c.Pace,
            // The real bandit driver, spawning nothing of its own: MaxLiveParties 0
            // keeps its census, raids, flight and looting for the parties you place.
            new Bandits.BanditConfig { Enabled = true, MaxLiveParties = 0 },
            new Ai.AiConfig { Enabled = false },
            setup: setup);
        host.LightCycle = SandboxWorld.Light(c);
        host.PreludeTicksPerSecond = c.Pace;
        host.SetPace(true, null);        // Edit: held at tick 0
        host.Start();

        var old = Current;
        Current = host;
        Composition = c;
        Placed = placed;
        Mode = SandboxMode.Edit;
        WorldEpoch++;
        if (genesisChanged) GenesisEpoch++;
        Api?.SwapHost(host);
        old?.Stop();
    }

    private static bool GenesisDiffers(Composition a, Composition b) =>
        a.Size != b.Size || a.Seed != b.Seed || a.Hour != b.Hour;

    // Replace the composition. Null on success, else why it was refused (and the
    // current world is left alone).
    public string? Set(Composition c)
    {
        lock (_gate)
        {
            if (c.Problem() is { } why) return why;
            Rebuild(c, GenesisDiffers(Composition, c));
            return null;
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            if (Mode == SandboxMode.Play) return;
            Mode = SandboxMode.Play;
            Current.Resume();
        }
    }

    public void Edit()
    {
        lock (_gate) Rebuild(Composition, genesisChanged: false);
    }

    // ---- storage -------------------------------------------------------------------

    public IReadOnlyList<string> Saved() =>
        Directory.Exists(_dir)
            ? Directory.GetFiles(_dir, "*.json").Select(Path.GetFileNameWithoutExtension)
                .OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    private string PathFor(string name)
    {
        var clean = new string(name.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_').ToArray()).Trim();
        if (clean.Length == 0) throw new ArgumentException("a composition needs a name (letters, digits, space, - or _)");
        return Path.Combine(_dir, clean + ".json");
    }

    public void Save(string name)
    {
        lock (_gate)
        {
            var path = PathFor(name);
            Directory.CreateDirectory(_dir);
            File.WriteAllText(path, JsonSerializer.Serialize(Composition, Pretty));
            Name = Path.GetFileNameWithoutExtension(path);
        }
    }

    public string? Open(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return $"no composition '{name}' in {_dir}";
        Composition? c;
        try { c = JsonSerializer.Deserialize<Composition>(File.ReadAllText(path), ServerJson.Options); }
        catch (JsonException e) { return $"'{name}' is not a composition: {e.Message}"; }
        if (c is null) return $"'{name}' is empty";
        var why = Set(c);
        if (why is null) Name = Path.GetFileNameWithoutExtension(path);
        return why;
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
        if (string.Equals(Name, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)) Name = "";
    }

    public void New()
    {
        lock (_gate)
        {
            var c = Composition.Empty();
            Rebuild(c, GenesisDiffers(Composition, c));
            Name = "";
        }
    }

    private static readonly JsonSerializerOptions Pretty = new(ServerJson.Options) { WriteIndented = true };

    // ---- the dev routes ------------------------------------------------------------

    public bool Handle(HttpListenerContext ctx, string method, string path, string body)
    {
        try
        {
            switch (method, path)
            {
                case ("GET", "/v2/dev/sandbox"):
                    WriteState(ctx);
                    return true;
                case ("PUT", "/v2/dev/composition"):
                {
                    Composition? c;
                    try { c = JsonSerializer.Deserialize<Composition>(body, ServerJson.Options); }
                    catch (JsonException e) { return Refuse(ctx, $"not a composition: {e.Message}"); }
                    if (c is null) return Refuse(ctx, "empty composition");
                    if (Set(c) is { } why) return Refuse(ctx, why);
                    WriteState(ctx);
                    return true;
                }
                case ("POST", "/v2/dev/composition/new"):
                    New();
                    WriteState(ctx);
                    return true;
                case ("GET", "/v2/dev/compositions"):
                    HttpApi.WriteJson(ctx, 200, JsonSerializer.Serialize(new { names = Saved() }, ServerJson.Options));
                    return true;
                case ("POST", "/v2/dev/composition/save"):
                    Save(NameOf(body));
                    WriteState(ctx);
                    return true;
                case ("POST", "/v2/dev/composition/open"):
                    if (Open(NameOf(body)) is { } openWhy) return Refuse(ctx, openWhy);
                    WriteState(ctx);
                    return true;
                case ("POST", "/v2/dev/composition/delete"):
                    Delete(NameOf(body));
                    WriteState(ctx);
                    return true;
                case ("POST", "/v2/dev/play"):
                    Play();
                    WriteState(ctx);
                    return true;
                case ("POST", "/v2/dev/edit"):
                    Edit();
                    WriteState(ctx);
                    return true;
                case ("GET", "/v2/dev/catalogue"):
                    HttpApi.WriteJson(ctx, 200, JsonSerializer.Serialize(Catalogue(), ServerJson.Options));
                    return true;
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or KeyNotFoundException or InvalidOperationException)
        {
            return Refuse(ctx, e.Message);
        }
        return false;
    }

    private static string NameOf(string body) =>
        JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement
            .TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

    private static bool Refuse(HttpListenerContext ctx, string why)
    {
        HttpApi.WriteJson(ctx, 400, JsonSerializer.Serialize(new { error = why }, ServerJson.Options));
        return true;
    }

    public SandboxState State() => new()
    {
        Mode = Mode == SandboxMode.Play ? "play" : "edit",
        Name = Name,
        WorldEpoch = WorldEpoch,
        GenesisEpoch = GenesisEpoch,
        Tick = Current.VirtualTick,
        Paused = Current.IsPaused,
        Composition = Composition,
        Placed = Placed.ToArray(),
        AnchorX = SandboxWorld.Anchor(Composition).X,
        AnchorY = SandboxWorld.Anchor(Composition).Y,
    };

    private void WriteState(HttpListenerContext ctx) =>
        HttpApi.WriteJson(ctx, 200, JsonSerializer.Serialize(State(), ServerJson.Options));

    public static SandboxCatalogue Catalogue() => new()
    {
        Roles = Composition.PlaceableRoles.Select(r => (int)r).ToArray(),
        Gear = Enum.GetValues<Resource>()
            .Where(r => EquipmentCatalog.TryGetSpec(r, out var s) && s.PowerModifier + s.HealthModifier != 0)
            .Select(r =>
            {
                var s = EquipmentCatalog.Spec(r);
                return new SandboxGear
                {
                    Item = (int)r,
                    Roles = s.AllowedRoles.Select(x => (int)x).OrderBy(x => x).ToArray(),
                    Power = s.PowerModifier,
                    Health = s.HealthModifier,
                };
            })
            .ToArray(),
        Kingdoms = Composition.Kingdoms,
        MaxPartySize = Sim.Core.Bandits.BanditConstants.MaxPartySize,
        MaxWithdrawBelow = Subtile.Count,
    };

    public void Dispose() => Current?.Stop();
}

public sealed class SandboxState
{
    public string Mode { get; set; } = "edit";
    public string Name { get; set; } = "";
    public int WorldEpoch { get; set; }
    public int GenesisEpoch { get; set; }
    public long Tick { get; set; }
    public bool Paused { get; set; }
    public Composition Composition { get; set; } = new();
    public PlacedPiece[] Placed { get; set; } = [];
    // Blue's castle tile: composed positions are offsets from it.
    public int AnchorX { get; set; }
    public int AnchorY { get; set; }
}

// What the palette and inspector may offer. Gear: the battle items only (a cart is
// a hauler's tool, not a weapon), each with the roles that may wear it.
public sealed class SandboxCatalogue
{
    public int[] Roles { get; set; } = [];
    public SandboxGear[] Gear { get; set; } = [];
    public int[] Kingdoms { get; set; } = [];
    public int MaxPartySize { get; set; }
    public int MaxWithdrawBelow { get; set; }
}

public sealed class SandboxGear
{
    public int Item { get; set; }
    public int[] Roles { get; set; } = [];
    public int Power { get; set; }
    public int Health { get; set; }
}
