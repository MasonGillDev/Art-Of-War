using Sim.Core.Battlefields;
using Sim.Core.World;

namespace Sim.Core.Groups;

// Append-only (serialized, on the wire). How a group marches (M50).
public enum MarchMode : byte
{
    SingleFile = 1,   // everyone on the lead's exact trail: fast (caravans)
    Column     = 2,   // the group's formation, held on the march
}

// One place in a saved formation: whose it is, and where, in a FRONT-IS-NORTH frame
// relative to the leader: A subtiles to the right, B subtiles behind.
public readonly record struct FormationSlot(int UnitId, UnitRole Role, int A, int B);

// A formation the player arranged in the world and saved (SaveGroupFormationIntent).
public sealed class SavedFormation
{
    public Heading Front { get; init; }
    public List<FormationSlot> Slots { get; init; } = new();
}

// M50 — THE SHAPE A GROUP HOLDS (docs/m50-march-formations-spec.md). Pure: everything
// here is a function of the group's saved formation (or the default), its living
// members and the stored march path, so nothing is cached or stored for it.
public static class Formations
{
    // The formation's facing is the path's dominant direction over this many subtiles
    // (4 tiles): a staircase on a diagonal faces one way its whole length.
    public const int FacingWindow = 16;

    // Default column: four abreast, files −1, 0, +1, +2 right of the leader's line.
    private static (int A, int B) DefaultSlot(int i) => (i % 4 - 1, i / 4);

    // ---- frames ---------------------------------------------------------------------

    // A front-is-north offset turned to face `facing`: the world offset.
    public static (int Dx, int Dy) ToWorld(int a, int b, Heading facing) => facing switch
    {
        Heading.North => (a, b),
        Heading.East  => (-b, a),
        Heading.South => (-a, -b),
        _             => (b, -a),   // West
    };

    // A world offset seen from a formation facing `facing`: (A right, B behind).
    public static (int A, int B) ToFrame(int dx, int dy, Heading facing) => facing switch
    {
        Heading.North => (dx, dy),
        Heading.East  => (dy, -dx),
        Heading.South => (-dx, -dy),
        _             => (-dy, dx),   // West
    };

    // ---- who stands where -----------------------------------------------------------

    // Every member's place in the group's formation, front to back. A saved formation's
    // slot goes to its own unit; else a same-role member, then anyone; gaps close from the
    // back; members without a slot form rows four abreast behind. No saved formation: the
    // default column in fill order (role, id).
    public static List<(Unit Member, int A, int B)> Assign(Group group, IReadOnlyList<Unit> members)
    {
        var result = new List<(Unit, int, int)>(members.Count);
        var ordered = FormationLayout.FillOrder(members);
        if (group.Formation is not { } saved)
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                var (a, b) = DefaultSlot(i);
                result.Add((ordered[i], a, b));
            }
            return result;
        }

        var slots = saved.Slots.OrderBy(s => s.B).ThenBy(s => Math.Abs(s.A)).ThenBy(s => s.A).ToList();
        var holder = new Unit?[slots.Count];
        var free = new List<Unit>(ordered);
        var byId = ordered.ToDictionary(u => u.Id);
        for (var i = 0; i < slots.Count; i++)
            if (byId.TryGetValue(slots[i].UnitId, out var own) && free.Remove(own)) holder[i] = own;
        for (var i = 0; i < slots.Count; i++)
            if (holder[i] is null && free.FirstOrDefault(u => u.Role == slots[i].Role) is { } same) { holder[i] = same; free.Remove(same); }
        for (var i = 0; i < slots.Count; i++)
            if (holder[i] is null && free.Count > 0) { holder[i] = free[0]; free.RemoveAt(0); }
        // Close gaps from the back: the rearmost filled slot behind an empty one moves up.
        for (var i = 0; i < slots.Count; i++)
        {
            if (holder[i] is not null) continue;
            for (var j = slots.Count - 1; j > i; j--)
                if (holder[j] is not null) { holder[i] = holder[j]; holder[j] = null; break; }
        }
        for (var i = 0; i < slots.Count; i++)
            if (holder[i] is { } h) result.Add((h, slots[i].A, slots[i].B));
        // Newcomers: rows four abreast behind the rearmost slot.
        var behind = slots.Count == 0 ? 0 : slots.Max(s => s.B) + 1;
        for (var i = 0; i < free.Count; i++)
        {
            var (a, b) = DefaultSlot(i);
            result.Add((free[i], a, behind + b));
        }
        return result;
    }

    // The march order for single file: the formation front to back.
    public static List<Unit> FileOrder(Group group, IReadOnlyList<Unit> members) =>
        Assign(group, members).OrderBy(x => x.B).ThenBy(x => Math.Abs(x.A)).ThenBy(x => x.A).Select(x => x.Member).ToList();

    // ---- facing ---------------------------------------------------------------------

    // The way the formation faces at path index `j`: the dominant direction of the path
    // over the FacingWindow subtiles leading to it (ahead of it, near the start). A tie
    // takes the whole path's dominant direction. Pure function of the path.
    public static Heading FacingAt(IReadOnlyList<WorldSubtile> path, int j)
    {
        if (path.Count < 2) return Heading.North;
        j = Math.Clamp(j, 0, path.Count - 1);
        var from = Math.Max(0, j - FacingWindow);
        var to = j;
        if (to - from < FacingWindow) to = Math.Min(path.Count - 1, from + FacingWindow);
        return Dominant(path[to].X - path[from].X, path[to].Y - path[from].Y)
            ?? Dominant(path[^1].X - path[0].X, path[^1].Y - path[0].Y)
            ?? Heading.North;
    }

    private static Heading? Dominant(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return null;
        if (Math.Abs(dx) > Math.Abs(dy)) return dx > 0 ? Heading.East : Heading.West;
        if (Math.Abs(dy) > Math.Abs(dx)) return dy > 0 ? Heading.South : Heading.North;
        return null;   // a tie: no dominant direction
    }
}
