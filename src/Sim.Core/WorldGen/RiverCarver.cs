namespace Sim.Core.WorldGen;

// Carves rivers into the generated map as a per-tile EDGE mask (docs/rivers.md).
// Generator-only float math, off the replay path (same contract as NoiseField
// and ContinentShaper): runs once, freezes an integer RiverEdge[,] the sim
// cannot tell from a hand-authored one.
//
// Rivers live on the CORNER GRID — the (Width+1) × (Height+1) lattice of tile
// corners, each at the mean elevation of the tiles around it. That is the same
// corner lattice the client's terrain mesh is built on, so a river drawn along
// tile boundaries follows the drawn ground. A river is a walk from corner to
// corner along tile boundaries; each step marks the boundary on both tiles it
// separates.
//
// Why a priority flood rather than plain steepest descent: octave noise is full
// of pits, and a river that walks downhill into one has nowhere to go. The
// flood (Barnes et al. 2014) visits every corner in order of "filled" height
// outward from the sea, so each corner's flood PARENT is one step along a
// monotone non-increasing path to a sink. Following parents from a source is
// therefore guaranteed to reach the sea — no dead ends, no lakes to special-
// case, no loops.
public static class RiverCarver
{
    public static RiverEdge[,] Carve(double[,] elevation, Biome[,] grid, GenerationConfig cfg)
    {
        int w = cfg.Width, h = cfg.Height;
        var rivers = new RiverEdge[w, h];
        if (cfg.RiverCount <= 0) return rivers;

        int cw = w + 1, ch = h + 1;                 // corner lattice
        var height = new double[cw, ch];
        var sink = new bool[cw, ch];
        for (var cy = 0; cy < ch; cy++)
        for (var cx = 0; cx < cw; cx++)
        {
            double sum = 0; var n = 0; var touchesWater = false; var onBorder = false;
            for (var oy = -1; oy <= 0; oy++)
            for (var ox = -1; ox <= 0; ox++)
            {
                int tx = cx + ox, ty = cy + oy;
                if (tx < 0 || ty < 0 || tx >= w || ty >= h) { onBorder = true; continue; }
                sum += elevation[tx, ty]; n++;
                if (grid[tx, ty] == Biome.Water) touchesWater = true;
            }
            height[cx, cy] = n == 0 ? 0.0 : sum / n;
            sink[cx, cy] = touchesWater || onBorder;
        }

        // ---- priority flood from every sink -------------------------------
        // Ties broken by (y, x) so the parent forest is a pure function of
        // the input — same config, same rivers (pinned by RiversTests).
        var filled = new double[cw, ch];
        var parent = new (int x, int y)[cw, ch];
        var visited = new bool[cw, ch];
        var pq = new PriorityQueue<(int x, int y), (double h, int y, int x)>();
        for (var cy = 0; cy < ch; cy++)
        for (var cx = 0; cx < cw; cx++)
        {
            if (!sink[cx, cy]) continue;
            filled[cx, cy] = height[cx, cy];
            parent[cx, cy] = (cx, cy);
            visited[cx, cy] = true;
            pq.Enqueue((cx, cy), (filled[cx, cy], cy, cx));
        }
        while (pq.Count > 0)
        {
            var (x, y) = pq.Dequeue();
            foreach (var (nx, ny) in CornerNeighbours(x, y, cw, ch))
            {
                if (visited[nx, ny]) continue;
                visited[nx, ny] = true;
                filled[nx, ny] = Math.Max(height[nx, ny], filled[x, y]);
                parent[nx, ny] = (x, y);
                pq.Enqueue((nx, ny), (filled[nx, ny], ny, nx));
            }
        }

        // ---- sources: foothills, inland, spaced apart --------------------
        // Inside the source band and clear of Mountain tiles, so a river
        // rises where the hills meet the plain, not on a summit.
        var candidates = new List<(double h, int y, int x)>();
        for (var cy = 1; cy < ch - 1; cy++)
        for (var cx = 1; cx < cw - 1; cx++)
        {
            if (sink[cx, cy]) continue;
            var hc = height[cx, cy];
            if (hc < cfg.RiverSourceMinElevation || hc > cfg.RiverSourceMaxElevation) continue;
            if (TouchesMountain(grid, w, h, cx, cy)) continue;
            candidates.Add((hc, cy, cx));
        }
        // Highest first; (y, x) breaks ties deterministically.
        candidates.Sort((a, b) =>
            a.h != b.h ? b.h.CompareTo(a.h) : a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));

        var sources = new List<(int x, int y)>();
        foreach (var (_, cy, cx) in candidates)
        {
            if (sources.Count >= cfg.RiverCount) break;
            var tooClose = false;
            foreach (var (sx, sy) in sources)
                if (Math.Max(Math.Abs(sx - cx), Math.Abs(sy - cy)) < cfg.RiverSourceSpacing) { tooClose = true; break; }
            if (!tooClose) sources.Add((cx, cy));
        }

        // ---- walk each source downhill to the sea ------------------------
        // Steepest descent on the FILLED heights: take the strictly lowest
        // neighbour, and only when there is none (a filled lake plateau)
        // fall back to the flood parent, which always progresses toward a
        // sink. Descent follows the actual slope, so a river winds down a
        // valley; the parent chain alone traced the flood's visiting order
        // and came out as long axis-aligned streaks. Terminates: a lowest
        // step strictly drops, a parent step moves strictly up the flood
        // forest, and neither can return to a higher corner.
        var walked = new bool[cw, ch];
        foreach (var (sx, sy) in sources)
        {
            int x = sx, y = sy;
            for (var steps = 0; steps < cfg.RiverMaxLength && !sink[x, y]; steps++)
            {
                walked[x, y] = true;
                var (nx, ny) = parent[x, y];
                var best = filled[x, y];
                foreach (var (ax, ay) in CornerNeighbours(x, y, cw, ch))
                {
                    if (walked[ax, ay]) continue;
                    if (filled[ax, ay] < best) { best = filled[ax, ay]; nx = ax; ny = ay; }
                }
                if (walked[nx, ny]) break;  // can't happen by the argument above; fail closed
                if (!TryMark(rivers, grid, w, h, x, y, nx, ny, out var alreadyRiver)) break;
                if (alreadyRiver) break;    // joined an existing river: confluence
                x = nx; y = ny;
            }
            // Reset for the next source so rivers may share corners (confluences).
            Array.Clear(walked);
        }
        return rivers;
    }

    private static bool TouchesMountain(Biome[,] grid, int w, int h, int cx, int cy)
    {
        for (var oy = -1; oy <= 0; oy++)
        for (var ox = -1; ox <= 0; ox++)
        {
            int tx = cx + ox, ty = cy + oy;
            if (tx < 0 || ty < 0 || tx >= w || ty >= h) continue;
            if (grid[tx, ty] == Biome.Mountain) return true;
        }
        return false;
    }

    // Mark the boundary segment between corners (ax,ay)-(bx,by) on the two
    // tiles it separates. Returns false if the segment cannot carry a river
    // (off the map, or flanked by water — the river has reached its mouth).
    // `alreadyRiver` reports a segment that was marked by an earlier walk.
    private static bool TryMark(RiverEdge[,] rivers, Biome[,] grid, int w, int h,
                                int ax, int ay, int bx, int by, out bool alreadyRiver)
    {
        alreadyRiver = false;
        int t1x, t1y, t2x, t2y; RiverEdge e1, e2;
        if (ay == by)
        {   // horizontal segment: separates the tile above from the tile below
            var cx = Math.Min(ax, bx);
            t1x = cx; t1y = ay - 1; e1 = RiverEdge.South;   // north tile's south edge
            t2x = cx; t2y = ay;     e2 = RiverEdge.North;   // south tile's north edge
        }
        else
        {   // vertical segment: separates the tile left from the tile right
            var cy = Math.Min(ay, by);
            t1x = ax - 1; t1y = cy; e1 = RiverEdge.East;    // west tile's east edge
            t2x = ax;     t2y = cy; e2 = RiverEdge.West;    // east tile's west edge
        }
        if (t1x < 0 || t1y < 0 || t1x >= w || t1y >= h) return false;
        if (t2x < 0 || t2y < 0 || t2x >= w || t2y >= h) return false;
        if (grid[t1x, t1y] == Biome.Water || grid[t2x, t2y] == Biome.Water) return false;

        if ((rivers[t1x, t1y] & e1) != 0) { alreadyRiver = true; return true; }
        rivers[t1x, t1y] |= e1;
        rivers[t2x, t2y] |= e2;
        return true;
    }

    // 4-neighbourhood on the corner lattice, N E S W like TileGrid.Neighbors.
    private static IEnumerable<(int x, int y)> CornerNeighbours(int x, int y, int cw, int ch)
    {
        if (y > 0) yield return (x, y - 1);
        if (x < cw - 1) yield return (x + 1, y);
        if (y < ch - 1) yield return (x, y + 1);
        if (x > 0) yield return (x - 1, y);
    }
}
