using Sim.Core.World;

namespace Sim.Core.Mining;

// M44 — the announcement half of a survey (the M30 visibility contract,
// docs/goal-shaped-intents.md): every survey ends out loud — found, barren
// or abandoned. Applies nothing (SurveyRules has done the state work); it
// lands in ResolvedLog for the projector's alert feed and twin-run hashes.
public sealed class SurveyReportEvent : ScheduledEvent
{
    public int UnitId { get; }
    public int OwnerId { get; }
    public TileCoord Target { get; }
    // The vein found, or null (barren slopes, or an abandoned survey).
    public TileCoord? Vein { get; }
    // M51 — the ore the miner found in it (CopperOre, IronOre, SteelOre); None
    // with no vein (docs/m51-ore-tiers-spec.md).
    public Resource Ore { get; }
    // Null on a completed survey; the reason when it was abandoned.
    public string? Abandoned { get; }

    public SurveyReportEvent(int unitId, int ownerId, TileCoord target, TileCoord? vein, string? abandoned, Resource ore = Resource.None)
    {
        UnitId = unitId;
        OwnerId = ownerId;
        Target = target;
        Vein = vein;
        Abandoned = abandoned;
        Ore = ore;
    }

    public override void Apply(Simulation sim) { }

    public override string Describe() => Abandoned is { } why
        ? $"SurveyReport(unit={UnitId} @ {Target.X},{Target.Y}: abandoned — {why})"
        : Vein is { } v
            ? $"SurveyReport(unit={UnitId} @ {Target.X},{Target.Y}: {Ore} vein at {v.X},{v.Y})"
            : $"SurveyReport(unit={UnitId} @ {Target.X},{Target.Y}: barren)";
}
