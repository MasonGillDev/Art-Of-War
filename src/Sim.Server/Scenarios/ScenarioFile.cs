using Sim.Core.Battlefields;
using Sim.Core.World;

namespace Sim.Server.Scenarios;

// M41 — a battle test-bed scenario (docs/m41-status.md Phase 4): a SITUATION IN
// THE WORLD, not a board. The ground is a real generated continent (small, but
// the same worldgen as the game); units stand on tiles relative to Blue's
// castle and battles open the way they open in the game, by units arriving.
// Nobody is placed on a subtile.
//
// Format, one statement per line (# starts a comment):
//
//   title: Border skirmish
//   note:  shown in the test bed's panel
//   size:  64            the generated map's width and height (tiles)
//   seed:  4242          the map seed
//   tps:   4             host pace at world scale (the clock holds at every beat)
//   turn:  60            ticks per battle turn (CombatConfig.RoundIntervalTicks)
//   founders: none       none | keep — the kingdoms' genesis people
//   war: blue red        these two sides are at war from tick 0
//   castle-facing: north the side Blue's castle gate faces (north | east | south | west)
//   unit <side> <role> <dx> <dy> [items…] [doctrine=hold|advance|support|withdraw]
//        [withdraw-below=N] [hp=N]
//   move <unit> <dx> <dy> [at=<tick>]     a world order (unit = the unit line's number, from 1)
//
// Sides: blue (seat 0, the test bed's own), red (seat 1). Offsets are tiles
// from Blue's castle; a unit on water lands on the nearest walkable tile.
public sealed record ScenarioUnit(
    int Side, UnitRole Role, int Dx, int Dy,
    IReadOnlyList<Resource> Items, BattleDoctrine? Doctrine, int? Hp);

public sealed record ScenarioMove(int Unit, int Dx, int Dy, long At);

public sealed record ScenarioFile(
    string Name, string Title, string Note,
    int Size, int MapSeed, double TicksPerSecond, long TurnTicks, bool KeepFounders,
    IReadOnlyList<(int A, int B)> Wars,
    IReadOnlyList<ScenarioUnit> Units,
    IReadOnlyList<ScenarioMove> Moves)
{
    // Which side Blue's castle faces: its gate (docs/structure-footprints.md).
    public Heading CastleFacing { get; init; } = Heading.North;

    public static ScenarioFile Load(string path) =>
        Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path));

    public static ScenarioFile Parse(string name, string text)
    {
        string title = name, note = "";
        int size = 64, seed = 4242;
        double tps = 4;
        long turn = 60;
        var keep = false;
        var facing = Heading.North;
        var wars = new List<(int, int)>();
        var units = new List<ScenarioUnit>();
        var moves = new List<ScenarioMove>();

        var lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            try
            {
                if (TryHeader(line, out var key, out var value))
                {
                    switch (key)
                    {
                        case "title": title = value; break;
                        case "note": note = value; break;
                        case "size": size = int.Parse(value); break;
                        case "seed": seed = int.Parse(value); break;
                        case "tps": tps = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                        case "turn": turn = long.Parse(value); break;
                        case "founders": keep = value == "keep"; break;
                        case "castle-facing":
                            facing = value switch
                            {
                                "north" => Heading.North,
                                "east" => Heading.East,
                                "south" => Heading.South,
                                "west" => Heading.West,
                                _ => throw new FormatException($"castle-facing '{value}' is not north, east, south or west"),
                            };
                            break;
                        case "war":
                            var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            wars.Add((Side(p[0]), Side(p[1])));
                            break;
                        default: throw new FormatException($"unknown setting '{key}'");
                    }
                    continue;
                }
                var w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (w[0])
                {
                    case "unit":
                    {
                        var items = new List<Resource>();
                        BattleDoctrine? doctrine = null;
                        int? hp = null;
                        var below = 0;
                        foreach (var extra in w.Skip(5))
                        {
                            if (extra.StartsWith("doctrine=")) doctrine = new BattleDoctrine(Doctrine(extra[9..]));
                            else if (extra.StartsWith("withdraw-below=")) below = int.Parse(extra[15..]);
                            else if (extra.StartsWith("hp=")) hp = int.Parse(extra[3..]);
                            else items.Add(Item(extra));
                        }
                        if (below > 0) doctrine = (doctrine ?? BattleDoctrine.Hold) with { WithdrawBelow = below };
                        units.Add(new ScenarioUnit(Side(w[1]), Role(w[2]), int.Parse(w[3]), int.Parse(w[4]), items, doctrine, hp));
                        break;
                    }
                    case "move":
                    {
                        var at = 0L;
                        foreach (var extra in w.Skip(4))
                            if (extra.StartsWith("at=")) at = long.Parse(extra[3..]);
                        moves.Add(new ScenarioMove(int.Parse(w[1]), int.Parse(w[2]), int.Parse(w[3]), at));
                        break;
                    }
                    default: throw new FormatException($"unknown statement '{w[0]}'");
                }
            }
            catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException)
            {
                throw new FormatException($"{name}: line {lineNo}: {e.Message} ({line})", e);
            }
        }
        foreach (var m in moves)
            if (m.Unit < 1 || m.Unit > units.Count)
                throw new FormatException($"{name}: move names unit {m.Unit}, but there are {units.Count} units");
        return new ScenarioFile(name, title, note, size, seed, tps, turn, keep, wars, units, moves) { CastleFacing = facing };
    }

    private static bool TryHeader(string line, out string key, out string value)
    {
        var colon = line.IndexOf(':');
        var space = line.IndexOf(' ');
        if (colon > 0 && (space < 0 || colon < space))
        {
            key = line[..colon].Trim();
            value = line[(colon + 1)..].Trim();
            return true;
        }
        key = value = "";
        return false;
    }

    public static int Side(string s) => s switch
    {
        "blue" => 0,
        "red" => 1,
        _ => throw new FormatException($"unknown side '{s}' (blue | red)"),
    };

    private static UnitRole Role(string s) => s switch
    {
        "soldier" => UnitRole.Soldier,
        "archer" => UnitRole.Archer,
        "farmer" => UnitRole.Farmer,
        "hauler" => UnitRole.Hauler,
        "builder" => UnitRole.Builder,
        "scout" => UnitRole.Scout,
        "citizen" => UnitRole.None,
        _ => throw new FormatException($"unknown role '{s}'"),
    };

    private static Resource Item(string s) => s switch
    {
        "sword" => Resource.Sword,
        "bow" => Resource.Bow,
        "shield" => Resource.Shield,
        _ => throw new FormatException($"unknown item '{s}' (sword | bow | shield)"),
    };

    private static DoctrineBehaviour Doctrine(string s) => s switch
    {
        "hold" => DoctrineBehaviour.Hold,
        "advance" => DoctrineBehaviour.Advance,
        "support" => DoctrineBehaviour.Support,
        "withdraw" => DoctrineBehaviour.Withdraw,
        _ => throw new FormatException($"unknown doctrine '{s}'"),
    };
}
