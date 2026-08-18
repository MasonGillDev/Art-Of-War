using System.Linq;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server;

// Grave markers — host-level presentation state (the same tier as GameHost's
// notices; never sim state, never hashed). The sim's single death pipeline
// (CombatRules.OnUnitDeath) removes the unit but records nothing, and Sim.Core
// is closed to modification, so deaths are DETECTED by diffing the unit map
// after each Run and ATTRIBUTED by exclusion from the same resolved-log
// window: an applied DeathByAgeEvent names its victim (natural causes — no
// grave) and an applied DespawnBanditPartyIntent names its party (melted into
// the fog — no grave); boats leave wrecks, not graves. Everything else that
// vanished died to combat or starvation.
//
// A grave is the marker OF THE DEATH'S LOOT PILE: it is minted only when the
// death left ground resources on its tile (the pipeline drops cargo +
// equipment there — an empty-handed victim leaves no marker) and it is
// retired the moment that tile's pile empties (hauled home, stolen by
// bandits). MaxGraves is only a backstop for unlooted wilderness graves.
//
// Extracted from GameHost so the lifecycle is testable without the clock
// thread. NOT thread-safe by design: GameHost calls every member under its
// _gate, exactly like the notice/report state it sits beside.
public sealed class GraveTracker
{
    // Backstop cap; loot-linked retirement is the normal exit.
    public const int MaxGraves = 400;

    private long _nextGraveId = 1;
    private readonly List<GraveDto> _graves = new();
    private readonly Dictionary<int, HashSet<long>> _seen = new();
    private readonly Dictionary<int, (int X, int Y, bool Boat)> _unitSnapshot = new();
    private readonly List<(int X, int Y)> _fallen = new();

    // Live markers, oldest first — the observability/test seam.
    public IReadOnlyList<GraveDto> Graves => _graves;

    // Baseline for the unit-map diff. Call once at construction (the genesis
    // roster); Harvest re-baselines itself after every diff. Clear + re-add
    // reuses the dictionary's storage, so a 20 ms cadence costs no
    // steady-state garbage.
    public void SnapshotUnits(GameWorld world)
    {
        _unitSnapshot.Clear();
        foreach (var (id, u) in world.Units)
            _unitSnapshot[id] = (u.Position.X, u.Position.Y, u.Role == UnitRole.Boat);
    }

    // The diff + attribution described above. Call right after Run.
    // `resolvedLogFrom` is the caller's harvest cursor: the exclusion scan
    // reads [resolvedLogFrom, log.Count) WITHOUT consuming it — GameHost's
    // rejection harvester owns the advance. The window and the diff cover the
    // exact same Run, so an exclusion event and its vanished unit always land
    // in the same harvest.
    public void Harvest(Simulation sim, int resolvedLogFrom)
    {
        var world = sim.World;
        _fallen.Clear();
        HashSet<int>? excluded = null;
        foreach (var (id, snap) in _unitSnapshot)
        {
            if (world.Units.ContainsKey(id)) continue;
            if (snap.Boat) continue;   // vehicles leave wrecks, not graves
            if (excluded is null)
            {
                excluded = new HashSet<int>();
                var log = sim.ResolvedLog;
                for (var i = resolvedLogFrom; i < log.Count; i++)
                {
                    switch (log[i])
                    {
                        // Applied consequence events leave Outcome null (the
                        // same convention GameHost.HarvestRejections leans
                        // on); a REJECTED age death did NOT kill its unit.
                        case Sim.Core.Population.DeathByAgeEvent age
                            when age.Outcome is null || !age.Outcome.IsRejected:
                            excluded.Add(age.UnitId);
                            break;
                        case IntentEvent { Intent: Sim.Core.Bandits.DespawnBanditPartyIntent d } ie
                            when !ie.Outcome.IsRejected:
                            foreach (var uid in d.UnitIds) excluded.Add(uid);
                            break;
                    }
                }
            }
            if (excluded.Contains(id)) continue;
            // Empty-handed victims leave no marker — the grave IS the loot
            // pile's headstone. (The pipeline deposited the pile during the
            // same Run this diff covers, so the check sees it.)
            if (!HasLoot(world, snap.X, snap.Y)) continue;
            _fallen.Add((snap.X, snap.Y));
        }

        foreach (var (x, y) in _fallen)
            _graves.Add(new GraveDto { Id = _nextGraveId++, X = x, Y = y, Tick = sim.Now });

        // Retire graves whose loot is gone (hauled home, stolen by bandits).
        // Every sweep, not just death windows — piles drain at any time. A
        // retired grave leaves every view, fogged viewers included; that
        // "remembered grave winked out" tell is decor-tier and accepted.
        for (var i = _graves.Count - 1; i >= 0; i--)
        {
            if (HasLoot(world, _graves[i].X, _graves[i].Y)) continue;
            foreach (var seen in _seen.Values) seen.Remove(_graves[i].Id);
            _graves.RemoveAt(i);
        }

        // Rolling cap: the oldest markers crumble away. Forget them from the
        // per-player seen sets too, so those don't grow past the live list.
        if (_graves.Count > MaxGraves)
        {
            var drop = _graves.Count - MaxGraves;
            for (var i = 0; i < drop; i++)
                foreach (var seen in _seen.Values) seen.Remove(_graves[i].Id);
            _graves.RemoveRange(0, drop);
        }

        SnapshotUnits(world);
    }

    // Fog-gate the graves notice-style: a grave enters this player's view the
    // first poll its tile is CURRENTLY visible and stays for as long as it
    // lives — the marker never moves, so that memory stays accurate (and
    // scouting an old battlefield reveals graves from fights you never
    // witnessed). Reveal mode sees everything and marks nothing seen.
    public GraveDto[] Project(int playerId, bool reveal, TileDto[] visible)
    {
        if (_graves.Count == 0) return [];
        if (reveal) return _graves.ToArray();
        if (!_seen.TryGetValue(playerId, out var seen))
            _seen[playerId] = seen = new HashSet<long>();
        HashSet<long>? vis = null;   // lazy — most polls carry no unseen graves
        List<GraveDto>? outList = null;
        foreach (var g in _graves)
        {
            if (!seen.Contains(g.Id))
            {
                if (vis is null)
                {
                    vis = new HashSet<long>();
                    foreach (var t in visible) vis.Add(TileKey(t.X, t.Y));
                }
                if (!vis.Contains(TileKey(g.X, g.Y))) continue;
                seen.Add(g.Id);
            }
            (outList ??= new List<GraveDto>()).Add(g);
        }
        return outList?.ToArray() ?? [];
    }

    // Loose resources on the tile? Consumers remove drained entries and the
    // tile key itself when a pile empties (HaulPickupEvent / LoadCargoIntent),
    // but sum defensively rather than trust Count alone.
    private static bool HasLoot(GameWorld world, int x, int y) =>
        world.GroundResources.TryGetValue(new TileCoord(x, y), out var pile)
        && pile.Any(kv => kv.Value > 0);

    private static long TileKey(int x, int y) => ((long)x << 32) ^ (uint)y;
}
