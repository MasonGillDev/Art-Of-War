using Sim.Core.World;

namespace Sim.Core.WorldGen;

// Where every kingdom's castle goes when a world holds more than one (docs/fair-start-placement.md).
//
// THE LAND IS SHARED OUT EVENLY. Seats start on a ring around the mainland's centre (turned
// by the map seed, so seat 0 is not always in the same corner), then Lloyd relaxation moves
// each seat to the centre of the land nearest it until the shares settle: every kingdom ends
// up in the middle of a share of the continent of about the same size, with the far land at
// the edges between them and along the coast. The pre-2026-10-05 placement anchored on the
// human's centre start and scanned rings top-down, so every AI landed on the north side and
// all the far land piled up in the south.
//
// A seat then snaps to the nearest tile a castle can start on (Viable), in the order the seats
// were laid out, keeping MinSeparation from the seats already placed. A seat with no viable
// tile anywhere is left out, and the caller says so.
//
// Generator-only float math, off the replay path (same contract as NoiseField and
// ContinentShaper): it runs once and the result is frozen into the GenesisSpec.
public static class SeatPlacer
{
    // Castles at least this far apart (Chebyshev tiles).
    public const int MinSeparation = 24;

    // A start needs a real meadow (M17, 2026-06-11: a start whose nearest grassland sits 10
    // tiles out dies to haul distance) and wood in reach: a castle on a desert's edge with no
    // forest for 14 tiles starved in the fertility lab (2026-10-05).
    public const int MeadowRadius = 6;
    public const int MeadowMin = 40;
    public const int ForestRadius = 10;

    // Where the ring starts, as a share of the way from the centre to the coast.
    private const double RingShare = 0.5;
    private const int Relaxations = 40;

    // Castle tiles for `seats` kingdoms; seat 0 first. One seat keeps the generator's own
    // centre start (map.Start), so single-kingdom worlds are unchanged. May return fewer than
    // asked when the land runs out, never none.
    public static List<TileCoord> Place(GeneratedMap map, int seats)
    {
        if (seats <= 1) return new List<TileCoord> { map.Start };
        var main = Mainland(map);
        var tiles = new List<(int X, int Y)>();
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
                if (main[x, y]) tiles.Add((x, y));

        var targets = Ring(map, main, tiles, seats);
        Relax(tiles, targets);

        // Each seat snaps inside its own share first, so a share with no good start doesn't
        // pull its castle toward a neighbour's; only a share with nothing viable looks anywhere.
        var placed = new List<TileCoord>();
        for (var k = 0; k < targets.Count; k++)
        {
            int ox = (int)Math.Round(targets[k].X), oy = (int)Math.Round(targets[k].Y);
            var seat = k;
            if ((NearestViable(map, main, ox, oy, placed, (x, y) => NearestSeat(targets, x, y) == seat)
                    ?? NearestViable(map, main, ox, oy, placed, null)) is { } at)
                placed.Add(at);
        }
        if (placed.Count == seats) return placed;
        // Nowhere viable at all: the lone generator start (StartPicker vouches for it).
        if (placed.Count == 0) return new List<TileCoord> { map.Start };

        // A continent too small for every seat's share: pack instead. From the first seat,
        // each next castle goes on the viable tile farthest from every castle so far, which
        // fits the most onto little land. Keep whichever fits more.
        var packed = new List<TileCoord> { placed[0] };
        while (packed.Count < seats && Farthest(map, main, packed) is { } at) packed.Add(at);
        return packed.Count > placed.Count ? packed : placed;
    }

    // Can a castle start here? Grassland on the mainland, a meadow around it, wood in reach,
    // and no other castle within MinSeparation.
    public static bool Viable(GeneratedMap map, bool[,] mainland, int x, int y, IReadOnlyList<TileCoord> placed)
    {
        if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) return false;
        if (!mainland[x, y] || map.Grid[x, y] != Biome.Grassland) return false;
        foreach (var c in placed)
            if (Math.Max(Math.Abs(c.X - x), Math.Abs(c.Y - y)) < MinSeparation) return false;
        return Count(map, x, y, MeadowRadius, Biome.Grassland) >= MeadowMin
            && Count(map, x, y, ForestRadius, Biome.Forest) > 0;
    }

    // The land a castle may stand on: the 4-connected land holding the generator's start
    // (which StartPicker put in the heart of the continent). Islands are left to boats.
    public static bool[,] Mainland(GeneratedMap map)
    {
        var main = new bool[map.Width, map.Height];
        var queue = new Queue<TileCoord>();
        main[map.Start.X, map.Start.Y] = true;
        queue.Enqueue(map.Start);
        while (queue.TryDequeue(out var t))
            foreach (var (dx, dy) in Around)
            {
                int x = t.X + dx, y = t.Y + dy;
                if (x < 0 || y < 0 || x >= map.Width || y >= map.Height || main[x, y]) continue;
                if (map.Grid[x, y] is Biome.Water or Biome.None) continue;
                main[x, y] = true;
                queue.Enqueue(new TileCoord(x, y));
            }
        return main;
    }

    // Seats evenly round a ring, each RingShare of the way from the mainland's centre to its
    // farthest land in that direction. The ring is turned by the map seed.
    private static List<(double X, double Y)> Ring(GeneratedMap map, bool[,] main, List<(int X, int Y)> tiles, int seats)
    {
        double cx = tiles.Average(t => t.X), cy = tiles.Average(t => t.Y);
        var turn = (int)(((uint)map.Seed * 2654435761u) % 360u) * Math.PI / 180;
        var targets = new List<(double X, double Y)>(seats);
        for (var i = 0; i < seats; i++)
        {
            var angle = turn + 2 * Math.PI * i / seats;
            double dx = Math.Cos(angle), dy = Math.Sin(angle);
            var reach = 0.0;
            for (var s = 0.0; ; s += 0.5)
            {
                int x = (int)Math.Round(cx + dx * s), y = (int)Math.Round(cy + dy * s);
                if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) break;
                if (main[x, y]) reach = s;
            }
            targets.Add((cx + dx * reach * RingShare, cy + dy * reach * RingShare));
        }
        return targets;
    }

    // Lloyd relaxation over the mainland: each tile goes to the nearest seat, each seat moves
    // to the centre of its tiles. Ties go to the lower seat.
    private static void Relax(List<(int X, int Y)> tiles, List<(double X, double Y)> seats)
    {
        var sumX = new double[seats.Count];
        var sumY = new double[seats.Count];
        var count = new int[seats.Count];
        for (var round = 0; round < Relaxations; round++)
        {
            Array.Clear(sumX); Array.Clear(sumY); Array.Clear(count);
            foreach (var (x, y) in tiles)
            {
                var best = NearestSeat(seats, x, y);
                sumX[best] += x; sumY[best] += y; count[best]++;
            }
            for (var k = 0; k < seats.Count; k++)
                if (count[k] > 0) seats[k] = (sumX[k] / count[k], sumY[k] / count[k]);
        }
    }

    // The seat whose share holds (x, y): the nearest, ties to the lower seat.
    private static int NearestSeat(List<(double X, double Y)> seats, int x, int y)
    {
        var best = 0;
        var bestD = double.MaxValue;
        for (var k = 0; k < seats.Count; k++)
        {
            double ex = seats[k].X - x, ey = seats[k].Y - y;
            var d = ex * ex + ey * ey;
            if (d < bestD) { bestD = d; best = k; }
        }
        return best;
    }

    // The nearest viable tile to (ox, oy) that `within` allows (any, if null), in the
    // codebase's (ring, y, x) scan order.
    private static TileCoord? NearestViable(GeneratedMap map, bool[,] main, int ox, int oy, List<TileCoord> placed,
        Func<int, int, bool>? within)
    {
        var maxR = Math.Max(map.Width, map.Height);
        for (var r = 0; r <= maxR; r++)
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    int x = ox + dx, y = oy + dy;
                    if (Viable(map, main, x, y, placed) && (within is null || within(x, y))) return new TileCoord(x, y);
                }
        return null;
    }

    // The viable tile farthest (Chebyshev) from every castle placed; ties to the first in
    // (y, x) order.
    private static TileCoord? Farthest(GeneratedMap map, bool[,] main, List<TileCoord> placed)
    {
        TileCoord? best = null;
        var bestD = -1;
        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
            {
                if (!Viable(map, main, x, y, placed)) continue;
                var d = int.MaxValue;
                foreach (var c in placed) d = Math.Min(d, Math.Max(Math.Abs(c.X - x), Math.Abs(c.Y - y)));
                if (d > bestD) { bestD = d; best = new TileCoord(x, y); }
            }
        return best;
    }

    private static int Count(GeneratedMap map, int cx, int cy, int radius, Biome biome)
    {
        var n = 0;
        for (var y = Math.Max(0, cy - radius); y <= Math.Min(map.Height - 1, cy + radius); y++)
            for (var x = Math.Max(0, cx - radius); x <= Math.Min(map.Width - 1, cx + radius); x++)
                if (map.Grid[x, y] == biome) n++;
        return n;
    }

    private static readonly (int Dx, int Dy)[] Around = { (0, -1), (1, 0), (0, 1), (-1, 0) };
}
