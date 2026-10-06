using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Logistics;
using Sim.Core.World;

namespace Sim.Tests;

// CraftEquipmentIntent — instant conversion of raw resources to an
// equipment item inside the crafting building's own holdings
// (docs/equipment-model.md). Costs and the crafting building derive from
// EquipmentCatalog; since docs/refining-structures.md the weapons are
// forged at the SMITHY and the Barracks forges nothing.
public class CraftEquipmentTests
{
    private static Simulation MakeSim(int w = 8, int h = 8)
    {
        var grid = new TileGrid(w, h, Biome.Grassland);
        var world = new GameWorld(grid);
        return new Simulation(world, seed: 1);
    }

    private static Smithy AddSmithy(Simulation sim, TileCoord at, int owner = 0) =>
        (Smithy)sim.World.AddStructure(new Smithy(at) { OwnerId = owner });

    private static void StockExactCost(StorageStructure store, Resource item)
    {
        foreach (var (r, n) in EquipmentCatalog.Spec(item).CraftCost)
            store.Deposit(r, n);
    }

    [Fact]
    public void Craft_AtBarracks_Rejected_ForgingMovedToSmithy()
    {
        // docs/refining-structures.md: the Barracks trains and holds gear
        // but forges nothing. Materials in a Barracks stay materials.
        var sim = MakeSim();
        var barracks = sim.World.AddStructure(new Barracks(new TileCoord(2, 2)) { OwnerId = 0 });
        StockExactCost(barracks, Resource.BronzeSword);

        var outcome = new CraftEquipmentIntent(barracks.At, Resource.BronzeSword) { PlayerId = 0 }.Resolve(sim);

        Assert.False(outcome.IsApplied);
        Assert.Equal(0, barracks.AmountOf(Resource.BronzeSword));
        foreach (var (r, n) in EquipmentCatalog.Spec(Resource.BronzeSword).CraftCost)
            Assert.Equal(n, barracks.AmountOf(r));
    }

    [Fact]
    public void Sword_CostsIron_NotOre()
    {
        // The two-hop chain: Ore is a Smelter input, never a Sword input.
        var cost = EquipmentCatalog.Spec(Resource.BronzeSword).CraftCost;
        Assert.True(cost.ContainsKey(Resource.Bronze));
        Assert.False(cost.ContainsKey(Resource.CopperOre));
        Assert.Equal(StructureKind.Smithy, EquipmentCatalog.Spec(Resource.BronzeSword).CraftedAt);
    }

    [Theory]
    [InlineData(Resource.BronzeSword)]
    [InlineData(Resource.Bow)]
    [InlineData(Resource.Shield)]
    public void Craft_ConsumesInputs_DepositsItem(Resource item)
    {
        var sim = MakeSim();
        var barracks = AddSmithy(sim, new TileCoord(2, 2));
        StockExactCost(barracks, item);

        var outcome = new CraftEquipmentIntent(barracks.At, item) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsApplied);
        foreach (var (r, _) in EquipmentCatalog.Spec(item).CraftCost)
            Assert.Equal(0, barracks.AmountOf(r));
        Assert.Equal(1, barracks.AmountOf(item));
    }

    [Fact]
    public void Craft_InsufficientInput_Rejected_NothingMutated()
    {
        // Stock one unit short of ONE input — the reject must leave every
        // holding untouched (fail-clean: no partial withdrawal).
        var sim = MakeSim();
        var barracks = AddSmithy(sim, new TileCoord(2, 2));
        var cost = EquipmentCatalog.Spec(Resource.BronzeSword).CraftCost;
        var before = new Dictionary<Resource, int>();
        var first = true;
        foreach (var (r, n) in cost)
        {
            var stocked = first ? n - 1 : n;
            barracks.Deposit(r, stocked);
            before[r] = stocked;
            first = false;
        }

        var outcome = new CraftEquipmentIntent(barracks.At, Resource.BronzeSword) { PlayerId = 0 }.Resolve(sim);

        Assert.False(outcome.IsApplied);
        foreach (var (r, n) in before)
            Assert.Equal(n, barracks.AmountOf(r));
        Assert.Equal(0, barracks.AmountOf(Resource.BronzeSword));
    }

    [Fact]
    public void Craft_OnNonBarracksStorage_Rejected()
    {
        // A Castle with the materials is still not a crafting site.
        var sim = MakeSim();
        var castle = sim.World.AddStructure(new Castle(new TileCoord(2, 2)));
        foreach (var (r, n) in EquipmentCatalog.Spec(Resource.BronzeSword).CraftCost)
            castle.Deposit(r, n);

        var outcome = new CraftEquipmentIntent(castle.At, Resource.BronzeSword) { PlayerId = 0 }.Resolve(sim);

        Assert.False(outcome.IsApplied);
        Assert.Equal(0, castle.AmountOf(Resource.BronzeSword));
    }

    [Fact]
    public void Craft_OnEnemyBarracks_Rejected()
    {
        var sim = MakeSim();
        var barracks = AddSmithy(sim, new TileCoord(2, 2), owner: 1);
        StockExactCost(barracks, Resource.BronzeSword);

        var outcome = new CraftEquipmentIntent(barracks.At, Resource.BronzeSword) { PlayerId = 0 }.Resolve(sim);

        Assert.False(outcome.IsApplied);
        Assert.Equal(0, barracks.AmountOf(Resource.BronzeSword));
    }

    [Fact]
    public void Craft_NonEquipmentResource_Rejected()
    {
        var sim = MakeSim();
        var barracks = AddSmithy(sim, new TileCoord(2, 2));
        barracks.Deposit(Resource.Wood, 100);

        var outcome = new CraftEquipmentIntent(barracks.At, Resource.Food) { PlayerId = 0 }.Resolve(sim);

        Assert.False(outcome.IsApplied);
    }

    [Fact]
    public void Craft_SameTickContention_FirstSubmittedWins_BothOrders()
    {
        // Materials for exactly one sword; two craft intents the same
        // tick. (At, Seq) submission order decides — swap the order, the
        // outcome order swaps. Pins fairness, not just reproducibility.
        for (var swap = 0; swap < 2; swap++)
        {
            var sim = MakeSim();
            var barracks = AddSmithy(sim, new TileCoord(2, 2));
            StockExactCost(barracks, Resource.BronzeSword);

            var a = new CraftEquipmentIntent(barracks.At, Resource.BronzeSword);
            var b = new CraftEquipmentIntent(barracks.At, Resource.BronzeSword);
            sim.SubmitIntent(0, swap == 0 ? a : b);
            sim.SubmitIntent(0, swap == 0 ? b : a);
            sim.Run();

            // Exactly one sword crafted; the second intent rejected on
            // missing inputs.
            Assert.Equal(1, barracks.AmountOf(Resource.BronzeSword));
            var outcomes = sim.ResolvedLog.OfType<Sim.Core.Intents.IntentEvent>()
                .Where(e => e.Intent is CraftEquipmentIntent)
                .ToList();
            Assert.Equal(2, outcomes.Count);
            Assert.True(outcomes[0].Outcome.IsApplied);
            Assert.True(outcomes[1].Outcome.IsRejected);
        }
    }

    [Fact]
    public void CraftedSword_HaulableToStockpile()
    {
        // Equipment rides the existing logistics with zero special cases:
        // craft at the Smithy, haul the finished sword to a stockpile.
        var sim = MakeSim();
        var barracks = AddSmithy(sim, new TileCoord(0, 0));
        var stockpile = sim.World.AddStructure(new Stockpile(new TileCoord(3, 0)));
        StockExactCost(barracks, Resource.BronzeSword);
        sim.World.AddUnit(new Unit(1, new TileCoord(0, 0)) { Role = UnitRole.Hauler });

        new CraftEquipmentIntent(barracks.At, Resource.BronzeSword) { PlayerId = 0 }.Resolve(sim);
        sim.SubmitIntent(0, new HaulIntent(1, barracks.At, stockpile.At, Resource.BronzeSword));
        sim.Run();

        Assert.Equal(0, barracks.AmountOf(Resource.BronzeSword));
        Assert.Equal(1, stockpile.AmountOf(Resource.BronzeSword));
    }
}
