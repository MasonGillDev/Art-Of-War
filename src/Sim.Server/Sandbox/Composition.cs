using Sim.Core.Bandits;
using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Equipment;
using Sim.Core.World;

namespace Sim.Server.Sandbox;

// The battle sandbox's document (docs/battle-sandbox.md): a situation you compose in
// Edit mode. It is data, not a world. SandboxWorld builds a fresh small real world
// from it every time; Play runs that world; Back to edit builds it again. The same
// composition always plays out the same way.
//
// JSON, camelCase, and shaped for Unity's JsonUtility (the client sends and reads
// it): no nullables, no nested arrays, no dictionaries. "None" is spelled out
// (Doctrine -1 = the role's default).
//
// Positions are tiles relative to Blue's castle, so a composition survives a change
// of seed. Nobody is placed on a subtile: the game seats them (Placement.Seat).
public sealed class Composition
{
    // Map: the generated continent's size (tiles) and seed.
    public int Size { get; set; } = 64;
    public int Seed { get; set; } = 4242;

    // The hour the world opens at, 0 ≤ Hour < 24: where tick 0 falls in the light
    // cycle (Atmosphere/WorldClock). Presentation-only today, as in the game.
    public double Hour { get; set; } = 8.0;

    // Host pace, ticks per real second. The game's main act runs at 1 (60 s turns);
    // its prelude at 4.
    public double Pace { get; set; } = 1.0;

    // The side Blue's castle gate faces: 0 N, 1 E, 2 S, 3 W (Heading).
    public int CastleFacing { get; set; }

    // Pairs of factions at war from tick 0. Bandits are everyone's enemy already.
    public CompositionWar[] Wars { get; set; } = [new CompositionWar { A = Blue, B = Red }];

    public CompositionUnit[] Units { get; set; } = [];
    public CompositionParty[] Parties { get; set; } = [];
    public CompositionMarch[] Marches { get; set; } = [];

    public const int Blue = 0, Red = 1;
    public const int MinSize = 48, MaxSize = 160;
    public const double MinPace = 0.25, MaxPace = 16;

    // The factions a composed unit may belong to. Bandits come as parties.
    public static readonly int[] Kingdoms = [Blue, Red];

    // The roles you can drag in. Bandits come as parties; boats and heirs are not
    // battle pieces; a King is (one per faction, crowned at genesis).
    public static readonly UnitRole[] PlaceableRoles =
    [
        UnitRole.Soldier, UnitRole.Archer, UnitRole.King,
        UnitRole.Farmer, UnitRole.Hauler, UnitRole.Builder, UnitRole.Scout,
        UnitRole.Miner, UnitRole.Lumberjack, UnitRole.Quarryman, UnitRole.None,
    ];

    // Why this composition can't be built, or null. Everything that would make a
    // situation the game can't produce is refused here, with the reason.
    public string? Problem()
    {
        if (Size < MinSize || Size > MaxSize) return $"map size {Size} outside {MinSize}..{MaxSize}";
        if (Hour < 0 || Hour >= 24) return $"hour {Hour} outside 0..24";
        if (Pace < MinPace || Pace > MaxPace) return $"pace {Pace} outside {MinPace}..{MaxPace}";
        if (CastleFacing is < 0 or > 3) return $"castle facing {CastleFacing} is not 0..3";
        foreach (var w in Wars)
            if (!Kingdoms.Contains(w.A) || !Kingdoms.Contains(w.B) || w.A == w.B)
                return $"war {w.A}–{w.B} is not between two kingdoms";

        var ids = new HashSet<int>();
        var kings = new HashSet<int>();
        foreach (var u in Units)
        {
            if (!ids.Add(u.Id)) return $"id {u.Id} is used twice";
            if (!Kingdoms.Contains(u.Faction)) return $"unit {u.Id}: faction {u.Faction} is not a kingdom";
            var role = (UnitRole)u.Role;
            if (!PlaceableRoles.Contains(role)) return $"unit {u.Id}: role {u.Role} can't be placed";
            if (role == UnitRole.King && !kings.Add(u.Faction)) return $"unit {u.Id}: faction {u.Faction} already has a king";
            // The loadout, by the storehouse road's own rules: a scratch body of the
            // role wears each item in turn.
            var body = new Unit(0, default) { Role = role, OwnerId = u.Faction };
            foreach (var item in u.Gear)
            {
                if (item is < 0 or > byte.MaxValue || !Enum.IsDefined((Resource)item)) return $"unit {u.Id}: item {item} is not an item";
                if (EquipRules.Grant(body, (Resource)item) is { } why) return $"unit {u.Id}: {why}";
            }
            if (u.Doctrine != CompositionUnit.DefaultDoctrine
                && DoctrineCatalog.Blocker(role, (DoctrineBehaviour)u.Doctrine) is { } bad)
                return $"unit {u.Id}: {bad}";
            if (u.WithdrawBelow < 0 || u.WithdrawBelow > Subtile.Count)
                return $"unit {u.Id}: withdraw threshold {u.WithdrawBelow} outside 0..{Subtile.Count}";
        }
        foreach (var p in Parties)
        {
            if (!ids.Add(p.Id)) return $"id {p.Id} is used twice";
            if (p.Size < 1 || p.Size > BanditConstants.MaxPartySize)
                return $"party {p.Id}: size {p.Size} outside 1..{BanditConstants.MaxPartySize}";
        }
        foreach (var m in Marches)
        {
            var u = Units.FirstOrDefault(x => x.Id == m.Unit);
            if (u is null) return $"a march names unit {m.Unit}, which isn't a composed unit";
            // Blue takes orders only in Play, through the game's UI (user decision 3).
            if (u.Faction == Blue) return $"unit {m.Unit} is Blue's: Blue takes orders only in Play";
            if (m.After < 0) return $"unit {m.Unit}: a march can't leave before the start";
        }
        return null;
    }

    public static Composition Empty() => new();
}

public sealed class CompositionWar
{
    public int A { get; set; }
    public int B { get; set; }
}

// One composed person. Id is the composition's own (stable across edits), not the
// sim's unit id (SandboxWorld reports which sim unit each became).
public sealed class CompositionUnit
{
    public const int DefaultDoctrine = -1;

    public int Id { get; set; }
    public int Faction { get; set; }
    public int Role { get; set; }          // UnitRole
    public int Dx { get; set; }
    public int Dy { get; set; }
    public int[] Gear { get; set; } = [];  // Resource ids (Sword, Bow, Shield…)
    public int Doctrine { get; set; } = DefaultDoctrine;  // DoctrineBehaviour, or -1 = the role's default
    public int WithdrawBelow { get; set; }
}

// A bandit party: the bandit driver runs it (natural spawning off).
public sealed class CompositionParty
{
    public int Id { get; set; }
    public int Dx { get; set; }
    public int Dy { get; set; }
    public int Size { get; set; } = 3;
}

// A world order for a non-Blue unit: walk to (Dx, Dy), leaving After ticks into Play.
// A unit's marches go out in order of After.
public sealed class CompositionMarch
{
    public int Unit { get; set; }
    public int Dx { get; set; }
    public int Dy { get; set; }
    public long After { get; set; }
}
