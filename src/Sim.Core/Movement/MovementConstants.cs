namespace Sim.Core.Movement;

// Tuning constants for crowding cost and the per-tile unit cap.
// Numbers are placeholders — tune in play. Shape is locked here:
// banded integer additives (not a continuous function) per architecture §2.5,
// and one global cap.
public static class MovementConstants
{
    // Hard per-tile unit cap. MoveArrivalEvent / GroupArrivalEvent reject the
    // arrival if accepting it would push the destination tile over this
    // value. Set high enough that normal play never bumps it; it's a panic
    // switch against unbounded-stacking pathologies, not a gameplay knob.
    public const int MaxUnitsPerTile = 50;

    // Banded crowding cost (added to terrain cost on top of road effects).
    //   1-3 units  : +0   (normal play — solo units, small chains, no penalty)
    //   4-7 units  : +10  (small group, noticeable cost)
    //   8-15 units : +25  (medium army, sluggish)
    //   16+ units  : +50  (massive pileup, very slow)
    // Flat integer additive per band. NEVER a continuous function of count —
    // that's the coupled-interval trap (architecture §2.5). Bands let the
    // cost stay exact under integer math and easy to reason about.
    public static int BandedCrowdingCost(int unitCount)
    {
        if (unitCount <= 3)  return 0;
        if (unitCount <= 7)  return 10;
        if (unitCount <= 15) return 25;
        return 50;
    }
}
