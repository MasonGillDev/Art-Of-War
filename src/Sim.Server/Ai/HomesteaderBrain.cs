using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Server.Ai.Rungs;
using Sim.Server.Wire;

namespace Sim.Server.Ai;

// M17 — the Homesteader: a peaceful economy brain (docs/m17-ai-players-spec.md).
//
// FAIRNESS IS THE SIGNATURE: Think takes the projected ViewDto — the same
// fog-filtered payload a human client renders — plus the clock and its
// own memory. It can never reference GameWorld or Simulation (pinned by
// AiPlayerTests.Brain_TouchesOnlyTheView, which sweeps the whole Ai
// namespace). It MAY read Sim.Core catalogs and enums: those are game
// RULES (a human knows build costs from the UI), not world state.
//
// THIS FILE IS THE ARBITER, nothing else (docs/m17-defender-spec.md,
// Phase 0): the behaviors live one-per-file in Rungs/, the shared
// vocabulary (view digest, reservation ledger, labor ledger, common
// plays) lives in ThinkContext. Two layers per think:
//
//   * THE STRATEGIC LADDER — strict priority, first rung that emits
//     claims the think: Eat → Build → Train → Grow → Scout. Rungs with
//     nothing to DO fall through, so an in-progress goal never starves
//     the rungs below. The thresholds that decide when a rung fires
//     live in AiConfig — they ARE the arbitration.
//   * LOGISTICS — hauls are BACKGROUND, not decisions (the first
//     arbitration lesson: the trace showed the AI hauling food while
//     its camp site sat at zero builders). Runs AFTER the ladder so
//     strategic decisions reserve their units first (lesson #5:
//     priority isn't just rung order, it's who reserves people first).
//
// OBSERVATION-DRIVEN: progress is read from the next view (the site
// exists, the buffer fell, the unit arrived), never from remembered
// promises — a restarted server re-derives every goal. AiMemory holds
// droppable hints only (scout rotation, rejected-site blacklist).
public sealed class HomesteaderBrain
{
    private readonly AiConfig _cfg;
    private readonly IRung[] _ladder;

    public HomesteaderBrain(AiConfig cfg)
    {
        _cfg = cfg;
        // The ladder as DATA — order is the whole arbitration policy.
        _ladder = new IRung[]
        {
            new EatRung(),
            new BuildRung(),
            new TrainRung(),
            new GrowRung(),
            new ScoutRung(),
        };
    }

    public Decision Think(ViewDto view, long now, AiMemory mem)
    {
        // Site-placement feedback by OBSERVATION (the brain can't see
        // rejection notices): we ordered a site last think and the view
        // shows nothing at that tile → the placement was rejected
        // (insufficient claimable land, contested tile, …). Blacklist the
        // tile so NearestFreeTile offers the next candidate. MUST run
        // BEFORE the digest is built — the digest snapshots the blacklist,
        // and updating it afterwards made every rejected tile get retried
        // exactly once (off-by-one-think, seen live as doubled PlaceSite
        // attempts).
        if (mem.PendingSite is { } pending && now > pending.OrderedAt)
        {
            var occupied = view.Structures.Any(s => s.X == pending.Tile.X && s.Y == pending.Tile.Y);
            if (!occupied) mem.BlacklistedTiles.Add((pending.Tile.X, pending.Tile.Y));
            mem.PendingSite = null;
        }

        var ctx = ThinkContext.Build(view, _cfg, mem, now);
        if (ctx.Castle is null) return new Decision("dead", "no castle", new List<Intent>());

        // STRATEGIC FIRST: decisions (place/staff/breed/scout) reserve
        // their units before logistics swarms the rest. The other order
        // let the haul swarm take every idle unit every think — the camp
        // sat unstaffed for 29 days while food piled up.
        Decision? strategic = null;
        foreach (var rung in _ladder)
            if ((strategic = rung.TryClaim(ctx)) is not null) break;

        var intents = new List<Intent>();
        if (strategic is not null) intents.AddRange(strategic.Intents);
        var hauls = LogisticsLayer.Emit(ctx);
        intents.AddRange(hauls);

        var rung_ = strategic?.Rung ?? (hauls.Count > 0 ? "logistics" : "idle");
        var why = strategic?.Why ?? (hauls.Count > 0 ? $"{hauls.Count} haul(s)" : "all needs met");
        if (strategic is not null && hauls.Count > 0) why += $" (+{hauls.Count} haul)";

        foreach (var intent in intents)
            if (intent is PlaceSiteIntent p)
                mem.PendingSite = (p.Tile, now);
        return new Decision(rung_, why, intents);
    }
}
