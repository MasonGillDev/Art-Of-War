using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Core.Wilderness;

// Wilderness bands (docs/wilderness-bands.md): how far every tile lies from the kingdoms,
// measured once at genesis and frozen. The distance is foot travel time from the nearest
// STARTING castle (the real movement cost, rivers included); the bands cut the reachable
// land by share, nearest first, so every map has the same proportions whatever its size or
// seed. Later systems (loot tiers, camps, resource sites, danger) read the band; nothing in
// the sim writes the field after genesis, so a canal, a fallen castle or a new outpost never
// moves it.

// Append-only (serialized through the field's cuts, compared by order). The order IS the
// meaning: a higher band is farther from everyone.
public enum WildBand : byte
{
    None = 0,       // water at genesis: no band
    Settled = 1,
    Frontier = 2,
    Wild = 3,
    Deep = 4,
}

// Where the bands start, as a share (percent) of the reachable land counted nearest first.
// The defaults: the nearest 40% is Settled, then Frontier to 70%, Wild to 90%, and the
// farthest 10% is Deep (measured on the default map, docs/wilderness-bands.md).
public sealed record WildernessConfig(
    int FrontierStartsAtPercent = 40,
    int WildStartsAtPercent = 70,
    int DeepStartsAtPercent = 90);

public sealed class WildernessField
{
    // Marker values in the minutes array. Every real distance is below both.
    public const ushort Water = ushort.MaxValue;           // no band
    public const ushort BeyondReach = ushort.MaxValue - 1; // land no foot can reach (an island): Deep
    public const int MaxMinutes = ushort.MaxValue - 2;     // distances cap here (≈ 45 game-days)

    // A world with no starting castle (or one built without genesis): no bands anywhere.
    public static readonly WildernessField Empty = new(0, 0, Array.Empty<ushort>(), 0, 0, 0);

    public int Width { get; }
    public int Height { get; }
    // The band cut-offs, in walking minutes: a tile at least this far is that band or beyond.
    public int FrontierFrom { get; }
    public int WildFrom { get; }
    public int DeepFrom { get; }
    private readonly ushort[] _minutes;   // (y, x) order

    internal WildernessField(int width, int height, ushort[] minutes, int frontierFrom, int wildFrom, int deepFrom)
    {
        Width = width;
        Height = height;
        _minutes = minutes;
        FrontierFrom = frontierFrom;
        WildFrom = wildFrom;
        DeepFrom = deepFrom;
    }

    public bool IsEmpty => _minutes.Length == 0;

    // The tile's band. None for water, off the map, or a world with no field.
    public WildBand BandAt(TileCoord t)
    {
        if (!Holds(t)) return WildBand.None;
        var m = _minutes[t.Y * Width + t.X];
        if (m == Water) return WildBand.None;
        if (m == BeyondReach || m >= DeepFrom) return WildBand.Deep;
        if (m >= WildFrom) return WildBand.Wild;
        return m >= FrontierFrom ? WildBand.Frontier : WildBand.Settled;
    }

    // Walking minutes from the nearest starting castle, or null for water, land no foot can
    // reach, off the map, or a world with no field.
    public int? MinutesAt(TileCoord t)
    {
        if (!Holds(t)) return null;
        var m = _minutes[t.Y * Width + t.X];
        return m >= BeyondReach ? null : m;
    }

    // The raw cell, for Snapshot.
    internal ushort RawAt(int index) => _minutes[index];

    private bool Holds(TileCoord t) => !IsEmpty && t.X >= 0 && t.Y >= 0 && t.X < Width && t.Y < Height;

    internal static WildernessField FromRaw(int width, int height, ushort[] minutes, int frontierFrom, int wildFrom, int deepFrom) =>
        new(width, height, minutes, frontierFrom, wildFrom, deepFrom);

    // Measure the world. Pure read of the world; integer Dijkstra over 4-neighbours from
    // every castle at once (shortest distances are unique, so the queue's tie order can't
    // change the result). Run at genesis, after the castles stand.
    public static WildernessField Compute(GameWorld world, IReadOnlyList<TileCoord> castles, WildernessConfig config)
    {
        if (config.FrontierStartsAtPercent is < 0 or > 100
            || config.WildStartsAtPercent < config.FrontierStartsAtPercent
            || config.DeepStartsAtPercent < config.WildStartsAtPercent
            || config.DeepStartsAtPercent > 100)
            throw new ArgumentException("WildernessConfig: the bands must start at rising shares between 0 and 100.", nameof(config));
        if (castles.Count == 0) return Empty;

        var grid = world.Grid;
        int w = grid.Width, h = grid.Height;
        var dist = new int[w * h];
        Array.Fill(dist, int.MaxValue);
        var queue = new PriorityQueue<TileCoord, int>();
        foreach (var c in castles)
        {
            if (!grid.InBounds(c)) continue;
            dist[c.Y * w + c.X] = 0;
            queue.Enqueue(c, 0);
        }
        while (queue.TryDequeue(out var t, out var d))
        {
            if (d > dist[t.Y * w + t.X]) continue;
            foreach (var n in grid.Neighbors(t))
            {
                var cost = MovementCost.TerrainCostFor(world, t, n, 0, Traversal.Foot);
                if (cost >= Sim.Core.World.Biomes.Impassable) continue;
                var nd = d + cost;
                if (nd >= dist[n.Y * w + n.X]) continue;
                dist[n.Y * w + n.X] = nd;
                queue.Enqueue(n, nd);
            }
        }

        var minutes = new ushort[w * h];
        var reached = new List<int>();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (grid.BiomeAt(new TileCoord(x, y)) == Biome.Water) { minutes[i] = Water; continue; }
                if (dist[i] == int.MaxValue) { minutes[i] = BeyondReach; continue; }
                minutes[i] = (ushort)Math.Min(dist[i], MaxMinutes);
                reached.Add(minutes[i]);
            }

        reached.Sort();
        // The distance at that share of the reachable land; 100% means no tile gets there.
        int Cut(int percent) =>
            percent >= 100 || reached.Count == 0 ? MaxMinutes + 1
            : reached[(int)((long)reached.Count * percent / 100)];
        return new WildernessField(w, h, minutes,
            Cut(config.FrontierStartsAtPercent), Cut(config.WildStartsAtPercent), Cut(config.DeepStartsAtPercent));
    }
}
