namespace Sim.Core.Bandits;

// M39 — bandit camps (docs/bandit-camps.md): a bandit-owned structure whose
// garrison stands guard and whose raiders ride out on a schedule and carry
// their loot home to its hoard.
//
// THE SIM OWNS THE CAMP'S LIFE; THE DRIVER PLAYS THE RAIDS OUT. Here: the camp
// is built, its garrison regrows, a raid is mustered (the camp's durable
// Raiders list says who is out), the dead are pruned, and a razing is
// credited. The bandit driver (Sim.Server) walks the raiders to their target,
// steals, brings them home and unloads them into the hoard through ordinary
// intents. Keeping who-is-out in the sim means a restarted driver cannot send a
// raid twice.
//
// SINGLE MUTATION POINT for BanditCamp's own fields (Raiders, LastRaidTick,
// LastRecruitTick, the tick anchor); the hoard itself is ordinary storage
// (Deposit on a raider's unload, Teardown's spill on a razing).
public static class Camps
{
    // Build a camp at `at` for `target`, stocked and garrisoned, its first
    // raid due FirstRaidDelay from now. Returns the camp.
    internal static BanditCamp Build(Simulation sim, TileCoord at, int targetOwnerId, int sourceMilestoneId)
    {
        var world = sim.World;
        var cfg = world.CampConfig;
        var camp = new BanditCamp(at)
        {
            OwnerId = BanditConstants.OwnerId,
            TargetOwnerId = targetOwnerId,
            SourceMilestoneId = sourceMilestoneId,
        };
        // "Last raid" set so the first muster is allowed at now + FirstRaidDelay.
        camp.LastRaidTick = sim.Now + cfg.FirstRaidDelayTicks - cfg.RaidPeriodTicks;
        camp.LastRecruitTick = sim.Now;
        camp.Deposit(Resource.Bronze, cfg.HoardStartIron);
        camp.Deposit(Resource.CopperOre, cfg.HoardStartOre);
        world.AddStructure(camp);

        SpawnBanditPartyIntent.Materialize(sim, at, Math.Clamp(cfg.GarrisonStart, 0, cfg.GarrisonCap));

        camp.NextTickAt = sim.Now + Math.Max(1, cfg.TickPeriodTicks);
        camp.NextTickSeq = sim.Schedule(camp.NextTickAt, new CampTickEvent(at));
        return camp;
    }

    // The camp takes stock: prune who is back or dead, regrow the garrison,
    // muster a raid if one is due. Then book the next look.
    internal static void Tick(Simulation sim, BanditCamp camp)
    {
        var world = sim.World;
        var cfg = world.CampConfig;
        var fighting = world.CombatStates.ContainsKey(camp.At);

        Prune(world, camp);

        if (sim.Now - camp.LastRecruitTick >= cfg.RecruitPeriodTicks)
        {
            camp.LastRecruitTick = sim.Now;
            // The cap is the camp's whole strength: its guard plus its riders
            // still out (pruned of the dead above), so a raid cannot swell it.
            if (!fighting && Garrison(world, camp).Count + camp.Raiders.Count < cfg.GarrisonCap)
                SpawnBanditPartyIntent.Materialize(sim, camp.At, 1);
        }

        if (!fighting
            && camp.Raiders.Count == 0
            && sim.Now - camp.LastRaidTick >= cfg.RaidPeriodTicks
            && camp.TotalHeld() < cfg.HoardCap)
        {
            var home = Garrison(world, camp);
            if (cfg.RaidSize > 0 && home.Count >= cfg.RaidSize + cfg.MinHome)
            {
                camp.Raiders.AddRange(home.Take(cfg.RaidSize));   // lowest ids ride
                camp.RaidDeparted = false;
                camp.LastRaidTick = sim.Now;
            }
        }

        camp.NextTickAt = sim.Now + Math.Max(1, cfg.TickPeriodTicks);
        camp.NextTickSeq = sim.Schedule(camp.NextTickAt, new CampTickEvent(camp.At));
    }

    // Bandits standing on the camp who are not out raiding, ascending id.
    public static List<int> Garrison(GameWorld world, BanditCamp camp)
    {
        var ids = new List<int>();
        foreach (var u in world.Units.Values)
            if (u.OwnerId == BanditConstants.OwnerId && u.Position == camp.At && !u.IsEmbarked
                && !camp.Raiders.Contains(u.Id))
                ids.Add(u.Id);
        return ids;
    }

    // A raider is back when the raid has left and it stands on the camp with
    // nothing left to unload; the dead are gone.
    private static void Prune(GameWorld world, BanditCamp camp) =>
        camp.Raiders.RemoveAll(id =>
            !world.Units.TryGetValue(id, out var u) || IsBack(camp, u));

    public static bool IsBack(BanditCamp camp, Unit u) =>
        camp.RaidDeparted && u.Position == camp.At && u.Cargo.Total == 0 && !u.IsWalking;

    // A bandit entered a tile (TileEntry): a raider off its camp means
    // the raid has left.
    internal static void OnMoved(GameWorld world, Unit unit)
    {
        if (unit.OwnerId != BanditConstants.OwnerId) return;
        if (CampOfRaider(world, unit.Id) is { } camp && unit.Position != camp.At) camp.RaidDeparted = true;
    }

    // The camp whose raiders include this unit, if any.
    public static BanditCamp? CampOfRaider(GameWorld world, int unitId)
    {
        foreach (var s in world.Structures.Values)
            if (s is BanditCamp c && c.Raiders.Contains(unitId)) return c;
        return null;
    }

    // Every removal path converges on Population.OnUnitRemoved: a dead raider
    // leaves its camp's list at once.
    internal static void OnUnitRemoved(Simulation sim, Unit unit)
    {
        if (unit.OwnerId != BanditConstants.OwnerId) return;
        foreach (var s in sim.World.Structures.Values)
            if (s is BanditCamp c) c.Raiders.Remove(unit.Id);
    }

    // The camp fell (CombatRoundEvent, after SiegeDamage.RazeStructure: the hoard
    // has spilled and rubble stands). Every enrolled owner standing there when
    // it fell is credited; the rumour about it is over.
    internal static void OnRazed(Simulation sim, BanditCamp camp, IEnumerable<int> razers)
    {
        Sim.Core.Progression.Omens.OnCampRazed(sim, camp.At);
        foreach (var owner in razers.Distinct().OrderBy(o => o))
            Sim.Core.Progression.Progression.Bump(sim, owner,
                Sim.Core.Progression.ProgressKey.CampRazed(camp.SourceMilestoneId));
    }
}

// M39 — a camp takes stock (daily). Fenced on the camp's (NextTickAt,
// NextTickSeq) anchor; regenerated on restore.
public sealed class CampTickEvent : ScheduledEvent
{
    public TileCoord CampAt { get; }
    public CampTickEvent(TileCoord campAt) { CampAt = campAt; }

    public override void Apply(Simulation sim)
    {
        if (!sim.World.Structures.TryGetValue(CampAt, out var s) || s is not BanditCamp camp)
        {
            Outcome = IntentOutcome.Reject($"no camp at {CampAt}");
            return;
        }
        if (camp.NextTickAt != At || camp.NextTickSeq != Seq)
        {
            Outcome = IntentOutcome.Reject($"stale camp tick at {CampAt}");
            return;
        }
        Camps.Tick(sim, camp);
    }

    public override string Describe() => $"CampTick(@ {CampAt.X},{CampAt.Y})";
}
