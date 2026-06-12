using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.World;

namespace Sim.Server.Ai.Rungs;

// Rung 2: Build — a LIVE wood income exists.
public sealed class BuildRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        var camps = ctx.Own.Where(s => (StructureKind)s.Kind == StructureKind.LumberCamp)
            .OrderBy(s => s.Y).ThenBy(s => s.X).ToList();
        var sites = ctx.Own.Where(s => (StructureKind)s.Kind == StructureKind.ConstructionSite
            && (StructureKind)s.TargetKind == StructureKind.LumberCamp).ToList();

        // Camps die too — forest claims exhaust in ~52 days (DegradeAmount
        // 2). Without rotation the lab's training-boosted colony hit wood
        // ZERO at day 200 and froze at 17 farms while breeding ran to pop
        // 159: the whole construction economy hung off one dead camp.
        if (ctx.DetectExhausted(camps, Resource.Wood) is { } release)
            return new Decision("build", "camp exhausted — releasing the crew", release);
        var liveCamps = camps.Where(c => !ctx.Mem.DeadExtractors.Contains((c.X, c.Y))).ToList();

        if (liveCamps.Count + sites.Count < 1)
        {
            var campSpec = StructureCatalog.Spec(StructureKind.LumberCamp);
            var t = ctx.NearestPocketTile(Biome.Forest, ctx.Cfg.SiteSearchRange,
                        campSpec.ClaimCount, campSpec.ClaimRange)
                ?? ctx.NearestFreeTile(Biome.Forest, ctx.Cfg.SiteSearchRange);
            if (t is { } tile)
            {
                ctx.Mem.ForestStarved = false;
                return new Decision("build", "no live lumber camp — placing one",
                    new List<Intent> { new PlaceSiteIntent(tile, StructureKind.LumberCamp) { PlayerId = ctx.PlayerId } });
            }
            // No known forest: starve the scouts back out (they reveal
            // everything, grassland and forest alike).
            ctx.Mem.ForestStarved = true;
            return null;
        }
        ctx.Mem.ForestStarved = false;
        foreach (var site in sites)
            if (ctx.EnsureBuilders(site) is { Count: > 0 } b)
                return new Decision("build", "building a camp", b);
        foreach (var camp in liveCamps)
            if (ctx.StaffExtractor(camp, UnitRole.Lumberjack) is { Count: > 0 } st)
                return new Decision("build", "staffing the camp", st);
        return null;
    }
}
