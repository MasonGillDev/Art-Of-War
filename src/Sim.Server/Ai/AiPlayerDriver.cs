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
    public BrainKind Kind { get; }
    public RivalPersonality? Personality { get; }
    public DecisionTrace Trace { get; }

    private readonly AiConfig _cfg;
    private readonly IBrain _brain;
    private readonly AiMemory _mem = new();
    private long _lastThink = long.MinValue;

    // M25 — the host picks the brain per faction (--rivals K → the highest
    // K AI faction ids run the Rival ladder). Kind is pinned here so tests
    // and the trace can tell WHO a faction is without probing behavior.
    // A Rival may also carry a PERSONALITY: the DRIVER applies it to the
    // config, so the label and the knobs it names can never disagree
    // (Homesteaders ignore it — the spread is a war temperament).
    public AiPlayerDriver(int playerId, AiConfig cfg, BrainKind kind = BrainKind.Homesteader,
        RivalPersonality? personality = null)
    {
        PlayerId = playerId;
        Kind = kind;
        Personality = kind == BrainKind.Rival ? personality : null;
        _cfg = Personality is { } p ? RivalPersonalities.Apply(cfg, p) : cfg;
        _brain = kind == BrainKind.Rival
            ? new RivalBrain(_cfg)
            : new HomesteaderBrain(_cfg);
        Trace = new DecisionTrace(_cfg.TraceCapacity);
    }

    private bool _defeated;

    public void Think(Simulation sim, ViewProjector projector, long now)
    {
        if (!_cfg.Enabled || _defeated) return;
        if (_lastThink != long.MinValue && now - _lastThink < _cfg.ThinkPeriodTicks) return;
        _lastThink = now;

        // The brain's whole world: the same fog-filtered view a human
        // client renders for this player id.
        var view = projector.Project(sim, now, PlayerId, reveal: false);
        // A defeated faction is out of the game: every intent it submits
        // rejects at the gate, so thinking is pure reject-spam. Latch off
        // permanently (there is no un-defeat).
        if (view.Factions.FirstOrDefault(f => f.Id == PlayerId)?.Defeated == true)
        {
            _defeated = true;
            return;
        }
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
