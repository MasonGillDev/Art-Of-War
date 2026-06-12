using Sim.Core.Intents;

namespace Sim.Server.Ai;

// M17 Phase 2 — the rung contract (docs/m17-defender-spec.md, Phase 0).
// A rung is one strategic behavior on the brain's strict priority
// ladder. TryClaim returns a Decision when the rung has something to DO
// this think (claiming it), null to fall through to the next rung.
// Rungs are stateless: everything they see arrives in the ThinkContext
// (view digest + config + memory + the shared reservation ledger), so
// the fairness contract — the brain never touches GameWorld/Simulation —
// holds for every rung by construction (pinned by reflection in
// AiPlayerTests.Brain_TouchesOnlyTheView, which sweeps this whole
// namespace).
public interface IRung
{
    Decision? TryClaim(ThinkContext ctx);
}

// What a think concluded: which rung fired, the human-readable reason
// (the decision-trace line), and the intents to submit.
public sealed record Decision(string Rung, string Why, List<Intent> Intents);
