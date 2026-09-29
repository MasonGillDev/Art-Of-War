namespace Sim.Core.Roads;

// Per-ARC road state (docs/roads-on-edges.md): the lane between two
// adjacent tiles. Sparse: only arcs with Condition > 0 live in
// GameWorld.Roads. When CatchUpDecay drops Condition to 0, the arc is
// removed from the set (absent arcs return plain biome cost via the
// fallback path in Road.EffectiveCost).
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
