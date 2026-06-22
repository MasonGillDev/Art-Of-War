using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.World;
using Snapshot = Sim.Core.Persistence.Snapshot;

namespace Sim.Tests;

// M8 Phase E: birth + stop-on-removal. THE CRUX of the milestone is
// MidGestation_SnapshotRoundTrip_BirthFiresAtSameTick — the M4 regen
// proof for population.
public class HouseBirthTests
{
    private const long Gestation = 50;
    private const int Food = 5;

    private static readonly PopulationConfig Cfg = new(
        TicksPerYear: 10,
        MinTrainAge: 15,
        MinFertileAge: 18,
        MaxFertileAge: 40,
        GestationTicks: Gestation,
        BirthFoodCost: Food,
        LifespanMinYears: 500, // very long so tests don't trip old-age
        LifespanMaxYears: 500);

    private static Simulation MakeReadyToBreed(int ageA = 25, int ageB = 25)
    {
        var tile = new TileCoord(5, 5);
        var spec = new GenesisSpec
        {
            Width = 10, Height = 10,
            Population = Cfg,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    StartingAgeYears = 25,
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, tile, UnitRole.Builder, StartingAgeYears: ageA),
                        new UnitSpawn(2, tile, UnitRole.Builder, StartingAgeYears: ageB),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xA8E);
        var house = sim.World.AddStructure(new House(tile) { OwnerId = 0 });
        house.Deposit(Resource.Food, Food);
        return sim;
    }

    [Fact]
    public void Breeding_Completes_SpawnsChildWithNoRole_ParentsFreed()
    {
        var sim = MakeReadyToBreed();
        var tile = new TileCoord(5, 5);
        sim.SubmitIntent(0, new BeginBreedingIntent(tile, 1, 2));
        sim.Run(until: Gestation);

        // House vacated.
        var house = (House)sim.World.Structures[tile];
        Assert.Null(house.Occupation);

        // Parents Idle again.
        Assert.Equal(Activity.Idle, sim.World.Units[1].Activity);
        Assert.Equal(Activity.Idle, sim.World.Units[2].Activity);

        // A child spawned on the house tile, role-less, owned by player 0.
        var children = sim.World.Units.Values.Where(u => u.OwnerId == 0 && u.Role == UnitRole.None).ToList();
        Assert.Single(children);
        Assert.Equal(tile, children[0].Position);
        Assert.Equal(Gestation, children[0].BornTick);
        Assert.NotNull(children[0].DeathTick);
    }

    [Fact]
    public void Child_BornAtCurrentTick_HasOwnLifespan()
    {
        var sim = MakeReadyToBreed();
        sim.SubmitIntent(0, new BeginBreedingIntent(new TileCoord(5, 5), 1, 2));
        sim.Run(until: Gestation);
        var child = sim.World.Units.Values.First(u => u.Role == UnitRole.None && u.OwnerId == 0);
        // Lifespan = 500 years * 10 ticks/year = 5000; born at tick 50.
        Assert.Equal(Gestation + 5000, child.DeathTick);
    }

    [Fact]
    public void AttackedButSurvived_BreedingContinues()
    {
        // Inflict damage on a parent (Health drops) but they don't die →
        // breeding completes normally.
        var sim = MakeReadyToBreed();
        sim.SubmitIntent(0, new BeginBreedingIntent(new TileCoord(5, 5), 1, 2));
        sim.Run(until: Gestation / 2);

        sim.World.Units[1].Health = 1; // simulate near-fatal damage, but alive
        sim.Run(until: Gestation);

        Assert.Single(sim.World.Units.Values.Where(u => u.OwnerId == 0 && u.Role == UnitRole.None));
    }

    [Fact]
    public void ParentAgesPast40_MidGestation_BreedingContinues()
    {
        // Parent at 40 (max fertility) — passes the gate; ages past 40
        // during gestation; still completes (window checked at start only).
        var sim = MakeReadyToBreed(ageA: 40, ageB: 25);
        sim.SubmitIntent(0, new BeginBreedingIntent(new TileCoord(5, 5), 1, 2));
        sim.Run(until: Gestation);

        var house = (House)sim.World.Structures[new TileCoord(5, 5)];
        Assert.Null(house.Occupation);
        Assert.Single(sim.World.Units.Values.Where(u => u.OwnerId == 0 && u.Role == UnitRole.None));
    }

    [Fact]
    public void Combat_KillsParent_MidGestation_StopsBreeding()
    {
        // Set up combat that kills parent 1 mid-gestation. The other parent
        // survives and is freed.
        var spec = new GenesisSpec
        {
            Width = 20, Height = 20,
            Population = Cfg,
            Combat = new CombatConfig(RoundIntervalTicks: 5),
            Diplomacy = new DiplomacyConfig(Delay: 5, ProposalExpiryTicks: 200),
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(0, 0),
                    StartingAgeYears = 25,
                    UnitSpawns = new[]
                    {
                        new UnitSpawn(1, new TileCoord(5, 5), UnitRole.Builder, StartingAgeYears: 25),
                        new UnitSpawn(2, new TileCoord(5, 5), UnitRole.Builder, StartingAgeYears: 25),
                    },
                },
                new FactionStartSpec { OwnerId = 1, CastlePosition = new TileCoord(15, 15) },
            },
        };
        var sim = new Simulation(spec, seed: 0xA8E);
        sim.World.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Enemy);
        var tile = new TileCoord(5, 5);
        var house = sim.World.AddStructure(new House(tile) { OwnerId = 0 });
        house.Deposit(Resource.Food, Food);

        sim.SubmitIntent(0, new BeginBreedingIntent(tile, 1, 2));
        sim.Run(until: Gestation / 2);

        // Overwhelming attack force on the house tile.
        for (var i = 0; i < 6; i++)
            sim.World.AddUnit(new Unit(200 + i, tile) { Role = UnitRole.Builder, OwnerId = 1 });
        CombatTrigger.MaybeBeginCombatOnTile(sim, tile);

        sim.Run(until: Gestation * 2);

        // Both parents dead in this overwhelming attack. Breeding ends
        // either via vacant House (parents killed first) or via the M24
        // siege razing the structure outright (combat keeps going after
        // the defenders fall) — both outcomes stop the pregnancy. Pin the
        // STOP, not the specific shape of the house tile.
        switch (sim.World.Structures[tile])
        {
            case House h: Assert.Null(h.Occupation); break;
            case Rubble: break;   // M24 — house razed; no pregnancy to track
            default: Assert.Fail($"unexpected structure at {tile}"); break;
        }
        // No child was born (BirthEvent fenced).
        Assert.Empty(sim.World.Units.Values.Where(u => u.OwnerId == 0 && u.Role == UnitRole.None));
    }

    [Fact]
    public void OldAge_KillsParent_MidGestation_StopsBreeding()
    {
        // Long-lifespan world (avoid both genesis units rolling
        // short-lifespan-floored deaths), then explicitly override parent
        // 1's DeathTick to land mid-gestation.
        var sim = MakeReadyToBreed();
        var tile = new TileCoord(5, 5);
        // Cancel the original DeathByAgeEvent by re-pointing the anchor;
        // the original event fences when it fires at the old tick.
        var midGestation = Gestation / 2;
        sim.World.Units[1].DeathTick = midGestation;
        sim.World.Units[1].DeathSeq = sim.Schedule(midGestation, new DeathByAgeEvent(1));

        sim.SubmitIntent(0, new BeginBreedingIntent(tile, 1, 2));
        sim.Run(until: Gestation);

        Assert.Null(((House)sim.World.Structures[tile]).Occupation);
        Assert.False(sim.World.Units.ContainsKey(1));
        // Parent 2 freed, alive.
        Assert.True(sim.World.Units.ContainsKey(2));
        Assert.Equal(Activity.Idle, sim.World.Units[2].Activity);
        // No child.
        Assert.Empty(sim.World.Units.Values.Where(u => u.Role == UnitRole.None && u.OwnerId == 0));
    }

    [Fact]
    public void Twin_BreedingScenario_HashesMatch()
    {
        Simulation Run()
        {
            var sim = MakeReadyToBreed();
            sim.SubmitIntent(0, new BeginBreedingIntent(new TileCoord(5, 5), 1, 2));
            sim.Run(until: Gestation + 20);
            return sim;
        }
        Assert.Equal(Snapshot.Hash(Run()), Snapshot.Hash(Run()));
    }

    // THE CRUX — M4 regen proof for population.
    [Fact]
    public void MidGestation_SnapshotRoundTrip_BirthFiresAtSameTick()
    {
        // Path A: uninterrupted.
        var simA = MakeReadyToBreed();
        simA.SubmitIntent(0, new BeginBreedingIntent(new TileCoord(5, 5), 1, 2));
        simA.Run(until: Gestation + 20);
        var hashA = Snapshot.Hash(simA);

        // Path B: snapshot at half-gestation, restore, finish.
        var simB = MakeReadyToBreed();
        simB.SubmitIntent(0, new BeginBreedingIntent(new TileCoord(5, 5), 1, 2));
        simB.Run(until: Gestation / 2);
        var house = (House)simB.World.Structures[new TileCoord(5, 5)];
        Assert.NotNull(house.Occupation);
        var bytes = Snapshot.Serialize(simB);
        var restored = Snapshot.Restore(bytes, seed: 0xA8E);
        Assert.NotNull(((House)restored.World.Structures[new TileCoord(5, 5)]).Occupation);

        restored.Run(until: Gestation + 20);
        Assert.Equal(hashA, Snapshot.Hash(restored));
    }
}
