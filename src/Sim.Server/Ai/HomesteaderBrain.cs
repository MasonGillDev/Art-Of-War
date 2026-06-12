using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Population;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server.Ai;

// M17 — the Homesteader: a peaceful economy brain (docs/m17-ai-players-spec.md).
//
// FAIRNESS IS THE SIGNATURE: Think takes the projected ViewDto — the same
// fog-filtered payload a human client renders — plus the clock and its
// own memory. It can never reference GameWorld or Simulation (pinned by
// AiPlayerTests.Brain_TouchesOnlyTheView). It MAY read Sim.Core catalogs
// and enums: those are game RULES (a human knows build costs from the
// UI), not world state.
//
// TWO LAYERS PER THINK (the first arbitration lesson, learned the
// designed way — the decision trace showed the AI hauling food while its
// camp site sat at zero builders):
//
//   * LOGISTICS — hauls are BACKGROUND, not decisions. Every think, every
//     needed haul (full buffers home, site deliveries, house stocking)
//     gets its own idle carrier. Hauling competes for carriers, never for
//     the think. Builders are NOT carriers — conscripting them as food
//     mules was exactly how the camp site starved.
//   * THE STRATEGIC LADDER — strict priority, first rung that emits
//     claims the think: Eat (farm placed/built/staffed) → Build (camp) →
//     Grow (house, breeding) → Scout. Rungs with nothing to DO fall
//     through, so an in-progress goal never starves the rungs below.
//     The thresholds that decide when a rung fires live in AiConfig —
//     they ARE the arbitration.
//
// OBSERVATION-DRIVEN: progress is read from the next view (the site
// exists, the buffer fell, the unit arrived), never from remembered
// promises — a restarted server re-derives every goal. AiMemory holds
// droppable hints only (scout rotation, rejected-site blacklist).
public sealed class HomesteaderBrain
{
    public sealed record Decision(string Rung, string Why, List<Intent> Intents);

    private readonly AiConfig _cfg;
    public HomesteaderBrain(AiConfig cfg) { _cfg = cfg; }

    public Decision Think(ViewDto view, long now, AiMemory mem)
    {
        // Site-placement feedback by OBSERVATION (the brain can't see
        // rejection notices): we ordered a site last think and the view
        // shows nothing at that tile → the placement was rejected
        // (insufficient claimable land, contested tile, …). Blacklist the
        // tile so NearestFreeTile offers the next candidate. MUST run
        // BEFORE the digest is built — the digest snapshots the blacklist,
        // and updating it afterwards made every rejected tile get retried
        // exactly once (off-by-one-think, seen live as doubled PlaceSite
        // attempts).
        if (mem.PendingSite is { } pending && now > pending.OrderedAt)
        {
            var occupied = view.Structures.Any(s => s.X == pending.Tile.X && s.Y == pending.Tile.Y);
            if (!occupied) mem.BlacklistedTiles.Add((pending.Tile.X, pending.Tile.Y));
            mem.PendingSite = null;
        }

        var d = Digest.Build(view, _cfg, mem);
        if (d.Castle is null) return new Decision("dead", "no castle", new List<Intent>());

        // STRATEGIC FIRST: decisions (place/staff/breed/scout) reserve
        // their units before logistics swarms the rest. The other order
        // let the haul swarm take every idle unit every think — the camp
        // sat unstaffed for 29 days while food piled up. Arbitration
        // lesson #5: priority isn't just rung order, it's who gets to
        // reserve people first.
        var strategic = Eat(d, view, mem, now)
            ?? Build(d, mem)
            ?? Train(d, view, mem)
            ?? Grow(d, view, mem)
            ?? Scout(d, mem);

        var intents = new List<Intent>();
        if (strategic is not null) intents.AddRange(strategic.Intents);
        var hauls = Logistics(d, view);
        intents.AddRange(hauls);

        var rung = strategic?.Rung ?? (hauls.Count > 0 ? "logistics" : "idle");
        var why = strategic?.Why ?? (hauls.Count > 0 ? $"{hauls.Count} haul(s)" : "all needs met");
        if (strategic is not null && hauls.Count > 0) why += $" (+{hauls.Count} haul)";

        foreach (var intent in intents)
            if (intent is PlaceSiteIntent p)
                mem.PendingSite = (p.Tile, now);
        return new Decision(rung, why, intents);
    }

    // ---- background: logistics — every needed haul, distinct carriers ----

    private List<Intent> Logistics(Digest d, ViewDto view)
    {
        var intents = new List<Intent>();
        var urgent = view.InFamine
            || (view.FoodRunwayTicks >= 0 && view.FoodRunwayTicks < _cfg.FoodRunwayFloorTicks);

        // Recovery: a laden idle unit is a deadlock — every selector
        // requires empty cargo, so leftover cargo (a haul that over-picked
        // for a small site need) turns a citizen into a zombie carrying
        // the faction's wood. Walk them home and unload into the castle.
        foreach (var u in d.OwnUnits.Where(u =>
                     d.IsIdleStill(u) && d.IsFree(u) && u.CargoAmount > 0))
        {
            d.Reserve(u);
            intents.Add(u.X == d.CastleTile.X && u.Y == d.CastleTile.Y
                ? new UnloadCargoIntent(u.Id) { PlayerId = d.PlayerId }
                : new MoveIntent(u.Id, d.CastleTile) { PlayerId = d.PlayerId });
        }

        // Site deliveries — construction unblocks everything else. ONE
        // delivery in flight per site: a 25-capacity carrier over-fills a
        // 10-wood need, so racing three of them strands two with stuck
        // cargo (the zombie bug above). A delivery is "in flight" when any
        // own laden-or-hauling unit is heading for the site tile.
        foreach (var site in d.OwnSites())
        {
            var siteTile = Digest.TileOf(site);
            var inFlight = d.OwnUnits.Any(u =>
                u.DestX == siteTile.X && u.DestY == siteTile.Y
                && (u.Activity == (int)Activity.Hauling || u.CargoAmount > 0));
            if (inFlight) continue;
            foreach (var need in site.Needed)
            {
                var missing = need.Amount - Digest.AmountOf(site.Holdings, (Resource)need.Resource);
                if (missing <= 0) continue;
                if (Digest.AmountOf(d.Castle!.Holdings, (Resource)need.Resource) <= 0) continue;
                if (d.TakeIdleCarrier() is not { } carrier) return intents;
                intents.Add(new HaulIntent(carrier.Id, d.CastleTile, siteTile,
                    (Resource)need.Resource) { PlayerId = d.PlayerId });
                break;
            }
        }

        // Full (or famine-urgent) extractor buffers go home. SWARM the
        // buffer: keep allocating carriers until their combined capacity
        // covers it — most citizens carry only 5 (UnitCargoCatalog, a
        // rule, not state), and a single small carrier per think left the
        // farm dormant-at-cap for whole days while food flatlined at the
        // famine line.
        foreach (var ex in d.OwnExtractors())
        {
            var spec = StructureCatalog.Spec((StructureKind)ex.Kind);
            if (spec.OutputResource == Resource.None) continue;
            // Enough in the warehouse? Leave the rest in the buffer — the
            // extractor will idle at cap (and stop degrading its claims)
            // until a build spends the stock down. Food is never capped:
            // it's the consumable.
            if (spec.OutputResource != Resource.Food
                && Digest.AmountOf(d.Castle!.Holdings, spec.OutputResource) >= _cfg.ResourceStockTarget)
                continue;
            var remaining = Digest.AmountOf(ex.Holdings, spec.OutputResource);
            // Food gets PULL below the growth floor: smaller, more frequent
            // trips realize the farm's full rate instead of letting it idle
            // buffer-capped between threshold crossings (trips are minutes;
            // the income difference decided whether the first house went up
            // before or after the founders' fertility window).
            var hungry = spec.OutputResource == Resource.Food
                && (urgent || view.CastleFood < _cfg.GrowthFoodFloor);
            var floor = hungry ? Math.Min(8, _cfg.HaulBufferThreshold) : _cfg.HaulBufferThreshold;
            for (var trips = 0; trips < 4; trips++)
            {
                var wanted = urgent && spec.OutputResource == Resource.Food
                    ? remaining > 0
                    : remaining >= floor;
                if (!wanted) break;
                if (d.TakeIdleCarrier() is not { } carrier) return intents;
                intents.Add(new HaulIntent(carrier.Id, Digest.TileOf(ex), d.CastleTile,
                    spec.OutputResource) { PlayerId = d.PlayerId });
                remaining -= UnitCargoCatalog.CapacityFor((UnitRole)carrier.Role);
            }
        }

        // Keep EVERY house stocked for births while the larder is
        // comfortable — houses are pregnancy slots and an unstocked slot
        // is an idle one.
        if (view.CastleFood > _cfg.GrowthFoodFloor)
            foreach (var house in d.Own.Where(s => (StructureKind)s.Kind == StructureKind.House))
            {
                if (Digest.AmountOf(house.Holdings, Resource.Food) >= _cfg.BirthFoodCost) continue;
                if (d.TakeIdleCarrier() is not { } stocker) return intents;
                intents.Add(new HaulIntent(stocker.Id, d.CastleTile, Digest.TileOf(house),
                    Resource.Food) { PlayerId = d.PlayerId });
            }

        return intents;
    }

    // ---- rung 1: Eat — enough working farms for the mouths ----------------

    // One FARMHAND's daily output, from the CATALOG (rules, not state):
    // BaseRate × role bonus × periods per day. The planner budgets
    // WORKERS, not farms — farms are just buildings that hold workers.
    private static long FarmhandDailyOutput()
    {
        var spec = StructureCatalog.Spec(StructureKind.Farm);
        return (long)spec.BaseRatePerWorker
            * spec.RoleBonusNumerator / spec.RoleBonusDenominator
            * (Sim.Core.Time.Day / spec.ProductionPeriodTicks);
    }

    // THE LABOR LEDGER, shared by Eat (how many hands can the colony
    // field?) and Grow (can it afford another mouth?). Pool = adults in
    // general roles (Builders build, Haulers haul, Scouts scout — fixed
    // jobs), not designated as parents. Hands = farmhands demand asks
    // for, capped at pool − 3 (one camp worker, two kept breedable).
    // Returns (pool, fieldable hands, hands DEMAND requires). The last is
    // uncapped — Grow's carrying-capacity gate must compare the pool
    // against what the mouths genuinely need, not against the capped
    // number (capped-vs-pool is ≥3 by construction — a gate that can
    // never close, which is how the first version failed).
    //
    // HONEST PRICING: only Farmer-role hands earn the 2:1 bonus; a
    // generalist produces the base rate. The first ledger priced every
    // hand as a farmer and overstated supply ~40% — the colony rode the
    // resulting knife-edge into the cascade. (Role TRAINING at a School
    // is the real fix for the scarcity this exposes — next brain phase.)
    private (int Pool, int Hands, int HandsDemanded) LaborLedger(Digest d, ViewDto view)
    {
        var spec = StructureCatalog.Spec(StructureKind.Farm);
        var periodsPerDay = Sim.Core.Time.Day / spec.ProductionPeriodTicks;
        var farmerDaily = (long)spec.BaseRatePerWorker * periodsPerDay
            * spec.RoleBonusNumerator / spec.RoleBonusDenominator;
        var generalDaily = (long)spec.BaseRatePerWorker * periodsPerDay;

        var dailyDemand = (long)view.Population
            * Sim.Core.Food.FoodConsumptionConstants.FoodPerCitizenPerPeriod
            * (Sim.Core.Time.Day / Sim.Core.Food.FoodConsumptionConstants.FoodConsumptionPeriod);
        var required = dailyDemand * _cfg.FarmHeadroomPercent / 100;

        // IsFreeOrDesignated, not IsFree: designated parents are still the
        // colony's labor in the census sense (counting them as missing
        // deadlocked the gate). Scouts COUNT — exploration has a budget
        // (ScoutLegBudget), then they work. Builders/Haulers keep their
        // fixed jobs.
        bool InPool(UnitDto u) => u.Age >= _cfg.MinAdultAgeYears
            && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler)
            && d.IsFreeOrDesignated(u);
        var farmers = d.OwnUnits.Count(u => InPool(u) && (UnitRole)u.Role == UnitRole.Farmer);
        var pool = d.OwnUnits.Count(InPool);

        // Cover demand with farmer-hands first (they're worth double),
        // generalists for the remainder.
        var farmerHands = (int)Math.Min(farmers, (required + farmerDaily - 1) / farmerDaily);
        var rest = required - (long)farmerHands * farmerDaily;
        var generalHands = rest > 0 ? (int)((rest + generalDaily - 1) / generalDaily) : 0;
        var demanded = Math.Max(1, farmerHands + generalHands);

        return (pool, Math.Min(demanded, Math.Max(1, pool - 3)), demanded);
    }

    private Decision? Eat(Digest d, ViewDto view, AiMemory mem, long now)
    {
        var farms = d.Own.Where(s => (StructureKind)s.Kind == StructureKind.Farm)
            .OrderBy(s => s.Y).ThenBy(s => s.X).ToList();
        var sites = d.Own.Where(s => (StructureKind)s.Kind == StructureKind.ConstructionSite
            && (StructureKind)s.TargetKind == StructureKind.Farm).ToList();

        // DEATH DETECTION — the rotation trigger (shared with Build's
        // camps; see DetectExhausted). Dead farms release their crew and
        // stop counting as supply.
        if (DetectExhausted(d, mem, farms, Resource.Food) is { } release)
            return new Decision("eat", "farm exhausted — releasing the crew", release);
        // Age accounting: remember when each farm first appeared; one
        // within ReplaceAhead of its working lifetime is DYING — still
        // staffed and producing, but no longer counted as future supply,
        // so its replacement starts before the cliff instead of after.
        foreach (var farm in farms)
            mem.FirstSeen.TryAdd((farm.X, farm.Y), now);
        bool Dying(StructDto f) =>
            now >= mem.FirstSeen.GetValueOrDefault((f.X, f.Y), now)
                + _cfg.FarmLifetimeTicks - _cfg.FarmReplaceAheadTicks;
        var liveFarms = farms.Where(f => !mem.DeadExtractors.Contains((f.X, f.Y))).ToList();
        var countedFarms = liveFarms.Count(f => !Dying(f));

        // CAPACITY PLANNING — the sprawl driver, in WORKERS not farms:
        // the ledger says how many farmhands the colony needs AND can
        // field; farms exist to hold them. (Staffing to cap consumed the
        // whole workforce; ignoring the adult pool drafted everyone and
        // killed logistics — both extinction events in the lab's log.)
        var spec = StructureCatalog.Spec(StructureKind.Farm);
        var (_, handsNeeded, _) = LaborLedger(d, view);
        var farmsNeeded = (handsNeeded + spec.WorkerCap - 1) / spec.WorkerCap;
        var urgent = view.InFamine
            || (view.FoodRunwayTicks >= 0 && view.FoodRunwayTicks < _cfg.FoodRunwayFloorTicks);
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
        mem.LandStarved =
            d.CountFreeTiles(Biome.Grassland, _cfg.SiteSearchRange, _cfg.LandBankFloorTiles)
                < _cfg.LandBankFloorTiles
            || mem.ForestStarved;   // Build's distress flag — don't clobber it

        if (wantAnother)
        {
            if (d.NearestFreeTile(Biome.Grassland, _cfg.SiteSearchRange) is { } t)
                return new Decision("eat",
                    $"live farms {liveFarms.Count}+{sites.Count} of {farmsNeeded} needed — placing one",
                    new List<Intent> { new PlaceSiteIntent(t, StructureKind.Farm) { PlayerId = d.PlayerId } });
            if (liveFarms.Count == 0 && sites.Count == 0) return null;
        }
        foreach (var site in sites)
            if (EnsureBuilders(d, site) is { Count: > 0 } b)
                return new Decision("eat", "building a farm", b);

        // Staff against the budget: fill farms in order until the hands
        // run out — the LAST farm is usually partial, and that's correct.
        var hands = handsNeeded - liveFarms.Sum(f => f.Workers);
        foreach (var farm in liveFarms)
        {
            if (hands <= 0) break;
            var target = Math.Min(farm.WorkerCap, farm.Workers + hands);
            if (StaffExtractor(d, farm, UnitRole.Farmer, target) is { Count: > 0 } st)
                return new Decision("eat", $"staffing a farm toward {handsNeeded} hands", st);
            hands -= Math.Max(0, target - farm.Workers);
        }
        return null;
    }

    // ---- rung 2: Build — a LIVE wood income exists --------------------------

    private Decision? Build(Digest d, AiMemory mem)
    {
        var camps = d.Own.Where(s => (StructureKind)s.Kind == StructureKind.LumberCamp)
            .OrderBy(s => s.Y).ThenBy(s => s.X).ToList();
        var sites = d.Own.Where(s => (StructureKind)s.Kind == StructureKind.ConstructionSite
            && (StructureKind)s.TargetKind == StructureKind.LumberCamp).ToList();

        // Camps die too — forest claims exhaust in ~52 days (DegradeAmount
        // 2). Without rotation the lab's training-boosted colony hit wood
        // ZERO at day 200 and froze at 17 farms while breeding ran to pop
        // 159: the whole construction economy hung off one dead camp.
        if (DetectExhausted(d, mem, camps, Resource.Wood) is { } release)
            return new Decision("build", "camp exhausted — releasing the crew", release);
        var liveCamps = camps.Where(c => !mem.DeadExtractors.Contains((c.X, c.Y))).ToList();

        if (liveCamps.Count + sites.Count < 1)
        {
            if (d.NearestFreeTile(Biome.Forest, _cfg.SiteSearchRange) is { } t)
            {
                mem.ForestStarved = false;
                return new Decision("build", "no live lumber camp — placing one",
                    new List<Intent> { new PlaceSiteIntent(t, StructureKind.LumberCamp) { PlayerId = d.PlayerId } });
            }
            // No known forest: starve the scouts back out (they reveal
            // everything, grassland and forest alike).
            mem.ForestStarved = true;
            return null;
        }
        mem.ForestStarved = false;
        foreach (var site in sites)
            if (EnsureBuilders(d, site) is { Count: > 0 } b)
                return new Decision("build", "building a camp", b);
        foreach (var camp in liveCamps)
            if (StaffExtractor(d, camp, UnitRole.Lumberjack) is { Count: > 0 } st)
                return new Decision("build", "staffing the camp", st);
        return null;
    }

    // A staffed extractor whose buffer reads zero for 12 consecutive
    // thinks (≈12 game-hours) is claim-exhausted (M15) — a healthy one
    // rebuilds its buffer within hours of any haul. The brain can't see
    // dormancy in the view, but it can see this. Returns the crew-release
    // intents the moment an extractor is declared dead. The husk and its
    // claims stay (DemolishIntent is a known core-game deferral), but the
    // PEOPLE move on.
    private List<Intent>? DetectExhausted(Digest d, AiMemory mem,
        List<StructDto> extractors, Resource output)
    {
        foreach (var ex in extractors)
        {
            var key = (ex.X, ex.Y);
            if (mem.DeadExtractors.Contains(key)) continue;
            var starvedBuffer = Digest.AmountOf(ex.Holdings, output) == 0 && ex.Workers > 0;
            mem.ZeroBufferThinks[key] = starvedBuffer ? mem.ZeroBufferThinks.GetValueOrDefault(key) + 1 : 0;
            if (mem.ZeroBufferThinks[key] < 12) continue;
            mem.DeadExtractors.Add(key);
            var crew = d.OwnUnits.Where(u =>
                    u.X == ex.X && u.Y == ex.Y && u.Activity == (int)Activity.Working)
                .Select(u => u.Id).ToList();
            if (crew.Count > 0)
                return new List<Intent> { new UnassignWorkersIntent(new TileCoord(ex.X, ex.Y), crew)
                    { PlayerId = d.PlayerId } };
        }
        return null;
    }

    // ---- rung 3: Train — a Farmer is worth two generalists ----------------

    // The labor ledger prices a generalist farmhand at HALF a Farmer's
    // output (the 2:1 role bonus) — the measured ~40% supply gap that
    // rode two lab colonies into the cascade. Training is the relief
    // valve: natives are born Role=None (blank apprentices), the School
    // is instant and free once built, and every retrained Farmer doubles
    // a hand. Sits ABOVE Grow: multiplying food beats adding mouths.
    private Decision? Train(Digest d, ViewDto view, AiMemory mem)
    {
        // Prune: trainee graduated, died, or vanished.
        if (mem.DesignatedTrainee is { } tid)
        {
            var t = d.OwnUnits.FirstOrDefault(u => u.Id == tid);
            if (t is null || (UnitRole)t.Role == UnitRole.Farmer)
                mem.DesignatedTrainee = null;
        }

        var (pool, _, handsDemanded) = LaborLedger(d, view);
        var farmers = d.OwnUnits.Count(u =>
            (UnitRole)u.Role == UnitRole.Farmer && u.Age >= _cfg.MinAdultAgeYears);
        // Enough Farmers to cover every hand the colony can field? Done.
        if (farmers >= Math.Min(handsDemanded, Math.Max(1, pool - 3))) return null;

        var school = d.OwnStructure(StructureKind.School);
        var site = d.OwnSite(StructureKind.School);
        if (school is null && site is null)
        {
            if (d.NearestFreeTile(requiredBiome: null, _cfg.SiteSearchRange) is { } t)
                return new Decision("train", "no school — placing one",
                    new List<Intent> { new PlaceSiteIntent(t, StructureKind.School) { PlayerId = d.PlayerId } });
            return null;
        }
        if (site is not null && EnsureBuilders(d, site) is { Count: > 0 } b)
            return new Decision("train", "building the school", b);
        if (school is null) return null;
        var schoolTile = Digest.TileOf(school);

        // Designate an apprentice (ownership across thinks — same lesson
        // as parents): natives (Role None) first, then off-role
        // generalists; never Builders/Haulers/Scouts/Farmers.
        if (mem.DesignatedTrainee is null)
        {
            var cand = d.OwnUnits.Where(u => d.IsIdleStill(u) && d.IsFree(u)
                    && u.CargoAmount == 0 && u.Age >= _cfg.MinAdultAgeYears
                    && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler
                        or UnitRole.Scout or UnitRole.Farmer))
                .OrderBy(u => (UnitRole)u.Role == UnitRole.None ? 0 : 1).ThenBy(u => u.Id)
                .FirstOrDefault();
            if (cand is null) return null;
            mem.DesignatedTrainee = cand.Id;
        }
        var trainee = d.OwnUnits.First(u => u.Id == mem.DesignatedTrainee);
        if (trainee.X == schoolTile.X && trainee.Y == schoolTile.Y && d.IsIdleStill(trainee))
            return new Decision("train", "graduating a farmer",
                new List<Intent> { new TrainUnitIntent(trainee.Id, UnitRole.Farmer) { PlayerId = d.PlayerId } });
        if (d.IsIdleStill(trainee))
            return new Decision("train", "apprentice walking to school",
                new List<Intent> { new MoveIntent(trainee.Id, schoolTile) { PlayerId = d.PlayerId } });
        return null;   // mid-march
    }

    // ---- rung 4: Grow — breed when the larder allows -----------------------

    private Decision? Grow(Digest d, ViewDto view, AiMemory mem)
    {
        if (view.CastleFood <= _cfg.GrowthFoodFloor) return null;

        bool Fertile(UnitDto u) =>
            u.Age >= _cfg.MinFertileAgeYears && u.Age <= _cfg.MaxFertileAgeYears;

        // HOUSES SCALE WITH THE FERTILE POPULATION — a house is a
        // pregnancy SLOT, and one slot caps the colony at one birth per
        // gestation cycle no matter how the demographic knobs are tuned
        // (the lab's TicksPerYear runs were measuring this ceiling, not
        // the clock). Pregnancies overlap across houses; pair setup
        // stays serial through the single designation.
        var houses = d.Own.Where(s => (StructureKind)s.Kind == StructureKind.House)
            .OrderBy(s => s.Y).ThenBy(s => s.X).ToList();
        var houseSites = d.Own.Where(s => (StructureKind)s.Kind == StructureKind.ConstructionSite
            && (StructureKind)s.TargetKind == StructureKind.House).ToList();
        var fertileAdults = d.OwnUnits.Count(u => Fertile(u)
            && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler));
        var housesNeeded = Math.Max(1, fertileAdults / Math.Max(1, _cfg.FertileAdultsPerHouse));

        if (houses.Count + houseSites.Count < housesNeeded
            && d.NearestFreeTile(requiredBiome: null, _cfg.SiteSearchRange) is { } t)
            return new Decision("grow",
                $"houses {houses.Count}+{houseSites.Count} of {housesNeeded} — placing one",
                new List<Intent> { new PlaceSiteIntent(t, StructureKind.House) { PlayerId = d.PlayerId } });
        foreach (var hs in houseSites)
            if (EnsureBuilders(d, hs) is { Count: > 0 } b)
                return new Decision("grow", "building a house", b);
        if (houses.Count == 0) return null;

        // Target: the first house that is VACANT (no pregnancy — the
        // occupation signature is two own units Working on the tile) and
        // STOCKED for a birth. None ready → logistics is stocking, or
        // every slot is mid-pregnancy.
        bool Vacant(StructDto h) => d.OwnUnits.Count(u =>
            u.X == h.X && u.Y == h.Y && u.Activity == (int)Activity.Working) < 2;
        var target = houses.FirstOrDefault(h => Vacant(h)
            && Digest.AmountOf(h.Holdings, Resource.Food) >= _cfg.BirthFoodCost);
        if (target is null) return null;
        var houseTile = Digest.TileOf(target);

        // CARRYING CAPACITY — don't breed mouths the workforce can't
        // feed. A child eats for 13 years before it can work; the lab
        // watched unthrottled breeding outrun the adult labor pool into
        // extinction twice. Growth resumes when natives come of age and
        // the pool widens — population rises in generational waves.
        var (pool, _, handsDemanded) = LaborLedger(d, view);
        if ((long)pool * 100 < (long)handsDemanded * _cfg.GrowthLaborSlackPercent) return null;

        // DESIGNATED PARENTS — ownership across thinks, not just within
        // one. A per-think reservation can't stop Eat from re-staffing a
        // recruit the think AFTER Grow freed them (the thrash loop the lab
        // caught: recruit → re-assign → recruit, one birth in 130 days).
        // Designation persists in memory until breeding starts; every
        // other selector skips designated units.
        mem.DesignatedParents.RemoveWhere(id =>
        {
            var u = d.OwnUnits.FirstOrDefault(x => x.Id == id);
            return u is null || !Fertile(u)
                || (u.Activity == (int)Activity.Working
                    && houses.Any(h => h.X == u.X && h.Y == u.Y));   // breeding started — job done
        });

        bool Eligible(UnitDto u) => Fertile(u) && d.IsFree(u)
            && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler)
            && !mem.DesignatedParents.Contains(u.Id);
        if (mem.DesignatedParents.Count < 2)
        {
            // Free idlers first, then conscript off the fields.
            foreach (var u in d.OwnUnits.Where(u => Eligible(u) && d.IsIdleStill(u)).OrderBy(u => u.Id))
            {
                if (mem.DesignatedParents.Count >= 2) break;
                mem.DesignatedParents.Add(u.Id);
            }
            foreach (var u in d.OwnUnits.Where(u => Eligible(u)
                         && u.Activity == (int)Activity.Working).OrderBy(u => u.Id))
            {
                if (mem.DesignatedParents.Count >= 2) break;
                mem.DesignatedParents.Add(u.Id);
            }
        }
        if (mem.DesignatedParents.Count < 2) return null;   // nobody fertile left — the cliff

        // March the designated pair through: unassign if working, walk if
        // away, breed when both stand idle on the house tile.
        var parents = d.OwnUnits.Where(u => mem.DesignatedParents.Contains(u.Id))
            .OrderBy(u => u.Id).ToList();
        var intents = new List<Intent>();
        foreach (var p in parents)
        {
            if (p.Activity == (int)Activity.Working)
            {
                var post = d.Own.FirstOrDefault(s => s.X == p.X && s.Y == p.Y);
                if (post is not null)
                    intents.Add(new UnassignWorkersIntent(new TileCoord(post.X, post.Y),
                        new[] { d.Reserve(p).Id }) { PlayerId = d.PlayerId });
            }
            else if (d.IsIdleStill(p) && (p.X != houseTile.X || p.Y != houseTile.Y))
                intents.Add(new MoveIntent(d.Reserve(p).Id, houseTile) { PlayerId = d.PlayerId });
        }
        if (intents.Count > 0)
            return new Decision("grow", "escorting designated parents", intents);

        if (parents.Count >= 2 && parents.All(p =>
                d.IsIdleStill(p) && p.X == houseTile.X && p.Y == houseTile.Y))
        {
            mem.DesignatedParents.Clear();   // re-derive after the birth
            return new Decision("grow", "parents in place — breeding",
                new List<Intent> { new BeginBreedingIntent(houseTile,
                    d.Reserve(parents[0]).Id, d.Reserve(parents[1]).Id) { PlayerId = d.PlayerId } });
        }
        return null;   // pair mid-march
    }

    // ---- rung 4: Scout — reveal the frontier (it's fogged) ------------------

    private Decision? Scout(Digest d, AiMemory mem)
    {
        // SCOUT-ROLE UNITS ONLY, and only within the exploration BUDGET —
        // unless the colony is LAND-STARVED, which re-opens scouting on
        // demand (a fixed lifetime budget left a 300-day colony starving
        // at pop 67 with the whole continent in the fog). Legs spiral
        // wider as the count grows, so renewed exploration pushes past
        // the already-known halo.
        if (mem.ScoutLeg >= _cfg.ScoutLegBudget && !mem.LandStarved) return null;
        var scout = d.OwnUnits.FirstOrDefault(u =>
            d.IsIdleStill(u) && d.IsFree(u) && (UnitRole)u.Role == UnitRole.Scout);
        if (scout is null) return null;
        d.Reserve(scout);

        // Rotate through 8 compass legs, each full sweep reaching farther
        // (leg length grows every 8 legs) — droppable memory; a restart
        // just restarts the sweep.
        var dirs = new (int X, int Y)[] { (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1) };
        var dir = dirs[mem.ScoutLeg % dirs.Length];
        var reach = _cfg.ScoutRange * (1 + mem.ScoutLeg / dirs.Length);
        mem.ScoutLeg++;
        var dest = new TileCoord(
            Math.Clamp(d.CastleTile.X + dir.X * reach, 0, d.MapWidth - 1),
            Math.Clamp(d.CastleTile.Y + dir.Y * reach, 0, d.MapHeight - 1));
        if (dest == new TileCoord(scout.X, scout.Y)) return null;
        return new Decision("scout", $"sweeping leg {mem.ScoutLeg - 1}",
            new List<Intent> { new MoveIntent(scout.Id, dest) { PlayerId = d.PlayerId } });
    }

    // ---- shared plays --------------------------------------------------------

    // Get the site its builders: assign the ones standing on it, march the
    // ones that aren't. (Materials are logistics' job.)
    private List<Intent> EnsureBuilders(Digest d, StructDto site)
    {
        var intents = new List<Intent>();
        if (site.BuildersPresent >= site.BuildersRequired) return intents;
        var siteTile = Digest.TileOf(site);
        var builders = d.OwnUnits.Where(u =>
                (UnitRole)u.Role == UnitRole.Builder && d.IsIdleStill(u) && d.IsFree(u)
                && u.CargoAmount == 0 && u.Age >= _cfg.MinAdultAgeYears)
            .OrderBy(u => u.Id).ToList();
        var onTile = builders.Where(u => u.X == siteTile.X && u.Y == siteTile.Y)
            .Select(u => d.Reserve(u).Id).ToList();
        if (onTile.Count > 0)
            intents.Add(new AssignBuildersIntent(siteTile, onTile) { PlayerId = d.PlayerId });
        else
            foreach (var b in builders.Take(site.BuildersRequired - site.BuildersPresent))
                intents.Add(new MoveIntent(d.Reserve(b).Id, siteTile) { PlayerId = d.PlayerId });
        return intents;
    }

    // Get an extractor staffed toward `target` workers (prefer the
    // matching role — the 2:1 rate bonus — falling back to any
    // non-builder, non-hauler adult). Farms are budgeted by the Eat
    // planner; everything else defaults to one worker — wood income
    // doesn't race demographics.
    private List<Intent> StaffExtractor(Digest d, StructDto ex, UnitRole prefer, int target = 1)
    {
        var intents = new List<Intent>();
        if (ex.Workers >= Math.Max(1, target)) return intents;

        var tile = Digest.TileOf(ex);
        // Builders belong on sites, Haulers on the road (their 25-capacity
        // is the logistics backbone — assigning one as a farmhand starves
        // the haul loop). Everyone else can work — including scouts past
        // their exploration budget. Matching role first, scouts last.
        var candidates = d.OwnUnits.Where(u =>
                d.IsIdleStill(u) && d.IsFree(u) && u.CargoAmount == 0
                && u.Age >= _cfg.MinAdultAgeYears
                && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler))
            .OrderBy(u => (UnitRole)u.Role == prefer ? 0
                : (UnitRole)u.Role == UnitRole.Scout ? 2 : 1)
            .ThenBy(u => u.Id)
            .ToList();
        var onTile = candidates.FirstOrDefault(u => u.X == tile.X && u.Y == tile.Y);
        if (onTile is not null)
            intents.Add(new AssignWorkersIntent(tile, new[] { d.Reserve(onTile).Id }) { PlayerId = d.PlayerId });
        else if (candidates.FirstOrDefault() is { } walker)
            intents.Add(new MoveIntent(d.Reserve(walker).Id, tile) { PlayerId = d.PlayerId });
        return intents;
    }

    // ---- the view digest ------------------------------------------------------

    private sealed class Digest
    {
        public int PlayerId;
        public int MapWidth, MapHeight;
        public StructDto? Castle;
        public TileCoord CastleTile;
        public List<StructDto> Own = new();
        public List<UnitDto> OwnUnits = new();
        private readonly Dictionary<(int X, int Y), int> _biome = new();
        private readonly HashSet<(int X, int Y)> _blocked = new();   // structures + claims + blacklist

        // Per-think unit reservation, shared by EVERY selector (carriers,
        // staffing, builders, parents, scouts). Without it, two layers
        // task the same unit in one think and move-on-busy silently
        // cancels the first order — the bug that starved the bootstrap
        // (food hauls yanked off carriers by scout moves).
        private readonly HashSet<int> _reserved = new();
        private HashSet<int> _designated = new();
        public bool IsFree(UnitDto u) =>
            !_reserved.Contains(u.Id) && !_designated.Contains(u.Id);
        public UnitDto Reserve(UnitDto u) { _reserved.Add(u.Id); return u; }
        // Grow is the ONLY caller allowed to act on designated units; it
        // checks designation explicitly rather than through IsFree.
        public bool IsFreeOrDesignated(UnitDto u) => !_reserved.Contains(u.Id);

        public static Digest Build(ViewDto view, AiConfig cfg, AiMemory mem)
        {
            var d = new Digest { PlayerId = view.PlayerId, MapWidth = view.Width, MapHeight = view.Height };
            d._designated = new HashSet<int>(mem.DesignatedParents);
            if (mem.DesignatedTrainee is { } trainee) d._designated.Add(trainee);
            foreach (var t in view.Visible) d._biome[(t.X, t.Y)] = t.Biome;
            foreach (var t in view.Remembered) d._biome.TryAdd((t.X, t.Y), t.Biome);
            foreach (var b in mem.BlacklistedTiles) d._blocked.Add(b);
            foreach (var s in view.Structures)
            {
                d._blocked.Add((s.X, s.Y));
                for (var i = 0; i < s.ClaimX.Length; i++) d._blocked.Add((s.ClaimX[i], s.ClaimY[i]));
                if (s.OwnerId != view.PlayerId) continue;
                d.Own.Add(s);
                if ((StructureKind)s.Kind == StructureKind.Castle)
                {
                    d.Castle = s;
                    d.CastleTile = new TileCoord(s.X, s.Y);
                }
            }
            d.OwnUnits = view.Units.Where(u => u.OwnerId == view.PlayerId).OrderBy(u => u.Id).ToList();
            return d;
        }

        public StructDto? OwnExtractor(StructureKind kind) =>
            Own.FirstOrDefault(s => (StructureKind)s.Kind == kind);
        public StructDto? OwnStructure(StructureKind kind) => OwnExtractor(kind);
        public StructDto? OwnSite(StructureKind target) =>
            Own.FirstOrDefault(s => (StructureKind)s.Kind == StructureKind.ConstructionSite
                && (StructureKind)s.TargetKind == target);
        public IEnumerable<StructDto> OwnSites() =>
            Own.Where(s => (StructureKind)s.Kind == StructureKind.ConstructionSite)
               .OrderBy(s => s.Y).ThenBy(s => s.X);
        public IEnumerable<StructDto> OwnExtractors() =>
            Own.Where(s => (StructureKind)s.Kind is StructureKind.Farm or StructureKind.LumberCamp
                or StructureKind.Quarry or StructureKind.Mine)
               .OrderBy(s => s.Y).ThenBy(s => s.X);

        // Idle AND standing still — a marching unit reads Activity.Idle
        // (movement lives on the arrival anchors), so "has no destination"
        // is the real stillness check. M16 lesson.
        public bool IsIdleStill(UnitDto u) => u.Activity == (int)Activity.Idle && u.DestX < 0;

        // Allocate a haul carrier for THIS think: idle, still, empty-handed,
        // never a Builder (conscripting builders as food mules is how the
        // camp site starved — they're needed on sites). Children may haul
        // (only role-tied assignments are age-gated); Haulers first for
        // the capacity.
        public UnitDto? TakeIdleCarrier()
        {
            var pick = OwnUnits.Where(u => IsIdleStill(u) && u.CargoAmount == 0
                    && (UnitRole)u.Role != UnitRole.Builder && IsFree(u))
                .OrderBy(u => (UnitRole)u.Role == UnitRole.Hauler ? 0 : 1)
                .ThenBy(u => u.Id)
                .FirstOrDefault();
            return pick is null ? null : Reserve(pick);
        }

        // Known claimable-land inventory: how many free tiles of `biome`
        // the colony KNOWS about within range, counting up to `cap` (the
        // land-bank check needs "fewer than N?", not the true total).
        public int CountFreeTiles(Biome biome, int range, int cap)
        {
            var count = 0;
            foreach (var (key, b) in _biome)
            {
                if (b != (int)biome) continue;
                if (Math.Max(Math.Abs(key.X - CastleTile.X), Math.Abs(key.Y - CastleTile.Y)) > range) continue;
                if (_blocked.Contains(key)) continue;
                if (++count >= cap) return count;
            }
            return count;
        }

        // Nearest known tile to the castle matching the biome requirement,
        // unoccupied and unclaimed — ring scan in (dist, y, x) order so the
        // choice is deterministic. requiredBiome null = any walkable land.
        public TileCoord? NearestFreeTile(Biome? requiredBiome, int range)
        {
            for (var r = 1; r <= range; r++)
            for (var dy = -r; dy <= r; dy++)
            for (var dx = -r; dx <= r; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                var key = (CastleTile.X + dx, CastleTile.Y + dy);
                if (!_biome.TryGetValue(key, out var biome)) continue;
                if (requiredBiome is { } req ? biome != (int)req
                    : biome is (int)Biome.Water or (int)Biome.None) continue;
                if (_blocked.Contains(key)) continue;
                return new TileCoord(key.Item1, key.Item2);
            }
            return null;
        }

        public static int AmountOf(ResAmtDto[] holdings, Resource r) =>
            holdings.FirstOrDefault(h => h.Resource == (int)r)?.Amount ?? 0;

        public static TileCoord TileOf(StructDto s) => new(s.X, s.Y);
    }
}

// Droppable hints only — everything else is re-derived from the view.
public sealed class AiMemory
{
    public int ScoutLeg;
    // Site order awaiting confirmation + tiles where placement was
    // observed to fail (rejected server-side; skip them next search).
    public (Sim.Core.World.TileCoord Tile, long OrderedAt)? PendingSite;
    public HashSet<(int X, int Y)> BlacklistedTiles { get; } = new();
    // Claim-exhaustion inference: consecutive zero-buffer observations per
    // staffed extractor; past the threshold the extractor is treated as
    // dead (crew released, dropped from supply counts). Droppable like the
    // rest — a restart re-observes for 12 thinks and re-concludes.
    public Dictionary<(int X, int Y), int> ZeroBufferThinks { get; } = new();
    public HashSet<(int X, int Y)> DeadExtractors { get; } = new();
    // Set when the known-land inventory drops below the bank floor —
    // re-opens scouting past its budget. ForestStarved is Build's
    // distress flag (no known forest for a replacement camp); Eat ORs it
    // into LandStarved each think.
    public bool LandStarved;
    public bool ForestStarved;
    // First think each extractor was observed — farm mortality accounting
    // (age ≈ working life; replacements pre-build before the cliff).
    public Dictionary<(int X, int Y), long> FirstSeen { get; } = new();
    // The apprentice en route to the School — same ownership rule as
    // parents (cleared when they graduate to Farmer or die).
    public int? DesignatedTrainee;
    // Cross-think OWNERSHIP of breeding candidates (arbitration lesson #6):
    // per-think reservations can't stop Eat from re-staffing a freed parent
    // the think after Grow freed them. Designation persists until the
    // breeding starts; every other selector skips designated units.
    public HashSet<int> DesignatedParents { get; } = new();
}
