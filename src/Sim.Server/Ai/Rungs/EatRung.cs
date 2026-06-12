using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server.Ai.Rungs;

// Rung 1: Eat — enough working farms for the mouths.
public sealed class EatRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        var farms = ctx.Own.Where(s => (StructureKind)s.Kind == StructureKind.Farm)
            .OrderBy(s => s.Y).ThenBy(s => s.X).ToList();
        var sites = ctx.Own.Where(s => (StructureKind)s.Kind == StructureKind.ConstructionSite
            && (StructureKind)s.TargetKind == StructureKind.Farm).ToList();

        // DEATH DETECTION — the rotation trigger (shared with Build's
        // camps; see ThinkContext.DetectExhausted). Dead farms release
        // their crew and stop counting as supply.
        if (ctx.DetectExhausted(farms, Resource.Food) is { } release)
            return new Decision("eat", "farm exhausted — releasing the crew", release);
        // Age accounting: remember when each farm first appeared; one
        // within ReplaceAhead of its working lifetime is DYING — still
        // staffed and producing, but no longer counted as future supply,
        // so its replacement starts before the cliff instead of after.
        // (Slash-and-burn bookkeeping — under ROTATION farms never age
        // out, so soil replaces age as the supply signal below.)
        foreach (var farm in farms)
            ctx.Mem.FirstSeen.TryAdd((farm.X, farm.Y), ctx.Now);
        bool Dying(StructDto f) =>
            ctx.Now >= ctx.Mem.FirstSeen.GetValueOrDefault((f.X, f.Y), ctx.Now)
                + ctx.Cfg.FarmLifetimeTicks - ctx.Cfg.FarmReplaceAheadTicks;
        var liveFarms = farms.Where(f => !ctx.Mem.DeadExtractors.Contains((f.X, f.Y))).ToList();

        // CROP ROTATION (soil-aware; ClaimFertility is own-only on the
        // wire — the brain sees exactly what a player walking their own
        // fields sees). Worst claim tile is the metric: the desert latch
        // is per-tile and permanent. Hysteresis lives in the STAFFING
        // state: a working farm keeps its crew until soil < RestSoilBelow,
        // an empty farm re-enters service only above ResumeSoilAbove —
        // so the boundary can't oscillate.
        int Soil(StructDto f) =>
            f.ClaimFertility is { Length: > 0 } cf ? cf.Min() : int.MaxValue;
        bool InService(StructDto f) => !ctx.Cfg.RotateFarms
            || (f.Workers > 0 ? Soil(f) >= ctx.Cfg.RestSoilBelow
                              : Soil(f) >= ctx.Cfg.ResumeSoilAbove);
        if (ctx.Cfg.RotateFarms)
            foreach (var farm in liveFarms)
            {
                if (farm.Workers == 0 || Soil(farm) >= ctx.Cfg.RestSoilBelow) continue;
                var crew = ctx.OwnUnits.Where(u =>
                        u.X == farm.X && u.Y == farm.Y && u.Activity == (int)Activity.Working)
                    .Select(u => u.Id).ToList();
                if (crew.Count == 0) continue;
                return new Decision("eat",
                    $"resting a tired farm at {farm.X},{farm.Y} (soil {Soil(farm)}) — rotation",
                    new List<Intent> { new UnassignWorkersIntent(
                        ThinkContext.TileOf(farm), crew) { PlayerId = ctx.PlayerId } });
            }

        var countedFarms = ctx.Cfg.RotateFarms
            ? liveFarms.Count(InService)
            : liveFarms.Count(f => !Dying(f));

        // CAPACITY PLANNING — the sprawl driver, in WORKERS not farms:
        // the ledger says how many farmhands the colony needs AND can
        // field; farms exist to hold them. (Staffing to cap consumed the
        // whole workforce; ignoring the adult pool drafted everyone and
        // killed logistics — both extinction events in the lab's log.)
        var spec = StructureCatalog.Spec(StructureKind.Farm);
        var (_, handsNeeded, _) = ctx.LaborLedger();
        var farmsNeeded = (handsNeeded + spec.WorkerCap - 1) / spec.WorkerCap;
        var urgent = ctx.View.InFamine
            || (ctx.View.FoodRunwayTicks >= 0 && ctx.View.FoodRunwayTicks < ctx.Cfg.FoodRunwayFloorTicks);
        // Urgency buys ONE farm of insurance over the books, not a spree —
        // the undamped backstop built six farms in ten days and burned
        // claim land it could never staff.
        var wantAnother = countedFarms + sites.Count < farmsNeeded
            || (urgent && sites.Count == 0 && liveFarms.Count < farmsNeeded + 1);

        // THE LAND BANK — exploration is driven by known-land INVENTORY,
        // not by crisis. When the fog hides everything past a thin halo,
        // SiteSearchRange means nothing (a 300-day colony starved at pop
        // 67 with a continent in the fog); and re-opening scouting only
        // when placement FAILS rescues ~6 days too late against a 3-day
        // famine grace (the next run died faster). Below the floor, the
        // Scout rung runs past its budget, spiraling wider — while the
        // granary is still full.
        ctx.Mem.LandStarved =
            ctx.CountPocketTiles(Biome.Grassland, ctx.Cfg.SiteSearchRange,
                    spec.ClaimCount, spec.ClaimRange, ctx.Cfg.LandBankFloorPockets)
                < ctx.Cfg.LandBankFloorPockets
            || ctx.Mem.ForestStarved;   // Build's distress flag — don't clobber it

        if (wantAnother)
        {
            // Certified pocket first (fast, never rejected); else an
            // optimistic attempt on the nearest free grass tile — the
            // brain's pocket model is CONSERVATIVE (fog-edge neighbors
            // count as nothing), so when it certifies no site the server,
            // which sees the real map, gets the final word. Rejections
            // blacklist and move on.
            var t = ctx.NearestPocketTile(Biome.Grassland, ctx.Cfg.SiteSearchRange,
                        spec.ClaimCount, spec.ClaimRange)
                ?? ctx.NearestFreeTile(Biome.Grassland, ctx.Cfg.SiteSearchRange);
            if (t is { } tile)
                return new Decision("eat",
                    $"live farms {liveFarms.Count}+{sites.Count} of {farmsNeeded} needed — placing one",
                    new List<Intent> { new PlaceSiteIntent(tile, StructureKind.Farm) { PlayerId = ctx.PlayerId } });
            if (liveFarms.Count == 0 && sites.Count == 0) return null;
        }
        foreach (var site in sites)
            if (ctx.EnsureBuilders(site) is { Count: > 0 } b)
                return new Decision("eat", "building a farm", b);

        // Staff against the budget: fill farms in order until the hands
        // run out — the LAST farm is usually partial, and that's correct.
        // Under rotation, freshest soil works first and resting farms
        // are skipped (they re-enter via InService once recovered).
        var staffable = ctx.Cfg.RotateFarms
            ? liveFarms.Where(InService)
                .OrderByDescending(Soil).ThenBy(f => f.Y).ThenBy(f => f.X).ToList()
            : liveFarms;
        var hands = handsNeeded - liveFarms.Sum(f => f.Workers);
        foreach (var farm in staffable)
        {
            if (hands <= 0) break;
            var target = Math.Min(farm.WorkerCap, farm.Workers + hands);
            if (ctx.StaffExtractor(farm, UnitRole.Farmer, target) is { Count: > 0 } st)
                return new Decision("eat", $"staffing a farm toward {handsNeeded} hands", st);
            hands -= Math.Max(0, target - farm.Workers);
        }
        return null;
    }
}
