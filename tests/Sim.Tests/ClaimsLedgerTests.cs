using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Persistence;
using Sim.Core.World;

namespace Sim.Tests;

// Automation substrate, Phase A (docs/automation-substrate.md) — the Claims
// Ledger: claim-on-commit, at-most-one-claim-per-unit as the arbitration
// mechanism, derived dormancy, the Protected flag, and the durability
// contract (claims survive snapshot round-trip AND replay from the log).
//
// These pin Layer 0. Everything the substrate builds later — selectors,
// Maintain thermostats, in-flight-aware quota conditions — assumes exactly
// these semantics.
public class ClaimsLedgerTests
{
    private static Simulation BuildWorld()
    {
        var grid = new TileGrid(10, 10, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        world.AddUnit(new Unit(1, new TileCoord(1, 1)) { Role = UnitRole.Hauler });
        world.AddUnit(new Unit(2, new TileCoord(2, 2)) { Role = UnitRole.Farmer });
        world.AddUnit(new Unit(3, new TileCoord(3, 3)) { Role = UnitRole.None });
        world.NextUnitId = 4;
        return new Simulation(world, seed: 11);
    }

    private static IntentOutcome Submit(Simulation sim, long at, Intent intent)
    {
        sim.SubmitIntent(at, intent);
        sim.Run(at);
        var ev = Assert.IsType<IntentEvent>(sim.ResolvedLog[^1]);
        return ev.Outcome;
    }

    // ---- claim-on-commit + the arbitration fence -------------------------

    [Fact]
    public void Claim_RecordsLedgerEntry_AndRemovesUnitFromTheDormantPool()
    {
        var sim = BuildWorld();
        var unit = sim.World.Units[1];
        Assert.True(ClaimLedger.IsDormant(sim.World, unit));

        Assert.False(Submit(sim, 0,
            ClaimUnitIntent.Claim(unitId: 1, orderId: 7, ClaimPurpose.Crew)).IsRejected);

        Assert.True(ClaimLedger.IsClaimed(sim.World, 1));
        Assert.Equal(7, ClaimLedger.ClaimOf(sim.World, 1)!.Value.OrderId);
        // THE POINT of claim-on-commit: the pool shrinks the moment an order
        // commits, so a later evaluation in the same pass can't grab them.
        Assert.False(ClaimLedger.IsDormant(sim.World, unit));
    }

    [Fact]
    public void SecondOrderClaimingTheSameUnit_IsRejected_TheLedgerIsTheMutex()
    {
        var sim = BuildWorld();
        Assert.False(Submit(sim, 0,
            ClaimUnitIntent.Claim(1, orderId: 7, ClaimPurpose.InFlight)).IsRejected);

        var loser = Submit(sim, 0, ClaimUnitIntent.Claim(1, orderId: 9, ClaimPurpose.InFlight));

        Assert.True(loser.IsRejected);
        Assert.Contains("already claimed", loser.Reason);
        // Winner keeps it — first-come in canonical order, deterministically.
        Assert.Equal(7, ClaimLedger.ClaimOf(sim.World, 1)!.Value.OrderId);
    }

    [Fact]
    public void Release_ReturnsUnitToThePool_ButOnlyForTheOwningOrder()
    {
        var sim = BuildWorld();
        Submit(sim, 0, ClaimUnitIntent.Claim(1, orderId: 7, ClaimPurpose.Crew));

        var wrongOrder = Submit(sim, 0, ClaimUnitIntent.Release(1, orderId: 9));
        Assert.True(wrongOrder.IsRejected);
        Assert.True(ClaimLedger.IsClaimed(sim.World, 1));

        Assert.False(Submit(sim, 0, ClaimUnitIntent.Release(1, orderId: 7)).IsRejected);
        Assert.False(ClaimLedger.IsClaimed(sim.World, 1));
        Assert.True(ClaimLedger.IsDormant(sim.World, sim.World.Units[1]));
    }

    [Fact]
    public void Repurpose_FlipsInFlightToCrew_WhenTheErrandLands()
    {
        var sim = BuildWorld();
        Submit(sim, 0, ClaimUnitIntent.Claim(2, orderId: 4, ClaimPurpose.InFlight));

        Assert.False(Submit(sim, 0,
            ClaimUnitIntent.Repurpose(2, orderId: 4, ClaimPurpose.Crew)).IsRejected);

        Assert.Equal(ClaimPurpose.Crew, ClaimLedger.ClaimOf(sim.World, 2)!.Value.Purpose);
        // Same purpose twice is a no-op the caller should know about.
        Assert.True(Submit(sim, 0,
            ClaimUnitIntent.Repurpose(2, orderId: 4, ClaimPurpose.Crew)).IsRejected);
    }

    [Fact]
    public void ClaimingAnotherPlayersUnit_IsRejected()
    {
        var sim = BuildWorld();
        sim.World.AddUnit(new Unit(9, new TileCoord(5, 5)) { OwnerId = 1, Role = UnitRole.Hauler });

        // Player 0 reaching for player 1's unit (PlayerId defaults to 0).
        var intent = ClaimUnitIntent.Claim(9, orderId: 7, ClaimPurpose.Crew);

        Assert.True(Submit(sim, 0, intent).IsRejected);
        Assert.False(ClaimLedger.IsClaimed(sim.World, 9));
    }

    [Fact]
    public void ClaimingADeadUnit_IsRejected()
    {
        var sim = BuildWorld();
        sim.World.Units.Remove(2);

        Assert.True(Submit(sim, 0,
            ClaimUnitIntent.Claim(2, orderId: 7, ClaimPurpose.Crew)).IsRejected);
    }

    // ---- derived dormancy ------------------------------------------------

    [Fact]
    public void Dormancy_IsDerived_NotStored_AndTracksEveryAvailabilitySignal()
    {
        var sim = BuildWorld();
        var world = sim.World;
        var unit = world.Units[1];
        Assert.True(ClaimLedger.IsDormant(world, unit));

        // Marching — the M16 pitfall: a marching unit still reads Idle, so
        // dormancy MUST consult the anchors, not Activity.
        unit.SubtileRoute = new List<Sim.Core.Battlefields.WorldSubtile> { new(4, 4) };
        Assert.False(ClaimLedger.IsDormant(world, unit));
        unit.SubtileRoute = null;
        Assert.True(ClaimLedger.IsDormant(world, unit));

        // Working.
        Assert.True(unit.TrySetActivity(Activity.Working, new TileCoord(1, 1)));
        Assert.False(ClaimLedger.IsDormant(world, unit));
        Assert.True(unit.TrySetActivity(Activity.Idle));

        // Under its group's command — members move as one; not individually
        // available. A DISMISSED group's member is free (M46): membership alone
        // blocks nothing.
        var group = new Sim.Core.Groups.Group(3) { OwnerId = unit.OwnerId, State = Sim.Core.Groups.GroupState.Idle };
        world.Groups[3] = group;
        unit.GroupId = 3;
        Assert.False(ClaimLedger.IsDormant(world, unit));
        group.State = Sim.Core.Groups.GroupState.Dismissed;
        Assert.True(ClaimLedger.IsDormant(world, unit));
        unit.GroupId = null;
        world.Groups.Remove(3);

        // A SCHEDULED death is not death. Every genesis unit carries a
        // pre-rolled DeathTick (the old-age date) from tick 0; actual death
        // removes the unit from world.Units, so presence == alive. The first
        // cut asserted the opposite here, which pinned the bug that made
        // every unit on a real server read as a corpse (SubstrateLifespanTests).
        unit.DeathTick = 100;
        Assert.True(ClaimLedger.IsDormant(world, unit));
    }

    [Fact]
    public void HeldBy_CountsPerOrder_AndPerPurpose()
    {
        var sim = BuildWorld();
        Submit(sim, 0, ClaimUnitIntent.Claim(1, orderId: 7, ClaimPurpose.Crew));
        Submit(sim, 0, ClaimUnitIntent.Claim(2, orderId: 7, ClaimPurpose.InFlight));
        Submit(sim, 0, ClaimUnitIntent.Claim(3, orderId: 8, ClaimPurpose.Crew));

        Assert.Equal(2, ClaimLedger.HeldBy(sim.World, 7));
        Assert.Equal(1, ClaimLedger.HeldBy(sim.World, 7, ClaimPurpose.Crew));
        Assert.Equal(1, ClaimLedger.HeldBy(sim.World, 7, ClaimPurpose.InFlight));
        Assert.Equal(new List<int> { 1, 2 }, ClaimLedger.UnitsOf(sim.World, 7));
    }

    [Fact]
    public void InFlight_CountsOnlyTransientClaims_OfMatchingOrders()
    {
        var sim = BuildWorld();
        // Order 7 produces haulers; order 8 produces something else.
        Submit(sim, 0, ClaimUnitIntent.Claim(1, orderId: 7, ClaimPurpose.InFlight));
        Submit(sim, 0, ClaimUnitIntent.Claim(2, orderId: 7, ClaimPurpose.Crew));
        Submit(sim, 0, ClaimUnitIntent.Claim(3, orderId: 8, ClaimPurpose.InFlight));

        // This is the count a quota condition adds to live headcount so a
        // redundant producer reads "already covered" and stands down.
        Assert.Equal(1, ClaimLedger.InFlight(sim.World, orderId => orderId == 7));
    }

    // ---- Protected -------------------------------------------------------

    [Fact]
    public void Protected_IsAPlainFlag_ThatSurvivesRoundTrip()
    {
        var sim = BuildWorld();
        sim.World.Units[1].Protected = true;

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 11);

        Assert.True(restored.World.Units[1].Protected);
        Assert.False(restored.World.Units[2].Protected);
        // Protection is about conscription, not availability: a Protected
        // unit that is genuinely idle is still dormant.
        Assert.True(ClaimLedger.IsDormant(restored.World, restored.World.Units[1]));
    }

    // ---- durability ------------------------------------------------------

    [Fact]
    public void Claims_RoundTripTheSnapshot_ByteIdentical()
    {
        var sim = BuildWorld();
        Submit(sim, 0, ClaimUnitIntent.Claim(1, orderId: 7, ClaimPurpose.Crew));
        Submit(sim, 0, ClaimUnitIntent.Claim(3, orderId: 8, ClaimPurpose.InFlight));

        var bytes = Snapshot.Serialize(sim);
        var restored = Snapshot.Restore(bytes, seed: 11);

        Assert.Equal(2, restored.World.Claims.Count);
        Assert.Equal(7, ClaimLedger.ClaimOf(restored.World, 1)!.Value.OrderId);
        Assert.Equal(ClaimPurpose.Crew, ClaimLedger.ClaimOf(restored.World, 1)!.Value.Purpose);
        Assert.Equal(8, ClaimLedger.ClaimOf(restored.World, 3)!.Value.OrderId);
        Assert.Equal(ClaimPurpose.InFlight, ClaimLedger.ClaimOf(restored.World, 3)!.Value.Purpose);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void InFlightPullSurvivesRestore_TheUnitStaysCommitted()
    {
        // The recovery scenario the durability contract exists for: a unit
        // is walking to a school under an InFlight claim when the process
        // dies. After restore it must still be spoken for — otherwise the
        // driver re-issues the pull against a unit already on its way.
        var sim = BuildWorld();
        var trainee = sim.World.Units[3];
        trainee.SubtileRoute = new List<Sim.Core.Battlefields.WorldSubtile> { new(16, 16), new(16, 17) };
        trainee.SubtileRouteTick = 300;
        Submit(sim, 0, ClaimUnitIntent.Claim(3, orderId: 12, ClaimPurpose.InFlight));

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 11);

        Assert.True(ClaimLedger.IsClaimed(restored.World, 3));
        Assert.False(ClaimLedger.IsDormant(restored.World, restored.World.Units[3]));
    }

    [Fact]
    public void ClaimIntents_ReplayFromTheLog_ReproducingTheLedger()
    {
        // The headline contract, ledger edition: claims are durable intents,
        // so replaying the log rebuilds the ledger with no driver present.
        var sim = BuildWorld();
        Submit(sim, 0, ClaimUnitIntent.Claim(1, orderId: 7, ClaimPurpose.Crew));
        Submit(sim, 10, ClaimUnitIntent.Claim(2, orderId: 7, ClaimPurpose.InFlight));
        Submit(sim, 20, ClaimUnitIntent.Repurpose(2, orderId: 7, ClaimPurpose.Crew));
        Submit(sim, 30, ClaimUnitIntent.Release(1, orderId: 7));

        var replay = BuildWorld();
        foreach (var ev in sim.ResolvedLog.OfType<IntentEvent>())
        {
            if (ev.Outcome.IsRejected) continue;
            replay.SubmitIntent(ev.At, ev.Intent);
            replay.Run(ev.At);
        }

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(replay));
        Assert.False(ClaimLedger.IsClaimed(replay.World, 1));
        Assert.Equal(ClaimPurpose.Crew, ClaimLedger.ClaimOf(replay.World, 2)!.Value.Purpose);
    }

    [Fact]
    public void ClaimIntent_SurvivesDurableJsonRoundTrip()
    {
        // Durable intents must round-trip their JSON or recovery breaks on
        // the first log that contains one (IntentJson.TypeNames is frozen).
        var original = ClaimUnitIntent.Claim(5, orderId: 3, ClaimPurpose.InFlight);

        var (typeName, json) = Sim.Persistence.IntentJson.Serialize(original);
        var back = Assert.IsType<ClaimUnitIntent>(
            Sim.Persistence.IntentJson.Deserialize(typeName, json));

        Assert.Equal("ClaimUnitIntent", typeName);
        Assert.Equal(5, back.UnitId);
        Assert.Equal(3, back.OrderId);
        Assert.Equal(ClaimOp.Claim, back.Op);
        Assert.Equal(ClaimPurpose.InFlight, back.Purpose);
    }
}
