namespace Sim.Core.Landing;

// Two-act pacing (docs/two-act-pacing.md). Day X — "the landing" — is the tick
// that ends the prelude: from then on the host runs the world at its slow pace,
// and war bands come out of the fog for every kingdom. Genesis-set, immutable,
// snapshotted (v40).
//
// Tick 0 = no landing: a one-act world. That is the default, so every
// hand-built test world and every balance lab keeps its behaviour and its hash.
//
// THE SIM NEVER READS THE HOST'S PACE. It knows only WHEN the landing is. The
// host reads this same tick to change its pace, so the drop cannot drift from
// the sim's own idea of day X: a pause, a stall or a replay moves neither.
//
// The host each kingdom faces (every knob tunable; sized against the battlefield
// grid, docs/battlefield-grid.md, where an edge admits 4 lanes):
//   Fronts            war bands, each from its own compass side (1..4);
//   BandSize          bandits per band (4 fills one edge row of the board);
//   MinDistance /     the ring (Chebyshev tiles from the target's castle) a band
//   MaxDistance       comes out of the fog in;
//   StagingDistance   how far out from the castle a band gathers, in sight of it;
//   AssaultDelayTicks from the landing to the moment every band strikes at once.
public readonly record struct LandingConfig(
    long Tick,
    int BandSize = 4,
    int Fronts = 3,
    int MinDistance = 14,
    int MaxDistance = 26,
    int StagingDistance = 2,
    long AssaultDelayTicks = Time.Day)
{
    public bool Enabled => Tick > 0;

    // The landing at the start of game-day `day` (day 30 = tick 30 × Time.Day),
    // with the default host. 0 or less = no landing.
    public static LandingConfig OnDay(int day) =>
        new(day <= 0 ? 0 : (long)day * Time.Day);
}
