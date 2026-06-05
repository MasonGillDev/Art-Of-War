namespace Sim.Core.Biomes;

// Per-tile fertility deviation from worldgen baseline. Sparse: only tiles whose
// current deviation != 0 live in GameWorld.Fertility. When a catch-up brings
// deviation back to 0 (recovery climbed to baseline), the tile is removed from
// the dict.
//
// Mutable class — events mutate it in place. Matches RoadState / Extractor;
// avoids record-value-copy footguns when the same instance lives in a
// dictionary and gets updated frequently.
//
// THE LATCH IS IMPLICIT (no flag): a tile is "desert-latched" iff
// (baseline + Deviation) < DesertThreshold. Recovery is forced to 0 whenever
// the implicit latch holds; combined with the invariant that rate is constant
// between transitions, this gives permanent-desert semantics without storing a
// per-tile flag. See docs/biome-degradation.md for the proof sketch.
public sealed class Fertility
{
    // Signed delta from worldgen baseline fertility. Range [-baseline, 0]:
    // recovery clamps at 0 (cannot exceed baseline; sparse entry is removed),
    // degrade clamps at -baseline (current fertility never goes negative).
    public int Deviation;

    // Last tick at which CatchUp finalised this tile's deviation. Reads applied
    // since then are pure (no state change); future writes resume from here.
    public long LastUpdateTick;

    public Fertility(int deviation, long lastUpdateTick)
    {
        Deviation = deviation;
        LastUpdateTick = lastUpdateTick;
    }
}
