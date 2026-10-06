using Sim.Core.World;

namespace Sim.Core.Battlefields;

// A structure's footprint on its tile's board (docs/structure-footprints.md): a
// fixed pattern of subtile types per structure kind, set by the structure's
// Facing and, for wall lines, by which neighbours it joins.
//
// Built so far: Castle, Wall, Gate, Tower, BanditCamp, Farm, School, House,
// Workshop, Dock, Quarry, Lodge, Cache, Rubble (drawn all open), Smelter,
// Smithy, Mine, LumberCamp, Barracks, Stockpile, ConstructionSite. Every other
// kind keeps plain ground until the user draws its footprint.
public static class Footprints
{
    // The footprint of whatever stands on `tile`, joined to its neighbours.
    public static SubtileLayer For(GameWorld world, TileCoord tile)
    {
        var s = world.Structures.GetValueOrDefault(tile);
        return s is null ? SubtileLayer.Open : For(s, JoinsOf(world, s));
    }

    // As the viewer knows it: a structure joins only neighbours the viewer can
    // see (`knows`), the way the client joins its drawn walls, so a wall's shape
    // never gives away a wall in the fog. Open water (no structure) is terrain
    // and always known.
    public static SubtileLayer For(GameWorld world, Structure s, Func<TileCoord, bool> knows)
    {
        var joins = JoinsOf(world, s)
            .Where(h => { var n = new TileCoord(s.At.X + h.Dx(), s.At.Y + h.Dy()); return !world.Structures.ContainsKey(n) || knows(n); })
            .ToList();
        return For(s, joins);
    }

    // A structure on its own, joining nothing.
    public static SubtileLayer For(Structure? s) => s is null ? SubtileLayer.Open : For(s, Array.Empty<Heading>());

    public static SubtileLayer For(Structure s, IReadOnlyCollection<Heading> joins) =>
        Build(Bridge.IsDeck(s) ? StructureKind.Bridge : s.Kind, s.OwnerId, FacingOf(s), joins);

    // A new structure's facing when the player gave none: a wall, gate or tower
    // faces away from the owner's nearest castle (its outer side: an enclosure
    // round the castle then turns its corners the right way); anything else
    // North. "Away" is the axis the tile lies further along, east or west on a
    // tie; north is y − 1.
    public static Heading DefaultFacing(GameWorld world, StructureKind kind, int owner, TileCoord at)
    {
        if (kind is not (StructureKind.Wall or StructureKind.Gate or StructureKind.Tower)) return Heading.North;
        Castle? castle = null;
        foreach (var c in world.Structures.Values.OfType<Castle>().Where(c => c.OwnerId == owner)
                     .OrderBy(c => Math.Abs(c.At.X - at.X) + Math.Abs(c.At.Y - at.Y)).ThenBy(c => c.At.Y).ThenBy(c => c.At.X))
        { castle = c; break; }
        if (castle is null) return Heading.North;
        var dx = at.X - castle.At.X;
        var dy = at.Y - castle.At.Y;
        if (dx == 0 && dy == 0) return Heading.North;
        return Math.Abs(dx) >= Math.Abs(dy)
            ? (dx > 0 ? Heading.East : Heading.West)
            : (dy < 0 ? Heading.North : Heading.South);
    }

    // Which way a structure's footprint is turned: its Facing, or for a dock
    // the side its slip lies on.
    public static Heading FacingOf(Structure s) => s is Dock dock ? SlipSide(dock) : s.Facing;

    // A kind's pattern on its own, facing North, joining nothing: what the
    // build-placement preview turns (the dock with its water on the north edge,
    // a wall line straight, a canal running north–south). Null for kinds that
    // stay plain ground. Owner-free: who may stand on walls is the viewer's call.
    public static SubtileLayer? Pattern(StructureKind kind)
    {
        var joins = kind is StructureKind.Canal or StructureKind.Bridge
            ? new[] { Heading.North, Heading.South } : Array.Empty<Heading>();
        var layer = Build(kind, null, Heading.North, joins);
        return layer.IsOpen && kind != StructureKind.Rubble ? null : layer;
    }

    // The one builder. `facing` turns the drawn patterns; a dock's facing is
    // the side its slip lies on.
    private static SubtileLayer Build(StructureKind kind, int? owner, Heading facing, IReadOnlyCollection<Heading> joins)
    {
        var turns = (int)facing;
        if (kind == StructureKind.Castle) return Turned(owner, turns, CastleNorth(), CastleTowers);
        if (kind == StructureKind.BanditCamp) return Turned(owner, turns, BanditCampNorth(), (new Subtile(3, 3), CampTowerReach));
        if (kind == StructureKind.Dock) return Turned(owner, turns, DockNorth());
        if (kind == StructureKind.Canal) return SubtileLayer.Build(owner, Channel(joins));
        if (kind == StructureKind.Bridge) return SubtileLayer.Build(owner, Deck(joins));
        if (Drawn(kind) is { } drawn) return Turned(owner, turns, drawn);
        var middle = kind switch
        {
            StructureKind.Wall => SubtileKind.Wall,
            StructureKind.Gate => SubtileKind.Gate,
            StructureKind.Tower => SubtileKind.Tower,
            _ => (SubtileKind?)null,
        };
        return middle is { } m ? SubtileLayer.Build(owner, Line(joins, facing, m)) : SubtileLayer.Open;
    }

    // The neighbours a structure joins, N, E, S, W:
    //   * a wall, gate or tower: a standing wall, gate, tower or castle of its
    //     own owner. The same rule the client's FortificationDresser draws the
    //     line by, so the board matches the picture;
    //   * a canal: another canal tile, or open water (the lake or river it was
    //     dug from), any owner.
    public static IReadOnlyList<Heading> JoinsOf(GameWorld world, Structure s)
    {
        var joins = new List<Heading>();
        if (s.Kind == StructureKind.Canal || Bridge.IsDeck(s))
        {
            foreach (var h in Headings.All)
            {
                var at = new TileCoord(s.At.X + h.Dx(), s.At.Y + h.Dy());
                if (!world.Grid.InBounds(at)) continue;
                if ((world.Structures.TryGetValue(at, out var n) && (n.Kind == StructureKind.Canal || Bridge.IsDeck(n)))
                    || (world.Grid.BiomeAt(at) == Biome.Water && n is null))
                    joins.Add(h);
            }
            return joins;
        }
        if (s.Kind is not (StructureKind.Wall or StructureKind.Gate or StructureKind.Tower)) return joins;
        foreach (var h in Headings.All)
        {
            var at = new TileCoord(s.At.X + h.Dx(), s.At.Y + h.Dy());
            if (world.Structures.TryGetValue(at, out var n) && n.OwnerId == s.OwnerId && n.Health > 0
                && n.Kind is StructureKind.Wall or StructureKind.Gate or StructureKind.Tower or StructureKind.Castle)
                joins.Add(h);
        }
        return joins;
    }

    // A pattern drawn facing North, turned clockwise a quarter per step to the
    // structure's Facing (East, South, West), closed sides turning with it.
    private static SubtileLayer Turned(int? owner, int turns, List<(Subtile At, SubtileKind Kind, Heading[] Closed)> north,
        params (Subtile At, int Moves)[] reach)
    {
        return SubtileLayer.Build(owner,
            north.Select(c => (
                Rotate(c.At, turns),
                c.Kind,
                (IReadOnlyList<Heading>)c.Closed.Select(h => (Heading)(((int)h + turns) % 4)).ToList())),
            reach.Select(r => (Rotate(r.At, turns), r.Moves)));
    }

    // A raider camp tower's archer reaches every subtile within this many moves
    // (user, 2026-09-28); Blocked subtiles stop the arrows.
    public const int CampTowerReach = 2;

    // The raider camp, facing North, as the user drew it (2026-09-28); the rest
    // is open land. The tower takes only the camp's own archers (Tower: the
    // owner's and allies' archers); they climb it from (2, 3) or (3, 2). The
    // corner (0, 0) is blocked too (user): open, it was a pocket reached only
    // from outside the tile.
    //
    //      x=0   x=1   x=2   x=3
    //   0  ███   ███    .     .
    //   1  ███    .    ███    .
    //   2  ███    .     .     .
    //   3   .     .     .    TWR
    private static List<(Subtile At, SubtileKind Kind, Heading[] Closed)> BanditCampNorth() => new()
    {
        (new Subtile(0, 0), SubtileKind.Blocked, Array.Empty<Heading>()),
        (new Subtile(1, 0), SubtileKind.Blocked, Array.Empty<Heading>()),
        (new Subtile(0, 1), SubtileKind.Blocked, Array.Empty<Heading>()),
        (new Subtile(2, 1), SubtileKind.Blocked, Array.Empty<Heading>()),
        (new Subtile(0, 2), SubtileKind.Blocked, Array.Empty<Heading>()),
        (new Subtile(3, 3), SubtileKind.Tower, Array.Empty<Heading>()),
    };

    // The user's drawings (2026-09-28), facing North; every subtile not named
    // is open land. ███ = Blocked.
    //
    //   Farm                          School
    //      x=0  x=1  x=2  x=3            x=0  x=1  x=2  x=3
    //   0   .    .    .    .          0   .    .    .    .
    //   1   .    .   ███   .          1   .   ███  ███   .
    //   2   .    .    .    .          2   .   ███  ███   .
    //   3   .    .    .    .          3   .    .    .    .
    //
    //   House                         Workshop
    //      x=0  x=1  x=2  x=3            x=0  x=1  x=2  x=3
    //   0   .   ███  ███   .          0   .    .    .    .
    //   1   .    .    .    .          1   .    .   ███   .
    //   2   .    .    .    .          2   .   ███  ███   .
    //   3   .    .   ███   .          3   .    .    .    .
    //
    //   Quarry                        Lodge                         Cache
    //      x=0  x=1  x=2  x=3            x=0  x=1  x=2  x=3            x=0  x=1  x=2  x=3
    //   0   .    .    .    .          0   .    .    .    .          0   .    .    .    .
    //   1   .    .   ▒▒▒   .          1   .   ▒▒▒   .    .          1   .   ███   .    .
    //   2   .    .    .    .          2   .   ▒▒▒   .    .          2   .    .    .    .
    //   3   .    .    .    .          3   .    .    .    .          3   .    .    .    .
    //
    //   Smelter                       Smithy                        Rubble: all open land
    //      x=0  x=1  x=2  x=3            x=0  x=1  x=2  x=3
    //   0   .    .    .    .          0   .    .    .    .
    //   1   .   ███  ▒▒▒   .          1   .   ███   .   ▒▒▒
    //   2   .    .    .    .          2   .    .    .    .
    //   3   .    .    .    .          3   .    .    .    .
    //
    //   Mine                          Lumber camp                   Barracks
    //      x=0  x=1  x=2  x=3            x=0  x=1  x=2  x=3            x=0  x=1  x=2  x=3
    //   0   .    .   ███   .          0   .    .    .    .          0  TWR   .    .    .
    //   1   .    .    .    .          1  ███   .    .    .          1   .    .   ███   .
    //   2   .    .    .    .          2   .    .   ███   .          2   .   ███  ███   .
    //   3   .    .    .    .          3   .    .    .    .          3   .    .    .    .
    //
    //   Stockpile                     Construction site (whatever is being built)
    //      x=0  x=1  x=2  x=3            x=0  x=1  x=2  x=3
    //   0   .    .    .    .          0   .    .    .    .
    //   1   .   ███   .    .          1   .   ███   .    .
    //   2   .   ███   .    .          2   .    .   ▒▒▒   .
    //   3   .    .    .    .          3   .    .    .    .
    //   (▒▒▒ = Cover; TWR = Tower: the owner's and allies' archers, normal reach)
    private static List<(Subtile At, SubtileKind Kind, Heading[] Closed)>? Drawn(StructureKind kind) => kind switch
    {
        StructureKind.Farm => Blocked((2, 1)),
        StructureKind.School => Blocked((1, 1), (2, 1), (1, 2), (2, 2)),
        StructureKind.House => Blocked((1, 0), (2, 0), (2, 3)),
        StructureKind.Workshop => Blocked((2, 1), (1, 2), (2, 2)),
        StructureKind.Quarry => Cover((2, 1)),
        StructureKind.Lodge => Cover((1, 1), (1, 2)),
        StructureKind.Cache => Blocked((1, 1)),
        // The idol itself, one blocked subtile (user: "you decide where"): just
        // off centre, so the ground round it stays open.
        StructureKind.Idol => Blocked((1, 1)),
        StructureKind.Rubble => new List<(Subtile At, SubtileKind Kind, Heading[] Closed)>(),
        StructureKind.Smelter => Blocked((1, 1)).Concat(Cover((2, 1))).ToList(),
        StructureKind.Smithy => Blocked((1, 1)).Concat(Cover((3, 1))).ToList(),
        StructureKind.CopperMine or StructureKind.IronMine or StructureKind.SteelMine => Blocked((2, 0)),
        StructureKind.Stockpile => Blocked((1, 1), (1, 2)),
        StructureKind.ConstructionSite => Blocked((1, 1)).Concat(Cover((2, 2))).ToList(),
        StructureKind.LumberCamp => Blocked((0, 1), (2, 2)),
        StructureKind.Barracks => Blocked((2, 1), (1, 2), (2, 2))
            .Append((new Subtile(0, 0), SubtileKind.Tower, Array.Empty<Heading>())).ToList(),
        _ => null,
    };

    private static List<(Subtile At, SubtileKind Kind, Heading[] Closed)> Cover(params (int X, int Y)[] cells) =>
        cells.Select(c => (new Subtile(c.X, c.Y), SubtileKind.Cover, Array.Empty<Heading>())).ToList();

    // The dock (~~~ = Water). The user drew it with its water on the east edge;
    // here it is turned to face North, the water on the north edge:
    //
    //   as drawn (facing east)        facing North
    //      x=0  x=1  x=2  x=3            x=0  x=1  x=2  x=3
    //   0   .    .    .   ~~~         0  ~~~  ~~~  ~~~  ~~~
    //   1   .    .   ███  ~~~         1   .   ███   .    .
    //   2   .    .    .   ~~~         2   .    .    .    .
    //   3   .    .    .   ~~~         3   .    .    .    .
    //
    // A dock faces its slip, the water tile its boats use: the water always lies
    // on that edge, whatever Facing says (SlipSide).
    private static List<(Subtile At, SubtileKind Kind, Heading[] Closed)> DockNorth()
    {
        var cells = Blocked((1, 1));
        for (var x = 0; x < Subtile.Size; x++)
            cells.Add((new Subtile(x, 0), SubtileKind.Water, Array.Empty<Heading>()));
        return cells;
    }

    // The side of the dock its slip lies on.
    private static Heading SlipSide(Dock dock) =>
        Battlefields.EdgeToward(dock.At, dock.Slip) ?? Heading.North;

    private static List<(Subtile At, SubtileKind Kind, Heading[] Closed)> Blocked(params (int X, int Y)[] cells) =>
        cells.Select(c => (new Subtile(c.X, c.Y), SubtileKind.Blocked, Array.Empty<Heading>())).ToList();

    // A castle corner tower's archer reaches every subtile within this many moves
    // (user, 2026-10-01): from a corner that is the gap and the nearest courtyard
    // subtile, so an attacker's foothold is under fire from both flanks.
    public const int CastleTowerReach = 2;

    // The castle, facing North (user, 2026-10-01; docs/structure-footprints.md,
    // Update 2026-10-01): a ring round an open 2×2 courtyard, each ring subtile
    // closed on its outer side (two at a corner). The corners are Towers; the keep
    // on the back wall is Blocked (nobody stands in it); one Open gap at (1, 0) on
    // the facing edge is the gate, which anyone may use. Turned clockwise a quarter
    // per step to East, South, West.
    //
    //      x=0   x=1   x=2   x=3
    //   0  TWR   gap    W    TWR
    //   1   W     .     .     W
    //   2   W     .     .     W
    //   3  TWR   ███   ███   TWR
    private static List<(Subtile At, SubtileKind Kind, Heading[] Closed)> CastleNorth()
    {
        var cells = new List<(Subtile, SubtileKind, Heading[])>();
        var gap = new Subtile(1, 0);
        var keep = new HashSet<Subtile> { new(1, 3), new(2, 3) };
        foreach (var at in Subtile.All())
        {
            var outer = Headings.All.Where(at.IsOnEdgeRow).ToArray();
            if (outer.Length == 0 || at == gap) continue;
            var kind = keep.Contains(at) ? SubtileKind.Blocked : outer.Length == 2 ? SubtileKind.Tower : SubtileKind.Wall;
            cells.Add((at, kind, outer));
        }
        return cells;
    }

    private static readonly (Subtile At, int Moves)[] CastleTowers =
        { (new Subtile(0, 0), CastleTowerReach), (new Subtile(3, 0), CastleTowerReach), (new Subtile(0, 3), CastleTowerReach), (new Subtile(3, 3), CastleTowerReach) };

    // A wall line through the tile, running to the edge of every neighbour it
    // joins, one row in from its outer side so it meets the neighbours' lines
    // at the shared edge:
    //   * the ARMS are the edges it runs to: the joined neighbours; a line
    //     joining one neighbour, or two opposite ones, runs straight across; a
    //     line joining nothing runs across its facing;
    //   * the OUTER sides (one per axis) are the side attackers stand on. At a
    //     corner Facing picks which way round: toward the sides away from the
    //     joined neighbours (the outer corner of an enclosure), or toward the
    //     joined sides (the inner corner). Elsewhere Facing is the outer side of
    //     the run across it, and a quarter turn anticlockwise of it for a run
    //     along it (N → W, S → E, E → N, W → S);
    //   * each wall subtile is closed on its run's outer side, except where the
    //     line itself carries on that way (the inside of a turn).
    // A gate or tower tile's special subtiles are the two middle ones of a
    // straight run, or the corner of a turn.
    private static List<(Subtile At, SubtileKind Kind, IReadOnlyList<Heading> Closed)> Line(
        IReadOnlyCollection<Heading> joins, Heading facing, SubtileKind middle)
    {
        var arms = new HashSet<Heading>(joins);
        if (arms.Count == 0)
        {
            var across = facing is Heading.North or Heading.South ? Heading.East : Heading.North;
            arms.Add(across);
            arms.Add(across.Opposite());
        }
        else if (arms.Count == 1)
            arms.Add(arms.First().Opposite());

        var horizontal = arms.Contains(Heading.East) || arms.Contains(Heading.West);
        var vertical = arms.Contains(Heading.North) || arms.Contains(Heading.South);
        var corner = arms.Count == 2 && horizontal && vertical;

        Heading outerV, outerH;   // the outer side of the east–west run, of the north–south run
        if (corner)
        {
            var a = arms.Single(h => h is Heading.East or Heading.West);
            var b = arms.Single(h => h is Heading.North or Heading.South);
            var outerCorner = facing == a.Opposite() || facing == b.Opposite();
            outerH = outerCorner ? a.Opposite() : a;
            outerV = outerCorner ? b.Opposite() : b;
        }
        else
        {
            var anticlockwise = (Heading)(((int)facing + 3) % 4);
            outerV = facing is Heading.North or Heading.South ? facing : anticlockwise;
            outerH = facing is Heading.East or Heading.West ? facing : anticlockwise;
        }
        var row = outerV == Heading.North ? 1 : 2;
        var col = outerH == Heading.West ? 1 : 2;

        // The runs: east–west along `row`, north–south along `col`, each from
        // the junction (or the far edge) to every arm's edge.
        var runOuter = new Dictionary<Subtile, List<Heading>>();
        void Add(Subtile c, Heading outer)
        {
            if (!runOuter.TryGetValue(c, out var list)) runOuter[c] = list = new List<Heading>();
            if (!list.Contains(outer)) list.Add(outer);
        }
        if (horizontal)
        {
            var from = arms.Contains(Heading.West) ? 0 : col;
            var to = arms.Contains(Heading.East) ? Subtile.Size - 1 : col;
            if (!vertical) { from = 0; to = Subtile.Size - 1; }
            for (var x = from; x <= to; x++) Add(new Subtile(x, row), outerV);
        }
        if (vertical)
        {
            var from = arms.Contains(Heading.North) ? 0 : row;
            var to = arms.Contains(Heading.South) ? Subtile.Size - 1 : row;
            if (!horizontal) { from = 0; to = Subtile.Size - 1; }
            for (var y = from; y <= to; y++) Add(new Subtile(col, y), outerH);
        }

        var cells = new List<(Subtile, SubtileKind, IReadOnlyList<Heading>)>();
        foreach (var (at, outers) in runOuter.OrderBy(c => c.Key.Index))
        {
            var special = corner
                ? at == new Subtile(col, row)
                : !(horizontal && vertical) && (horizontal ? at.X is 1 or 2 : at.Y is 1 or 2);
            var kind = special ? middle : SubtileKind.Wall;
            var closed = kind == SubtileKind.Gate
                ? new List<Heading>()
                : Headings.All.Where(h => outers.Contains(h) && !runOuter.ContainsKey(at.Step(h))).ToList();
            cells.Add((at, kind, closed));
        }
        return cells;
    }

    // A canal's channel (the user's drawing, 2026-09-28: two wide, banks either
    // side): the four middle subtiles always, and from them a two-wide arm to
    // the edge of every neighbour it joins, so it runs on into the next canal
    // tile or the water it was dug from, and turns 90° at a bend. A dead end
    // stops at the middle: you can walk round the end of a canal.
    //
    //   straight (north–south)        a bend (east and south)
    //      x=0  x=1  x=2  x=3            x=0  x=1  x=2  x=3
    //   0   .   ~~~  ~~~   .          0   .    .    .    .
    //   1   .   ~~~  ~~~   .          1   .   ~~~  ~~~  ~~~
    //   2   .   ~~~  ~~~   .          2   .   ~~~  ~~~  ~~~
    //   3   .   ~~~  ~~~   .          3   .   ~~~  ~~~   .
    private static List<(Subtile At, SubtileKind Kind, IReadOnlyList<Heading> Closed)> Channel(IReadOnlyCollection<Heading> joins)
    {
        var cells = new HashSet<Subtile> { new(1, 1), new(2, 1), new(1, 2), new(2, 2) };
        foreach (var h in joins)
            for (var lane = 1; lane <= 2; lane++)
                cells.Add(Battlefields.Cell(h, 0, lane));
        return cells.OrderBy(c => c.Index)
            .Select(c => (c, SubtileKind.Water, (IReadOnlyList<Heading>)Array.Empty<Heading>()))
            .ToList();
    }

    // A bridge (the user's drawing, 2026-09-28): the straight canal's channel
    // with a one-wide deck across it at row 1, joining the banks. On an
    // east–west canal it is the same turned a quarter clockwise (deck at column 2).
    //
    //   north–south canal (as drawn)   east–west canal
    //      x=0  x=1  x=2  x=3             x=0  x=1  x=2  x=3
    //   0   .   ~~~  ~~~   .           0   .    .    .    .
    //   1   .   ═══  ═══   .           1  ~~~  ~~~  ═══  ~~~
    //   2   .   ~~~  ~~~   .           2  ~~~  ~~~  ═══  ~~~
    //   3   .   ~~~  ~~~   .           3   .    .    .    .
    private static List<(Subtile At, SubtileKind Kind, IReadOnlyList<Heading> Closed)> Deck(IReadOnlyCollection<Heading> joins)
    {
        var eastWest = joins.Contains(Heading.East) || joins.Contains(Heading.West);
        var turns = eastWest ? 1 : 0;
        var cells = new List<(Subtile, SubtileKind, IReadOnlyList<Heading>)>();
        for (var y = 0; y < Subtile.Size; y++)
            for (var x = 1; x <= 2; x++)
                cells.Add((Rotate(new Subtile(x, y), turns), y == 1 ? SubtileKind.Bridge : SubtileKind.Water,
                    Array.Empty<Heading>()));
        return cells;
    }

    // A quarter turn clockwise, `turns` times, about the board's centre:
    // (x, y) → (3 − y, x), so the north edge becomes the east edge.
    public static Subtile Rotate(Subtile s, int turns)
    {
        for (var i = 0; i < ((turns % 4) + 4) % 4; i++)
            s = new Subtile(Subtile.Size - 1 - s.Y, s.X);
        return s;
    }
}
