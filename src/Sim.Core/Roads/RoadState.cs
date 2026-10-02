namespace Sim.Core.Roads;

// Per-LINK road state (M43, docs/subtile-movement.md): the step between two
// adjacent subtiles. Sparse: only links with Condition > 0 live in
// GameWorld.Roads. When CatchUpDecay drops Condition to 0, the link is
// removed from the set (absent links return the plain step cost via the
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
