using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Groups;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Rivers;
using Sim.Core.Roads;
using Sim.Core.World;

namespace Sim.Tests.Battlefields;

// M42 phase 3 (docs/subtile-movement.md, "Subtile moves"): a drawn chain of
// subtiles, walked exactly, at quarter-hop pace, with no road and a ford at river
// edges; blocked steps wait and never drop the route.
public class SubtileRouteTests
{
    private const int Blue = 0, Red = 1;
    private static readonly TileCoord Keep = new(10, 10);
    private static readonly TileCoord A = new(5, 5);
    private static readonly TileCoord B = new(6, 5);

    private static GameWorld World(Dictionary<TileCoord, RiverEdge>? rivers = null)
    {
        var w = Genesis.Build(new GenesisSpec
        {
            Width = 21, Height = 21,
            Rivers = rivers ?? new Dictionary<TileCoord, RiverEdge>(),
            Combat = new CombatConfig(RoundIntervalTicks: 60),
            FactionStarts = new[]
            {
                new FactionStartSpec { OwnerId = Blue, CastlePosition = Keep },
                new FactionStartSpec { OwnerId = Red, CastlePosition = new TileCoord(19, 19) },
            },
        });
        w.Diplomacy.SetState(FactionPair.Of(Blue, Red), RelationshipState.Enemy);
        return w;
    }

    private static int _nextId = 9000;
    private static Unit Put(GameWorld w, int owner, TileCoord at, Subtile on, UnitRole role = UnitRole.Farmer)
    {
        var u = new Unit(_nextId++, at) { Role = role, OwnerId = owner };
        w.AddUnit(u);
        u.Subtile = on;
        return u;
    }

    private static WorldSubtile W(TileCoord t, int x, int y) => WorldSubtile.Of(t, new Subtile(x, y));

    private static IntentOutcome Route(Simulation sim, Unit u, params WorldSubtile[] steps)
    {
        var i = new SubtileRouteIntent(u.Id, steps) { PlayerId = u.OwnerId };
        return i.Resolve(sim);
    }

    private static long RunUntil(Simulation sim, Func<bool> done, long limit = 3000)
    {
        for (var t = sim.Now + 1; t <= limit; t++)
        {
            sim.Run(until: t);
            if (done()) return t;
        }
        Assert.Fail("condition never held");
        return -1;
    }

    private static long Step(GameWorld w, TileCoord tile) => SubtileStepRules.StepTicks(w, tile);

    // ---- WorldSubtile ------------------------------------------------------------------

    [Fact]
    public void AWorldSubtile_NamesATileAndItsSubtile_AcrossTheWholeMap()
    {
        var s = W(new TileCoord(3, 7), 2, 1);
        Assert.Equal(new WorldSubtile(14, 29), s);
        Assert.Equal(new TileCoord(3, 7), s.Tile);
        Assert.Equal(new Subtile(2, 1), s.Sub);
        Assert.Equal(new TileCoord(-1, 0), new WorldSubtile(-1, 0).Tile);
        Assert.Equal(new Subtile(3, 0), new WorldSubtile(-1, 0).Sub);
        Assert.Equal(Heading.East, new WorldSubtile(3, 1).HeadingTo(new WorldSubtile(4, 1)));
        Assert.Null(new WorldSubtile(3, 1).HeadingTo(new WorldSubtile(4, 2)));
    }

    // ---- walking ----------------------------------------------------------------------

    [Fact]
    public void ARoute_IsWalkedExactlyAsDrawn_AtQuarterHopPace()
    {
        var w = World();
        var u = Put(w, Blue, A, new Subtile(0, 0));
        var sim = new Simulation(w, seed: 1);
        // The long way round a corner, not the shortest way.
        var steps = new[] { W(A, 1, 0), W(A, 2, 0), W(A, 2, 1), W(A, 2, 2), W(A, 1, 2) };
        Assert.False(Route(sim, u, steps).IsRejected);

        var seen = new List<Subtile?>();
        var end = sim.Now;
        foreach (var s in steps)
        {
            end = RunUntil(sim, () => u.Subtile == s.Sub);
            seen.Add(u.Subtile);
        }
        Assert.Equal(steps.Select(s => (Subtile?)s.Sub), seen);
        Assert.Equal(steps.Length * Step(w, A), end);      // a quarter hop a step, no shortcuts
        Assert.Null(u.SubtileRoute);
        Assert.Null(u.SubtileRouteSeq);
    }

    [Fact]
    public void AStepAcrossATileEdge_IsATileEntry_AtAQuarterHop_NotAWholeHop()
    {
        var w = World();
        var u = Put(w, Blue, A, new Subtile(3, 1));
        var sim = new Simulation(w, seed: 1);
        Assert.False(Route(sim, u, W(B, 0, 1)).IsRejected);
        var t = RunUntil(sim, () => u.Position == B);
        Assert.Equal(Step(w, B), t);
        Assert.Equal(new Subtile(0, 1), u.Subtile);
        Assert.Equal(A, u.EnteredFrom);
    }

    [Fact]
    public void ARoute_RidesTheRoadItStepsOn_AndWearsIt()
    {
        var w = World();
        var from = W(A, 3, 1);
        var to = W(B, 0, 1);
        var link = SubtileLink.Between(from, to);
        w.Roads[link] = new RoadState(RoadConstants.CONDITION_MAX, 0);
        var u = Put(w, Blue, A, new Subtile(3, 1));
        var sim = new Simulation(w, seed: 1);
        Route(sim, u, to);
        var t = RunUntil(sim, () => u.Position == B);
        var plain = (int)Step(w, B);
        var worn = Math.Max(RoadConstants.MIN_COST, plain - (int)((long)plain * RoadConstants.MAX_REDUCTION_PERCENT / 100L));
        Assert.Equal(worn, t);                             // the worn step, not the plain quarter hop
        Assert.True(worn < plain);
        Assert.Equal(RoadConstants.CONDITION_MAX, w.Roads[link].Condition);   // and it stays worn at the cap
    }

    [Fact]
    public void ARiverEdge_CostsTheFord_ToStepAcross()
    {
        var w = World(new Dictionary<TileCoord, RiverEdge>
        {
            [A] = RiverEdge.East,
            [B] = RiverEdge.West,
        });
        var u = Put(w, Blue, A, new Subtile(3, 1));
        var sim = new Simulation(w, seed: 1);
        Route(sim, u, W(B, 0, 1));
        var t = RunUntil(sim, () => u.Position == B);
        Assert.Equal(Step(w, B) + RiverConstants.CrossingCost, t);
    }

    [Fact]
    public void ARoute_MayCrossSeveralTilesWithinTheBlock()
    {
        var w = World();
        var u = Put(w, Blue, A, new Subtile(2, 1));
        var sim = new Simulation(w, seed: 1);
        var south = new TileCoord(B.X, B.Y + 1);
        // East into B, along it, then south into the tile below it: two edges crossed.
        var r = Route(sim, u, W(A, 3, 1), W(B, 0, 1), W(B, 1, 1), W(B, 1, 2), W(B, 1, 3), W(south, 1, 0), W(south, 1, 1));
        Assert.False(r.IsRejected, r.Reason);
        RunUntil(sim, () => u.SubtileRoute is null);
        Assert.Equal(south, u.Position);
        Assert.Equal(new Subtile(1, 1), u.Subtile);
        AssertNoFriendsShare(w);
    }

    // ---- refusals -----------------------------------------------------------------------

    [Fact]
    public void ARouteWithAGap_IsRefused()
    {
        var w = World();
        var u = Put(w, Blue, A, new Subtile(0, 0));
        var sim = new Simulation(w, seed: 1);
        var r = Route(sim, u, W(A, 1, 0), W(A, 3, 0));
        Assert.True(r.IsRejected);
        Assert.Contains("next to", r.Reason);
        Assert.True(Route(sim, u, W(A, 1, 1)).IsRejected);   // diagonal from (0,0)
        Assert.Null(u.SubtileRoute);
    }

    [Fact]
    public void ARoute_LeavingTheThreeByThreeBlock_IsRefused()
    {
        var w = World();
        var u = Put(w, Blue, A, new Subtile(3, 1));
        var sim = new Simulation(w, seed: 1);
        var east = Enumerable.Range(0, 5).Select(i => new WorldSubtile(W(A, 3, 1).X + 1 + i, W(A, 3, 1).Y)).ToArray();
        // The block is the tile it starts on and one tile all round: up to the far side of B is fine...
        var ok = Route(sim, u, east.Take(4).ToArray());
        Assert.False(ok.IsRejected, ok.Reason);
        // ...one more subtile is in the second tile away: refused.
        var r = Route(sim, u, east);
        Assert.True(r.IsRejected);
        Assert.Contains("3×3", r.Reason);
    }

    [Fact]
    public void ARoute_LongerThanTheLimit_IsRefused()
    {
        var w = World();
        var u = Put(w, Blue, A, new Subtile(0, 0));
        var sim = new Simulation(w, seed: 1);
        var steps = new List<WorldSubtile>();
        for (var i = 0; i < SubtileRoutes.MaxSteps + 1; i++)   // back and forth along a row
            steps.Add(new WorldSubtile(W(A, 0, 0).X + (i % 2 == 0 ? 1 : 0), W(A, 0, 0).Y));
        var r = Route(sim, u, steps.ToArray());
        Assert.True(r.IsRejected);
        Assert.Contains("at most", r.Reason);
        Assert.False(Route(sim, u, steps.Take(SubtileRoutes.MaxSteps).ToArray()).IsRejected);
    }

    [Fact]
    public void RoutesAreRefused_ForAWall_Water_TheMapEdge_AnotherPlayersUnit_AndAGroupedUnit()
    {
        var w = World();
        w.AddStructure(new Castle(B) { OwnerId = Blue });   // its ring: a hostile can't step through it
        var raider = Put(w, Red, A, new Subtile(3, 0), UnitRole.Soldier);
        var sim = new Simulation(w, seed: 1);
        // The castle's north-west ring subtile (0,0): closed on its outer sides to an enemy.
        Assert.True(Route(sim, raider, W(B, 0, 0)).IsRejected);
        // Someone else's unit.
        var mine = Put(w, Blue, A, new Subtile(0, 0));
        Assert.True(new SubtileRouteIntent(mine.Id, new[] { W(A, 1, 0) }) { PlayerId = Red }.Resolve(sim).IsRejected);
        // Water.
        var lake = new TileCoord(A.X, A.Y - 1);
        w.Grid.SetBiome(lake, Biome.Water);
        var s2 = Put(w, Blue, A, new Subtile(1, 0));
        var wet = Route(sim, s2, W(lake, 1, 3));
        Assert.True(wet.IsRejected);
        Assert.Contains("water", wet.Reason);
        // A grouped unit takes group orders.
        var g1 = Put(w, Blue, new TileCoord(2, 2), new Subtile(0, 0));
        var g2 = Put(w, Blue, new TileCoord(2, 2), new Subtile(1, 0));
        sim.SubmitIntent(0, new FormGroupIntent(new[] { g1.Id, g2.Id }, new TileCoord(2, 2)));
        sim.Run(until: 0);
        Assert.True(Route(sim, g1, W(new TileCoord(2, 2), 0, 1)).IsRejected);
        // The edge of the map.
        var corner = Put(w, Blue, new TileCoord(0, 0), new Subtile(0, 0));
        Assert.True(Route(sim, corner, new WorldSubtile(-1, 0)).IsRejected);
    }

    // ---- waiting, never dropping ------------------------------------------------------------

    [Fact]
    public void AFriendOnTheWay_IsPassedThrough_NobodyBlocksAWalk()
    {
        // The user (2026-09-29): moving friends pass through each other so a busy road doesn't jam;
        // only STOPPING needs room. The friend stands on a step of the route and the walker
        // simply steps over the same subtile and goes on.
        var w = World();
        var u = Put(w, Blue, A, new Subtile(0, 0));
        var friend = Put(w, Blue, A, new Subtile(1, 0));
        var sim = new Simulation(w, seed: 1);
        Route(sim, u, W(A, 1, 0), W(A, 2, 0));
        var shared = false;
        RunUntil(sim, () =>
        {
            if (u.Subtile == new Subtile(1, 0)) shared = true;      // on the friend's subtile, passing
            return u.Subtile == new Subtile(2, 0);
        }, limit: 3 * Step(w, A) + 2);
        Assert.True(shared);
        Assert.Equal(new Subtile(1, 0), friend.Subtile);            // the friend never moved
        Assert.Null(u.SubtileRoute);
    }

    [Fact]
    public void StoppingOnAFriend_ReAimsToTheNearestFreeSubtile()
    {
        var w = World();
        var u = Put(w, Blue, A, new Subtile(0, 0));
        var friend = Put(w, Blue, A, new Subtile(2, 0));
        var sim = new Simulation(w, seed: 1);
        Route(sim, u, W(A, 1, 0), W(A, 2, 0));
        RunUntil(sim, () => u.SubtileRoute is null, limit: 12 * Step(w, A) + 2);
        var at = WorldSubtile.Of(u.Position, u.Subtile!.Value);
        var they = WorldSubtile.Of(friend.Position, friend.Subtile!.Value);
        Assert.NotEqual(they, at);                                  // not on the friend
        Assert.Equal(1, Math.Abs(at.X - they.X) + Math.Abs(at.Y - they.Y));   // right beside them
        Assert.Equal(new Subtile(2, 0), friend.Subtile);
    }

    [Fact]
    public void ANeutralStandingOnTheLastStep_HoldsItToo()
    {
        // Only enemies share a subtile while standing: a neutral is not an enemy.
        var w = World();
        var u = Put(w, Blue, A, new Subtile(0, 0));
        var green = Put(w, 2, A, new Subtile(1, 0));
        var sim = new Simulation(w, seed: 1);
        Route(sim, u, W(A, 1, 0));
        RunUntil(sim, () => u.SubtileRoute is null, limit: 12 * Step(w, A) + 2);
        Assert.NotEqual(green.Subtile, u.Subtile);
    }

    [Fact]
    public void ARoute_CanBeCancelled_ByAnEmptyRoute_AndByAWorldMove()
    {
        var w = World();
        var u = Put(w, Blue, A, new Subtile(0, 0));
        var sim = new Simulation(w, seed: 1);
        Route(sim, u, W(A, 1, 0), W(A, 2, 0));
        Assert.NotNull(u.SubtileRoute);
        Assert.False(Route(sim, u).IsRejected);               // empty: cancel
        Assert.Null(u.SubtileRoute);
        sim.Run(until: 200);
        Assert.Equal(new Subtile(0, 0), u.Subtile);           // and it never moved

        Route(sim, u, W(A, 1, 0), W(A, 2, 0));
        sim.SubmitIntent(sim.Now, new MoveIntent(u.Id, new TileCoord(A.X, A.Y + 2)) { PlayerId = Blue });
        sim.Run(until: sim.Now);
        Assert.NotNull(u.PathFinalDest);                       // a tile walk replaced the drawn route
        Assert.True(u.SubtileRoute!.Count > 2);
    }

    [Fact]
    public void ANewRoute_TakesOverFromAWalkInFlight()
    {
        var w = World();
        var u = Put(w, Blue, A, new Subtile(0, 0));
        var sim = new Simulation(w, seed: 1);
        sim.SubmitIntent(0, new MoveIntent(u.Id, new TileCoord(A.X, A.Y + 3)) { PlayerId = Blue });
        sim.Run(until: 1);
        Assert.True(u.IsWalking);
        Assert.NotNull(u.PathFinalDest);
        Route(sim, u, W(A, 1, 0));
        Assert.Null(u.PathFinalDest);                          // a drawn route has no errand
        sim.Run(until: 400);
        Assert.Equal(A, u.Position);                           // the tile walk never went on
        Assert.Equal(new Subtile(1, 0), u.Subtile);
    }

    // ---- fog and ground truth ------------------------------------------------------------

    [Fact]
    public void AWallThatGoesUpAfterTheRouteWasDrawn_StopsTheUnit_AndEndsTheRoute()
    {
        // Everything in a route's 3×3 block is inside its own unit's sight, so a wall
        // can't be hidden when the route is drawn. One that goes up later is: the walk
        // checks the ground truth, finds it, and ends the route (waiting would be for ever).
        var w = World();
        var far = new TileCoord(18, 3);
        var after = new TileCoord(far.X + 1, far.Y);
        var u = Put(w, Blue, far, new Subtile(3, 1));
        var sim = new Simulation(w, seed: 1);
        var steps = Enumerable.Range(0, 4).Select(i => W(after, i, 1)).ToArray();
        var drawn = Route(sim, u, steps);
        Assert.False(drawn.IsRejected, drawn.Reason);

        w.AddStructure(new Wall(after) { OwnerId = Red, Facing = Heading.West });   // built while it walks
        sim.Run(until: 800);
        Assert.Null(u.SubtileRoute);
        Assert.Null(u.SubtileRouteSeq);
        Assert.Equal(far, u.Position);                          // it never got in
    }

    // ---- battles, saving, determinism ---------------------------------------------------------

    [Fact]
    public void AnEnemyArrivingOnTheUnitsTile_TurnsTheRouteIntoABattleRoute()
    {
        var w = World();
        var u = Put(w, Blue, A, new Subtile(0, 0), UnitRole.Soldier);
        var foe = Put(w, Red, new TileCoord(A.X - 1, A.Y), new Subtile(2, 2), UnitRole.Soldier);
        var sim = new Simulation(w, seed: 1);
        Route(sim, u, W(A, 1, 0), W(A, 2, 0), W(A, 3, 0), W(A, 3, 1), W(A, 3, 2), W(A, 3, 3));
        sim.SubmitIntent(0, new MoveIntent(foe.Id, A) { PlayerId = Red });
        RunUntil(sim, () => u.Board is not null);
        Assert.Null(u.SubtileRoute);                            // the board took it
        Assert.Null(u.SubtileRouteSeq);
        var order = u.Board!.Order!;
        Assert.Equal(BattleOrderKind.Route, order.Kind);
        // What was left of the route when the enemy came: the steps ahead, all inside the tile,
        // starting next to where the unit had got to and ending where it was drawn to end.
        Assert.InRange(order.Waypoints.Count, 1, 6);
        Assert.True(u.Subtile!.Value.IsAdjacentTo(order.Waypoints[0]));
        Assert.Equal(new Subtile(3, 3), order.Waypoints[^1]);
    }

    [Fact]
    public void ARouteGivenToAUnitOnAnOpenBoard_IsWalkedOneSubtileATurn()
    {
        var w = World();
        var u = Put(w, Blue, A, new Subtile(0, 0), UnitRole.Soldier);
        var foe = Put(w, Red, new TileCoord(A.X - 1, A.Y), new Subtile(2, 2), UnitRole.Soldier);
        var sim = new Simulation(w, seed: 1);
        sim.SubmitIntent(0, new MoveIntent(foe.Id, A) { PlayerId = Red });
        RunUntil(sim, () => u.Board is not null);
        var r = Route(sim, u, W(A, 1, 0), W(A, 2, 0));
        Assert.False(r.IsRejected, r.Reason);
        Assert.Equal(BattleOrderKind.Route, u.Board!.Order!.Kind);
        Assert.Null(u.SubtileRoute);

        var bf = w.Battlefields[A];
        RunUntil(sim, () => bf.TurnNumber >= 1);                // the first turn: one subtile, not two
        Assert.Equal(new Subtile(1, 0), u.Subtile);
        var turn = w.CombatConfig.RoundIntervalTicks;
        RunUntil(sim, () => u.Subtile == new Subtile(2, 0));
        Assert.Equal(0, sim.Now % turn);                         // and only on a beat
    }

    [Fact]
    public void ASaveMidRoute_RestoresAndRunsToTheSameEnd()
    {
        var w = World();
        var u = Put(w, Blue, A, new Subtile(0, 0));
        var sim = new Simulation(w, seed: 7);
        Route(sim, u, W(A, 1, 0), W(A, 2, 0), W(A, 3, 0), W(B, 0, 0), W(B, 1, 0));
        sim.Run(until: 2 * Step(w, A) + 1);
        Assert.Equal(new Subtile(2, 0), u.Subtile);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 7);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        var ru = restored.World.Units[u.Id];
        Assert.Equal(u.SubtileRoute, ru.SubtileRoute);
        Assert.Equal(u.SubtileRouteTick, ru.SubtileRouteTick);

        sim.Run(until: 1000);
        restored.Run(until: 1000);
        Assert.Equal(B, ru.Position);
        Assert.Equal(new Subtile(1, 0), ru.Subtile);
        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
    }

    [Fact]
    public void ATwinRun_OfSeveralRoutes_HashesEqual()
    {
        string Run()
        {
            _nextId = 9500;
            var w = World();
            var sim = new Simulation(w, seed: 3);
            for (var y = 0; y < 4; y++)
            {
                var u = Put(w, Blue, A, new Subtile(0, y));
                Route(sim, u, new WorldSubtile(W(A, 0, y).X + 1, W(A, 0, y).Y), new WorldSubtile(W(A, 0, y).X + 2, W(A, 0, y).Y),
                    new WorldSubtile(W(A, 0, y).X + 3, W(A, 0, y).Y), new WorldSubtile(W(A, 0, y).X + 4, W(A, 0, y).Y));
            }
            sim.Run(until: 1000);
            return Snapshot.Hash(sim);
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void APooledWorld_RefusesRoutes()
    {
        var w = Genesis.Build(new GenesisSpec
        {
            Width = 21, Height = 21,
            FactionStarts = new[] { new FactionStartSpec { OwnerId = Blue, CastlePosition = Keep } },
        });
        var u = new Unit(_nextId++, A) { Role = UnitRole.Farmer, OwnerId = Blue };
        w.AddUnit(u);
        var r = new SubtileRouteIntent(u.Id, new[] { W(A, 1, 0) }) { PlayerId = Blue }.Resolve(new Simulation(w, seed: 1));
        Assert.True(r.IsRejected);
    }

    private static void AssertNoFriendsShare(GameWorld w)
    {
        foreach (var g in w.Units.Values.Where(u => u.Subtile is not null).GroupBy(u => (u.Position, u.Subtile)))
        {
            var us = g.ToList();
            for (var i = 0; i < us.Count; i++)
                for (var j = i + 1; j < us.Count; j++)
                    Assert.True(w.Diplomacy.AreHostile(us[i].OwnerId, us[j].OwnerId));
        }
    }
}
