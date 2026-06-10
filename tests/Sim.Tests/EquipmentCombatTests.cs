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
    public void EquippedSoldiers_BeatEqualCount_UnequippedSoldiers()
    {
        // N sworded soldiers vs N bare soldiers. Replay the linear-
        // proportional, lowest-Health-first attrition from config to
        // compute the expected winner AND survivor count, then assert
        // the sim matches.
        const int n = 3;
        var tile = new TileCoord(10, 10);
        var sim = MakeWarScenario();
        var nextId = 100;
        for (var i = 0; i < n; i++) AddSoldier(sim, nextId++, tile, owner: 0, sword: true);
        for (var i = 0; i < n; i++) AddSoldier(sim, nextId++, tile, owner: 1, sword: false);

        // In-test replay from config: per-unit power and health.
        var baseHealth = UnitCombatCatalog.Spec(UnitRole.Soldier).BaseHealth;
        var basePower = UnitCombatCatalog.Spec(UnitRole.Soldier).BasePower;
        var swordPower = EquipmentCatalog.Spec(Resource.Sword).PowerModifier;
        var aSide = Enumerable.Repeat(baseHealth, n).ToList(); // each power basePower+swordPower
        var bSide = Enumerable.Repeat(baseHealth, n).ToList(); // each power basePower
        while (aSide.Count > 0 && bSide.Count > 0)
        {
            var aPower = aSide.Count * (basePower + swordPower);
            var bPower = bSide.Count * basePower;
            Distribute(aSide, bPower);
            Distribute(bSide, aPower);
        }

        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        sim.Run(until: RoundInterval * 100);

        Assert.Empty(sim.World.CombatStates);
        var survivors = sim.World.Units.Values.Where(u => u.Position == tile).ToList();
        Assert.True(bSide.Count == 0 && aSide.Count > 0, "replay sanity: equipped side wins");
        Assert.All(survivors, u => Assert.Equal(0, u.OwnerId));
        Assert.Equal(aSide.Count, survivors.Count);

        static void Distribute(List<int> healths, int damage)
        {
            healths.Sort(); // lowest-Health-first
            for (var i = 0; i < healths.Count && damage > 0; i++)
            {
                var hit = Math.Min(healths[i], damage);
                healths[i] -= hit;
                damage -= hit;
            }
            healths.RemoveAll(h => h <= 0);
        }
    }

    [Fact]
    public void ShieldBearer_OutlastsUnshieldedPeer()
    {
        // Two own-side soldiers under the same incoming damage: the
        // shield-bearer has more Health, so lowest-Health-first kills the
        // bare one first. Pin by dealing exactly enough damage to kill
        // one bare soldier.
        var tile = new TileCoord(10, 10);
        var sim = MakeWarScenario();
        var bare = AddSoldier(sim, 100, tile, owner: 0, sword: false);
        var shielded = AddSoldier(sim, 101, tile, owner: 0, sword: false);
        var shield = EquipmentCatalog.Spec(Resource.Shield);
        shielded.Buffs.Add(new Buff(shield.BuffKind, shield.PowerModifier, shield.HealthModifier, null));
        shielded.Health += shield.HealthModifier;

        // Enemy force sized to exactly the bare soldier's Health per
        // round: power = baseHealth → bare dies in round 1, shielded
        // survives with full health.
        var baseHealth = UnitCombatCatalog.Spec(UnitRole.Soldier).BaseHealth;
        var enemy = sim.World.AddUnit(new Unit(200, tile) { Role = UnitRole.Soldier, OwnerId = 1 });
        enemy.Buffs.Add(new Buff("test-power", baseHealth - UnitCombatCatalog.Spec(UnitRole.Soldier).BasePower, 0, null));

        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        sim.Run(until: RoundInterval + 1); // exactly one round

        Assert.False(sim.World.Units.ContainsKey(bare.Id));
        Assert.True(sim.World.Units.ContainsKey(shielded.Id));
        Assert.Equal(baseHealth + shield.HealthModifier, shielded.Health);
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

    [Fact]
    public void DamagedSurvivor_KeepsBuffsAndReducedHealth()
    {
        // A chip fight: equipped soldier vs one weak enemy. The soldier
        // survives wounded, loadout intact.
        var tile = new TileCoord(10, 10);
        var sim = MakeWarScenario();
        var soldier = AddSoldier(sim, 100, tile, owner: 0, sword: true);
        var healthBefore = soldier.Health;
        sim.World.AddUnit(new Unit(200, tile) { Role = UnitRole.Builder, OwnerId = 1 });
        var builderPower = UnitCombatCatalog.Spec(UnitRole.Builder).BasePower;

        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
        sim.Run(until: RoundInterval * 100);

        Assert.True(sim.World.Units.ContainsKey(soldier.Id));
        Assert.Single(soldier.Buffs);
        // The builder lands builderPower per round until it dies; rounds
        // survived = ceil(builderHealth / soldierPower), all config-derived.
        var soldierPower = UnitCombatCatalog.Spec(UnitRole.Soldier).BasePower
                         + EquipmentCatalog.Spec(Resource.Sword).PowerModifier;
        var builderHealth = UnitCombatCatalog.Spec(UnitRole.Builder).BaseHealth;
        var rounds = (builderHealth + soldierPower - 1) / soldierPower;
        Assert.Equal(healthBefore - rounds * builderPower, soldier.Health);
    }
}
