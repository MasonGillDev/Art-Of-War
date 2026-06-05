using Sim.Core.Engine;
using Sim.Core.Movement;
using Sim.Core.Population;
using Sim.Core.World;

namespace Sim.Tests;

// M8 follow-up: a breeding parent is LOCKED to the house for the duration
// of the gestation cycle. MoveIntent rejects (the one case where
// MoveIntent is not authoritative); there is no cancellation. Parents are
// released only by BirthEvent (cycle completes) or stop-on-removal via
// combat / aging death. See the trace in the project chat that motivated
// this rule.
public class BreedingMoveLockTests
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
        LifespanMinYears: 500,
        LifespanMaxYears: 500);

    private static readonly TileCoord HouseAt = new(5, 5);

    private static Simulation MakeReadyToBreed()
    {
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
                        new UnitSpawn(1, HouseAt, UnitRole.Builder, StartingAgeYears: 25),
                        new UnitSpawn(2, HouseAt, UnitRole.Builder, StartingAgeYears: 25),
                        // A third unit on the same tile, NOT a breeding parent —
                        // used by the "non-parent on house tile" test to prove
                        // the lock is parent-scoped, not tile-scoped.
                        new UnitSpawn(3, HouseAt, UnitRole.Builder, StartingAgeYears: 25),
                    },
                },
            },
        };
        var sim = new Simulation(spec, seed: 0xB10C);
        var house = sim.World.AddStructure(new House(HouseAt) { OwnerId = 0 });
        house.Deposit(Resource.Food, Food);
        return sim;
    }

    private static void StartBreeding(Simulation sim)
    {
        sim.SubmitIntent(0, new BeginBreedingIntent(HouseAt, 1, 2));
        sim.Run(until: 0);
    }

    // ====================================================================
    // The headline: a breeding parent cannot be moved.
    // ====================================================================

    [Fact]
    public void MoveIntent_OnBreedingParent_Rejects_BreedingContinues()
    {
        var sim = MakeReadyToBreed();
        StartBreeding(sim);

        // Pre-conditions: parents working at house, occupation set.
        var parentA = sim.World.Units[1];
        Assert.Equal(Activity.Working, parentA.Activity);
        Assert.Equal(HouseAt, parentA.Position);
        var prePos = parentA.Position;
        var preEpoch = parentA.AssignmentEpoch;

        // Try to move parent A away.
        sim.SubmitIntent(sim.Now, new MoveIntent(1, new TileCoord(9, 9)));
        sim.Run(until: sim.Now);

        // Move rejected; parent A unchanged (still Working, still at house,
        // epoch never bumped).
        Assert.Equal(Activity.Working, parentA.Activity);
        Assert.Equal(prePos, parentA.Position);
        Assert.Equal(preEpoch, parentA.AssignmentEpoch);
        Assert.Null(parentA.PathRemaining);
        Assert.Null(parentA.PathFinalDest);

        // House occupation still intact — naming both parents.
        var house = (House)sim.World.Structures[HouseAt];
        Assert.NotNull(house.Occupation);
        Assert.True(house.Occupation!.ContainsParent(1));
        Assert.True(house.Occupation.ContainsParent(2));

        // Let the cycle complete: BirthEvent fires on time, child spawns.
        sim.Run(until: Gestation);
        Assert.Null(((House)sim.World.Structures[HouseAt]).Occupation);
        var children = sim.World.Units.Values
            .Where(u => u.Role == UnitRole.None && u.OwnerId == 0).ToList();
        Assert.Single(children);
        Assert.Equal(HouseAt, children[0].Position);
    }

    [Fact]
    public void MoveIntent_OnBreedingParent_RejectionMessage_NamesHouseTile()
    {
        // The player should learn WHERE the unit is locked, so the rejection
        // message includes the house's coordinates.
        var sim = MakeReadyToBreed();
        StartBreeding(sim);

        var intent = new MoveIntent(1, new TileCoord(9, 9));
        var outcome = intent.Resolve(sim);

        Assert.True(outcome.IsRejected);
        Assert.Contains("breeding", outcome.Reason!);
        Assert.Contains($"({HouseAt.X},{HouseAt.Y})", outcome.Reason);
    }

    // ====================================================================
    // Edge: the lock is parent-scoped, not tile-scoped. Non-parents standing
    // on the house tile can still move freely.
    // ====================================================================

    [Fact]
    public void MoveIntent_OnNonParentStandingOnHouseTile_StillWorks()
    {
        var sim = MakeReadyToBreed();
        StartBreeding(sim);

        // Unit 3 was spawned on the house tile but is NOT a breeding parent
        // (parents are 1 and 2). It should still be movable.
        var bystander = sim.World.Units[3];
        Assert.Equal(HouseAt, bystander.Position);
        Assert.Equal(Activity.Idle, bystander.Activity);

        var dest = new TileCoord(8, 5);
        sim.SubmitIntent(sim.Now, new MoveIntent(3, dest));
        sim.Run();

        Assert.Equal(dest, bystander.Position);
    }

    // ====================================================================
    // After birth, parents are free again — and movable normally.
    // ====================================================================

    [Fact]
    public void MoveIntent_AfterBirth_FreedParent_MovesNormally()
    {
        var sim = MakeReadyToBreed();
        StartBreeding(sim);
        sim.Run(until: Gestation);

        // Parents are Idle again.
        var parentA = sim.World.Units[1];
        Assert.Equal(Activity.Idle, parentA.Activity);

        // Move now works.
        var dest = new TileCoord(8, 5);
        sim.SubmitIntent(sim.Now, new MoveIntent(1, dest));
        sim.Run();

        Assert.Equal(dest, parentA.Position);
    }

    // ====================================================================
    // Determinism: a rejected MoveIntent must not perturb the simulation
    // hash. (Pure-precondition rejection is no-op territory.)
    // ====================================================================

    [Fact]
    public void RejectedMoveIntent_DoesNotMutateState()
    {
        var sim = MakeReadyToBreed();
        StartBreeding(sim);
        var hashBefore = Sim.Core.Persistence.Snapshot.Hash(sim);

        // Resolve directly — no SubmitIntent (that would log the intent,
        // changing the hash). The Resolve path is what should be no-op.
        for (var i = 0; i < 50; i++)
            new MoveIntent(1, new TileCoord(9, 9)).Resolve(sim);

        Assert.Equal(hashBefore, Sim.Core.Persistence.Snapshot.Hash(sim));
    }
}
