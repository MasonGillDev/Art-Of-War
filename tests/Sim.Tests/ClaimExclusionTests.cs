using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Persistence;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M15 Phase 3 — claims as territory: reservation at site placement,
// exclusion in both directions (claims block claims AND structures block
// /are blocked by claims), same-tick land contention fairness, the
// site→extractor transfer copy, and the mid-build snapshot headline.
public class ClaimExclusionTests
{
    private static readonly StructureSpec CampSpec = StructureCatalog.Spec(StructureKind.LumberCamp);

    private static Simulation ForestSim(int size = 12)
    {
        var world = new GameWorld(new TileGrid(size, size, Biome.Forest));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        return new Simulation(world, seed: 1);
    }

    private static ConstructionSite PlaceCamp(Simulation sim, TileCoord at, int playerId = 0,
        List<TileCoord>? claims = null)
    {
        var outcome = new PlaceSiteIntent(at, StructureKind.LumberCamp, claimTiles: claims)
            { PlayerId = playerId }.Resolve(sim);
        Assert.True(outcome.IsApplied, outcome.Reason);
        return (ConstructionSite)sim.World.Structures[at];
    }

    // ---- reservation + exclusion ----

    [Fact]
    public void PlaceSite_ReservesClaim_AtPlacement()
    {
        var sim = ForestSim();
        var site = PlaceCamp(sim, new TileCoord(5, 5));
        Assert.Equal(CampSpec.ClaimCount, site.ClaimTiles.Count);
        // Every reserved tile answers to the claimant lookup immediately.
        foreach (var t in site.ClaimTiles)
            Assert.Equal(site.At, Claims.ClaimantAt(sim.World, t));
    }

    [Fact]
    public void SecondCamp_CannotOverlapClaim_AnyOwner()
    {
        // Enemy claim blocks just like an own claim — conquer to take land.
        var sim = ForestSim();
        var site = PlaceCamp(sim, new TileCoord(5, 5), playerId: 0);
        // Contest a tile the second site can actually reach, so the
        // rejection exercises the already-claimed rule (not range).
        var contested = site.ClaimTiles.First(t =>
            Math.Max(Math.Abs(t.X - 8), Math.Abs(t.Y - 5)) <= CampSpec.ClaimRange);

        var overlap = Claims.AutoSelect(sim.World, new TileCoord(8, 5), CampSpec, sim.Now)!
            .Take(CampSpec.ClaimCount - 1).Append(contested).ToList();
        var outcome = new PlaceSiteIntent(new TileCoord(8, 5), StructureKind.LumberCamp,
            claimTiles: overlap) { PlayerId = 1 }.Resolve(sim);

        Assert.False(outcome.IsApplied);
        Assert.Contains("already claimed", outcome.Reason);
    }

    [Fact]
    public void Structure_CannotBePlaced_OnClaimedTile_OwnOrEnemy()
    {
        // Full structural exclusion: even the claim owner can't build on
        // their own fields; neither can a rival (Stockpile = non-claiming
        // kind, no biome requirement — only the claim check can reject it).
        var sim = ForestSim();
        var site = PlaceCamp(sim, new TileCoord(5, 5), playerId: 0);
        var claimed = site.ClaimTiles[0];

        var own = new PlaceSiteIntent(claimed, StructureKind.Stockpile) { PlayerId = 0 }.Resolve(sim);
        Assert.False(own.IsApplied);
        Assert.Contains("claimed by", own.Reason);

        var rival = new PlaceSiteIntent(claimed, StructureKind.Stockpile) { PlayerId = 1 }.Resolve(sim);
        Assert.False(rival.IsApplied);
        Assert.Contains("claimed by", rival.Reason);
    }

    [Fact]
    public void AutoSelect_Placement_RejectsWhenLandExhausted()
    {
        // A second camp adjacent to the first: the shared forest can't
        // yield a second full claim once the first reserved its tiles AND
        // the world is small enough. Build a tight world: 5×5 forest island
        // in a grassland sea — one camp claims 6 of the island's tiles;
        // a second camp on the island can't fill its claim.
        var world = new GameWorld(new TileGrid(12, 12, Biome.Grassland));
        world.Players[0] = new Player(0);
        for (var y = 4; y <= 8; y++)
            for (var x = 4; x <= 8; x++)
                world.Grid.SetBiome(new TileCoord(x, y), Biome.Forest);
        var sim = new Simulation(world, seed: 1);

        PlaceCamp(sim, new TileCoord(6, 6));
        // 25-tile island − 1 site tile − 6 claimed = 18 forest left, but a
        // camp at (5,5) only reaches range-2 tiles, many of them claimed —
        // pick the corner-most placement that demonstrably can't fill.
        var outcome = new PlaceSiteIntent(new TileCoord(5, 5), StructureKind.LumberCamp)
            { PlayerId = 0 }.Resolve(sim);
        // Either insufficient land or the site tile itself is claimed —
        // both are the scarcity contract doing its job.
        Assert.False(outcome.IsApplied);
    }

    // ---- same-tick fairness ----

    [Fact]
    public void SameTick_LandContention_FirstSubmittedWins_BothOrders()
    {
        // A forest pocket that can support exactly ONE camp. Two players
        // place same-tick; submission order decides; swapping the order
        // swaps the winner. The pocket is sized from the CATALOG (exactly
        // ClaimCount shared claimable tiles + the two site tiles) so a
        // ClaimCount/ClaimRange retune can't silently let both camps fit
        // or starve them both.
        var spec = StructureCatalog.Spec(StructureKind.LumberCamp);
        for (var swap = 0; swap < 2; swap++)
        {
            var world = new GameWorld(new TileGrid(12, 12, Biome.Grassland));
            world.Players[0] = new Player(0);
            world.Players[1] = new Player(1);

            // Adjacent sites → maximal shared claim range. Paint forest on
            // both site tiles plus the first ClaimCount tiles of the range
            // INTERSECTION: whoever places first can fill exactly, and at
            // most one stray candidate remains for the loser.
            var siteA = new TileCoord(5, 6);
            var siteB = new TileCoord(6, 6);
            world.Grid.SetBiome(siteA, Biome.Forest);
            world.Grid.SetBiome(siteB, Biome.Forest);
            var painted = 0;
            for (var y = siteA.Y - spec.ClaimRange; y <= siteA.Y + spec.ClaimRange && painted < spec.ClaimCount; y++)
                for (var x = siteB.X - spec.ClaimRange; x <= siteA.X + spec.ClaimRange && painted < spec.ClaimCount; x++)
                {
                    var t = new TileCoord(x, y);
                    if (t == siteA || t == siteB) continue;
                    world.Grid.SetBiome(t, Biome.Forest);
                    painted++;
                }
            Assert.Equal(spec.ClaimCount, painted); // intersection big enough for the knobs
            var sim = new Simulation(world, seed: 1);

            var a = new PlaceSiteIntent(siteA, StructureKind.LumberCamp) { PlayerId = 0 };
            var b = new PlaceSiteIntent(siteB, StructureKind.LumberCamp) { PlayerId = 1 };
            sim.SubmitIntent(0, swap == 0 ? a : b);
            sim.SubmitIntent(0, swap == 0 ? b : a);
            sim.Run();

            var resolved = sim.ResolvedLog.OfType<Sim.Core.Intents.IntentEvent>()
                .Where(e => e.Intent is PlaceSiteIntent).ToList();
            Assert.Equal(2, resolved.Count);
            Assert.True(resolved[0].Outcome.IsApplied, "first-submitted placement must win the land");
            Assert.True(resolved[1].Outcome.IsRejected, "second-submitted placement must lose the land");
        }
    }

    // ---- transfer ----

    [Fact]
    public void BuildComplete_TransfersClaim_AsCopy_NotAlias()
    {
        var sim = ForestSim();
        var site = PlaceCamp(sim, new TileCoord(5, 5));
        var reserved = site.ClaimTiles.ToList();

        // Deliver materials + builder, run to completion.
        foreach (var (r, n) in site.Required) site.Deposit(r, n);
        var builder = sim.World.AddUnit(new Unit(1, site.At) { Role = UnitRole.Builder });
        sim.SubmitIntent(0, new AssignBuildersIntent(site.At, new[] { builder.Id }));
        sim.Run();

        var camp = Assert.IsType<Extractor>(sim.World.Structures[new TileCoord(5, 5)]);
        Assert.Equal(reserved, camp.ClaimTiles);            // same content, same order
        Assert.NotSame(site.ClaimTiles, camp.ClaimTiles);   // copied, never aliased
        // Claimant lookup now answers with the finished camp.
        Assert.Equal(camp.At, Claims.ClaimantAt(sim.World, reserved[0]));
    }

    // ---- THE PHASE-3 HEADLINE: pending claim survives recovery ----

    [Fact]
    public void MidBuild_PendingClaim_SnapshotRoundTrip_Identical()
    {
        Simulation Build()
        {
            var sim = ForestSim();
            var site = PlaceCamp(sim, new TileCoord(5, 5));
            foreach (var (r, n) in site.Required) site.Deposit(r, n);
            var builder = sim.World.AddUnit(new Unit(1, site.At) { Role = UnitRole.Builder });
            sim.SubmitIntent(0, new AssignBuildersIntent(site.At, new[] { builder.Id }));
            return sim;
        }
        var buildTicks = StructureCatalog.Spec(StructureKind.LumberCamp).BuildDurationTicks;
        var midTick = buildTicks / 2;
        var endTick = buildTicks * 2L;

        // Path A: uninterrupted.
        var a = Build();
        a.Run(until: endTick);
        var hashA = Snapshot.Hash(a);
        Assert.IsType<Extractor>(a.World.Structures[new TileCoord(5, 5)]);

        // Path B: snapshot mid-build (claim still pending on the SITE),
        // restore, continue. RegenerateQueue rebuilds the completion event;
        // the pending claim rides the site and transfers identically.
        var b = Build();
        b.Run(until: midTick);
        Assert.IsType<ConstructionSite>(b.World.Structures[new TileCoord(5, 5)]);
        var restored = Snapshot.Restore(Snapshot.Serialize(b), seed: 1);
        restored.Run(until: endTick);

        Assert.Equal(hashA, Snapshot.Hash(restored));
    }
}
