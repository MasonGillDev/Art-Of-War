using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Population;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server.Ai.Rungs;

// Rung 5: Muster — the STANDING ARMY (M17 Phase 2, user-locked:
// peacetime soldiers, not militia-on-demand — training is a walk to
// the Barracks, and a reactive army means the first raid always lands
// on civilians).
//
// Sits between Train and Grow: after the food economy is staffed (an
// army you can't feed is a famine with swords) but before breeding (a
// colony that grows before it garrisons feeds the bandits, who scale
// with prosperity). Bare soldiers first — Soldier (30hp/3pw) beats
// Bandit (25hp/3pw) one-on-one and the Barracks needs no new economy
// (100 wood + 20 stone; genesis stone covers it). Equipment (shields,
// then the sword/ore chain) waits until the lab shows bare squads
// losing — docs/m17-defender-spec.md.
//
// TWO BUDGETS, one roster (the siege-trap fix):
//   * PEACETIME: min(floor + structures/per, pop/PopulationPerSoldier)
//     — scales with what bandits punish (prosperity), capped by what
//     the colony can carry (the Sparta-starved lesson, ledger #11).
//   * WAR FOOTING: while the threat memory counts hostiles inside the
//     leash, the quota rises to headcount + 1, capped by the WARTIME
//     population share (a levy may hurt; it may not starve). The
//     larder gate is waived — arming during a raid beats saving for
//     a house. When the memory goes fully cold, veterans DEMOBILIZE
//     one at a time (Soldier → Farmer at the School), so the levy
//     unwinds instead of becoming a permanent Sparta.
public sealed class MusterRung : IRung
{
    public Decision? TryClaim(ThinkContext ctx)
    {
        // Prune: recruit graduated/died; veteran demobilized/died.
        if (ctx.Mem.DesignatedRecruit is { } rid)
        {
            var r = ctx.OwnUnits.FirstOrDefault(u => u.Id == rid);
            if (r is null || (UnitRole)r.Role == UnitRole.Soldier)
                ctx.Mem.DesignatedRecruit = null;
        }
        if (ctx.Mem.DesignatedVeteran is { } vid)
        {
            var v = ctx.OwnUnits.FirstOrDefault(u => u.Id == vid);
            if (v is null || (UnitRole)v.Role != UnitRole.Soldier)
                ctx.Mem.DesignatedVeteran = null;
        }

        var soldiers = ctx.OwnUnits.Count(u => (UnitRole)u.Role == UnitRole.Soldier);
        var peacetime = Math.Min(
            ctx.Cfg.SoldierQuotaFloor
                + ctx.Own.Count / Math.Max(1, ctx.Cfg.SoldiersPerStructures),
            ctx.View.Population / Math.Max(1, ctx.Cfg.PopulationPerSoldier));
        var hostiles = ctx.Mem.SightedHostiles
            .Where(kv => ctx.Now - kv.Value.Tick <= ctx.Cfg.ThreatMemoryTicks
                && Math.Max(Math.Abs(kv.Key.X - ctx.CastleTile.X),
                       Math.Abs(kv.Key.Y - ctx.CastleTile.Y)) <= ctx.Cfg.PursuitLeashTiles)
            .Sum(kv => kv.Value.Count);
        var war = hostiles == 0 ? 0
            : Math.Min(hostiles + 1,
                ctx.View.Population / Math.Max(1, ctx.Cfg.WarPopulationPerSoldier));
        // M25 — OFFENSIVE FOOTING: while this colony is prosecuting a campaign
        // (RivalRung set the target), raise the roster to a field army —
        // CampaignArmySize, capped by the same wartime population share as the
        // levy (a war tax may hurt, not starve). The Rival rung directs the
        // force this builds.
        var campaign = ctx.Mem.CampaignTarget is not null
            ? Math.Min(ctx.Cfg.CampaignArmySize,
                ctx.View.Population / Math.Max(1, ctx.Cfg.WarPopulationPerSoldier))
            : 0;
        var quota = Math.Max(Math.Max(peacetime, war), campaign);

        // The larder gate is PEACETIME discipline only — under attack OR on
        // campaign, a free instant retrain beats a comfortable granary (the
        // proactive war was only declared above the growth floor anyway).
        if (war == 0 && campaign == 0 && ctx.View.CastleFood <= ctx.Cfg.GrowthFoodFloor) return null;

        if (soldiers >= quota) return Demobilize(ctx, soldiers, peacetime);

        var barracks = ctx.OwnStructure(StructureKind.Barracks);
        var site = ctx.OwnSite(StructureKind.Barracks);
        if (barracks is null && site is null)
        {
            // DON'T BREAK GROUND YOU CAN'T COVER (arbitration lesson
            // #10): placing a site is free, but its deliveries compete
            // with every other site in (y,x) TILE order, not rung
            // order. The lab watched this 100-wood site, placed against
            // a day-5 wood economy, wedge the whole construction queue:
            // the third farm (10 wood, LAST in delivery order) starved
            // with both builders locked on it, the fully-provisioned
            // camp never got a builder, wood income died at zero, and
            // five sites sat for 20 days. A player checks the warehouse
            // before breaking ground; so does the brain — the Barracks
            // waits until the castle HOLDS its full cost.
            foreach (var (res, amt) in StructureCatalog.Spec(StructureKind.Barracks).BuildCost)
                if (ThinkContext.AmountOf(ctx.Castle!.Holdings, res) < amt)
                    return null;
            // A depot near the castle, not a frontier fort — any free
            // tile; materials are logistics' job like every other site.
            if (ctx.NearestFreeTile(requiredBiome: null, ctx.Cfg.SiteSearchRange) is { } t)
                return new Decision("muster", "no barracks — placing one",
                    new List<Intent> { new PlaceSiteIntent(t, StructureKind.Barracks) { PlayerId = ctx.PlayerId } });
            return null;
        }
        if (site is not null && ctx.EnsureBuilders(site) is { Count: > 0 } b)
            return new Decision("muster", "building the barracks", b);
        if (barracks is null) return null;
        var barracksTile = ThinkContext.TileOf(barracks);

        // Designate a recruit (cross-think ownership — the Train rung's
        // lesson): natives (Role None) first, then off-role generalists;
        // never the colony's specialists or the existing garrison.
        if (ctx.Mem.DesignatedRecruit is null)
        {
            bool Recruitable(UnitDto u) => ctx.IsFree(u)
                && u.CargoAmount == 0 && u.Age >= ctx.Cfg.MinAdultAgeYears
                && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler
                    or UnitRole.Scout or UnitRole.Farmer
                    or UnitRole.Soldier or UnitRole.Archer);
            var cand = ctx.OwnUnits.Where(u => ctx.IsIdleStill(u) && Recruitable(u))
                .OrderBy(u => (UnitRole)u.Role == UnitRole.None ? 0 : 1).ThenBy(u => u.Id)
                .FirstOrDefault();
            if (cand is not null)
            {
                ctx.Mem.DesignatedRecruit = cand.Id;
            }
            else
            {
                // Nobody stands idle in a fully-employed colony — the
                // quota stalled at 3/4 for thirty days in the lab while
                // every generalist worked a field. CONSCRIPT off the
                // fields (the Grow precedent): free a working
                // generalist's hands this think; the designation owns
                // them, Eat re-staffs the post, and the recruit walks
                // next think. Farmers stay exempt — retraining the food
                // engine wastes a School graduation.
                var worker = ctx.OwnUnits.Where(u =>
                        u.Activity == (int)Activity.Working && Recruitable(u))
                    .OrderBy(u => u.Id).FirstOrDefault();
                if (worker is null) return null;
                var post = ctx.Own.FirstOrDefault(s => s.X == worker.X && s.Y == worker.Y);
                if (post is null) return null;
                ctx.Mem.DesignatedRecruit = worker.Id;
                return new Decision("muster", "conscripting a field hand",
                    new List<Intent> { new UnassignWorkersIntent(
                        new TileCoord(post.X, post.Y), new[] { ctx.Reserve(worker).Id })
                        { PlayerId = ctx.PlayerId } });
            }
        }
        var recruit = ctx.OwnUnits.First(u => u.Id == ctx.Mem.DesignatedRecruit);
        if (recruit.X == barracksTile.X && recruit.Y == barracksTile.Y && ctx.IsIdleStill(recruit))
            return new Decision("muster", $"swearing in soldier {soldiers + 1} of {quota}",
                new List<Intent> { new TrainUnitIntent(recruit.Id, UnitRole.Soldier) { PlayerId = ctx.PlayerId } });
        if (ctx.IsIdleStill(recruit))
            return new Decision("muster", "recruit walking to barracks",
                new List<Intent> { new MoveIntent(recruit.Id, barracksTile) { PlayerId = ctx.PlayerId } });
        return null;   // mid-march
    }

    // The war levy unwinds: threat memory fully cold (ANY sighting —
    // even outside the leash — pauses the drawdown; that's the
    // hysteresis) and the roster above the peacetime budget → one
    // veteran walks to the School and goes back to the fields.
    private static Decision? Demobilize(ThinkContext ctx, int soldiers, int peacetime)
    {
        // M25 — never disband the army while a campaign is on: the Rival needs
        // every blade until the war ends (peace or the target's fall clears it).
        if (ctx.Mem.CampaignTarget is not null) return null;
        if (ctx.Mem.SightedHostiles.Count > 0 || soldiers <= peacetime) return null;
        var school = ctx.OwnStructure(StructureKind.School);
        if (school is null) return null;
        var schoolTile = ThinkContext.TileOf(school);

        if (ctx.Mem.DesignatedVeteran is null)
        {
            var vet = ctx.OwnUnits.Where(u => (UnitRole)u.Role == UnitRole.Soldier
                    && ctx.IsIdleStill(u) && ctx.IsFree(u) && u.CargoAmount == 0)
                .OrderBy(u => u.Id).FirstOrDefault();
            if (vet is null) return null;
            ctx.Mem.DesignatedVeteran = vet.Id;
        }
        var veteran = ctx.OwnUnits.First(u => u.Id == ctx.Mem.DesignatedVeteran);
        if (veteran.X == schoolTile.X && veteran.Y == schoolTile.Y && ctx.IsIdleStill(veteran))
            return new Decision("muster", $"demobilizing a veteran ({soldiers} > {peacetime} peacetime)",
                new List<Intent> { new TrainUnitIntent(veteran.Id, UnitRole.Farmer) { PlayerId = ctx.PlayerId } });
        if (ctx.IsIdleStill(veteran))
            return new Decision("muster", "veteran walking to the school",
                new List<Intent> { new MoveIntent(veteran.Id, schoolTile) { PlayerId = ctx.PlayerId } });
        return null;   // mid-march
    }
}
