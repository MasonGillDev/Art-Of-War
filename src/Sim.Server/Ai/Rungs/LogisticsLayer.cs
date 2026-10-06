using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server.Ai.Rungs;

// The BACKGROUND layer — hauls are not decisions (the first arbitration
// lesson, learned the designed way: the decision trace showed the AI
// hauling food while its camp site sat at zero builders). Every think,
// every needed haul (full buffers home, site deliveries, house
// stocking) gets its own idle carrier. Hauling competes for CARRIERS,
// never for the think — it runs after the strategic ladder has
// reserved its people.
public static class LogisticsLayer
{
    public static List<Intent> Emit(ThinkContext ctx)
    {
        var intents = new List<Intent>();
        var urgent = ctx.View.InFamine
            || (ctx.View.FoodRunwayTicks >= 0 && ctx.View.FoodRunwayTicks < ctx.Cfg.FoodRunwayFloorTicks);

        // Recovery: a laden idle unit is a deadlock — every selector
        // requires empty cargo, so leftover cargo (a haul that over-picked
        // for a small site need) turns a citizen into a zombie carrying
        // the faction's wood. Walk them home and unload into the castle.
        foreach (var u in ctx.OwnUnits.Where(u =>
                     ctx.IsIdleStill(u) && ctx.IsFree(u) && u.CargoAmount > 0))
        {
            ctx.Reserve(u);
            intents.Add(u.X == ctx.CastleTile.X && u.Y == ctx.CastleTile.Y
                ? new UnloadCargoIntent(u.Id) { PlayerId = ctx.PlayerId }
                : new MoveIntent(u.Id, ctx.CastleTile) { PlayerId = ctx.PlayerId });
        }

        // Site deliveries — construction unblocks everything else. ONE
        // delivery in flight per site: a 25-capacity carrier over-fills a
        // 10-wood need, so racing three of them strands two with stuck
        // cargo (the zombie bug above). A delivery is "in flight" when any
        // own laden-or-hauling unit is heading for the site tile.
        foreach (var site in ctx.OwnSites())
        {
            var siteTile = ThinkContext.TileOf(site);
            var inFlight = ctx.OwnUnits.Any(u =>
                u.DestX == siteTile.X && u.DestY == siteTile.Y
                && (u.Activity == (int)Activity.Hauling || u.CargoAmount > 0));
            if (inFlight) continue;
            foreach (var need in site.Needed)
            {
                var missing = need.Amount - ThinkContext.AmountOf(site.Holdings, (Resource)need.Resource);
                if (missing <= 0) continue;
                if (ThinkContext.AmountOf(ctx.Castle!.Holdings, (Resource)need.Resource) <= 0) continue;
                if (ctx.TakeIdleCarrier() is not { } carrier) return intents;
                intents.Add(new HaulIntent(carrier.Id, ctx.CastleTile, siteTile,
                    (Resource)need.Resource) { PlayerId = ctx.PlayerId });
                break;
            }
        }

        // Full (or famine-urgent) extractor buffers go home. SWARM the
        // buffer: keep allocating carriers until their combined capacity
        // covers it — most citizens carry only 5 (UnitCargoCatalog, a
        // rule, not state), and a single small carrier per think left the
        // farm dormant-at-cap for whole days while food flatlined at the
        // famine line.
        // M19 — houses are FOOD HOMES: stock against CONSUMPTION, not
        // just the birth cost. Target = the residents' grace-window
        // worth of meals (the famine grace IS the reaction budget — a
        // fuller cache means a local famine can't outrun the hauls)
        // plus the birth cost so breeding never waits, capped by the
        // cache. LocalFood is the live SIGNED level (consumption is
        // lazy; raw Holdings lie between events).
        //
        // SOURCE (Phase 3b — neighborhoods feed themselves): the
        // NEAREST own food buffer within HomeAssignRadius beats the
        // castle — the frontier loop is a 3-tile shuttle from the farm
        // next door, which is the whole point of moving the sink out
        // here (castle-routed stocking would walk farm → castle →
        // house, DOUBLE the old distance). This block runs BEFORE the
        // buffers-go-home loop so local needs claim the harvest first;
        // `claimed` debits this think's stocking hauls so the surplus
        // loop can't double-ship the same food.
        //
        // CASTLE FALLBACK ALLOCATION: the castle keeps only ITS OWN
        // residents' grace share and distributes the rest — gating on
        // the GROWTH floor starved every house cache whenever the
        // castle ran lean, which got the lab's poorest colony robbed
        // into local famines by parked bandits while 200 food sat in
        // the keep. Houses in the red always get stocked: life
        // outranks everything.
        var perResidentDaily = Sim.Core.Time.Day
            / Sim.Core.Food.FoodConsumptionConstants.FoodConsumptionPeriod
            * Sim.Core.Food.FoodConsumptionConstants.FoodPerCitizenPerPeriod;
        var graceDays = (int)Math.Max(1,
            Sim.Core.Food.FoodConsumptionConstants.StarvationStartDelay / Sim.Core.Time.Day);
        var radius = Sim.Core.Food.FoodConsumptionConstants.HomeAssignRadius;
        var cache = StructureCatalog.Spec(StructureKind.House).StorageCapacity;
        var houses = ctx.Own.Where(s => (StructureKind)s.Kind == StructureKind.House).ToList();
        var castleResidents = Math.Max(0, ctx.View.Population - houses.Sum(h => h.Residents));
        var castleKeep = castleResidents * perResidentDaily * graceDays;
        var claimed = new Dictionary<(int X, int Y), int>();
        int Cheb(StructDto a, StructDto b) =>
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        foreach (var house in houses)
        {
            var target = Math.Min(cache,
                house.Residents * perResidentDaily * graceDays + ctx.Cfg.BirthFoodCost);
            if (house.LocalFood >= target) continue;
            var source = ctx.OwnExtractors()
                .Where(e => StructureCatalog.Spec((StructureKind)e.Kind).OutputResource == Resource.Food)
                .Where(e => Cheb(e, house) <= radius)
                .Where(e => ThinkContext.AmountOf(e.Holdings, Resource.Food)
                    - claimed.GetValueOrDefault((e.X, e.Y)) > 0)
                .OrderBy(e => Cheb(e, house)).ThenBy(e => e.Y).ThenBy(e => e.X)
                .FirstOrDefault();
            TileCoord sourceTile;
            if (source is not null)
                sourceTile = ThinkContext.TileOf(source);
            else if (ThinkContext.AmountOf(ctx.Castle!.Holdings, Resource.Food) > 0
                     && (house.LocalFood <= 0 || ctx.View.CastleFood > castleKeep))
                sourceTile = ctx.CastleTile;   // castle keeps its own grace share
            else
                continue;
            if (ctx.TakeIdleCarrier() is not { } stocker) return intents;
            intents.Add(new HaulIntent(stocker.Id, sourceTile, ThinkContext.TileOf(house),
                Resource.Food) { PlayerId = ctx.PlayerId });
            if (source is not null)
                claimed[(source.X, source.Y)] = claimed.GetValueOrDefault((source.X, source.Y))
                    + UnitCargoCatalog.CapacityFor((UnitRole)stocker.Role);
        }

        // Full (or famine-urgent) extractor buffers go home — minus
        // whatever this think's house stocking already claimed.
        foreach (var ex in ctx.OwnExtractors())
        {
            var spec = StructureCatalog.Spec((StructureKind)ex.Kind);
            if (spec.OutputResource == Resource.None) continue;
            // Enough in the warehouse? Leave the rest in the buffer — the
            // extractor will idle at cap (and stop degrading its claims)
            // until a build spends the stock down. Food is never capped:
            // it's the consumable.
            if (spec.OutputResource != Resource.Food
                && ThinkContext.AmountOf(ctx.Castle!.Holdings, spec.OutputResource) >= ctx.Cfg.ResourceStockTarget)
                continue;
            var remaining = ThinkContext.AmountOf(ex.Holdings, spec.OutputResource)
                - claimed.GetValueOrDefault((ex.X, ex.Y));
            // Food gets PULL below the growth floor: smaller, more frequent
            // trips realize the farm's full rate instead of letting it idle
            // buffer-capped between threshold crossings (trips are minutes;
            // the income difference decided whether the first house went up
            // before or after the founders' fertility window).
            var hungry = spec.OutputResource == Resource.Food
                && (urgent || ctx.View.CastleFood < ctx.Cfg.GrowthFoodFloor);
            var floor = hungry ? Math.Min(8, ctx.Cfg.HaulBufferThreshold) : ctx.Cfg.HaulBufferThreshold;
            for (var trips = 0; trips < 4; trips++)
            {
                var wanted = urgent && spec.OutputResource == Resource.Food
                    ? remaining > 0
                    : remaining >= floor;
                if (!wanted) break;
                if (ctx.TakeIdleCarrier() is not { } carrier) return intents;
                intents.Add(new HaulIntent(carrier.Id, ThinkContext.TileOf(ex), ctx.CastleTile,
                    spec.OutputResource) { PlayerId = ctx.PlayerId });
                remaining -= UnitCargoCatalog.CapacityFor((UnitRole)carrier.Role);
            }
        }

        // ARMOURY FEED LINES (2026-09-19, ForgeRung/ArmRung) — the lowest
        // priority hauls: food and construction always claim carriers
        // first. Three lines, one delivery in flight per destination (the
        // site rule — a 25-load over a 20-target strands cargo):
        //   castle -> smelter  (Ore + Wood inputs, below ArmFeedFloor)
        //   smelter -> smithy  (Iron output; castle when no smithy stands)
        //   castle -> smithy   (Wood / Stone / Iron working stock)
        // The castle keeps ArmCastleReserve of wood and stone back —
        // hafts and fuel never outbid a farm's ten planks (lesson #10).
        if (ctx.Cfg.Arm || ctx.Cfg.Carts)
            FeedArmoury(ctx, intents);

        return intents;
    }

    private static void FeedArmoury(ThinkContext ctx, List<Intent> intents)
    {
        var castle = ctx.Castle!;
        var smithy = ctx.Cfg.Arm ? ctx.OwnStructure(StructureKind.Smithy) : null;
        var swordSpec = Sim.Core.Equipment.EquipmentCatalog.Spec(Resource.BronzeSword);
        var ironPerSword = swordSpec.CraftCost.TryGetValue(Resource.Bronze, out var ips) ? ips : 0;

        bool InFlightTo(TileCoord dest) => ctx.OwnUnits.Any(u =>
            u.DestX == dest.X && u.DestY == dest.Y
            && (u.Activity == (int)Activity.Hauling || u.CargoAmount > 0));
        // What the castle can spare of `r`: everything for ore/iron, the
        // reserve held back for construction's wood and stone.
        int Spare(Resource r)
        {
            var held = ThinkContext.AmountOf(castle.Holdings, r);
            return r is Resource.Wood or Resource.Stone ? held - ctx.Cfg.ArmCastleReserve : held;
        }
        bool Haul(TileCoord from, TileCoord to, Resource r)
        {
            if (ctx.TakeIdleCarrier() is not { } carrier) return false;
            intents.Add(new HaulIntent(carrier.Id, from, to, r) { PlayerId = ctx.PlayerId });
            return true;
        }

        foreach (var furnace in ctx.Own.Where(s => ctx.Cfg.Arm && (StructureKind)s.Kind == StructureKind.Smelter)
                     .OrderBy(s => s.Y).ThenBy(s => s.X))
        {
            // M51 — the AI smelts bronze only (docs/m51-ore-tiers-spec.md): it mines
            // copper and arms with bronze swords; climbing the ladder is a follow-up.
            var recipe = StructureCatalog.Spec(StructureKind.Smelter).Recipes.Single(r => r.Output == Resource.Bronze);
            var tile = ThinkContext.TileOf(furnace);
            // Bars out first (frees the buffer, so the furnace never idles at cap).
            var iron = ThinkContext.AmountOf(furnace.Holdings, recipe.Output);
            var dest = smithy is not null ? ThinkContext.TileOf(smithy) : ctx.CastleTile;
            var smithyShort = smithy is not null
                && ThinkContext.AmountOf(smithy.Holdings, Resource.Bronze) < ironPerSword;
            if ((iron >= ctx.Cfg.HaulBufferThreshold || (smithyShort && iron >= ironPerSword))
                && !InFlightTo(dest)
                && !Haul(tile, dest, recipe.Output)) return;
            // Inputs in: one per think, the recipe line that covers the
            // FEWEST batches first (ore is 2 a batch, fuel 1 — a furnace
            // with 20 wood and no ore is starving, not half-fed).
            if (InFlightTo(tile)) continue;
            var shortest = recipe.Inputs
                .Where(kv => ThinkContext.AmountOf(furnace.Holdings, kv.Key) < ctx.Cfg.ArmFeedFloor)
                .Where(kv => Spare(kv.Key) > 0)
                .OrderBy(kv => ThinkContext.AmountOf(furnace.Holdings, kv.Key) / kv.Value)
                .ThenBy(kv => kv.Key)
                .Select(kv => (Resource?)kv.Key)
                .FirstOrDefault();
            if (shortest is { } input && !Haul(ctx.CastleTile, tile, input)) return;
        }

        // Craft stores' working stock: the smithy (iron, wood, stone) and —
        // CartRung — the workshop (wood, stone). One delivery in flight
        // per store; the lowest line first.
        var stores = new List<(StructDto Store, Resource[] Lines)>();
        if (smithy is not null)
            stores.Add((smithy, new[] { Resource.Bronze, Resource.Wood, Resource.Stone }));
        if (ctx.Cfg.Carts && ctx.OwnStructure(StructureKind.Workshop) is { } workshop)
            stores.Add((workshop, new[] { Resource.Wood, Resource.Stone }));
        foreach (var (store, lines) in stores)
        {
            var storeTile = ThinkContext.TileOf(store);
            if (InFlightTo(storeTile)) continue;
            // Fill to the TARGET, never less than the store's costliest
            // recipe for that line: a 5-load citizen topping a workshop to
            // "half of twenty" left it at ten wood forever against a
            // twenty-wood cart (the first cart lab forged nothing).
            var line = lines
                .Where(r => ThinkContext.AmountOf(store.Holdings, r) < StockTarget(ctx, (StructureKind)store.Kind, r))
                .Where(r => Spare(r) > 0)
                .OrderBy(r => ThinkContext.AmountOf(store.Holdings, r)).ThenBy(r => r)
                .Select(r => (Resource?)r).FirstOrDefault();
            if (line is { } r && !Haul(ctx.CastleTile, storeTile, r)) return;
        }
    }

    private static int StockTarget(ThinkContext ctx, StructureKind store, Resource line)
    {
        var target = ctx.Cfg.ArmSmithyStockTarget;
        foreach (var item in new[] { Resource.BronzeSword, Resource.Bow, Resource.Shield, Resource.Cart })
        {
            var spec = Sim.Core.Equipment.EquipmentCatalog.Spec(item);
            if (spec.CraftedAt == store && spec.CraftCost.TryGetValue(line, out var need) && need > target)
                target = need;
        }
        return target;
    }
}
