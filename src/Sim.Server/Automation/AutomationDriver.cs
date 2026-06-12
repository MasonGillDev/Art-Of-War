using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Server.Automation;

// M18 — the player-automation brain: third instance of the M16 driver
// shape (BanditDriver, AiPlayerDriver). Pure reads in, ordinary durable
// intents out; the brain itself is EPHEMERAL — order definitions and
// cursors are sim state (snapshot + intent log), so a restarted server
// reloads them and resumes cold with no driver state to recover. Headline:
// AutomationDriverTests.Headline_ReplayFromIntentLog_HashesMatch — the
// intent log replayed WITHOUT this driver reproduces the same world.
//
// THREADING: Think() runs on the sim-owning thread inside the GameHost
// clock-loop lock, right after Run. Never blocks, never sleeps, self-gates
// to one pass per ThinkPeriodTicks.
//
// CANONICAL ARBITRATION: orders are evaluated in ascending order id
// (SortedDictionary iteration) — two runs of this driver over the same
// world state submit the same intents in the same sequence. No RNG.
//
// FOG: each owner's visibility set is computed ONCE per think
// (View.VisibleTiles, a pure read) and shared by all their orders'
// condition evaluations — automation reads only what its owner can see.
public sealed class AutomationDriver
{
    private readonly AutomationConfig _cfg;
    private readonly Dictionary<int, OrderRunner> _runners = new(); // orderId → runner
    private long _lastThink = long.MinValue;
    private int _resolvedCursor; // how far into sim.ResolvedLog outcomes have been harvested

    public AutomationDriver(AutomationConfig cfg) { _cfg = cfg; }

    public void Think(Simulation sim, long now)
    {
        if (!_cfg.Enabled) return;
        if (_lastThink != long.MinValue && now - _lastThink < _cfg.ThinkPeriodTicks) return;
        _lastThink = now;

        var world = sim.World;
        HarvestOutcomes(sim);

        // Census: drop runners whose orders were cleared (their pending
        // references would otherwise pin dead intents forever).
        if (_runners.Count > 0)
        {
            List<int>? stale = null;
            foreach (var id in _runners.Keys)
                if (!world.StandingOrders.ContainsKey(id))
                    (stale ??= new List<int>()).Add(id);
            if (stale is not null)
                foreach (var id in stale) _runners.Remove(id);
        }

        if (world.StandingOrders.Count == 0) return;

        // Per-owner visibility, computed lazily once per think.
        var visibility = new Dictionary<int, HashSet<TileCoord>>();
        foreach (var (id, order) in world.StandingOrders) // ascending id — canonical
        {
            if (!order.Enabled) continue;
            if (!visibility.TryGetValue(order.OwnerId, out var visible))
            {
                visible = View.VisibleTiles(world, order.OwnerId);
                visibility[order.OwnerId] = visible;
            }
            if (!_runners.TryGetValue(id, out var runner))
            {
                runner = new OrderRunner();
                _runners[id] = runner;
            }
            runner.Think(sim, order, visible, now, _cfg);
        }
    }

    // Match newly-resolved intent events against each runner's in-flight
    // action BY REFERENCE — the runner submitted the very instance, so
    // identity is exact (no fragile structural matching).
    private void HarvestOutcomes(Simulation sim)
    {
        var log = sim.ResolvedLog;
        for (; _resolvedCursor < log.Count; _resolvedCursor++)
        {
            if (log[_resolvedCursor] is not IntentEvent ie) continue;
            foreach (var runner in _runners.Values)
            {
                if (ReferenceEquals(runner.PendingAction, ie.Intent))
                {
                    runner.PendingOutcome = ie.Outcome;
                    break;
                }
            }
        }
    }
}
