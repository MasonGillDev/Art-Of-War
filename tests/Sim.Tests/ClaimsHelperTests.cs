using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M15 Phase 2 — the Claims helper in isolation (validation matrix,
// deterministic auto-selection, claimant lookup, in-band rollup, purity).
// All expectations derive from StructureCatalog specs — never hard-coded.
public class ClaimsHelperTests
{
    private static readonly StructureSpec Camp = StructureCatalog.Spec(StructureKind.LumberCamp);
    private static readonly StructureSpec Farm = StructureCatalog.Spec(StructureKind.Farm);

    // A Forest world big enough that a center site has abundant claimable
    // land: (2×ClaimRange+1)² − 1 candidates ≥ ClaimCount by construction.
    private static GameWorld ForestWorld(int size = 9) =>
        new(new TileGrid(size, size, Biome.Forest));

    private static TileCoord Center(int size = 9) => new(size / 2, size / 2);

    // ---- AutoSelect ----

    [Fact]
    public void AutoSelect_ReturnsClaimCount_Canonical_Deterministic()
    {
        var world = ForestWorld();
        var site = Center();

        var a = Claims.AutoSelect(world, site, Camp, now: 0);
        var b = Claims.AutoSelect(world, site, Camp, now: 0);

        Assert.NotNull(a);
        Assert.Equal(Camp.ClaimCount, a!.Count);
        Assert.Equal(a, b);   // twin calls identical (determinism)
        // Canonical (y, x) order.
        for (var i = 1; i < a.Count; i++)
        {
            var prev = a[i - 1];
            var cur = a[i];
            Assert.True(prev.Y < cur.Y || (prev.Y == cur.Y && prev.X < cur.X),
                "claim list must be sorted (y, x)");
        }
        // All within range, none on the site tile, all distinct.
        Assert.Equal(a.Count, a.Distinct().Count());
        Assert.All(a, t =>
        {
            Assert.NotEqual(site, t);
            Assert.True(Math.Max(Math.Abs(t.X - site.X), Math.Abs(t.Y - site.Y)) <= Camp.ClaimRange);
        });
    }

    [Fact]
    public void AutoSelect_PrefersNearestRing()
    {
        // With abundant land, every picked tile sits at Chebyshev distance 1
        // while ClaimCount ≤ 8 (the inner ring) — nearest-first contract.
        var world = ForestWorld();
        var site = Center();
        var picked = Claims.AutoSelect(world, site, Camp, now: 0)!;

        if (Camp.ClaimCount <= 8)
            Assert.All(picked, t =>
                Assert.Equal(1, Math.Max(Math.Abs(t.X - site.X), Math.Abs(t.Y - site.Y))));
    }

    [Fact]
    public void AutoSelect_InsufficientLand_ReturnsNull()
    {
        // Grassland world with too few Forest tiles in range.
        var world = new GameWorld(new TileGrid(9, 9, Biome.Grassland));
        var site = Center();
        // Paint one fewer Forest tile than the camp needs.
        for (var i = 0; i < Camp.ClaimCount - 1; i++)
            world.Grid.SetBiome(new TileCoord(site.X - 1 + i % 3, site.Y - 1 + i / 3), Biome.Forest);

        Assert.Null(Claims.AutoSelect(world, site, Camp, now: 0));
    }

    [Fact]
    public void AutoSelect_SkipsStructures_Claims_AndWrongBiome()
    {
        var world = ForestWorld();
        var site = Center();
        // Occupy one inner-ring tile with a structure, claim another via a
        // rival site, and degrade nothing — the selector must route around
        // both and still fill the claim from the remaining candidates.
        var occupied = new TileCoord(site.X - 1, site.Y - 1);
        world.AddStructure(new Tower(occupied));
        var rival = world.AddStructure(
            new ConstructionSite(new TileCoord(site.X + 3, site.Y), StructureKind.LumberCamp));
        var rivalClaim = new TileCoord(site.X + 1, site.Y);
        rival.ClaimTiles.Add(rivalClaim);

        var picked = Claims.AutoSelect(world, site, Camp, now: 0)!;

        Assert.NotNull(picked);
        Assert.DoesNotContain(occupied, picked);
        Assert.DoesNotContain(rivalClaim, picked);
    }

    // ---- Validate ----

    [Fact]
    public void Validate_AcceptsAutoSelectedClaim()
    {
        var world = ForestWorld();
        var site = Center();
        var claim = Claims.AutoSelect(world, site, Camp, now: 0)!;
        Assert.Null(Claims.Validate(world, site, Camp, claim, now: 0));
    }

    [Fact]
    public void Validate_RejectionMatrix()
    {
        var world = ForestWorld();
        var site = Center();
        var good = Claims.AutoSelect(world, site, Camp, now: 0)!;

        // Wrong count.
        Assert.Contains("exactly", Claims.Validate(world, site, Camp, good.Take(Camp.ClaimCount - 1).ToList(), 0));
        // Duplicates (hostile wire payload).
        var dup = good.Take(Camp.ClaimCount - 1).Append(good[0]).ToList();
        Assert.Contains("duplicate", Claims.Validate(world, site, Camp, dup, 0));
        // Out of range.
        var far = good.Take(Camp.ClaimCount - 1)
            .Append(new TileCoord(site.X + Camp.ClaimRange + 1, site.Y)).ToList();
        Assert.Contains("outside range", Claims.Validate(world, site, Camp, far, 0));
        // The building tile itself.
        var self = good.Take(Camp.ClaimCount - 1).Append(site).ToList();
        Assert.Contains("building tile", Claims.Validate(world, site, Camp, self, 0));
        // Wrong biome.
        var waterTile = new TileCoord(site.X - Camp.ClaimRange, site.Y);
        world.Grid.SetBiome(waterTile, Biome.Water);
        var wet = good.Take(Camp.ClaimCount - 1).Append(waterTile).ToList();
        Assert.Contains("requires", Claims.Validate(world, site, Camp, wet, 0));
        // Structure on tile.
        var towerTile = new TileCoord(site.X, site.Y - Camp.ClaimRange);
        world.AddStructure(new Tower(towerTile));
        var built = good.Take(Camp.ClaimCount - 1).Append(towerTile).ToList();
        Assert.Contains("has a structure", Claims.Validate(world, site, Camp, built, 0));
        // Already claimed by someone (any kind, any owner).
        var rival = world.AddStructure(
            new ConstructionSite(new TileCoord(site.X + 4, site.Y + 4), StructureKind.Farm) { OwnerId = 1 });
        var rivalTile = new TileCoord(site.X + Camp.ClaimRange, site.Y + Camp.ClaimRange);
        rival.ClaimTiles.Add(rivalTile);
        var contested = good.Take(Camp.ClaimCount - 1).Append(rivalTile).ToList();
        Assert.Contains("already claimed", Claims.Validate(world, site, Camp, contested, 0));
    }

    // ---- ClaimantAt / ClaimantDegradeAmount ----

    [Fact]
    public void ClaimantAt_SeesBothCarriers()
    {
        var world = ForestWorld();
        var ext = (Extractor)world.AddStructure(new Extractor(StructureKind.LumberCamp, new TileCoord(1, 1)));
        ext.ClaimTiles.Add(new TileCoord(2, 1));
        var siteStruct = (ConstructionSite)world.AddStructure(
            new ConstructionSite(new TileCoord(6, 6), StructureKind.Farm));
        siteStruct.ClaimTiles.Add(new TileCoord(7, 6));

        Assert.Equal(new TileCoord(1, 1), Claims.ClaimantAt(world, new TileCoord(2, 1)));
        Assert.Equal(new TileCoord(6, 6), Claims.ClaimantAt(world, new TileCoord(7, 6)));
        Assert.Null(Claims.ClaimantAt(world, new TileCoord(4, 4)));
    }

    [Fact]
    public void ClaimantDegradeAmount_GatesOnTickArmed_AndClaim()
    {
        var world = ForestWorld();
        var ext = (Extractor)world.AddStructure(new Extractor(StructureKind.LumberCamp, new TileCoord(1, 1)));
        var claimed = new TileCoord(2, 1);
        ext.ClaimTiles.Add(claimed);

        // Dormant claimant → 0.
        Assert.Equal(0, Claims.ClaimantDegradeAmount(world, claimed));
        // Producing claimant → its catalog amount; unclaimed tile stays 0
        // even right next to the producing camp (the edge-exploit fix).
        ext.TickArmed = true;
        Assert.Equal(Camp.DegradeAmount, Claims.ClaimantDegradeAmount(world, claimed));
        Assert.Equal(0, Claims.ClaimantDegradeAmount(world, new TileCoord(1, 2)));
    }

    // ---- InBandClaimCount ----

    [Fact]
    public void InBandClaimCount_CountsDerivedBiome()
    {
        var world = ForestWorld();
        var ext = (Extractor)world.AddStructure(new Extractor(StructureKind.LumberCamp, new TileCoord(1, 1)));
        var a = new TileCoord(2, 1);
        var b = new TileCoord(1, 2);
        ext.ClaimTiles.Add(a);
        ext.ClaimTiles.Add(b);
        Assert.Equal(2, Claims.InBandClaimCount(world, ext, now: 0));

        // Push one tile below the Forest band via a stored deviation that
        // lands it in Grassland (derived BiomeAt reads it live).
        var cfg = world.BiomeDegradationConfig;
        world.Fertility[a] = new Sim.Core.Biomes.Fertility(
            deviation: cfg.ForestThreshold - cfg.ForestBaseline - 1, lastUpdateTick: 0);
        Assert.Equal(1, Claims.InBandClaimCount(world, ext, now: 0));
    }

    // ---- Purity ----

    [Fact]
    public void Helpers_ArePureReads_NoMutation()
    {
        var world = ForestWorld();
        var site = Center();
        var ext = (Extractor)world.AddStructure(new Extractor(StructureKind.LumberCamp, new TileCoord(1, 1)) { });
        ext.ClaimTiles.Add(new TileCoord(2, 1));
        ext.TickArmed = true;
        var sim = new Simulation(world, seed: 1);
        var before = Snapshot.Hash(sim);

        for (var i = 0; i < 100; i++)
        {
            Claims.AutoSelect(world, site, Camp, now: 50);
            Claims.Validate(world, site, Farm, new List<TileCoord> { site }, now: 50);
            Claims.ClaimantAt(world, new TileCoord(2, 1));
            Claims.ClaimantDegradeAmount(world, new TileCoord(2, 1));
            Claims.InBandClaimCount(world, ext, now: 50);
        }

        Assert.Equal(before, Snapshot.Hash(sim));
    }
}
