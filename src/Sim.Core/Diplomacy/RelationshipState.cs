namespace Sim.Core.Diplomacy;

// M6 — symmetric per-pair relationship state. Three values; today only
// Enemy gates combat (AreHostile). Ally is inert-but-present — behaviorally
// identical to Neutral for M6, with mechanical benefits (shared vision,
// passage, gated corridors) deferred to later milestones. The enum is three
// so combat never needs retrofitting when ally grows teeth.
public enum RelationshipState : byte
{
    Neutral = 0,
    Enemy   = 1,
    Ally    = 2,
}
