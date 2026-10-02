using Sim.Core.Bandits;
using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.Equipment;
using Sim.Core.Movement;
using Sim.Core.World;

namespace Sim.Server.Sandbox;

// Where a composed piece really landed (the host's truth, not the editor's guess):
// the per-side cap, water and the castle can all move it. SimIds: the sim units it
// became (one for a unit, Size for a bandit party).
public sealed class PlacedPiece
{
    public int Id { get; set; }
    public int[] SimIds { get; set; } = [];
    public int X { get; set; }
    public int Y { get; set; }
    public bool Moved { get; set; }
}

// Builds the small real world a Composition describes (docs/battle-sandbox.md). The
// game's own worldgen (cached per size and seed: a rebuild is only genesis and the
// setup step), no AI brains, no caches, idols, progression or landing, and people
// created through the paths the game creates them by:
//   * a King is a genesis spawn named as the faction's crown, as in a real start;
//   * everyone else goes through Population.OnUnitAdded (the per-side cap, a
//     subtile from Placement.Seat), as a birth or a spawn does;
//   * gear through EquipRules.Grant (the storehouse road's rules), so a composed
//     unit is at full health: its role's base plus what its gear adds;
//   * bandits as parties, adopted by the real BanditDriver's census;
//   * doctrine through SetBattleDoctrineIntent, marches as MoveIntents at their tick.
public static class SandboxWorld
{
    // Worldgen is the one slow step; it depends only on (size, seed).
    private static readonly Dictionary<(int, int), WorldBuild> Maps = new();

    public static WorldBuild Map(int size, int seed)
    {
        lock (Maps)
        {
            if (Maps.TryGetValue((size, seed), out var cached)) return cached;
            var build = WorldFactory.Build(Options(size, seed, 1.0));
            Maps[(size, seed)] = build;
            return build;
        }
    }

    private static ServerOptions Options(int size, int seed, double pace) => new()
    {
        MapSeed = seed,
        MapWidth = size,
        MapHeight = size,
        AiPlayers = 1,           // Red's seat: a castle, no brain
        Bandits = false,
        CacheCount = 0,
        IdolCount = 0,
        LandingDay = 0,
        Progression = false,
        TicksPerSecond = pace,
    };

    public static ulong Seed(Composition c) => Options(c.Size, c.Seed, c.Pace).Seed;

    // The world's light: tick 0 falls at the composition's hour, on the game's own
    // cycle length (ServerOptions.LightCycleTicks' default).
    public static Atmosphere.LightCycleConfig Light(Composition c)
    {
        var cycle = new ServerOptions().LightCycleTicks;
        return new Atmosphere.LightCycleConfig(cycle, (long)Math.Round(c.Hour / 24.0 * cycle) % cycle);
    }

    // Blue's castle: the tile every composed position is relative to.
    public static TileCoord Anchor(Composition c) =>
        Map(c.Size, c.Seed).Spec.FactionStarts.First(f => f.OwnerId == Composition.Blue).CastlePosition;

    // The world and its before-tick-0 setup. `placed` is filled when setup runs.
    public static (WorldBuild Build, Action<Simulation> Setup) Prepare(Composition c, List<PlacedPiece> placed)
    {
        var map = Map(c.Size, c.Seed);
        var starts = map.Spec.FactionStarts.ToList();
        if (starts.All(f => f.OwnerId != Composition.Red))
            starts.Add(new FactionStartSpec
            {
                OwnerId = Composition.Red,
                CastlePosition = FarLand(map, starts[0].CastlePosition),
                CastleHoldings = new SortedDictionary<Resource, int> { [Resource.Food] = 1000 },
            });
        var anchor = starts.First(f => f.OwnerId == Composition.Blue).CastlePosition;

        // Kings are born at genesis, crowned there: the only way a realm gets one.
        var kingIds = new Dictionary<int, int>();   // composed id -> genesis unit id
        var nextGenesisId = 1;
        starts = starts.Select(f =>
        {
            var king = c.Units.FirstOrDefault(u => u.Faction == f.OwnerId && u.Role == (int)UnitRole.King);
            if (king is null)
                return f with { UnitSpawns = Array.Empty<UnitSpawn>(), KingUnitId = null };
            var id = nextGenesisId++;
            kingIds[king.Id] = id;
            var at = NearestWalkable(t => Walkable(map, t), new TileCoord(anchor.X + king.Dx, anchor.Y + king.Dy));
            return f with
            {
                UnitSpawns = new[] { new UnitSpawn(id, at, UnitRole.King, f.OwnerId) },
                KingUnitId = id,
            };
        }).ToList();

        var spec = map.Spec with
        {
            FactionStarts = starts,             // the game's own combat config: not overridden
            StartingWars = c.Wars.Select(w => (w.A, w.B)).ToList(),
        };
        var build = map with { Spec = spec };
        return (build, sim => Setup(sim, c, anchor, kingIds, placed));
    }

    private static void Setup(Simulation sim, Composition c, TileCoord anchor,
        IReadOnlyDictionary<int, int> kingIds, List<PlacedPiece> placed)
    {
        var world = sim.World;
        world.Structures[anchor].Facing = (Heading)c.CastleFacing;
        var years = new FactionStartSpec { CastlePosition = anchor }.StartingAgeYears;
        var simIds = new Dictionary<int, int>();

        foreach (var cu in c.Units)
        {
            var want = new TileCoord(anchor.X + cu.Dx, anchor.Y + cu.Dy);
            Unit unit;
            if (kingIds.TryGetValue(cu.Id, out var kingId))
                unit = world.Units[kingId];
            else
            {
                var at = NearestWalkable(t => Walkable(world, t), want);
                unit = Sim.Core.Population.Population.OnUnitAdded(sim, new Unit(world.TakeUnitId(), at)
                {
                    Role = (UnitRole)cu.Role,
                    OwnerId = cu.Faction,
                    BornTick = -years * world.PopulationConfig.TicksPerYear,
                });
                foreach (var item in cu.Gear)
                    EquipRules.Grant(unit, (Resource)item);   // Composition.Problem checked it
            }
            if (cu.Doctrine != CompositionUnit.DefaultDoctrine || cu.WithdrawBelow > 0)
            {
                var behaviour = cu.Doctrine == CompositionUnit.DefaultDoctrine
                    ? BattleDoctrine.DefaultFor(unit.Role).Behaviour
                    : (DoctrineBehaviour)cu.Doctrine;
                new SetBattleDoctrineIntent(unit.Id, (byte)behaviour, cu.WithdrawBelow) { PlayerId = cu.Faction }.Resolve(sim);
            }
            simIds[cu.Id] = unit.Id;
            placed.Add(new PlacedPiece
            {
                Id = cu.Id, SimIds = [unit.Id],
                X = unit.Position.X - anchor.X, Y = unit.Position.Y - anchor.Y,
                Moved = unit.Position != want,
            });
        }

        foreach (var p in c.Parties)
        {
            var want = new TileCoord(anchor.X + p.Dx, anchor.Y + p.Dy);
            var at = NearestWalkable(t => Walkable(world, t), want);
            var ids = new List<int>();
            TileCoord first = at;
            for (var i = 0; i < p.Size; i++)
            {
                var b = Sim.Core.Population.Population.OnUnitAdded(sim, new Unit(world.TakeUnitId(), at)
                {
                    Role = UnitRole.Bandit,
                    OwnerId = BanditConstants.OwnerId,
                    BornTick = sim.Now,
                });
                if (i == 0) first = b.Position;
                ids.Add(b.Id);
            }
            placed.Add(new PlacedPiece
            {
                Id = p.Id, SimIds = ids.ToArray(),
                X = first.X - anchor.X, Y = first.Y - anchor.Y,
                Moved = first != want,
            });
        }

        foreach (var m in c.Marches.OrderBy(m => m.After).ThenBy(m => m.Unit))
        {
            var cu = c.Units.First(u => u.Id == m.Unit);
            var to = NearestWalkable(t => Walkable(world, t), new TileCoord(anchor.X + m.Dx, anchor.Y + m.Dy));
            sim.SubmitIntent(m.After, new MoveIntent(simIds[m.Unit], to) { PlayerId = cu.Faction });
        }

        // People composed onto the same tile as an enemy are already in a fight:
        // the game's own trigger decides whether a board opens.
        foreach (var tile in world.Units.Values.Select(u => u.Position).Distinct()
                     .OrderBy(t => t.Y).ThenBy(t => t.X).ToList())
            CombatTrigger.MaybeBeginCombatOnTile(sim, tile);
    }

    private static bool Walkable(GameWorld world, TileCoord t) =>
        world.Grid.InBounds(t) && Biomes.MoveCost(world.Grid.BiomeAt(t)) < Biomes.Impassable;

    private static bool Walkable(WorldBuild build, TileCoord t) =>
        t.X >= 0 && t.Y >= 0 && t.X < build.Map.Width && t.Y < build.Map.Height
        && Biomes.MoveCost(build.Map.Grid[t.X, t.Y]) < Biomes.Impassable;

    // The nearest walkable tile to `t` (rings outward, row-major within a ring).
    private static TileCoord NearestWalkable(Func<TileCoord, bool> walkable, TileCoord t)
    {
        for (var r = 0; r < 40; r++)
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var c = new TileCoord(t.X + dx, t.Y + dy);
                    if (walkable(c)) return c;
                }
        return t;
    }

    // A land tile far from Blue for Red's castle, when worldgen found no AI start.
    private static TileCoord FarLand(WorldBuild build, TileCoord blue)
    {
        var best = blue;
        var bestD = -1;
        for (var y = 2; y < build.Map.Height - 2; y++)
            for (var x = 2; x < build.Map.Width - 2; x++)
            {
                if (Biomes.MoveCost(build.Map.Grid[x, y]) >= Biomes.Impassable) continue;
                var d = Math.Abs(x - blue.X) + Math.Abs(y - blue.Y);
                if (d > bestD) { bestD = d; best = new TileCoord(x, y); }
            }
        return best;
    }
}
