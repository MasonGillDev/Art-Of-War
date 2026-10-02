using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Server.Groups;

// M49 — the eyes of an Aggressive group (docs/m49-group-stance-spec.md). Spotting a
// target needs the owner's sight, so it lives out here with the other drivers (the
// patrol shape, docs/patrols.md): pure reads in, ordinary durable intents out
// (ChargeGroupIntent), so a replay of the intent log reproduces every charge without it.
//
// Each think, every Aggressive group under command that isn't fighting looks for the
// nearest hostile its owner can SEE within EngageRadius of any member — and, once it is
// already charging, anywhere within LeashRadius of where it set off, so the chase keeps
// up as the target moves. Nearest wins; ties go to the lower id, then (y, x). No
// weighing of threat or value: that is the player's call, not the stance's.
public sealed class GroupStanceDriver
{
    private readonly long _period;
    private long _lastThink = long.MinValue;

    public GroupStanceDriver(long thinkPeriodTicks = 10) { _period = thinkPeriodTicks; }

    public void Think(Simulation sim, long now)
    {
        if (_lastThink != long.MinValue && now - _lastThink < _period) return;
        _lastThink = now;
        var world = sim.World;
        var sight = new Dictionary<int, HashSet<TileCoord>>();

        foreach (var (_, group) in world.Groups)   // ascending id
        {
            if (group.Kind != GroupKind.Units || group.Stance != GroupStance.Aggressive) continue;
            if (group.State is not (GroupState.Idle or GroupState.Moving)) continue;
            if (world.Players.TryGetValue(group.OwnerId, out var p) && p.Defeated) continue;
            if (GroupStances.AnyFighting(world, group)) continue;
            var members = group.Members.Where(world.Units.ContainsKey).Select(id => world.Units[id])
                .Where(m => !m.IsEmbarked).ToList();
            if (members.Count == 0) continue;
            if (!sight.TryGetValue(group.OwnerId, out var visible))
                sight[group.OwnerId] = visible = View.VisibleTiles(world, group.OwnerId);

            var charging = group.ReturnTo is not null;
            var anchor = GroupStances.LeashAnchor(group);
            Unit? best = null;
            var bestDist = int.MaxValue;
            foreach (var (_, foe) in world.Units)   // ascending id
            {
                if (foe.IsEmbarked || !world.Diplomacy.AreHostile(group.OwnerId, foe.OwnerId)) continue;
                if (!visible.Contains(foe.Position)) continue;
                if (Chebyshev(anchor, foe.Position) > GroupConstants.LeashRadius) continue;
                var dist = members.Min(m => Chebyshev(m.Position, foe.Position));
                if (!charging && dist > GroupConstants.EngageRadius) continue;
                if (best is null || dist < bestDist
                    || (dist == bestDist && (foe.Position.Y, foe.Position.X, foe.Id).CompareTo((best.Position.Y, best.Position.X, best.Id)) < 0))
                {
                    best = foe;
                    bestDist = dist;
                }
            }
            if (best is null) continue;
            if (group.State == GroupState.Moving && group.PathFinalDest == best.Position) continue;   // already on its way
            sim.SubmitIntent(now, new ChargeGroupIntent(group.Id, best.Id) { PlayerId = group.OwnerId });
        }
    }

    private static int Chebyshev(TileCoord a, TileCoord b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
