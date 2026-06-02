namespace Sim.Core.Roads;

// Per-tile road state. Sparse: only tiles with Condition > 0 live in
// GameWorld.Roads. When CatchUpDecay drops Condition to 0, the tile is
// removed from the set (zero-condition tiles return plain biome cost via
// the fallback path in Roads.EffectiveCost).
//
// Mutable class — events mutate it in place. Matches the Extractor pattern;
// avoids record-value-copy footguns when the same instance lives in a
// dictionary and gets updated frequently.
public sealed class RoadState
{
    public int Condition;
    public long LastDecayTick;

    public RoadState(int condition, long lastDecayTick)
    {
        Condition = condition;
        LastDecayTick = lastDecayTick;
    }
}
