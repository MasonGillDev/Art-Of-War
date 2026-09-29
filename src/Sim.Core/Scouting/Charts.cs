namespace Sim.Core.Scouting;

// Append-only enum (serialized, and on the wire). The faint idea a scout brings
// home: WHAT KIND of thing, never what it holds. The wire carries the hint, never
// the secret's structure kind, so a modded client learns nothing a player would
// not.
public enum SecretHint : byte
{
    // Something man-made catching the light: a loot cache, a rumoured ruin.
    Glint       = 1,
    // A figure of stone: an idol.
    StoneFigure = 2,
    // M39 — smoke from a camp: bandits.
    Smoke       = 3,
}

// Append-only enum (serialized, and on the wire).
public enum ChartState : byte
{
    // Last seen there (SeenTick).
    Known = 1,
    // The owner has since seen the tile and the secret is gone (GoneTick): the
    // marker is struck through, never silently removed.
    Gone  = 2,
}

// One marker on a player's chart (docs/scouting-secrets.md).
public sealed class ChartEntry
{
    public TileCoord Tile { get; init; }
    public SecretHint Hint { get; internal set; }
    public long SeenTick { get; internal set; }
    public ChartState State { get; internal set; } = ChartState.Known;
    public long GoneTick { get; internal set; }

    // When this entry's knowledge dates from: a newer observation wins a merge.
    internal long ObservedTick => State == ChartState.Known ? SeenTick : GoneTick;
}

// M38 — the chart (docs/scouting-secrets.md): what a player KNOWS is out there
// because a scout came home and said so.
//
// SINGLE MUTATION POINT for GameWorld.Charts (Snapshot restore aside):
//   * Deliver       ScoutMissionRunner, at the mission's Returned transition:
//                   the only way an entry is born. A scout killed on the way
//                   home never reaches Returned, so its findings die with it.
//   * OnSight       Sight.AfterReveal: the owner's own eyes on a charted tile
//                   refresh it, or strike it if the secret is gone.
//   * OnSecretGone  CacheLooting.RemoveIfEmptied (and the idol crumbling):
//                   an owner who is watching the tile sees it go.
// Seeing a secret without a mission never charts it: seen is not charted.
public static class Charts
{
    // Which structures are secrets, and the hint each gives. Null = not a secret.
    public static SecretHint? HintFor(StructureKind kind) => kind switch
    {
        StructureKind.Cache => SecretHint.Glint,
        StructureKind.Idol  => SecretHint.StoneFigure,
        StructureKind.BanditCamp => SecretHint.Smoke,
        _ => null,
    };

    public static SortedDictionary<TileCoord, ChartEntry>? Of(GameWorld world, int playerId) =>
        world.Charts.TryGetValue(playerId, out var c) ? c : null;

    // The mission's log, walked in order, becomes chart knowledge: each secret
    // sighted is Known as of the last leg that saw it, and Gone if a later leg
    // swept its tile and it was not there.
    internal static void Deliver(Simulation sim, ScoutMission mission)
    {
        var found = new SortedDictionary<TileCoord, ChartEntry>(TileOrder.Instance);
        foreach (var leg in mission.Legs)
        {
            var here = new Dictionary<TileCoord, SecretHint>();
            foreach (var s in leg.Sightings)
                if (s.Structure is { } st && HintFor(st.Kind) is { } hint) here[s.Tile] = hint;

            var r2 = leg.Radius * leg.Radius;
            foreach (var (tile, entry) in found)
            {
                var dx = tile.X - leg.Center.X;
                var dy = tile.Y - leg.Center.Y;
                if (dx * dx + dy * dy > r2) continue;   // not swept by this leg
                if (here.TryGetValue(tile, out var h))
                {
                    entry.Hint = h;
                    entry.State = ChartState.Known;
                    entry.SeenTick = leg.Tick;
                }
                else if (entry.State == ChartState.Known)
                {
                    entry.State = ChartState.Gone;
                    entry.GoneTick = leg.Tick;
                }
            }
            foreach (var (tile, hint) in here)
                if (!found.ContainsKey(tile))
                    found[tile] = new ChartEntry { Tile = tile, Hint = hint, SeenTick = leg.Tick };
        }
        if (found.Count == 0) return;

        var chart = ChartFor(sim.World, mission.OwnerId);
        foreach (var (tile, entry) in found)
            if (!chart.TryGetValue(tile, out var old) || entry.ObservedTick >= old.ObservedTick)
                chart[tile] = entry;
    }

    // The owner's own sight swept a disc: charted secrets in it are refreshed
    // if still there, struck if gone. Never adds an entry.
    internal static void OnSight(Simulation sim, int playerId, TileCoord center, int radius)
    {
        if (radius <= 0 || Of(sim.World, playerId) is not { Count: > 0 } chart) return;
        var r2 = radius * radius;
        foreach (var (tile, entry) in chart)
        {
            var dx = tile.X - center.X;
            var dy = tile.Y - center.Y;
            if (dx * dx + dy * dy > r2) continue;
            Look(sim, entry);
        }
    }

    // A secret just left the world (emptied, crumbled). Any owner whose live
    // sight covers the tile sees it go.
    internal static void OnSecretGone(Simulation sim, TileCoord tile)
    {
        foreach (var (playerId, chart) in sim.World.Charts)
            if (chart.TryGetValue(tile, out var entry) && entry.State == ChartState.Known
                && View.Sees(sim.World, playerId, tile))
                Look(sim, entry);
    }

    private static void Look(Simulation sim, ChartEntry entry)
    {
        if (sim.World.Structures.TryGetValue(entry.Tile, out var s) && HintFor(s.Kind) is { } hint)
        {
            entry.Hint = hint;
            entry.State = ChartState.Known;
            entry.SeenTick = sim.Now;
        }
        else if (entry.State == ChartState.Known)
        {
            entry.State = ChartState.Gone;
            entry.GoneTick = sim.Now;
        }
    }

    private static SortedDictionary<TileCoord, ChartEntry> ChartFor(GameWorld world, int playerId)
    {
        if (!world.Charts.TryGetValue(playerId, out var chart))
            world.Charts[playerId] = chart = new SortedDictionary<TileCoord, ChartEntry>(TileOrder.Instance);
        return chart;
    }
}
