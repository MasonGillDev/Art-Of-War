using Sim.Core.Engine;

namespace Sim.Server.Ai;

// M17 — the shell around the brain. The SHELL owns the sim handle (for
// view-building and intent submission); the BRAIN sees only the
// projected ViewDto. That split IS the fairness contract — see
// HomesteaderBrain's header and AiFairnessTests.
//
// Same threading/cadence contract as the BanditDriver: Think() runs on
// the clock-loop thread under the host lock, self-gates to one
// evaluation per ThinkPeriodTicks, never blocks. Driver state (memory,
// trace) is ephemeral — the durable intent log carries the decisions.
public sealed class AiPlayerDriver
{
    public int PlayerId { get; }
    public DecisionTrace Trace { get; } = new();

    private readonly AiConfig _cfg;
    private readonly HomesteaderBrain _brain;
    private readonly AiMemory _mem = new();
    private long _lastThink = long.MinValue;

    public AiPlayerDriver(int playerId, AiConfig cfg)
    {
        PlayerId = playerId;
        _cfg = cfg;
        _brain = new HomesteaderBrain(cfg);
    }

    public void Think(Simulation sim, ViewProjector projector, long now)
    {
        if (!_cfg.Enabled) return;
        if (_lastThink != long.MinValue && now - _lastThink < _cfg.ThinkPeriodTicks) return;
        _lastThink = now;

        // The brain's whole world: the same fog-filtered view a human
        // client renders for this player id.
        var view = projector.Project(sim, now, PlayerId, reveal: false);
        var decision = _brain.Think(view, now, _mem);

        foreach (var intent in decision.Intents)
            sim.SubmitIntent(now, intent);

        var summary = decision.Intents.Count == 0
            ? "-"
            : string.Join("; ", decision.Intents.Select(i => i.Describe()));
        Trace.Record(now, decision.Rung, decision.Why, summary);
        if (_cfg.TracePrint && decision.Intents.Count > 0)
            Console.WriteLine($"[ai {PlayerId}] t{now} [{decision.Rung}] {decision.Why} => {summary}");
    }
}
