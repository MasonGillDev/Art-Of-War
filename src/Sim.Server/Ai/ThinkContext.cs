using Sim.Core.Intents;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Population;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server.Ai;

// M17 Phase 2 (Phase 0 decomposition) — everything a rung sees in one
// place: the view DIGEST (own structures/units, the remembered biome
// map, the blocked-tile set), the per-think reservation ledger that
// keeps two rungs from tasking the same unit, and the SHARED PLAYS
// (labor ledger, builder/staffing moves, exhaustion detection) that
// more than one rung runs. One context per think, built fresh from the
// fog-filtered ViewDto — rungs hold no state of their own.
//
// FAIRNESS: this class is constructed FROM the ViewDto and never holds
// a GameWorld or Simulation reference — the reflection pin in
// AiPlayerTests sweeps it like every other Ai type.
public sealed class ThinkContext
{
    public ViewDto View = null!;
    public AiConfig Cfg = null!;
    public AiMemory Mem = null!;
    public long Now;

    public int PlayerId;
    public int MapWidth, MapHeight;
    public StructDto? Castle;
    public TileCoord CastleTile;
    public List<StructDto> Own = new();
    public List<UnitDto> OwnUnits = new();
    // Tiles in LIVE sight this think (vs merely remembered) — the
    // Defend rung clears threat memory for tiles it can re-observe.
    public readonly HashSet<(int X, int Y)> VisibleTiles = new();
    private readonly Dictionary<(int X, int Y), int> _biome = new();
    // M35 — the environmental fertility baseline per known tile
    // (TileDto.Baseline): what makes one pocket better land than another.
    private readonly Dictionary<(int X, int Y), int> _baseline = new();
    private readonly HashSet<(int X, int Y)> _blocked = new();   // structures + claims + blacklist
    // M44 — the brain's own ore knowledge, straight off the view.
    private readonly HashSet<(int X, int Y)> _freeVeins = new();     // known, unmined
    private readonly HashSet<(int X, int Y)> _surveyed = new();      // known veins + proven barren

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

    public static ThinkContext Build(ViewDto view, AiConfig cfg, AiMemory mem, long now)
    {
        var d = new ThinkContext
        {
            View = view, Cfg = cfg, Mem = mem, Now = now,
            PlayerId = view.PlayerId, MapWidth = view.Width, MapHeight = view.Height,
        };
        d._designated = new HashSet<int>(mem.DesignatedParents);
        if (mem.DesignatedTrainee is { } trainee) d._designated.Add(trainee.Id);
        if (mem.DesignatedRecruit is { } recruit) d._designated.Add(recruit);
        if (mem.DesignatedVeteran is { } veteran) d._designated.Add(veteran);
        // M25 — campaign soldiers belong to ConquerRung (and raiders to
        // RaidRung) until the war ends: Defend's sortie, Muster's demob
        // and every staffing selector skip them through the same IsFree
        // gate.
        foreach (var id in mem.CampaignSoldiers) d._designated.Add(id);
        foreach (var id in mem.RaidParty) d._designated.Add(id);
        // Salvage crew (SalvageRung): same ownership rule — a civilian
        // mid-loot-walk is not free labor.
        foreach (var id in mem.SalvageCrew) d._designated.Add(id);
        // Scavenge expedition (ScavengeRung): ditto for the dead-kingdom
        // sweep — Muster's demob must not disband a party mid-march.
        foreach (var id in mem.ScavengeParty) d._designated.Add(id);
        foreach (var t in view.Visible)
        {
            d._biome[(t.X, t.Y)] = t.Biome;
            d._baseline[(t.X, t.Y)] = t.Baseline;
            d.VisibleTiles.Add((t.X, t.Y));
        }
        foreach (var t in view.Remembered)
        {
            d._biome.TryAdd((t.X, t.Y), t.Biome);
            d._baseline.TryAdd((t.X, t.Y), t.Baseline);
        }
        foreach (var b in mem.BlacklistedTiles) d._blocked.Add(b);
        foreach (var v in view.Veins)
        {
            d._surveyed.Add((v.X, v.Y));
            // M51 — the AI mines copper only (bronze swords); iron and steel
            // veins wait for the ladder follow-up (docs/m51-ore-tiers-spec.md).
            if (!v.Mined && v.Ore == (int)Resource.CopperOre) d._freeVeins.Add((v.X, v.Y));
        }
        for (var i = 0; i < view.BarrenX.Length; i++) d._surveyed.Add((view.BarrenX[i], view.BarrenY[i]));
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
        Own.Where(s => StructureCatalog.TryGetSpec((StructureKind)s.Kind, out var spec)
            && spec.RequiredBiome != Biome.None)   // the land-working extractors
           .OrderBy(s => s.Y).ThenBy(s => s.X);

    // Idle AND standing still — a marching unit reads Activity.Idle
    // (movement lives on the arrival anchors), so "has no destination"
    // is the real stillness check. M16 lesson.
    // M43 — the per-side cap: only units STANDING on a tile take its room (a walk through
    // takes none), and a walk ordered onto a full tile is re-aimed to the tile beside, so
    // ordering more there only shuffles them every think. Own castle/open ground is 16.
    public bool HasRoomAt(TileCoord t, int adding = 1) =>
        OwnUnits.Count(u => u.X == t.X && u.Y == t.Y && u.DestX < 0) + adding <= Sim.Core.Battlefields.Subtile.Count;

    // Free by the one rule the sim acts on (UnitAvailability, carried on the wire as
    // UnitDto.Busy): idle, standing still, not in a fight, not aboard. An own unit always
    // carries Busy; the Activity test stays for a view built without it.
    public bool IsIdleStill(UnitDto u) =>
        u.Activity == (int)Activity.Idle && u.DestX < 0 && u.Busy <= 0;

    // Allocate a haul carrier for THIS think: idle, still, empty-handed,
    // never a Builder (conscripting builders as food mules is how the
    // camp site starved — they're needed on sites), never the garrison
    // (a soldier hauling turnips isn't standing guard — M17 Phase 2).
    // Children may haul (only role-tied assignments are age-gated);
    // Haulers first for the capacity.
    public UnitDto? TakeIdleCarrier()
    {
        var pick = OwnUnits.Where(u => IsIdleStill(u) && u.CargoAmount == 0
                && (UnitRole)u.Role is not (UnitRole.Builder
                    or UnitRole.Soldier or UnitRole.Archer)
                && IsFree(u))
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

    // Does the brain's OWN map knowledge say `key` could host an
    // extractor of this spec — i.e., are there >= claimCount free
    // same-biome tiles within claimRange? Mirrors the server's M15
    // claim rule over REMEMBERED tiles (unknown counts as no). The
    // lab's rugged-map colonies died churning one rejected placement
    // per think through slope-scatter — the server knew these tiles
    // were worthless; now the brain does too.
    private bool IsPocket((int X, int Y) key, Biome biome, int claimCount, int claimRange)
    {
        var found = 0;
        for (var dy = -claimRange; dy <= claimRange; dy++)
        for (var dx = -claimRange; dx <= claimRange; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            var q = (key.X + dx, key.Y + dy);
            if (!_biome.TryGetValue(q, out var b) || b != (int)biome) continue;
            if (_blocked.Contains(q)) continue;
            if (++found >= claimCount) return true;
        }
        return false;
    }

    // The BEST known pocket the brain BELIEVES can host the spec: right
    // biome, free, a valid claim pocket around it — scored by the summed
    // environmental baseline of the claim the server would auto-select
    // there (M35, docs/environmental-fertility.md: riverside fields and the
    // forest heart are better land). Ties break by distance, then (y, x),
    // which is the scan order — so at strength 0, when every baseline is
    // the band's, this returns exactly the nearest pocket it always did.
    // The name is kept: callers ask for "the pocket to build on".
    public TileCoord? NearestPocketTile(Biome biome, int range, int claimCount, int claimRange)
    {
        TileCoord? best = null;
        long bestScore = long.MinValue;
        for (var r = 1; r <= range; r++)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            var key = (CastleTile.X + dx, CastleTile.Y + dy);
            if (!_biome.TryGetValue(key, out var b) || b != (int)biome) continue;
            if (_blocked.Contains(key)) continue;
            var score = PocketScore(key, biome, claimCount, claimRange);
            if (score is null) continue;
            if (score.Value > bestScore)
            {
                bestScore = score.Value;
                best = new TileCoord(key.Item1, key.Item2);
            }
        }
        return best;
    }

    // Sum of baselines over the claim the server's Claims.AutoSelect would
    // pick at `key` (rings out to claimRange in (d, y, x) order, first
    // claimCount free same-biome tiles), or null when the pocket can't
    // host a full claim. Mirrors IsPocket's rule over REMEMBERED tiles.
    private long? PocketScore((int X, int Y) key, Biome biome, int claimCount, int claimRange)
    {
        long score = 0;
        var found = 0;
        for (var d = 1; d <= claimRange && found < claimCount; d++)
        for (var dy = -d; dy <= d && found < claimCount; dy++)
        for (var dx = -d; dx <= d && found < claimCount; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != d) continue;
            var q = (key.X + dx, key.Y + dy);
            if (!_biome.TryGetValue(q, out var b) || b != (int)biome) continue;
            if (_blocked.Contains(q)) continue;
            score += _baseline.TryGetValue(q, out var bl) ? bl : 0;
            found++;
        }
        return found >= claimCount ? score : null;
    }

    // Pocket-aware land inventory — the bank should count BUILDABLE
    // sites, not stray tiles (40 scattered slope-grass tiles is zero
    // farms).
    public int CountPocketTiles(Biome biome, int range, int claimCount, int claimRange, int cap)
    {
        var count = 0;
        foreach (var (key, b) in _biome)
        {
            if (b != (int)biome) continue;
            if (Math.Max(Math.Abs(key.X - CastleTile.X), Math.Abs(key.Y - CastleTile.Y)) > range) continue;
            if (_blocked.Contains(key)) continue;
            if (!IsPocket(key, biome, claimCount, claimRange)) continue;
            if (++count >= cap) return count;
        }
        return count;
    }

    // Nearest known tile to the castle matching the biome requirement,
    // unoccupied and unclaimed — ring scan in (dist, y, x) order so the
    // choice is deterministic. requiredBiome null = any walkable land.
    public TileCoord? NearestFreeTile(Biome? requiredBiome, int range) =>
        NearestFreeTileNear(CastleTile, requiredBiome, range);

    // M44 — the nearest ore vein the brain knows that no mine stands on and
    // nothing blocks: where a Mine can go. (dist, y, x) order from the keep.
    public TileCoord? NearestFreeVein(int range)
    {
        TileCoord? best = null;
        var bestKey = (int.MaxValue, int.MaxValue, int.MaxValue);
        foreach (var (x, y) in _freeVeins)
        {
            if (_blocked.Contains((x, y))) continue;
            var d = Math.Max(Math.Abs(x - CastleTile.X), Math.Abs(y - CastleTile.Y));
            if (d > range) continue;
            var key = (d, y, x);
            if (key.CompareTo(bestKey) < 0) { bestKey = key; best = new TileCoord(x, y); }
        }
        return best;
    }

    // M44 — where to send a Miner to survey: the nearest known Mountain tile
    // its owner has not yet surveyed (neither a known vein nor proven
    // barren). Each sweep proves the slopes around it, so the next answer
    // moves outward on its own — no survey radius needed on the brain side.
    public TileCoord? NearestUnsurveyedMountain(int range)
    {
        for (var r = 0; r <= range; r++)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            var key = (CastleTile.X + dx, CastleTile.Y + dy);
            if (!_biome.TryGetValue(key, out var b) || b != (int)Biome.Mountain) continue;
            if (_surveyed.Contains(key)) continue;
            return new TileCoord(key.Item1, key.Item2);
        }
        return null;
    }

    // Same scan from an ARBITRARY origin (M19 Phase 3b: houses are
    // placed by the work cluster they feed, not by the keep).
    public TileCoord? NearestFreeTileNear(TileCoord origin, Biome? requiredBiome, int range)
    {
        for (var r = 1; r <= range; r++)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
            var key = (origin.X + dx, origin.Y + dy);
            if (!_biome.TryGetValue(key, out var biome)) continue;
            if (requiredBiome is { } req ? biome != (int)req
                : biome is (int)Biome.Water or (int)Biome.None) continue;
            if (_blocked.Contains(key)) continue;
            return new TileCoord(key.Item1, key.Item2);
        }
        return null;
    }

    // M26 — Fortify's per-tile query: does the brain's OWN map say this
    // tile is buildable land (biome remembered, not Water/None, free of
    // structures/claims/blacklist)? Unknown counts as NO — the ring grows
    // as knowledge does.
    public bool KnownBuildableLand(int x, int y) =>
        _biome.TryGetValue((x, y), out var b)
        && b is not ((int)Biome.Water or (int)Biome.None)
        && !_blocked.Contains((x, y));

    // M27 — Irrigate's queries. Diggable mirrors PlaceCanalIntent's
    // TileEligible over the brain's own map: known land, not Mountain
    // (rock — too solid to dig), free of structures/claims/blacklist.
    public bool KnownDiggable(int x, int y) =>
        _biome.TryGetValue((x, y), out var b)
        && b is not ((int)Biome.Water or (int)Biome.None or (int)Biome.Mountain)
        && !_blocked.Contains((x, y));

    // Any KNOWN water tile within Chebyshev `radius` of (x, y)?
    public bool KnownWaterNear(int x, int y, int radius)
    {
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
            if (_biome.TryGetValue((x + dx, y + dy), out var b) && b == (int)Biome.Water)
                return true;
        return false;
    }

    // Every known water tile, canonical (y, x) order — the canal BFS seeds.
    public IEnumerable<(int X, int Y)> KnownWaterTiles() =>
        _biome.Where(kv => kv.Value == (int)Biome.Water)
            .Select(kv => kv.Key)
            .OrderBy(t => t.Y).ThenBy(t => t.X);

    // M27 — the builder demand: the configured floor, raised to the largest
    // crew any own pending site requires. A canal takes 3 builders against
    // the genesis floor of 2 — without this, the dig would wait forever for
    // a third pair of hands TrainRung had no reason to train. Lab-neutral:
    // with no oversized site pending, this IS Cfg.BuilderFloor.
    public int BuilderDemand()
    {
        var need = Cfg.BuilderFloor;
        foreach (var s in OwnSites())
            if (s.BuildersRequired > need) need = s.BuildersRequired;
        return need;
    }

    // The hauler demand (2026-09-19): the configured floor, raised to one
    // Hauler per ExtractorsPerHauler own extractors. A belt of ten far
    // farms served by 5-capacity citizens was the food wall — every
    // think a food haul, every idler a mule (docs/ai-players.md).
    public int HaulerDemand()
    {
        var extractors = Own.Count(s => StructureCatalog.TryGetSpec((StructureKind)s.Kind, out var spec)
            && spec.IsExtractor);
        return Math.Max(Cfg.HaulerFloor, extractors / Math.Max(1, Cfg.ExtractorsPerHauler));
    }

    public static int AmountOf(ResAmtDto[] holdings, Resource r) =>
        holdings.FirstOrDefault(h => h.Resource == (int)r)?.Amount ?? 0;

    // The visible ground pile on (x, y), if any. Piles ride the wire only
    // for tiles in CURRENT sight (the M23 cache stance), so a unit
    // standing on a grave always sees what it's standing on — that's the
    // moment SalvageRung names the resource to load.
    public PileDto? PileAt(int x, int y) =>
        View.Piles.FirstOrDefault(p => p.X == x && p.Y == y);

    public static TileCoord TileOf(StructDto s) => new(s.X, s.Y);

    // Doctrine helper (M17 Phase 2): is this tile inside the civilian
    // danger radius of any live sighting? The Defend rung (ladder top)
    // prunes the memory every think, so entries here are fresh; the
    // expiry re-check is belt-and-braces.
    public bool UnderThreat(TileCoord t) =>
        Mem.SightedHostiles.Any(kv =>
            Now - kv.Value.Tick <= Cfg.ThreatMemoryTicks
            && Math.Max(Math.Abs(kv.Key.X - t.X), Math.Abs(kv.Key.Y - t.Y))
                <= Cfg.CivilianDangerRadius);

    // ---- M25: diplomacy digest (public knowledge on the wire) ----------------

    // The relationship row between US and `ownerId` (either pair order;
    // absent row = Neutral, the sparse default).
    private RelationshipDto? RelWith(int ownerId) =>
        View.Relationships.FirstOrDefault(r =>
            (r.LoId == PlayerId && r.HiId == ownerId)
            || (r.LoId == ownerId && r.HiId == PlayerId));

    // Effective war — combat's AreHostile would say yes. (Bandits are
    // hostile OUTSIDE diplomacy; callers test their owner id separately.)
    public bool AtWarWith(int ownerId) =>
        ownerId != PlayerId
        && RelWith(ownerId)?.State == (int)Sim.Core.Diplomacy.RelationshipState.Enemy;

    // Declared-but-not-yet-effective — the telegraph window. TRUE also
    // for the war's own aggressor (mobilization runs inside it).
    public bool PendingWarWith(int ownerId) =>
        ownerId != PlayerId && RelWith(ownerId) is { PendingEffectiveTick: >= 0 };

    public bool IsDefeated(int ownerId) =>
        View.Factions.FirstOrDefault(f => f.Id == ownerId)?.Defeated ?? false;

    // Foreign factions we are effectively at war with (never bandits —
    // they have no faction row; never the defeated — inert wreckage).
    public IEnumerable<int> HostileFactions() =>
        View.Factions.Where(f => f.Id != PlayerId && !f.Defeated && AtWarWith(f.Id))
            .Select(f => f.Id);

    // Any live war involving us, effective OR telegraphed — the window
    // in which the offense budget applies (mobilization runs INSIDE the
    // telegraph; that's what the Delay is for).
    public bool AtWarOrMobilizing() =>
        View.Factions.Any(f => f.Id != PlayerId && !f.Defeated
            && (AtWarWith(f.Id) || PendingWarWith(f.Id)));

    // ---- shared plays: used by more than one rung ---------------------------

    // THE LABOR LEDGER, shared by Eat (how many hands can the colony
    // field?), Train (are there enough Farmers?) and Grow (can it afford
    // another mouth?). Pool = adults in general roles (Builders build,
    // Haulers haul, Scouts scout — fixed jobs), not designated as
    // parents. Hands = farmhands demand asks for, capped at pool − 3
    // (one camp worker, two kept breedable). Returns (pool, fieldable
    // hands, hands DEMAND requires). The last is uncapped — Grow's
    // carrying-capacity gate must compare the pool against what the
    // mouths genuinely need, not against the capped number
    // (capped-vs-pool is ≥3 by construction — a gate that can never
    // close, which is how the first version failed).
    //
    // HONEST PRICING: only Farmer-role hands earn the 2:1 bonus; a
    // generalist produces the base rate. The first ledger priced every
    // hand as a farmer and overstated supply ~40% — the colony rode the
    // resulting knife-edge into the cascade.
    public (int Pool, int Hands, int HandsDemanded) LaborLedger()
    {
        // A PLANNING price, not a live rate: the catalog's untapered,
        // unhoused yield. Deliberately cautious — the settled-worker bonus
        // is unpriced headroom. Scaling it by the farms' reported
        // OutputPerDay was tried both ways on 2026-09-23 and lost both
        // (docs/structure-rates-on-the-wire.md, Update 2026-09-23).
        var spec = StructureCatalog.Spec(StructureKind.Farm);
        var periodsPerDay = Sim.Core.Time.Day / spec.ProductionPeriodTicks;
        var farmerDaily = (long)spec.BaseRatePerWorker * periodsPerDay
            * spec.RoleBonusNumerator / spec.RoleBonusDenominator;
        var generalDaily = (long)spec.BaseRatePerWorker * periodsPerDay;

        var dailyDemand = (long)View.Population * Sim.Core.Food.FoodConsumption.DemandPerDayPerCitizen;
        var required = dailyDemand * Cfg.FarmHeadroomPercent / 100;

        // IsFreeOrDesignated, not IsFree: designated parents are still the
        // colony's labor in the census sense (counting them as missing
        // deadlocked the gate). Scouts COUNT — exploration has a budget
        // (ScoutLegBudget), then they work. Builders/Haulers keep their
        // fixed jobs. Soldiers/Archers are NOT labor at all (M17 Phase
        // 2): the standing army eats without producing — that's the
        // defense budget, and the ledger must price it honestly.
        bool InPool(UnitDto u) => u.Age >= Cfg.MinAdultAgeYears
            && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler
                or UnitRole.Soldier or UnitRole.Archer)
            && IsFreeOrDesignated(u);
        var farmers = OwnUnits.Count(u => InPool(u) && (UnitRole)u.Role == UnitRole.Farmer);
        var pool = OwnUnits.Count(InPool);

        // Cover demand with farmer-hands first (they're worth double),
        // generalists for the remainder.
        var farmerHands = (int)Math.Min(farmers, (required + farmerDaily - 1) / farmerDaily);
        var rest = required - (long)farmerHands * farmerDaily;
        var generalHands = rest > 0 ? (int)((rest + generalDaily - 1) / generalDaily) : 0;
        var demanded = Math.Max(1, farmerHands + generalHands);

        return (pool, Math.Min(demanded, Math.Max(1, pool - 3)), demanded);
    }

    // A staffed extractor whose buffer reads zero for 12 consecutive
    // thinks (≈12 game-hours) is claim-exhausted (M15) — a healthy one
    // rebuilds its buffer within hours of any haul. The brain can't see
    // dormancy in the view, but it can see this. Returns the crew-release
    // intents the moment an extractor is declared dead. The husk and its
    // claims stay (DemolishIntent is a known core-game deferral), but the
    // PEOPLE move on.
    public List<Intent>? DetectExhausted(List<StructDto> extractors, Resource output)
    {
        foreach (var ex in extractors)
        {
            var key = (ex.X, ex.Y);
            if (Mem.DeadExtractors.Contains(key)) continue;
            var starvedBuffer = AmountOf(ex.Holdings, output) == 0 && ex.Workers > 0;
            Mem.ZeroBufferThinks[key] = starvedBuffer ? Mem.ZeroBufferThinks.GetValueOrDefault(key) + 1 : 0;
            if (Mem.ZeroBufferThinks[key] < 12) continue;
            Mem.DeadExtractors.Add(key);
            var crew = OwnUnits.Where(u =>
                    u.X == ex.X && u.Y == ex.Y && u.Activity == (int)Activity.Working)
                .Select(u => u.Id).ToList();
            if (crew.Count > 0)
                return new List<Intent> { new UnassignWorkersIntent(new TileCoord(ex.X, ex.Y), crew)
                    { PlayerId = PlayerId } };
        }
        return null;
    }

    // Get the site its builders: assign the ones standing on it, march the
    // ones that aren't. (Materials are logistics' job.)
    //
    // ASSIGN ONLY WHEN PROVISIONED (arbitration lesson #10): an assigned
    // builder is LOCKED (Activity=Building — invisible to every selector)
    // while site deliveries run in (y,x) TILE order, not rung order, so a
    // site can starve forever behind bigger demands it doesn't outrank.
    // The lab watched both builders lock onto a 10-wood farm that sat
    // LAST in the delivery queue: the fully-provisioned lumber camp never
    // got a builder, wood income died at zero, and four sites wedged for
    // 20 days (faction 1's long-standing bootstrap collapse). Builders
    // may MARCH early (a walk is re-targetable; arrival leaves them
    // idle-still and free), but they only swing hammers once every
    // material is on site — sites that can't start don't hoard hands.
    //
    // marchEarly:false (2026-09-19, the Forge ping-pong): a builder standing
    // idle on an UNPROVISIONED site is "free" to every other site's early
    // march, so two long-waiting sites (a 60-wood smithy and a mine, weeks
    // behind the delivery queue) bounced one builder between them for 36
    // lab days — walking, never hammering, while the houses waited. Rungs
    // whose sites wait long for materials march only once provisioned;
    // the hour of walk latency is nothing against the month of thrash.
    public List<Intent> EnsureBuilders(StructDto site, bool marchEarly = true)
    {
        var intents = new List<Intent>();
        if (site.BuildersPresent >= site.BuildersRequired) return intents;
        var siteTile = TileOf(site);
        // Recall doctrine (M17 Phase 2): builders are civilians — don't
        // march them into a live raid; the site waits for the militia.
        if (Cfg.RecallCiviliansUnderRaid && UnderThreat(siteTile)) return intents;
        var provisioned = site.Needed.All(n =>
            AmountOf(site.Holdings, (Resource)n.Resource) >= n.Amount);
        if (!provisioned && !marchEarly) return intents;
        var builders = OwnUnits.Where(u =>
                (UnitRole)u.Role == UnitRole.Builder && IsIdleStill(u) && IsFree(u)
                && u.CargoAmount == 0 && u.Age >= Cfg.MinAdultAgeYears)
            .OrderBy(u => u.Id).ToList();
        // Reserve only on EMISSION — reserving the waiting builders while
        // emitting nothing would hide them from every lower rung and
        // recreate the very wedge this gate exists to break.
        var onTile = builders.Where(u => u.X == siteTile.X && u.Y == siteTile.Y).ToList();
        if (onTile.Count > 0 && provisioned)
            intents.Add(new AssignBuildersIntent(siteTile,
                onTile.Select(u => Reserve(u).Id).ToList()) { PlayerId = PlayerId });
        else if (onTile.Count == 0)
            foreach (var b in builders.Take(site.BuildersRequired - site.BuildersPresent))
                intents.Add(new MoveIntent(Reserve(b).Id, siteTile) { PlayerId = PlayerId });
        return intents;
    }

    // Get an extractor staffed toward `target` workers (prefer the
    // matching role — the 2:1 rate bonus — falling back to any
    // non-builder, non-hauler adult). Farms are budgeted by the Eat
    // planner; everything else defaults to one worker — wood income
    // doesn't race demographics.
    public List<Intent> StaffExtractor(StructDto ex, UnitRole prefer, int target = 1)
    {
        var intents = new List<Intent>();
        if (ex.Workers >= Math.Max(1, target)) return intents;

        var tile = TileOf(ex);
        // Recall doctrine (M17 Phase 2): don't staff a post the Defend
        // rung just evacuated — Eat would otherwise march replacements
        // into the raid one think behind the recall, forever (the
        // recruit-thrash bug with blades). Re-staffing resumes the
        // think the threat cools.
        if (Cfg.RecallCiviliansUnderRaid && UnderThreat(tile)) return intents;
        // Builders belong on sites, Haulers on the road (their 25-capacity
        // is the logistics backbone — assigning one as a farmhand starves
        // the haul loop), the garrison on guard (M17 Phase 2). Everyone
        // else can work — including scouts past their exploration
        // budget. Matching role first, scouts last.
        var candidates = OwnUnits.Where(u =>
                IsIdleStill(u) && IsFree(u) && u.CargoAmount == 0
                && u.Age >= Cfg.MinAdultAgeYears
                && (UnitRole)u.Role is not (UnitRole.Builder or UnitRole.Hauler
                    or UnitRole.Soldier or UnitRole.Archer))
            .OrderBy(u => (UnitRole)u.Role == prefer ? 0
                : (UnitRole)u.Role == UnitRole.Scout ? 2 : 1)
            .ThenBy(u => u.Id)
            .ToList();
        var onTile = candidates.FirstOrDefault(u => u.X == tile.X && u.Y == tile.Y);
        if (onTile is not null)
            intents.Add(new AssignWorkersIntent(tile, new[] { Reserve(onTile).Id }) { PlayerId = PlayerId });
        else if (candidates.FirstOrDefault() is { } walker)
            intents.Add(new MoveIntent(Reserve(walker).Id, tile) { PlayerId = PlayerId });
        return intents;
    }
}
