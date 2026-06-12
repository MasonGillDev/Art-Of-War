using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Population;
using Sim.Core.World;

namespace Sim.Server.Ai.Rungs;

// Rung 3: Train — keep the colony's ORGANS alive, then multiply food.
//
// ROLE FLOORS first (the builder-extinction find, ledger #12): only
// Builder-role hands may raise a site (engine rule), Haulers are the
// 25-capacity logistics backbone, Scouts the exploration organ. A
// bandit raid that kills the last Builder otherwise freezes every
// site FOREVER — the colony sits on banked wood it can never spend.
// Floors restore the genesis shape before anything else trains.
//
// Then FARMER COVERAGE as before: the labor ledger prices a
// generalist farmhand at HALF a Farmer's output (the 2:1 role bonus)
// — the measured ~40% supply gap that rode two lab colonies into the
// cascade. Natives are born Role=None (blank apprentices), the School
// is instant and free once built, and every retrained Farmer doubles
// a hand. Sits ABOVE Grow: multiplying food beats adding mouths.
public sealed class TrainRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        // Prune: trainee reached their target role, died, or vanished.
        if (ctx.Mem.DesignatedTrainee is { } tr)
        {
            var t = ctx.OwnUnits.FirstOrDefault(u => u.Id == tr.Id);
            if (t is null || (UnitRole)t.Role == tr.Target)
                ctx.Mem.DesignatedTrainee = null;
        }

        // What does the colony need trained? A designated apprentice
        // keeps their original target (pipeline integrity); otherwise
        // re-derive from the floors and the ledger each think.
        var target = ctx.Mem.DesignatedTrainee?.Target ?? NeededRole(ctx);
        if (target is null) return null;

        var school = ctx.OwnStructure(StructureKind.School);
        var site = ctx.OwnSite(StructureKind.School);
        if (school is null && site is null)
        {
            if (ctx.NearestFreeTile(requiredBiome: null, ctx.Cfg.SiteSearchRange) is { } t)
                return new Decision("train", "no school — placing one",
                    new List<Intent> { new PlaceSiteIntent(t, StructureKind.School) { PlayerId = ctx.PlayerId } });
            return null;
        }
        if (site is not null && ctx.EnsureBuilders(site) is { Count: > 0 } b)
            return new Decision("train", "building the school", b);
        if (school is null) return null;
        var schoolTile = ThinkContext.TileOf(school);

        // Designate an apprentice (ownership across thinks — same lesson
        // as parents): natives (Role None) first, then off-role
        // generalists; never the floors' own roles, the food engine, or
        // the garrison.
        if (ctx.Mem.DesignatedTrainee is null)
        {
            var cand = ctx.OwnUnits.Where(u => ctx.IsIdleStill(u) && ctx.IsFree(u)
                    && u.CargoAmount == 0 && u.Age >= ctx.Cfg.MinAdultAgeYears
                    && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler
                        or UnitRole.Scout or UnitRole.Farmer
                        or UnitRole.Soldier or UnitRole.Archer))
                .OrderBy(u => (UnitRole)u.Role == UnitRole.None ? 0 : 1).ThenBy(u => u.Id)
                .FirstOrDefault();
            if (cand is null) return null;
            ctx.Mem.DesignatedTrainee = (cand.Id, target.Value);
        }
        var who = ctx.Mem.DesignatedTrainee.Value;
        var trainee = ctx.OwnUnits.First(u => u.Id == who.Id);
        if (trainee.X == schoolTile.X && trainee.Y == schoolTile.Y && ctx.IsIdleStill(trainee))
            return new Decision("train", $"graduating a {who.Target}",
                new List<Intent> { new TrainUnitIntent(trainee.Id, who.Target) { PlayerId = ctx.PlayerId } });
        if (ctx.IsIdleStill(trainee))
            return new Decision("train", $"apprentice walking to school ({who.Target})",
                new List<Intent> { new MoveIntent(trainee.Id, schoolTile) { PlayerId = ctx.PlayerId } });
        return null;   // mid-march
    }

    // Floors before coverage: hands, backbone, eyes — then the 2:1
    // food multiplier. Adults only (the same age gate training itself
    // enforces).
    private static UnitRole? NeededRole(ThinkContext ctx)
    {
        int Adults(UnitRole r) => ctx.OwnUnits.Count(u =>
            (UnitRole)u.Role == r && u.Age >= ctx.Cfg.MinAdultAgeYears);
        if (Adults(UnitRole.Builder) < ctx.Cfg.BuilderFloor) return UnitRole.Builder;
        if (Adults(UnitRole.Hauler) < ctx.Cfg.HaulerFloor) return UnitRole.Hauler;
        if (Adults(UnitRole.Scout) < ctx.Cfg.ScoutFloor) return UnitRole.Scout;
        var (pool, _, handsDemanded) = ctx.LaborLedger();
        if (Adults(UnitRole.Farmer) < Math.Min(handsDemanded, Math.Max(1, pool - 3)))
            return UnitRole.Farmer;
        return null;
    }
}
