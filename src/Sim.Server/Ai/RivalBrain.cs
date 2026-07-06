using Sim.Server.Ai.Rungs;
using Sim.Server.Wire;

namespace Sim.Server.Ai;

// M25 — the Rival: the offensive AI (docs/m25-rival-spec.md). Pure
// COMPOSITION over the shared rung vocabulary — the m17-defender spec's
// promise kept: same arbiter (BrainCore), same rungs where the job is
// the same, a different ladder plus the war rungs, instead of a
// 900-line fork.
//
// The ladder's shape IS the doctrine:
//   * Defend stays on top — don't lose your own castle while the army
//     is away (and its threat perception still runs every think).
//   * Eat outranks everything martial — ledger #11 ("Sparta starves")
//     is structural: an AI that raids on an empty granary dies to its
//     own quartermaster.
//   * Muster carries the OFFENSE budget (live only while a war is
//     telegraphed or effective), so a peacetime Rival's curve is
//     EXACTLY a Homesteader's — aggression changes nothing until War
//     finds a target it can afford.
//   * War → (Conquer, Raid — M25 phases 4/5) → Probe: policy, then
//     campaigns, then the scouting pressure that feeds them.
//
// Enemy-intel perception rides BrainCore's perceive hook — it updates
// every think no matter which rung claims.
public sealed class RivalBrain : IBrain
{
    private readonly AiConfig _cfg;
    private readonly IRung[] _ladder;

    public RivalBrain(AiConfig cfg)
    {
        _cfg = cfg;
        _ladder = new IRung[]
        {
            new DefendRung(),
            new EatRung(),
            new BuildRung(),
            new TrainRung(),
            // War ABOVE Muster (inverting the spec sketch): declaring
            // flips AtWarOrMobilizing, which is what tells Muster's
            // drawdown that the surplus is a WAR CHEST — the other order
            // let the demob walk the army to the School before WarRung
            // ever got a think (the lab caught the race on day 0).
            new WarRung(),
            new MusterRung(offense: true),
            // Raid ABOVE Conquer for the same reservation-order reason
            // (ledger #5): Conquer's PREP sweeps every free surplus
            // soldier into the campaign; the party reserves first.
            new RaidRung(),
            new ConquerRung(),
            new ProbeRung(),
            new GrowRung(),
            // M26 — same slot as the Homesteader's ladder (below Grow,
            // above Scout), so the peacetime curves stay identical (the
            // Sparta pin) — even conquerors wall their own keep.
            new FortifyRung(),
            // M27 — and water their fields (same slot as the Homesteader).
            new IrrigateRung(),
            new ScoutRung(),
        };
    }

    public Decision Think(ViewDto view, long now, AiMemory mem) =>
        BrainCore.Think(_ladder, _cfg, view, now, mem, EnemyIntel.Perceive);
}
