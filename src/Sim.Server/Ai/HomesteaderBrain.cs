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
//     claims the think: Defend → Eat → Build → Train → Muster → Grow
//     → Scout. Rungs with nothing to DO fall through, so an
//     in-progress goal never starves the rungs below. The thresholds
//     that decide when a rung fires live in AiConfig — they ARE the
//     arbitration.
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
//
// M25: the two-layer think loop itself moved to BrainCore (move-only —
// the Rival runs the identical arbiter over a different ladder). This
// file is now purely the Homesteader's COMPOSITION: its rung order.
public sealed class HomesteaderBrain : IBrain
{
    private readonly AiConfig _cfg;
    private readonly IRung[] _ladder;

    public HomesteaderBrain(AiConfig cfg)
    {
        _cfg = cfg;
        // The ladder as DATA — order is the whole arbitration policy.
        // Defend on top (dead farmers don't farm; it also runs the
        // threat perception every think), Muster between Train and
        // Grow: feed the army before raising it, garrison before
        // breeding (docs/m17-defender-spec.md).
        _ladder = new IRung[]
        {
            // M25 — answer white flags first: ending a war the
            // Homesteader never wanted beats maneuvering in it (fires
            // only when a Neutral offer is pending — otherwise free).
            new AcceptPeaceRung(),
            new DefendRung(),
            new EatRung(),
            new BuildRung(),
            new TrainRung(),
            new MusterRung(),
            new GrowRung(),
            // M26 — fortification is what quiet thinks buy: below Grow
            // (mouths before masonry) and above Scout (whose budget
            // already bounds it from starving).
            new FortifyRung(),
            // M27 — irrigation after safety: canals shorten the farm rest
            // cycle (docs/canals.md update); the longest-horizon spend
            // takes the quietest thinks.
            new IrrigateRung(),
            new ScoutRung(),
        };
    }

    public Decision Think(ViewDto view, long now, AiMemory mem) =>
        BrainCore.Think(_ladder, _cfg, view, now, mem);
}
