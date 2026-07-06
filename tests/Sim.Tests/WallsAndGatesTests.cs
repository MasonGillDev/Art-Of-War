using Sim.Core.Combat;
using Sim.Core.Diplomacy;
using Sim.Core.Engine;
using Sim.Core.Fortifications;
using Sim.Core.Logistics;
using Sim.Core.Movement;
using Sim.Core.Persistence;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Tests;

// M26 — walls & gates (docs/walls-and-gates.md). Pins:
//   * PlaceWallIntent: whole-line atomic validation, per-tile independent
//     sites (no canal-style cost scaling), fail-clean rejection.
//   * Blocking: entry-only, fog-split (planner sees own + visible blockers;
//     execution is ground truth at hop-schedule AND arrival-fire time).
//   * Gate: own/Ally passage, re-evaluated live (alliance broken mid-march
//     closes the gate).
//   * Adjacency sieges: combat state on the fort tile, forces gathered from
//     the 4-neighborhood, no defender shielding, raze → Rubble = breach.
//   * Determinism: pure-read blocking, snapshot round-trip, mid-siege
//     recovery, and the twin-run headline.
public class WallsAndGatesTests
{
    // ---- fixtures ------------------------------------------------------

    private static Simulation MakeSim(int size = 9)
    {
        var world = new GameWorld(new TileGrid(size, size, Biome.Grassland));
        world.Players[0] = new Player(0);
        world.Players[1] = new Player(1);
        return new Simulation(world, seed: 1);
    }

    private static void MakeEnemies(Simulation sim, int a = 0, int b = 1) =>
        sim.World.Diplomacy.SetState(FactionPair.Of(a, b), RelationshipState.Enemy);

    private static void MakeAllies(Simulation sim, int a = 0, int b = 1) =>
        sim.World.Diplomacy.SetState(FactionPair.Of(a, b), RelationshipState.Ally);

    private sealed class NoOpEvent : ScheduledEvent
    {
        public override void Apply(Simulation sim) { }
    }

    private static void AdvanceTo(Simulation sim, long tick)
    {
        if (tick <= sim.Now) return;
        sim.Schedule(tick, new NoOpEvent());
        sim.Run(until: tick);
    }

    // A standing wall column at x, spanning [y0, y1], owned by `owner`.
    private static void PlaceWallColumn(GameWorld world, int x, int y0, int y1, int owner)
    {
        for (var y = y0; y <= y1; y++)
            world.AddStructure(new Wall(new TileCoord(x, y)) { OwnerId = owner });
    }

    private static Unit AddUnit(Simulation sim, int id, TileCoord at, int owner,
        UnitRole role = UnitRole.Soldier)
    {
        var u = new Unit(id, at) { Role = role, OwnerId = owner };
        sim.World.AddUnit(u);
        return u;
    }

    // Plan a path the way MoveIntent.BeginMove does (fog-aware PlanCost).
    private static List<TileCoord>? Plan(Simulation sim, int playerId, TileCoord from, TileCoord to)
    {
        var visible = View.VisibleTiles(sim.World, playerId);
        return Pathfinding.FindPath(sim.World.Grid, from, to,
            t => MovementCost.PlanCost(sim.World, t, playerId, visible, sim.Now));
    }

    // ====================================================================
    // Catalog + spec plumbing
    // ====================================================================

    [Fact]
    public void WallAndGate_Specs_BlockingAndHealth()
    {
        var wall = StructureCatalog.Spec(StructureKind.Wall);
        var gate = StructureCatalog.Spec(StructureKind.Gate);

        Assert.True(wall.BlocksMovement);
        Assert.False(wall.AlliedPassage);           // a wall blocks its own owner
        Assert.True(gate.BlocksMovement);
        Assert.True(gate.AlliedPassage);
        Assert.True(wall.IsPlayerBuildable);
        Assert.True(gate.IsPlayerBuildable);
        // "High health so they are hard to destroy" — tougher than every
        // non-castle structure, gate the designated weak point.
        Assert.True(wall.BaseHealth > StructureCatalog.Spec(StructureKind.Barracks).BaseHealth);
        Assert.True(gate.BaseHealth < wall.BaseHealth);
        Assert.True(gate.BaseHealth > 0);

        var world = new GameWorld(new TileGrid(4, 4, Biome.Grassland));
        var w = world.AddStructure(new Wall(new TileCoord(0, 0)));
        var g = world.AddStructure(new Gate(new TileCoord(1, 0)));
        Assert.Equal(wall.BaseHealth, w.Health);
        Assert.Equal(gate.BaseHealth, g.Health);
    }

    // ====================================================================
    // PlaceWallIntent — whole-line validation, per-tile sites
    // ====================================================================

    [Fact]
    public void PlaceWall_Line_ExpandsToIndependentPerTileSites()
    {
        var sim = MakeSim();
        var path = new List<TileCoord> { new(4, 3), new(4, 4), new(4, 5) };

        Assert.True(new PlaceWallIntent(path) { PlayerId = 1 }.Resolve(sim).IsApplied);

        var spec = StructureCatalog.Spec(StructureKind.Wall);
        foreach (var t in path)
        {
            var site = Assert.IsType<ConstructionSite>(sim.World.Structures[t]);
            Assert.Equal(StructureKind.Wall, site.TargetKind);
            Assert.Equal(1, site.OwnerId);
            // PER-TILE pricing — no canal-style length scaling: N sites IS
            // the multiplication.
            foreach (var (r, n) in spec.BuildCost)
                Assert.Equal(n, site.Required[r]);
            Assert.Equal(spec.BuildDurationTicks, site.BuildDurationTicks);
            Assert.Empty(site.CanalPath);
        }
    }

    [Fact]
    public void PlaceWall_InvalidPaths_RejectFailClean()
    {
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(2, 2), Biome.Water);
        sim.World.AddStructure(new Stockpile(new TileCoord(5, 5)) { OwnerId = 0 });

        // Empty / too long.
        Assert.True(new PlaceWallIntent(new List<TileCoord>()) { PlayerId = 0 }.Resolve(sim).IsRejected);
        var tooLong = new List<TileCoord>();
        for (var i = 0; i <= PlaceWallIntent.MaxLength; i++)
            tooLong.Add(new TileCoord(i % 9, i / 9));
        Assert.True(new PlaceWallIntent(tooLong) { PlayerId = 0 }.Resolve(sim).IsRejected);

        // Duplicate tile.
        Assert.True(new PlaceWallIntent(new List<TileCoord> { new(1, 1), new(1, 1) })
        { PlayerId = 0 }.Resolve(sim).IsRejected);

        // Disconnected chain.
        Assert.True(new PlaceWallIntent(new List<TileCoord> { new(1, 1), new(3, 1) })
        { PlayerId = 0 }.Resolve(sim).IsRejected);

        // Out of bounds.
        Assert.True(new PlaceWallIntent(new List<TileCoord> { new(8, 8), new(9, 8) })
        { PlayerId = 0 }.Resolve(sim).IsRejected);

        // Water tile mid-line.
        Assert.True(new PlaceWallIntent(new List<TileCoord> { new(2, 1), new(2, 2) })
        { PlayerId = 0 }.Resolve(sim).IsRejected);

        // On a structure.
        Assert.True(new PlaceWallIntent(new List<TileCoord> { new(5, 4), new(5, 5) })
        { PlayerId = 0 }.Resolve(sim).IsRejected);

        // FAIL-CLEAN: nothing was placed by any rejection above.
        Assert.Single(sim.World.Structures);   // just the stockpile
    }

    [Fact]
    public void PlaceWall_OnClaimedOrCanalReservedTile_Rejected()
    {
        var sim = MakeSim(12);
        // A farm claiming (4,5): claims are physical territory (M15).
        var farm = new Extractor(StructureKind.Farm, new TileCoord(5, 5)) { OwnerId = 0 };
        farm.ClaimTiles.Add(new TileCoord(4, 5));
        sim.World.AddStructure(farm);
        Assert.True(new PlaceWallIntent(new List<TileCoord> { new(4, 4), new(4, 5) })
        { PlayerId = 0 }.Resolve(sim).IsRejected);

        // A tile promised to an in-flight canal is reserved (M21).
        sim.World.Grid.SetBiome(new TileCoord(0, 8), Biome.Water);
        Assert.True(new Sim.Core.Canals.PlaceCanalIntent(
            new List<TileCoord> { new(1, 8), new(2, 8) }) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.True(new PlaceWallIntent(new List<TileCoord> { new(2, 7), new(2, 8) })
        { PlayerId = 0 }.Resolve(sim).IsRejected);
    }

    [Fact]
    public void PlaceSite_RejectsWall_AcceptsGateOnLandOnly()
    {
        var sim = MakeSim();
        sim.World.Grid.SetBiome(new TileCoord(3, 3), Biome.Water);

        Assert.True(new PlaceSiteIntent(new TileCoord(2, 2), StructureKind.Wall)
        { PlayerId = 0 }.Resolve(sim).IsRejected);

        Assert.True(new PlaceSiteIntent(new TileCoord(3, 3), StructureKind.Gate)
        { PlayerId = 0 }.Resolve(sim).IsRejected);   // no gates in open water

        Assert.True(new PlaceSiteIntent(new TileCoord(2, 2), StructureKind.Gate)
        { PlayerId = 0 }.Resolve(sim).IsApplied);
        var site = Assert.IsType<ConstructionSite>(sim.World.Structures[new TileCoord(2, 2)]);
        Assert.Equal(StructureKind.Gate, site.TargetKind);
    }

    [Fact]
    public void WallSite_CompletesIntoStandingWall_BuilderWalksOff()
    {
        var sim = MakeSim();
        var at = new TileCoord(4, 4);
        Assert.True(new PlaceWallIntent(new List<TileCoord> { at }) { PlayerId = 1 }
            .Resolve(sim).IsApplied);

        var site = (ConstructionSite)sim.World.Structures[at];
        foreach (var (r, n) in site.Required) site.Deposit(r, n);
        var builder = AddUnit(sim, 10, at, owner: 1, role: UnitRole.Builder);
        builder.TrySetActivity(Activity.Building, at);
        site.StartOrResume(sim);
        sim.Run();

        var wall = Assert.IsType<Wall>(sim.World.Structures[at]);
        Assert.Equal(StructureCatalog.Spec(StructureKind.Wall).BaseHealth, wall.Health);
        // An UNFINISHED wall didn't block; the finished one does — but only
        // on ENTRY. The builder freed on its tile walks off unharmed.
        Assert.True(Fortification.BlocksMover(sim.World, at, moverOwnerId: 0));
        Assert.True(Fortification.BlocksMover(sim.World, at, moverOwnerId: 1)); // even the owner
        Assert.True(new MoveIntent(builder.Id, new TileCoord(1, 1)) { PlayerId = 1 }
            .Resolve(sim).IsApplied);
        sim.Run();
        Assert.Equal(new TileCoord(1, 1), builder.Position);
    }

    // ====================================================================
    // Blocking — plan side (fog split) and gate passage
    // ====================================================================

    [Fact]
    public void BlocksMover_GroundTruth_GateOpensForOwnerAndAlly()
    {
        var sim = MakeSim();
        var wallAt = new TileCoord(4, 4);
        var gateAt = new TileCoord(5, 4);
        sim.World.AddStructure(new Wall(wallAt) { OwnerId = 1 });
        sim.World.AddStructure(new Gate(gateAt) { OwnerId = 1 });

        // Wall: blocks everyone, owner included.
        Assert.True(Fortification.BlocksMover(sim.World, wallAt, 0));
        Assert.True(Fortification.BlocksMover(sim.World, wallAt, 1));

        // Gate: owner passes; neutral blocked; ally passes; enemy blocked.
        Assert.False(Fortification.BlocksMover(sim.World, gateAt, 1));
        Assert.True(Fortification.BlocksMover(sim.World, gateAt, 0));   // neutral
        MakeAllies(sim);
        Assert.False(Fortification.BlocksMover(sim.World, gateAt, 0));
        MakeEnemies(sim);
        Assert.True(Fortification.BlocksMover(sim.World, gateAt, 0));

        // Non-blocking structures never block.
        var stock = new TileCoord(6, 4);
        sim.World.AddStructure(new Stockpile(stock) { OwnerId = 1 });
        Assert.False(Fortification.BlocksMover(sim.World, stock, 0));
    }

    [Fact]
    public void BlocksPlan_FogSplit_OwnAlwaysKnown_ForeignOnlyWhenVisible()
    {
        var sim = MakeSim();
        var mine = new TileCoord(2, 2);
        var theirs = new TileCoord(6, 6);
        sim.World.AddStructure(new Wall(mine) { OwnerId = 0 });
        sim.World.AddStructure(new Wall(theirs) { OwnerId = 1 });

        var noVision = new HashSet<TileCoord>();
        var fullVision = new HashSet<TileCoord> { mine, theirs };

        // Own wall bends the plan regardless of vision.
        Assert.True(Fortification.BlocksPlan(sim.World, mine, 0, noVision));
        // A foreign wall in fog is invisible to the planner...
        Assert.False(Fortification.BlocksPlan(sim.World, theirs, 0, noVision));
        // ...and a hard no-go once seen.
        Assert.True(Fortification.BlocksPlan(sim.World, theirs, 0, fullVision));
        // Ground truth disagrees with the fogged plan — the bonk is real.
        Assert.True(Fortification.BlocksMover(sim.World, theirs, 0));
    }

    [Fact]
    public void Pathfinding_RoutesThroughOwnGate_NotThroughOwnWalls()
    {
        var sim = MakeSim();
        // Own full column x=4 with a gate at (4,4): the only way east.
        PlaceWallColumn(sim.World, 4, 0, 3, owner: 0);
        PlaceWallColumn(sim.World, 4, 5, 8, owner: 0);
        sim.World.AddStructure(new Gate(new TileCoord(4, 4)) { OwnerId = 0 });

        var path = Plan(sim, playerId: 0, new TileCoord(2, 1), new TileCoord(6, 1));
        Assert.NotNull(path);
        Assert.Contains(new TileCoord(4, 4), path!);

        // Seal the gate (replace with wall): no route exists at all.
        sim.World.Structures.Remove(new TileCoord(4, 4));
        sim.World.AddStructure(new Wall(new TileCoord(4, 4)) { OwnerId = 0 });
        Assert.Null(Plan(sim, playerId: 0, new TileCoord(2, 1), new TileCoord(6, 1)));
    }

    [Fact]
    public void AllianceRevokedMidMarch_ClosesTheGate()
    {
        var sim = MakeSim();
        MakeAllies(sim);
        PlaceWallColumn(sim.World, 4, 0, 3, owner: 1);
        PlaceWallColumn(sim.World, 4, 5, 8, owner: 1);
        sim.World.AddStructure(new Gate(new TileCoord(4, 4)) { OwnerId = 1 });

        var u = AddUnit(sim, 1, new TileCoord(2, 4), owner: 0);
        Assert.True(new MoveIntent(u.Id, new TileCoord(6, 4)) { PlayerId = 0 }
            .Resolve(sim).IsApplied);

        // First hop lands on (3,4); the hop INTO the gate is then scheduled.
        // Break the alliance while that hop is in flight: the arrival-fire
        // ground-truth check must stop the unit at the gate's face.
        AdvanceTo(sim, 45);
        Assert.Equal(new TileCoord(3, 4), u.Position);
        sim.World.Diplomacy.SetState(FactionPair.Of(0, 1), RelationshipState.Neutral);
        sim.Run();

        Assert.Equal(new TileCoord(3, 4), u.Position);
        Assert.Null(u.PathRemaining);
        Assert.Equal(Activity.Idle, u.Activity);
        // Neutral, not enemy — being locked out is not a casus belli.
        Assert.Empty(sim.World.CombatStates);
    }

    [Fact]
    public void WallCompletingMidMarch_StopsCommittedPath_NoSiegeWhenNeutral()
    {
        var sim = MakeSim();
        var u = AddUnit(sim, 1, new TileCoord(0, 4), owner: 0);
        Assert.True(new MoveIntent(u.Id, new TileCoord(8, 4)) { PlayerId = 0 }
            .Resolve(sim).IsApplied);

        // Mid-walk, player 1's wall column completes across the whole map.
        AdvanceTo(sim, 45);
        PlaceWallColumn(sim.World, 4, 0, 8, owner: 1);
        sim.Run();

        // The committed path is dead: the unit yields at the wall's face.
        Assert.Equal(3, u.Position.X);
        Assert.Null(u.PathRemaining);
        Assert.Equal(Activity.Idle, u.Activity);
        Assert.Empty(sim.World.CombatStates);   // neutral wall → no siege
    }

    // ====================================================================
    // Sieges — adjacency gather, no shielding, breach
    // ====================================================================

    [Fact]
    public void FogBonk_OpensSiege_RazesToRubble_BreachIsWalkable()
    {
        var sim = MakeSim();
        MakeEnemies(sim);
        PlaceWallColumn(sim.World, 4, 0, 8, owner: 1);
        // Weaken the target segment so the siege resolves in a few rounds
        // (health is a catalog balance knob; the mechanics are the pin).
        var segment = sim.World.Structures[new TileCoord(4, 4)];
        segment.Health = 5;

        var u = AddUnit(sim, 1, new TileCoord(0, 4), owner: 0);   // Soldier, power 3
        // The wall is beyond vision radius: the planner can't see it and
        // plans straight through (the unique-length optimal route).
        Assert.True(new MoveIntent(u.Id, new TileCoord(8, 4)) { PlayerId = 0 }
            .Resolve(sim).IsApplied);
        sim.Run();

        // Stopped at the face, siege ran, segment razed: the breach.
        Assert.Equal(new TileCoord(3, 4), u.Position);
        Assert.IsType<Rubble>(sim.World.Structures[new TileCoord(4, 4)]);
        Assert.Empty(sim.World.CombatStates);
        // Nobody was defeated — walls are not castles.
        Assert.False(sim.World.Players[1].Defeated);

        // The breach is ordinary ground: march through it.
        Assert.True(new MoveIntent(u.Id, new TileCoord(8, 4)) { PlayerId = 0 }
            .Resolve(sim).IsApplied);
        sim.Run();
        Assert.Equal(new TileCoord(8, 4), u.Position);
    }

    [Fact]
    public void SiegeDamage_IsAdjacentAttackerPower_PerRound()
    {
        var sim = MakeSim();
        MakeEnemies(sim);
        var wallAt = new TileCoord(4, 4);
        sim.World.AddStructure(new Wall(wallAt) { OwnerId = 1 });
        var fullHealth = sim.World.Structures[wallAt].Health;

        // Two soldiers end their march on the wall's face.
        var a = AddUnit(sim, 1, new TileCoord(2, 4), owner: 0);
        var b = AddUnit(sim, 2, new TileCoord(4, 2), owner: 0);
        Assert.True(new MoveIntent(a.Id, new TileCoord(3, 4)) { PlayerId = 0 }.Resolve(sim).IsApplied);
        Assert.True(new MoveIntent(b.Id, new TileCoord(4, 3)) { PlayerId = 0 }.Resolve(sim).IsApplied);
        sim.Run(until: 31);   // both single hops arrive at t=30; siege opens

        Assert.True(sim.World.CombatStates.ContainsKey(wallAt));

        // One full round later the wall has taken both soldiers' power.
        var interval = sim.World.CombatConfig.RoundIntervalTicks;
        var perRound = 2 * CombatRules.EffectivePower(a, sim.Now);
        AdvanceTo(sim, 30 + interval + 1);
        Assert.Equal(fullHealth - perRound, sim.World.Structures[wallAt].Health);

        // Attackers hold: another round, another bite.
        AdvanceTo(sim, 30 + 2 * interval + 1);
        Assert.Equal(fullHealth - 2 * perRound, sim.World.Structures[wallAt].Health);
    }

    [Fact]
    public void DefenderBehindWall_DoesNotShieldIt()
    {
        var sim = MakeSim();
        MakeEnemies(sim);
        var wallAt = new TileCoord(4, 4);
        sim.World.AddStructure(new Wall(wallAt) { OwnerId = 1 });
        var fullHealth = sim.World.Structures[wallAt].Health;

        // Defender stands on the far face (adjacent, unreachable through the
        // wall). Under a castle-style shielding rule this would make the
        // wall indestructible forever — the deadlock the design rejects.
        AddUnit(sim, 9, new TileCoord(5, 4), owner: 1);
        var attacker = AddUnit(sim, 1, new TileCoord(2, 4), owner: 0);
        Assert.True(new MoveIntent(attacker.Id, new TileCoord(3, 4)) { PlayerId = 0 }
            .Resolve(sim).IsApplied);

        var interval = sim.World.CombatConfig.RoundIntervalTicks;
        AdvanceTo(sim, 30 + interval + 1);
        Assert.True(sim.World.Structures[wallAt].Health < fullHealth,
            "wall must take damage despite the defender behind it");
        // And no unit combat happened — they never co-located.
        Assert.Equal(2, sim.World.Units.Count);
    }

    [Fact]
    public void MarchingPastAHostileWall_DoesNotAttackIt()
    {
        var sim = MakeSim();
        MakeEnemies(sim);
        // Flanking walls beside the route, away from the final destination.
        sim.World.AddStructure(new Wall(new TileCoord(3, 3)) { OwnerId = 1 });
        sim.World.AddStructure(new Wall(new TileCoord(3, 5)) { OwnerId = 1 });

        var u = AddUnit(sim, 1, new TileCoord(0, 4), owner: 0);
        Assert.True(new MoveIntent(u.Id, new TileCoord(6, 4)) { PlayerId = 0 }
            .Resolve(sim).IsApplied);
        sim.Run();

        // The unit passed between them (mid-path hops adjacent to both) and
        // kept walking: no siege, walls untouched.
        Assert.Equal(new TileCoord(6, 4), u.Position);
        Assert.Empty(sim.World.CombatStates);
        Assert.Equal(StructureCatalog.Spec(StructureKind.Wall).BaseHealth,
            sim.World.Structures[new TileCoord(3, 3)].Health);
    }

    [Fact]
    public void SiegeEnds_WhenAttackersLeave()
    {
        var sim = MakeSim();
        MakeEnemies(sim);
        var wallAt = new TileCoord(4, 4);
        sim.World.AddStructure(new Wall(wallAt) { OwnerId = 1 });

        var u = AddUnit(sim, 1, new TileCoord(2, 4), owner: 0);
        Assert.True(new MoveIntent(u.Id, new TileCoord(3, 4)) { PlayerId = 0 }.Resolve(sim).IsApplied);
        var interval = sim.World.CombatConfig.RoundIntervalTicks;
        AdvanceTo(sim, 30 + interval + 1);   // one round lands
        var damaged = sim.World.Structures[wallAt].Health;
        Assert.True(damaged < StructureCatalog.Spec(StructureKind.Wall).BaseHealth);

        // March away; the next round finds nobody in reach and ends cleanly.
        Assert.True(new MoveIntent(u.Id, new TileCoord(0, 0)) { PlayerId = 0 }.Resolve(sim).IsApplied);
        sim.Run();
        Assert.Empty(sim.World.CombatStates);
        Assert.Equal(damaged, sim.World.Structures[wallAt].Health);   // chip stops
    }

    // ====================================================================
    // Determinism
    // ====================================================================

    [Fact]
    public void BlockingChecks_ArePureReads()
    {
        var sim = MakeSim();
        MakeEnemies(sim);
        sim.World.AddStructure(new Wall(new TileCoord(4, 4)) { OwnerId = 1 });
        sim.World.AddStructure(new Gate(new TileCoord(4, 5)) { OwnerId = 1 });
        AddUnit(sim, 1, new TileCoord(3, 4), owner: 0);

        var visible = new HashSet<TileCoord> { new(4, 4), new(4, 5) };
        var before = Snapshot.Hash(sim);
        for (var i = 0; i < 100; i++)
        {
            Fortification.BlocksMover(sim.World, new TileCoord(4, 4), 0);
            Fortification.BlocksMover(sim.World, new TileCoord(4, 5), 0);
            Fortification.BlocksPlan(sim.World, new TileCoord(4, 4), 0, visible);
            MovementCost.PlanCost(sim.World, new TileCoord(4, 4), 0, visible, sim.Now);
        }
        Assert.Equal(before, Snapshot.Hash(sim));
    }

    [Fact]
    public void WallGateAndWallSites_RoundTripThroughSnapshot()
    {
        var sim = MakeSim();
        sim.World.AddStructure(new Wall(new TileCoord(2, 2)) { OwnerId = 0 });
        sim.World.Structures[new TileCoord(2, 2)].Health -= 137;   // mid-siege damage
        sim.World.AddStructure(new Gate(new TileCoord(3, 2)) { OwnerId = 0 });
        Assert.True(new PlaceWallIntent(new List<TileCoord> { new(5, 5), new(5, 6) })
        { PlayerId = 1 }.Resolve(sim).IsApplied);

        var restored = Snapshot.Restore(Snapshot.Serialize(sim), seed: 1);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(restored));
        var wall = Assert.IsType<Wall>(restored.World.Structures[new TileCoord(2, 2)]);
        Assert.Equal(StructureCatalog.Spec(StructureKind.Wall).BaseHealth - 137, wall.Health);
        Assert.IsType<Gate>(restored.World.Structures[new TileCoord(3, 2)]);
        var site = Assert.IsType<ConstructionSite>(restored.World.Structures[new TileCoord(5, 5)]);
        Assert.Equal(StructureKind.Wall, site.TargetKind);
    }

    [Fact]
    public void MidSiegeSnapshot_RecoversAndFinishesIdentically()
    {
        Simulation Build()
        {
            var sim = MakeSim();
            MakeEnemies(sim);
            PlaceWallColumn(sim.World, 4, 0, 8, owner: 1);
            sim.World.Structures[new TileCoord(4, 4)].Health = 20;
            var u = AddUnit(sim, 1, new TileCoord(0, 4), owner: 0);
            Assert.True(new MoveIntent(u.Id, new TileCoord(8, 4)) { PlayerId = 0 }
                .Resolve(sim).IsApplied);
            return sim;
        }

        var interval = new GameWorld(new TileGrid(2, 2, Biome.Grassland))
            .CombatConfig.RoundIntervalTicks;
        var midTick = 90 + interval + 5;    // walk (3 hops) + one round: mid-siege
        var endTick = 90 + 20 * interval;   // comfortably past the raze

        var a = Build();
        a.Run(until: endTick);
        var hashA = Snapshot.Hash(a);
        Assert.IsType<Rubble>(a.World.Structures[new TileCoord(4, 4)]);

        var b = Build();
        b.Run(until: midTick);
        Assert.True(b.World.CombatStates.ContainsKey(new TileCoord(4, 4)),
            "must snapshot MID-siege");
        var restored = Snapshot.Restore(Snapshot.Serialize(b), seed: 1);
        restored.Run(until: endTick);

        Assert.Equal(hashA, Snapshot.Hash(restored));
    }

    // Headline (M26 contract, architecture §1): build a wall line by intent,
    // an enemy is stopped by it, besieges it, breaches it, and marches
    // through — twice, hash-identical.
    [Fact]
    public void Walls_TwinRun_HashesMatch()
    {
        Simulation Run()
        {
            var sim = MakeSim();
            MakeEnemies(sim);

            // Player 1 walls the whole column by intent, then the segments
            // complete instantly-for-the-test via the standard build dance.
            var line = new List<TileCoord>();
            for (var y = 0; y <= 8; y++) line.Add(new TileCoord(4, y));
            Assert.True(new PlaceWallIntent(line) { PlayerId = 1 }.Resolve(sim).IsApplied);
            var builderId = 100;
            foreach (var t in line)
            {
                var site = (ConstructionSite)sim.World.Structures[t];
                foreach (var (r, n) in site.Required) site.Deposit(r, n);
                var builder = AddUnit(sim, builderId++, t, owner: 1, role: UnitRole.Builder);
                builder.TrySetActivity(Activity.Building, t);
                site.StartOrResume(sim);
            }
            var buildDone = StructureCatalog.Spec(StructureKind.Wall).BuildDurationTicks + 1;
            AdvanceTo(sim, buildDone);
            // Soften the doomed segment so the siege fits the test budget.
            sim.World.Structures[new TileCoord(4, 4)].Health = 10;

            // March the builders clear of the line so the breach stays empty.
            for (var i = 0; i < 9; i++)
            {
                var b = sim.World.Units[100 + i];
                Assert.True(new MoveIntent(b.Id, new TileCoord(8, i == 4 ? 3 : i)) { PlayerId = 1 }
                    .Resolve(sim).IsApplied);
            }
            AdvanceTo(sim, buildDone + 200);

            // The attack: fog-blind straight march, bonk, siege, breach.
            var u = AddUnit(sim, 1, new TileCoord(0, 4), owner: 0);
            Assert.True(new MoveIntent(u.Id, new TileCoord(8, 4)) { PlayerId = 0 }
                .Resolve(sim).IsApplied);
            var siegeDone = buildDone + 200 + 90
                + 10 * sim.World.CombatConfig.RoundIntervalTicks;
            AdvanceTo(sim, siegeDone);

            // Through the breach.
            Assert.True(new MoveIntent(u.Id, new TileCoord(8, 4)) { PlayerId = 0 }
                .Resolve(sim).IsApplied);
            AdvanceTo(sim, siegeDone + 400);
            Assert.Equal(new TileCoord(8, 4), sim.World.Units[1].Position);
            return sim;
        }

        Assert.Equal(Snapshot.Hash(Run()), Snapshot.Hash(Run()));
    }
}
