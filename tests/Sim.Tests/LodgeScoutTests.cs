using Sim.Core.Engine;
using Sim.Core.Population;
using Sim.Core.World;
using Sim.Server;

namespace Sim.Tests;

// M38 Phase A (docs/scouting-secrets.md): scouts are trained at the Lodge, not
// the School, and a human seat starts with none (its roster is the human
// opening, docs/human-opening.md). AI factions keep theirs.
public class LodgeScoutTests
{
    private static (Simulation sim, Unit citizen) CitizenOn(StructureKind trainer)
    {
        var world = new GameWorld(new TileGrid(8, 8, Biome.Grassland));
        world.Players[0] = new Player(0);
        var at = new TileCoord(3, 3);
        Structure building = trainer == StructureKind.Lodge
            ? new Lodge(at) { OwnerId = 0 }
            : new School(at) { OwnerId = 0 };
        world.AddStructure(building);
        var citizen = world.AddUnit(new Unit(1, at) { OwnerId = 0, BornTick = -30 * world.PopulationConfig.TicksPerYear });
        return (new Simulation(world, seed: 1), citizen);
    }

    [Fact]
    public void TheLodge_TrainsScouts()
    {
        Assert.Equal(StructureKind.Lodge, RoleTrainerCatalog.TrainerFor(UnitRole.Scout));
        var (sim, citizen) = CitizenOn(StructureKind.Lodge);
        Assert.True(TrainingRules.Train(sim, citizen, UnitRole.Scout));
        Assert.Equal(UnitRole.Scout, citizen.Role);
    }

    [Fact]
    public void TheSchool_NoLongerTrainsScouts()
    {
        var (sim, citizen) = CitizenOn(StructureKind.School);
        Assert.False(TrainingRules.Train(sim, citizen, UnitRole.Scout));
        Assert.True(TrainingRules.Train(sim, citizen, UnitRole.Miner));   // the School still trains civilians
    }

    // ---- the Lodge arrives with "A good home" (2026-09-24) ----

    private static (Simulation sim, TileCoord at) OpenGround(bool enrolled)
    {
        var world = new GameWorld(new TileGrid(12, 12, Biome.Grassland));
        world.Players[0] = new Player(0) { Progress = enrolled ? new Sim.Core.Progression.ProgressLedger() : null };
        world.AddStructure(new Castle(new TileCoord(1, 1)) { OwnerId = 0 });
        return (new Simulation(world, seed: 1), new TileCoord(6, 6));
    }

    private static bool Place(Simulation sim, TileCoord at, StructureKind kind)
    {
        sim.SubmitIntent(sim.Now, new Sim.Core.Logistics.PlaceSiteIntent(at, kind) { PlayerId = 0 });
        sim.Run(until: sim.Now);
        return sim.ResolvedLog[^1].Outcome.IsApplied;
    }

    [Fact]
    public void TheLodge_IsUnknown_UntilAGoodHome()
    {
        var (sim, at) = OpenGround(enrolled: true);
        Assert.True(Sim.Core.Progression.Progression.IsLocked(sim.World, 0, StructureKind.Lodge));
        Assert.False(Place(sim, at, StructureKind.Lodge));
        Assert.Contains("do not yet know", sim.ResolvedLog[^1].Outcome.Reason);

        // Three houses finished: A good home fires, and the Lodge is known.
        Sim.Core.Progression.Progression.Bump(sim, 0,
            Sim.Core.Progression.ProgressKey.Completed(StructureKind.House),
            sim.World.ProgressionConfig.GoodHomeHouses);
        Assert.True(sim.World.Players[0].Progress!.HasFired(Sim.Core.Progression.MilestoneCatalog.AGoodHome));
        Assert.False(Sim.Core.Progression.Progression.IsLocked(sim.World, 0, StructureKind.Lodge));
        Assert.True(Place(sim, at, StructureKind.Lodge));
    }

    [Fact]
    public void APlayerNotEnrolled_KnowsTheLodgeFromTheStart()
    {
        var (sim, at) = OpenGround(enrolled: false);
        Assert.Empty(Sim.Core.Progression.Progression.LockedKinds(sim.World, 0));
        Assert.True(Place(sim, at, StructureKind.Lodge));
    }

    [Fact]
    public void OnlyTheLockedKinds_AreLocked()
    {
        var (sim, at) = OpenGround(enrolled: true);
        Assert.Equal(new[] { StructureKind.Lodge }, Sim.Core.Progression.Progression.LockedKinds(sim.World, 0));
        Assert.True(Place(sim, at, StructureKind.House));
    }

    [Fact]
    public void TheWire_TellsTheOwnerWhatIsLocked_AndNobodyElse()
    {
        var build = WorldFactory.Build(new ServerOptions
            { MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 1, Progression = true });
        var sim = new Simulation(build.Spec, seed: 1);
        var projector = new ViewProjector(build);

        Assert.Equal(new[] { (int)StructureKind.Lodge }, projector.Project(sim, sim.Now, playerId: 0, reveal: false).LockedKinds);
        Assert.Empty(projector.Project(sim, sim.Now, playerId: 1, reveal: false).LockedKinds);

        Sim.Core.Progression.Progression.Bump(sim, 0,
            Sim.Core.Progression.ProgressKey.Completed(StructureKind.House), sim.World.ProgressionConfig.GoodHomeHouses);
        Assert.Empty(projector.Project(sim, sim.Now, playerId: 0, reveal: false).LockedKinds);
    }

    [Fact]
    public void AHumanSeat_StartsWithoutScouts_AiSeatsKeepTheirs()
    {
        var human = new Simulation(WorldFactory.Build(new ServerOptions
            { MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 1, Progression = true }).Spec, seed: 1);
        var lab = new Simulation(WorldFactory.Build(new ServerOptions
            { MapWidth = 96, MapHeight = 96, MapSeed = 7, AiPlayers = 1 }).Spec, seed: 1);

        int Count(Simulation s, int owner, UnitRole role) =>
            s.World.Units.Values.Count(u => u.OwnerId == owner && u.Role == role);

        Assert.Equal(0, Count(human, 0, UnitRole.Scout));
        Assert.Equal(2, Count(human, 1, UnitRole.Scout));   // the AI faction keeps its pair
        Assert.Equal(2, Count(lab, 0, UnitRole.Scout));     // and so does an AI-driven seat 0
    }
}
