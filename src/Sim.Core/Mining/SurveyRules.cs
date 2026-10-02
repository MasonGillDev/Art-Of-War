using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Core.Mining;

// M44 — the survey lifecycle (docs/stone-and-ore-land.md).
//
// Begin → walk (Activity.Moving) → OnArrival → dig (Activity.Waiting at the
// slope, one SurveyCompleteEvent) → Complete (report, Idle). Every exit runs
// through Complete or Cancel, both of which clear Unit.Survey and leave the
// body Idle — never a silent anchor (the M30 visibility contract).
//
// The cancel hooks are the existing retask/stop points: Retask.Release (any
// new order) and Walk.Halt (the walk to the slope failed). A fight does not
// cancel: the dig clock keeps running, and the Miner reports if he is
// still alive when it rings.
public static class SurveyRules
{
    internal static void Begin(Simulation sim, Unit unit, TileCoord target)
    {
        unit.Survey = new SurveyPlan(target);
        if (unit.Position == target)
        {
            StartDig(sim, unit);
            return;
        }
        unit.TrySetActivity(Activity.Moving, target);
        MoveIntent.BeginMove(sim, unit, target);
        if (!unit.IsWalking)
            Cancel(sim, unit, "no way onto the slope");
    }

    // The walk to the slope finished (Walk.DispatchOnArrival).
    internal static void OnArrival(Simulation sim, Unit unit)
    {
        if (unit.Survey is not { } plan) return;
        if (unit.Position != plan.Target)
        {
            Cancel(sim, unit, "could not reach the slope");
            return;
        }
        StartDig(sim, unit);
    }

    private static void StartDig(Simulation sim, Unit unit)
    {
        var plan = unit.Survey!;
        unit.TrySetActivity(Activity.Idle);
        unit.TrySetActivity(Activity.Waiting, plan.Target);
        var at = sim.Now + sim.World.VeinConfig.SurveyTicks;
        plan.CompleteTick = at;
        plan.CompleteSeq = sim.Schedule(at, new SurveyCompleteEvent(unit.Id));
    }

    // Abandon the survey: anchor cleared (the pending event fences out on the
    // missing plan), body Idle, and the abandonment announced.
    internal static void Cancel(Simulation sim, Unit unit, string reason)
    {
        if (unit.Survey is not { } plan) return;
        unit.Survey = null;
        Walk.Stop(unit);
        unit.TrySetActivity(Activity.Idle);
        sim.Schedule(sim.Now, new SurveyReportEvent(unit.Id, unit.OwnerId, plan.Target, null, reason));
        Sim.Core.Groups.GroupMuster.OnFreed(sim, unit);   // M46
    }

    // The dig is done. Sweep the Mountain tiles within SurveyRadius of the
    // slope in (distance, y, x) order: the first vein this faction doesn't
    // know yet is the find, and every vein-less mountain tile nearer than it
    // is proven barren. No find → every vein-less mountain tile in reach is
    // barren. Veins the faction already knows are neither news nor barren.
    internal static void Complete(Simulation sim, Unit unit)
    {
        var plan = unit.Survey!;
        var world = sim.World;
        var owner = unit.OwnerId;
        var radius = world.VeinConfig.SurveyRadius;
        TileCoord? found = null;
        var foundDist = int.MaxValue;

        for (var d = 0; d <= radius && found is null; d++)
        for (var dy = -d; dy <= d && found is null; dy++)
        for (var dx = -d; dx <= d && found is null; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != d) continue;
            var t = new TileCoord(plan.Target.X + dx, plan.Target.Y + dy);
            if (!world.Grid.InBounds(t) || world.Grid.BiomeAt(t) != Biome.Mountain) continue;
            if (Veins.IsVein(world, t) && !Veins.Knows(world, owner, t))
            {
                found = t;
                foundDist = d;
            }
        }

        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) >= foundDist) continue;
            var t = new TileCoord(plan.Target.X + dx, plan.Target.Y + dy);
            if (!world.Grid.InBounds(t) || world.Grid.BiomeAt(t) != Biome.Mountain) continue;
            if (!Veins.IsVein(world, t)) Veins.MarkBarren(world, owner, t);
        }
        if (found is { } vein) Veins.Learn(world, owner, vein);

        unit.Survey = null;
        unit.TrySetActivity(Activity.Idle);
        sim.Schedule(sim.Now, new SurveyReportEvent(unit.Id, owner, plan.Target, found, null));
        Sim.Core.Groups.GroupMuster.OnFreed(sim, unit);   // M46: the survey done, it answers its group's muster
    }
}
