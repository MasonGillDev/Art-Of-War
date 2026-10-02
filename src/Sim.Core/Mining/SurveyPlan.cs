using Sim.Core.World;

namespace Sim.Core.Mining;

// M44 — a Miner's in-flight survey (docs/stone-and-ore-land.md). Lives on
// Unit.Survey from SurveyIntent until the report (or a cancel).
//
// Two phases, told apart by the anchor:
//   * walking to the slope — CompleteTick null; the walk's own anchor
//     drives it and Walk.DispatchOnArrival starts the dig;
//   * digging — CompleteTick/CompleteSeq set (architecture §2.8): the one
//     SurveyCompleteEvent, rebuilt by RegenerateQueue on restore and fenced
//     on CompleteTick so a cancelled-then-restarted survey's old event
//     no-ops.
public sealed class SurveyPlan
{
    public TileCoord Target { get; }
    public long? CompleteTick { get; internal set; }
    public long? CompleteSeq { get; internal set; }

    public SurveyPlan(TileCoord target) { Target = target; }
}
