using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Logistics;
using Sim.Core.World;

namespace Sim.Tests;

// Equipment buffs under live combat (docs/equipment-model.md): buffed
// power feeds the round event, equipment drops on death, and the loot
// loop (kill → ground pile → haul → re-equip) closes on existing
// machinery. Expected attrition is replayed in-test from catalog +
// config values — never hard-coded.
public class EquipmentCombatTests
{
    private const long RoundInterval = 10; // mirrored into CombatConfig below

    private static Simulation MakeWarScenario()
    {
        var spec = new GenesisSpec
        {
            Width = 20, Height = 20,
            Diplomacy = new DiplomacyConfig(Delay: 50, ProposalExpiryTicks: 200),
            Combat = new CombatConfig(RoundIntervalTicks: RoundInterval),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = 0, CastlePosition = new TileCoord(0, 0) },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(19, 19) },
            },
        };
        var world = Genesis.Build(spec);
        world.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        return new Simulation(world, seed: 0xA12);
    }

    private static Unit AddSoldier(Simulation sim, int id, TileCoord tile, int owner, bool sword = false)
    {
        var u = sim.World.AddUnit(new Unit(id, tile) { Role = UnitRole.Soldier, OwnerId = owner });
        if (sword)
        {
            var spec = EquipmentCatalog.Spec(Resource.Sword);
            u.Buffs.Add(new Buff(spec.BuffKind, spec.PowerModifier, spec.HealthModifier, null));
            u.Health += spec.HealthModifier;
        }
        return u;
    }

    [Fact]
    public void SoldierDies_FullLoadout_BothItemsDropToGroundPile()
    {
        var tile = new TileCoord(10, 10);
        var sim = MakeWarScenario();
        var doomed = AddSoldier(sim, 100, tile, owner: 0, sword: true);
        var shield = EquipmentCatalog.Spec(Resource.Shield);
        doomed.Buffs.Add(new Buff(shield.BuffKind, shield.PowerModifier, shield.HealthModifier, null));
        doomed.Health += shield.HealthModifier;

        doomed.Health = 0;
        CombatRules.OnUnitDeath(sim, doomed);

        var pile = sim.World.GroundResources[tile];
        Assert.Equal(1, pile[Resource.Sword]);
        Assert.Equal(1, pile[Resource.Shield]);
        Assert.False(sim.World.Units.ContainsKey(doomed.Id));
    }

    [Fact]
    public void LootedSword_HauledFromBattlefield_ReEquipsAnotherSoldier()
    {
        // The capture economy end-to-end: dead soldier's sword → ground
        // pile → existing haul (ground-pile pickup) → owned storage →
        // equip a fresh soldier.
        var tile = new TileCoord(5, 5);
        var sim = MakeWarScenario();
        var victim = AddSoldier(sim, 100, tile, owner: 1, sword: true);
        victim.Health = 0;
        CombatRules.OnUnitDeath(sim, victim);

        var stockpile = sim.World.AddStructure(new Stockpile(new TileCoord(2, 5)) { OwnerId = 0 });
        sim.World.AddUnit(new Unit(101, tile) { Role = UnitRole.Hauler, OwnerId = 0 });
        sim.SubmitIntent(0, new HaulIntent(101, tile, stockpile.At, Resource.Sword));
        sim.Run();
        Assert.Equal(1, stockpile.AmountOf(Resource.Sword));

        var recruit = sim.World.AddUnit(new Unit(102, stockpile.At) { Role = UnitRole.Soldier, OwnerId = 0 });
        var outcome = new EquipUnitIntent(recruit.Id, Resource.Sword) { PlayerId = 0 }.Resolve(sim);

        Assert.True(outcome.IsApplied);
        Assert.Single(recruit.Buffs);
        Assert.Equal(0, stockpile.AmountOf(Resource.Sword));
    }

}
