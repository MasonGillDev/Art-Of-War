using Sim.Core;
using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Core.Persistence;
using Sim.Core.Population;
using Sim.Core.World;
using Sim.Server.Automation;

namespace Sim.Tests;

// THE KEYSTONE LAB (docs/automation-substrate.md Phase C) — the async proof
// the whole vision rests on: a kingdom running ONLY substrate orders
// survives a stretch of real absence with no player input at all.
//
// The pin is 160 GAME-DAYS (230,400 ticks). At the intended 4 tps that is a
// working day plus a night — the longest a player is realistically away —
// and it is the number the progression arc will eventually promise them
// ("build these two things and you can go to sleep").
//
// WHAT THIS PROVES
//   * A full order network — Staff, Train, Haul, Breed — runs unattended
//     for 160 game-days with no player input and nothing wedges: no order
//     auto-disables, no claim outlives its unit, children keep being born.
//   * BREEDING TRACKS THE FOOD SUPPLY. No order computes global headroom
//     anywhere; each house's own larder — kept stocked by its own supply
//     line — is the whole signal, so a colony that works its fields
//     out-breeds one that doesn't. The composition claim
//     (docs/automation-as-core-game.md law 3), measured rather than argued.
//   * The whole economy REPLAYS byte-identically. Since Phase E the food is
//     grown in-world by staffed farms instead of being hand-fed to a
//     granary each day, so every change here came through an intent or a
//     scheduled event.
//
// WHAT IT IS NOT
//   Not a balance test. Whether a PARTICULAR colony out-grows its fields
//   depends on how the player sized their network — as two earlier drafts
//   of this fixture demonstrated by starving to death beside a full
//   warehouse (see the comments on Houses and BuildColony). Recipe
//   CORRECTNESS is pinned by SubstrateWorkforceTests / SubstrateBreedTests
//   against small fixtures, where a failure means the verb is broken rather
//   than the economy being mis-tuned.
public class SubstrateKeystoneLabTests
{
    private const int Days = 160;
    private static readonly long Horizon = Days * Time.Day;   // 230,400 ticks

    private static readonly TileCoord Keep = new(10, 10);
    private static readonly TileCoord Granary = new(6, 10);   // the farms' output store
    private static readonly TileCoord School = new(12, 10);
    private static readonly TileCoord[] Farms = { new(4, 8), new(4, 12) };
    // TWO houses, not four. The first lab run wired four houses and five
    // supply lines against a small hauler pool; the low-priority lines never
    // won a hand, newborns homed into larders that had never been stocked,
    // and the colony died with a FULL granary. The machine was working — the
    // player's network was over-subscribed. Sizing the network to the crew
    // is the player's job, and this is what a viable one looks like.
    private static readonly TileCoord[] Houses = { new(9, 9), new(11, 9) };

    // A pre-built, pre-staffed colony: a castle, a granary standing in for
    // worked fields (a fixed, renewing food income), and four houses.
    private static Simulation BuildColony(int granaryFood = 60_000)
    {
        var grid = new TileGrid(24, 24, Biome.Grassland);
        var world = new GameWorld(grid);
        world.Players[0] = new Player(0);

        var castle = new Castle(Keep) { OwnerId = 0 };
        castle.Holdings[Resource.Food] = 400;
        world.AddStructure(castle);

        // REAL FARMS (Phase E). Earlier versions of this lab hand-fed a
        // granary once per game-day to stand in for production, which meant
        // the colony's food came from outside the sim — it could not be
        // replayed, and it hid the very loop the keystone is supposed to
        // prove. With Staff and Train the colony can work its own fields,
        // so the food now comes from the ground.
        var granary = new Stockpile(Granary) { OwnerId = 0 };
        granary.Holdings[Resource.Food] = Math.Min(granaryFood, 300);
        world.AddStructure(granary);

        foreach (var f in Farms)
            world.AddStructure(new Extractor(StructureKind.Farm, f) { OwnerId = 0 });

        world.AddStructure(new School(School) { OwnerId = 0 });

        foreach (var h in Houses)
        {
            var house = new House(h) { OwnerId = 0 };
            house.Holdings[Resource.Food] = 60;
            world.AddStructure(house);
        }

        // Founders: adults spread across the fertility window so they do not
        // all age out on the same day (the synchronized-cliff lesson).
        //
        // HALF HAULERS, HALF FARMERS — and the breeding orders below select
        // FARMERS ONLY. The first run of this lab used an any-role breeding
        // selector and the colony died: breeding conscripted the haulers,
        // locked them for a 2.25-day gestation each, and three of the four
        // houses never received a single delivery in 160 days. Newborns
        // homed into empty larders and starved while the granary sat full.
        //
        // That is not a bug — it is the workforce/breeding tension the game
        // is about, and the selector's role filter is the player's lever
        // for it. Keeping the logistics crew out of the breeding pool is a
        // DESIGN DECISION the player makes; this is what making it looks like.
        var cfg = new PopulationConfig();
        var id = 1;
        for (var i = 0; i < 16; i++)
        {
            var age = 19 + i;                           // 19..34
            world.AddUnit(new Unit(id++, new TileCoord(6 + (i % 8), 13 + i / 8))
            {
                Role = i < 8 ? UnitRole.Hauler : UnitRole.Farmer,
                OwnerId = 0,
                BornTick = -age * cfg.TicksPerYear,
            });
        }
        world.NextUnitId = id;

        var explored = new HashSet<TileCoord>();
        for (var y = 0; y < 24; y++)
            for (var x = 0; x < 24; x++) explored.Add(new TileCoord(x, y));
        world.Explored[0] = explored;

        return new Simulation(world, seed: 99);
    }

    // The player's whole machine: feed the keep, feed each house, and breed
    // in each house while its larder can carry another mouth.
    private static void InstallOrders(Simulation sim) =>
        InstallOrders(sim, populationCap: 24, staffTheFields: true);

    private static void InstallOrders(Simulation sim, long populationCap, bool staffTheFields = true)
    {
        var cfg = new PopulationConfig();
        var priority = 0;

        // STAFF the fields — keep each farm at full strength. When no farmer
        // is free this reports a shortage and waits; the Train order below
        // is what answers it. Neither knows the other exists; they meet in
        // the labour pool.
        if (staffTheFields)
        {
            foreach (var f in Farms)
            {
                Install(sim, new Order
                {
                    Priority = priority++,
                    SubjectKind = SubjectKind.Structure, SubjectTile = f,
                    Program = ProgramKind.Maintain, Recipe = RecipeKind.Staff,
                    Target = 2,
                    CrewMode = CrewMode.Pull,
                    Selector = Selector.OfRole(UnitRole.Farmer, f, radius: 14, minAgeYears: cfg.MinTrainAge),
                    Trigger = Trigger.When(Predicate.WorkersBelow(f, 2)),
                });
            }
        }

        // TRAIN the professions the kingdom runs on. Children grow up with
        // no trade; these quotas are what turn them into farmers and
        // haulers. In-flight counting keeps the two quotas from racing.
        foreach (var (role, quota) in new[] { (UnitRole.Farmer, 10), (UnitRole.Hauler, 9) })
        {
            Install(sim, new Order
            {
                Priority = priority++,
                SubjectKind = SubjectKind.RoleCount, SubjectRole = role,
                Program = ProgramKind.Maintain, Recipe = RecipeKind.Train,
                Target = quota, SourceTile = School,
                CrewMode = CrewMode.Pull,
                Selector = Selector.OfRole(UnitRole.None, School, radius: 16, minAgeYears: cfg.MinTrainAge),
                Trigger = Trigger.When(Predicate.RoleCountBelow(role, quota)),
            });
        }

        // Crop to granary, granary to keep: the harvest has to be carried.
        foreach (var f in Farms)
        {
            Install(sim, new Order
            {
                Priority = priority++,
                SubjectKind = SubjectKind.Structure, SubjectTile = Granary,
                Program = ProgramKind.Maintain, Recipe = RecipeKind.Haul,
                Target = 300, SourceTile = f, Resource = Resource.Food,
                CrewMode = CrewMode.Pull,
                Selector = Selector.OfRole(UnitRole.Hauler, f, radius: 14),
                Trigger = Trigger.When(
                    Predicate.StockBelow(Granary, Resource.Food, 300),
                    Predicate.StockAtLeast(f, Resource.Food, 15)),
            });
        }

        // Food to the keep (the deep reserve everyone unhoused eats from).
        Install(sim, new Order
        {
            Priority = priority++,
            SubjectKind = SubjectKind.Structure, SubjectTile = Keep,
            Program = ProgramKind.Maintain, Recipe = RecipeKind.Haul,
            Target = 600, SourceTile = Granary, Resource = Resource.Food,
            CrewMode = CrewMode.Pull,
            Selector = Selector.OfRole(UnitRole.Hauler, Keep, radius: 12),
            Trigger = Trigger.When(
                Predicate.StockBelow(Keep, Resource.Food, 600),
                Predicate.StockAtLeast(Granary, Resource.Food, 30)),
        });

        // Food to each house — the larder that both FEEDS the residents and
        // PAYS for births. One supply line per house; the breeding trigger
        // reads what this delivers.
        foreach (var h in Houses)
        {
            Install(sim, new Order
            {
                Priority = priority++,
                SubjectKind = SubjectKind.Structure, SubjectTile = h,
                Program = ProgramKind.Maintain, Recipe = RecipeKind.Haul,
                Target = 80, SourceTile = Granary, Resource = Resource.Food,
                CrewMode = CrewMode.Pull,
                Selector = Selector.OfRole(UnitRole.Hauler, h, radius: 12),
                Trigger = Trigger.When(
                    Predicate.StockBelow(h, Resource.Food, 80),
                    Predicate.StockAtLeast(Granary, Resource.Food, 30)),
            });
        }

        // Breed in each house on TWO conditions, and the pair matters:
        //
        //   StockAtLeast(house, 50)  — the RATE throttle. Purely local; no
        //       order computes the kingdom's food balance. A house whose
        //       supply line has fallen behind simply stops breeding.
        //
        //   PopulationBelow(cap)     — the CEILING. The larder throttle is
        //       REACTIVE: it stops breeding once food is already short, by
        //       which time the mouths exist and must be fed. The first lab
        //       run showed exactly that overshoot. A population target is
        //       the player's forward-looking cap, and it is why both
        //       predicate families exist.
        //
        // FARMERS ONLY — the logistics crew keeps hauling (see BuildColony).
        var breedSelector = new Selector(
            UnitRole.Farmer, AnyRole: false,
            cfg.MinFertileAge, cfg.MaxFertileAge,
            RequireDormant: true, Anchor: default, Radius: 14);
        foreach (var h in Houses)
        {
            Install(sim, new Order
            {
                Priority = priority++,
                SubjectKind = SubjectKind.Structure, SubjectTile = h,
                Program = ProgramKind.Maintain, Recipe = RecipeKind.Breed,
                CrewMode = CrewMode.Pull,
                Selector = breedSelector with { Anchor = h },
                Trigger = Trigger.When(
                    Predicate.StockAtLeast(h, Resource.Food, 50),
                    Predicate.PopulationBelow(populationCap)),
            });
        }
    }

    private static void Install(Simulation sim, Order order)
    {
        sim.SubmitIntent(0, new SetOrderIntent(order));
        sim.Run(0);
        var ev = Assert.IsType<IntentEvent>(sim.ResolvedLog[^1]);
        Assert.False(ev.Outcome.IsRejected, $"order rejected: {ev.Outcome.Reason}");
    }

    // No out-of-band feeding: every grain in this world was grown by a
    // farmer the colony staffed and carried by a hauler it trained. That is
    // what makes the run replayable — nothing changes the world except
    // intents and the events they schedule.
    private static (int births, int starved, int trained) RunColony(
        Simulation sim, SubstrateDriver driver, long horizon)
    {
        int starved = 0, births = 0, trained = 0, seenLog = 0;
        // RUN THEN THINK — the GameHost order.
        for (long t = 0; t <= horizon; t += Time.Hour)
        {
            sim.Run(t);
            driver.Think(sim, t);

            for (; seenLog < sim.ResolvedLog.Count; seenLog++)
            {
                switch (sim.ResolvedLog[seenLog])
                {
                    case Sim.Core.Food.StarvationDeathEvent: starved++; break;
                    case BirthEvent: births++; break;
                    case IntentEvent { Outcome.IsRejected: false }
                         and { Intent: Sim.Core.Population.TrainUnitIntent }: trained++; break;
                }
            }
        }
        return (births, starved, trained);
    }

    [Fact]
    public void Keystone_MachineRunsFor160UnattendedGameDays_WithoutWedgingOrLeaking()
    {
        // THE ASYNC PIN, scoped to what the substrate can prove today: over
        // 230,400 ticks with ZERO player input, the machine keeps working.
        // Orders keep firing, children keep being born, no order wedges into
        // auto-disable, and the claims ledger never leaks a body.
        //
        // "Auto-disabled" is the failure that matters most here: it is the
        // machine quietly dying while the player sleeps, which is the exact
        // thing docs/automation-as-core-game.md forbids.
        var sim = BuildColony();
        InstallOrders(sim);
        var driver = new SubstrateDriver(new AutomationConfig { ThinkPeriodTicks = Time.Hour });

        var (births, _, _) = RunColony(sim, driver, Horizon);

        Assert.True(births > 0, "no births in 160 game-days — the Breed rows never fired");
        Assert.All(sim.World.Orders.Values, o =>
            Assert.True(o.Enabled, $"order {o.OrderId} ({o.Recipe}) auto-disabled"));

        // Let the machine take ONE more housekeeping pass before auditing the
        // ledger. Reaping a dead hand's claim is something a recipe does on
        // its next think, so a unit that dies inside the final hour leaves a
        // claim that is pending collection, not leaked. Without this settle
        // the assertion below is really testing "nobody died recently", which
        // is a property of the trajectory rather than of the substrate — and
        // it fails the moment any tuning shifts when deaths land.
        var settle = Horizon + Time.Hour;
        sim.Run(settle);
        driver.Think(sim, settle);
        sim.Run(settle);          // the driver's Release intents resolve here

        // No claim outlives its unit: every ledger entry names a living body.
        //
        // Scoped to players still IN the game. A defeated kingdom's driver is
        // switched off (every intent a defeated player submits is rejected),
        // so its ledger is frozen at the moment of defeat rather than leaking
        // — there is no longer a machine running to leak from.
        foreach (var (unitId, claim) in sim.World.Claims)
        {
            var owner = sim.World.Orders.TryGetValue(claim.OrderId, out var o) ? o.OwnerId : -1;
            if (owner >= 0 && sim.World.Players.TryGetValue(owner, out var p) && p.Defeated) continue;
            Assert.True(sim.World.Units.ContainsKey(unitId),
                $"claim by order {claim.OrderId} outlived unit {unitId}");
        }
    }

    [Fact]
    public void Keystone_BreedingTracksTheFoodSupply_NotAGlobalCalculation()
    {
        // THE COMPOSITION CLAIM (docs/automation-as-core-game.md law 3): no
        // order computes the kingdom's food balance anywhere. Each house's
        // OWN larder — kept stocked by its own supply line — is the entire
        // signal. So a colony whose fields yield more must raise more
        // children, purely as a consequence of the physical stock moving.
        // Lean: the same colony with no Staff orders, so the fields never
        // get worked and the only food is what the granary started with.
        var lean = BuildColony();
        InstallOrders(lean, populationCap: 24, staffTheFields: false);
        var leanDriver = new SubstrateDriver(new AutomationConfig { ThinkPeriodTicks = Time.Hour });
        var (leanBirths, _, _) = RunColony(lean, leanDriver, Horizon);

        var fat = BuildColony();
        InstallOrders(fat);
        var fatDriver = new SubstrateDriver(new AutomationConfig { ThinkPeriodTicks = Time.Hour });
        var (fatBirths, _, _) = RunColony(fat, fatDriver, Horizon);

        Assert.True(fatBirths > leanBirths,
            $"a colony that works its fields ({fatBirths} births) did not out-breed one that " +
            $"doesn't ({leanBirths}) — the local larder is not throttling growth");
    }

    [Fact]
    public void Keystone_ReplaysDeterministically_AtScale()
    {
        // Determinism at SCALE — a subtle ordering bug survives a 10-tick
        // fixture and dies here. Now that the colony grows its own food,
        // every change to this world came through an intent or a scheduled
        // event, so the whole economy replays (the hand-fed granary of the
        // earlier draft could not).
        var sim = BuildColony();
        InstallOrders(sim);
        var driver = new SubstrateDriver(new AutomationConfig { ThinkPeriodTicks = Time.Hour });
        var horizon = 20 * Time.Day;

        for (long t = 0; t <= horizon; t += Time.Hour)
        {
            sim.Run(t);
            driver.Think(sim, t);
        }
        sim.Run(horizon);

        var replay = BuildColony();
        foreach (var batch in sim.ResolvedLog.OfType<IntentEvent>()
                     .OrderBy(e => e.Seq).GroupBy(e => e.At))
        {
            replay.Run(until: batch.Key);
            foreach (var ev in batch)
            {
                var (typeName, payload) = Sim.Persistence.IntentJson.Serialize(ev.Intent);
                replay.SubmitIntent(batch.Key,
                    Sim.Persistence.IntentJson.Deserialize(typeName, payload));
            }
        }
        replay.Run(horizon);

        Assert.Equal(Snapshot.Hash(sim), Snapshot.Hash(replay));
    }
}
