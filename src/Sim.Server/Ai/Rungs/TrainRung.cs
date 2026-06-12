using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Population;
using Sim.Core.World;

namespace Sim.Server.Ai.Rungs;

// Rung 3: Train — a Farmer is worth two generalists.
//
// The labor ledger prices a generalist farmhand at HALF a Farmer's
// output (the 2:1 role bonus) — the measured ~40% supply gap that
// rode two lab colonies into the cascade. Training is the relief
// valve: natives are born Role=None (blank apprentices), the School
// is instant and free once built, and every retrained Farmer doubles
// a hand. Sits ABOVE Grow: multiplying food beats adding mouths.
public sealed class TrainRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        // Prune: trainee graduated, died, or vanished.
        if (ctx.Mem.DesignatedTrainee is { } tid)
        {
            var t = ctx.OwnUnits.FirstOrDefault(u => u.Id == tid);
            if (t is null || (UnitRole)t.Role == UnitRole.Farmer)
                ctx.Mem.DesignatedTrainee = null;
        }

        var (pool, _, handsDemanded) = ctx.LaborLedger();
        var farmers = ctx.OwnUnits.Count(u =>
            (UnitRole)u.Role == UnitRole.Farmer && u.Age >= ctx.Cfg.MinAdultAgeYears);
        // Enough Farmers to cover every hand the colony can field? Done.
        if (farmers >= Math.Min(handsDemanded, Math.Max(1, pool - 3))) return null;

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
        // generalists; never Builders/Haulers/Scouts/Farmers.
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
            ctx.Mem.DesignatedTrainee = cand.Id;
        }
        var trainee = ctx.OwnUnits.First(u => u.Id == ctx.Mem.DesignatedTrainee);
        if (trainee.X == schoolTile.X && trainee.Y == schoolTile.Y && ctx.IsIdleStill(trainee))
            return new Decision("train", "graduating a farmer",
                new List<Intent> { new TrainUnitIntent(trainee.Id, UnitRole.Farmer) { PlayerId = ctx.PlayerId } });
        if (ctx.IsIdleStill(trainee))
            return new Decision("train", "apprentice walking to school",
                new List<Intent> { new MoveIntent(trainee.Id, schoolTile) { PlayerId = ctx.PlayerId } });
        return null;   // mid-march
    }
}
