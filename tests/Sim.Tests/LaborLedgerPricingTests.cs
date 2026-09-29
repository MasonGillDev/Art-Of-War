using Sim.Core;
using Sim.Core.Food;
using Sim.Core.World;
using Sim.Server.Ai;
using Sim.Server.Wire;

namespace Sim.Tests;

// The labor ledger prices a farmhand from the CATALOG, and farms' reported
// OutputPerDay must not move that price: scaling by it was tried both ways and
// lost in the lab (docs/structure-rates-on-the-wire.md, Update 2026-09-23).
// Expected numbers derive from the catalog, never hard-coded.
public class LaborLedgerPricingTests
{
    private static readonly StructureSpec Farm = StructureCatalog.Spec(StructureKind.Farm);
    private static long FarmerDaily =>
        (long)Farm.BaseRatePerWorker * (Time.Day / Farm.ProductionPeriodTicks)
        * Farm.RoleBonusNumerator / Farm.RoleBonusDenominator;

    // A colony of `population` adult Farmers; `crew` of them working one farm
    // at (5, 5) that reports `farmOutput` per day (null = no farm).
    private static ThinkContext Colony(int population, int crew, int? farmOutput, bool producing = true)
    {
        var units = new List<UnitDto>();
        for (var i = 0; i < population; i++)
        {
            var working = i < crew && farmOutput is not null;
            units.Add(new UnitDto
            {
                Id = i + 1, OwnerId = 0, Role = (int)UnitRole.Farmer, Age = 30,
                X = working ? 5 : 1, Y = working ? 5 : 1,
                Activity = (int)(working ? Activity.Working : Activity.Idle),
            });
        }
        var structures = new List<StructDto> { new() { Kind = (int)StructureKind.Castle, OwnerId = 0, X = 1, Y = 1 } };
        if (farmOutput is { } o)
            structures.Add(new StructDto
            {
                Kind = (int)StructureKind.Farm, OwnerId = 0, X = 5, Y = 5,
                Workers = crew, WorkerCap = Farm.WorkerCap, OutputPerDay = o, Producing = producing,
            });
        var view = new ViewDto
        {
            PlayerId = 0, Width = 32, Height = 32, Population = population,
            Units = units.ToArray(), Structures = structures.ToArray(),
        };
        return ThinkContext.Build(view, new AiConfig(), new AiMemory(), now: 0);
    }

    [Fact]
    public void Ledger_PricesFromTheCatalog_WhateverTheFarmsReport()
    {
        var bare = Colony(40, crew: 2, farmOutput: null).LaborLedger();
        foreach (var reported in new[] { 1, (int)FarmerDaily, (int)(2 * FarmerDaily), (int)(4 * FarmerDaily) })
            Assert.Equal(bare.HandsDemanded, Colony(40, crew: 2, farmOutput: reported).LaborLedger().HandsDemanded);

        var cfg = new AiConfig();
        var required = 40L * FoodConsumption.DemandPerDayPerCitizen * cfg.FarmHeadroomPercent / 100;
        Assert.Equal((int)((required + FarmerDaily - 1) / FarmerDaily), bare.HandsDemanded);
    }
}
