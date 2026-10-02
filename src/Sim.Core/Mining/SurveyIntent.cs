using Sim.Core.Intents;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Core.Mining;

// M44 — "go find ore on that slope" (docs/stone-and-ore-land.md). A Miner
// walks to a Mountain tile, digs for VeinConfig.SurveyTicks, then reports
// the nearest unknown vein within VeinConfig.SurveyRadius — or that the
// slopes are barren. Goal-shaped (docs/goal-shaped-intents.md): one
// decision; the sim walks and waits.
//
// Validated here at resolution time (docs/intent-validation.md); a
// rejection mutates nothing.
public sealed class SurveyIntent : Intent
{
    public int UnitId { get; }
    public TileCoord Target { get; }

    [System.Text.Json.Serialization.JsonConstructor]
    public SurveyIntent(int unitId, TileCoord target)
    {
        UnitId = unitId;
        Target = target;
    }

    public override IntentOutcome Resolve(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(UnitId, out var unit))
            return IntentOutcome.Reject($"unit {UnitId} does not exist");
        if (unit.OwnerId != PlayerId)
            return IntentOutcome.Reject($"unit {UnitId} not owned by player {PlayerId}");
        if (Retask.Refusal(sim, unit) is { } refusal)
            return IntentOutcome.Reject(refusal);
        // Only a trained Miner knows what a vein looks like.
        if (unit.Role != UnitRole.Miner)
            return IntentOutcome.Reject($"unit {UnitId} is a {unit.Role}; only a Miner can survey");
        if (!world.Grid.InBounds(Target))
            return IntentOutcome.Reject($"target {Target.X},{Target.Y} out of bounds");
        // Worldgen biome: Mountain never drifts (off the fertility ladder),
        // and every faction knows where the mountains are (M22).
        if (world.Grid.BiomeAt(Target) != Biome.Mountain)
            return IntentOutcome.Reject($"target {Target.X},{Target.Y} is {world.Grid.BiomeAt(Target)}; ore is found in Mountain");
        if (unit.Position != Target && !Walk.CanReach(world, unit, Target))
            return IntentOutcome.Reject($"unit {UnitId} cannot reach {Target.X},{Target.Y}");

        Retask.Release(sim, unit);
        SurveyRules.Begin(sim, unit, Target);
        return IntentOutcome.Applied;
    }

    public override string Describe() =>
        $"SurveyIntent(unit={UnitId} -> {Target.X},{Target.Y})";
}
