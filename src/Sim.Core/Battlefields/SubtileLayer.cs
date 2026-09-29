namespace Sim.Core.Battlefields;

// What stands on each subtile besides units (docs/structure-footprints.md,
// 2026-09-28; the slot left by M41 build decision D3). The type decides who may
// enter a subtile, from which sides, who may stand on it, and what fighting from
// it does. The rules never look at structures or terrain directly: they ask the
// layer CanStand, CanStep and GivesCover.
public enum SubtileKind : byte
{
    // Anyone moves freely.
    Open = 0,

    // Nobody enters or stands here.
    Blocked = 1,

    // The owner and allies stand here, entering from any side but a closed one
    // (the wall's outer side). Enemies never enter. Only archers deal damage
    // from it (a unit on it can't be reached for a duel, so this holds by
    // construction).
    Wall = 2,

    // As Wall, but only the owner's and allies' archers stand here, and they
    // reach the subtile in front and the two beside that one.
    Tower = 3,

    // The way through a wall: the owner and allies cross it from any side.
    Gate = 4,

    // Anyone moves freely; an archer can't pick a target standing here.
    Cover = 5,

    // Water: only boats will stand here (user, 2026-09-28). Boats don't come
    // onto boards yet (docs/structure-footprints.md, "the dock"), so for now
    // nobody stands on it. Arrows fly over it.
    Water = 6,

    // A bridge deck over water: anyone walks on it; boats pass underneath.
    Bridge = 7,
}

// Who is asking to move. Friendly = the unit's owner is the layer's owner or an
// ally of it (the structure's side). Archer = its role is ranged.
public readonly record struct BoardMover(bool Friendly, bool Archer);

public sealed class SubtileLayer
{
    private readonly SubtileKind[] _kinds;
    private readonly byte[] _closed;   // per subtile: bit (1 << Heading) = that side is closed
    private readonly byte[] _reach;    // per subtile: an archer's reach from it, in moves; 0 = the normal 1

    // The owner of what stands here (the structure's owner), or null for a board
    // with nothing that cares who you are.
    public int? Owner { get; }

    // A board of plain ground: all 16 subtiles Open.
    public static readonly SubtileLayer Open = new(null, new SubtileKind[Subtile.Count], new byte[Subtile.Count], new byte[Subtile.Count]);

    private SubtileLayer(int? owner, SubtileKind[] kinds, byte[] closed, byte[] reach)
    {
        _reach = reach;
        Owner = owner;
        _kinds = kinds;
        _closed = closed;
    }

    // A layer with the named subtiles set (no closed sides); everything else Open.
    public static SubtileLayer With(params (Subtile At, SubtileKind Kind)[] cells) =>
        Build(null, cells.Select(c => (c.At, c.Kind, (IReadOnlyList<Heading>)Array.Empty<Heading>())));

    // The general form: an owner, and each named subtile's kind and closed sides.
    // `reach`: subtiles where an archer reaches further than its neighbours
    // (see ReachAt).
    public static SubtileLayer Build(int? owner, IEnumerable<(Subtile At, SubtileKind Kind, IReadOnlyList<Heading> Closed)> cells,
        IEnumerable<(Subtile At, int Moves)>? reach = null)
    {
        var reachArr = new byte[Subtile.Count];
        foreach (var (at, moves) in reach ?? Array.Empty<(Subtile, int)>())
        {
            if (!at.IsOnBoard) throw new ArgumentException($"{at} is not on the board.");
            reachArr[at.Index] = (byte)Math.Clamp(moves, 0, Subtile.Size * 2);
        }
        var kinds = new SubtileKind[Subtile.Count];
        var closed = new byte[Subtile.Count];
        foreach (var (at, kind, sides) in cells)
        {
            if (!at.IsOnBoard) throw new ArgumentException($"{at} is not on the board.");
            kinds[at.Index] = kind;
            byte mask = 0;
            foreach (var h in sides) mask |= (byte)(1 << (int)h);
            closed[at.Index] = mask;
        }
        return new SubtileLayer(owner, kinds, closed, reachArr);
    }

    public SubtileKind KindAt(Subtile s) => s.IsOnBoard ? _kinds[s.Index] : SubtileKind.Open;

    // Is side `h` of subtile `s` closed? Only on-board subtiles have sides.
    public bool IsClosed(Subtile s, Heading h) => s.IsOnBoard && (_closed[s.Index] & (1 << (int)h)) != 0;

    // The sides of `s` that are closed, in N, E, S, W order.
    public IEnumerable<Heading> ClosedSides(Subtile s) => Headings.All.Where(h => IsClosed(s, h));

    // May this mover stand on `s`? Off the board is never a place to stand.
    public bool CanStand(Subtile s, BoardMover m)
    {
        if (!s.IsOnBoard) return false;
        return _kinds[s.Index] switch
        {
            SubtileKind.Open or SubtileKind.Cover or SubtileKind.Bridge => true,
            SubtileKind.Wall or SubtileKind.Gate => m.Friendly,
            SubtileKind.Tower => m.Friendly && m.Archer,
            _ => false,
        };
    }

    // May this mover take the one-subtile step `from` → `to`? Either end may be
    // just off the board (coming on, leaving): the on-board end's side facing the
    // other must be open, and an on-board `to` must be a place it may stand.
    public bool CanStep(Subtile from, Subtile to, BoardMover m)
    {
        if (Headings.Between(from, to) is not { } h) return false;
        if (!from.IsOnBoard && !to.IsOnBoard) return false;
        if (IsClosed(from, h)) return false;
        if (to.IsOnBoard && (!CanStand(to, m) || IsClosed(to, h.Opposite()))) return false;
        return true;
    }

    // How far an archer standing on `s` shoots, in moves: every subtile it
    // could walk to in that many orthogonal steps if Blocked subtiles were the
    // only obstacle (arrows fly over walls, not through buildings). 1, the
    // four neighbours, unless the footprint says more (a raider camp's tower: 2).
    public int ReachStored(Subtile s) => s.IsOnBoard ? _reach[s.Index] : 0;

    public int ReachAt(Subtile s) => s.IsOnBoard && _reach[s.Index] > 1 ? _reach[s.Index] : 1;

    public bool GivesCover(Subtile s) => s.IsOnBoard && _kinds[s.Index] == SubtileKind.Cover;

    // The side a tower faces: its first closed side (N, E, S, W order). Null for
    // anything that isn't a tower, or a tower with no closed side.
    public Heading? TowerFront(Subtile s)
    {
        if (KindAt(s) != SubtileKind.Tower) return null;
        foreach (var h in Headings.All)
            if (IsClosed(s, h)) return h;
        return null;
    }

    public bool IsOpen
    {
        get
        {
            for (var i = 0; i < Subtile.Count; i++)
                if (_kinds[i] != SubtileKind.Open || _closed[i] != 0 || _reach[i] > 1) return false;
            return true;
        }
    }
}
