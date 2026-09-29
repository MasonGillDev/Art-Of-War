namespace Sim.Core.Progression;

// M37 — the progression rules (docs/progression.md). The sim decides THAT a
// milestone fires and applies its effects; drivers only play out what the
// effects put in the world.
//
// SINGLE MUTATION POINT for ProgressLedger: Bump (counters) and Fire (the
// fired set). Called from:
//   * TrainingRules.Train               Bump RoleTrained
//   * Construction.Complete             Bump StructureCompleted
//   * ProductionTickEvent (refiners)    Bump ResourceRefined
//   * Omens (a raid resolves)           Bump ThreatRepelled / ThreatLost
//   * Check alone, where a GAUGE may have moved: Population.OnUnitAdded and
//     every Sight.Reveal call made from a sim event or intent.
//
// A player without a ledger (AI seats, the bandit and cache sentinels) and a
// defeated player are no-ops everywhere: nothing counts, nothing fires.
public static class Progression
{
    public static void Bump(Simulation sim, int playerId, ProgressKey key, long n = 1) =>
        Bump(sim, playerId, key, n, sim.World.Milestones);

    internal static void Bump(Simulation sim, int playerId, ProgressKey key, long n, IReadOnlyList<Milestone> rows)
    {
        if (n <= 0 || Ledger(sim.World, playerId) is not { } ledger) return;
        ledger.Counts[key] = ledger.Count(key) + n;
        Check(sim, playerId, rows);
    }

    public static void Check(Simulation sim, int playerId) => Check(sim, playerId, sim.World.Milestones);

    // Fire every row that now holds, in catalog order, until none does: a
    // firing can satisfy a later (or earlier) row's Fired(id) condition in the
    // same call.
    internal static void Check(Simulation sim, int playerId, IReadOnlyList<Milestone> rows)
    {
        if (rows.Count == 0 || Ledger(sim.World, playerId) is not { } ledger) return;
        var player = sim.World.Players[playerId];
        bool fired;
        do
        {
            fired = false;
            foreach (var row in rows)
            {
                if (ledger.Fired.Contains(row.Id) || !row.When.Holds(sim.World, player, ledger)) continue;
                Fire(sim, player, ledger, row);
                fired = true;
            }
        } while (fired);
    }

    private static void Fire(Simulation sim, Player player, ProgressLedger ledger, Milestone row)
    {
        // Mark first: an effect that bumps a counter re-enters Check, and must
        // not fire this row again.
        ledger.Fired.Add(row.Id);
        foreach (var e in row.Then) e.Apply(sim, player, row);
    }

    // Is `kind` still locked for this player? Only an enrolled player can be
    // locked, and only while a row that unlocks the kind has not fired. Pure
    // read (PlaceSiteIntent and the projector ask it).
    public static bool IsLocked(GameWorld world, int playerId, StructureKind kind)
    {
        if (!world.Players.TryGetValue(playerId, out var p) || p.Progress is not { } ledger) return false;
        foreach (var row in world.Milestones)
        {
            if (ledger.HasFired(row.Id)) continue;
            foreach (var e in row.Then)
                if (e is Unlock u && u.Kind == kind) return true;
        }
        return false;
    }

    // Every kind still locked for this player, ascending.
    public static IReadOnlyList<StructureKind> LockedKinds(GameWorld world, int playerId)
    {
        var locked = new SortedSet<StructureKind>();
        if (!world.Players.TryGetValue(playerId, out var p) || p.Progress is not { } ledger) return Array.Empty<StructureKind>();
        foreach (var row in world.Milestones)
        {
            if (ledger.HasFired(row.Id)) continue;
            foreach (var e in row.Then)
                if (e is Unlock u) locked.Add(u.Kind);
        }
        return locked.ToList();
    }

    private static ProgressLedger? Ledger(GameWorld world, int playerId) =>
        world.Players.TryGetValue(playerId, out var p) && !p.Defeated ? p.Progress : null;
}
