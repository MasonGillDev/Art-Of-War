using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Hauling;
using Sim.Core.Logistics;
using Sim.Core.World;
using Sim.Server.Automation;

namespace Sim.Server.Hauling;

// Why a job did or didn't get a hauler this think. Presentation only: the
// HUD reads it to say why a job is stalled.
// Crosses the wire as an int: values are fixed, append-only. 0 = not yet
// looked at (a job added since the last think).
public enum HaulJobState
{
    Dispatched       = 1, // a hauler was sent this think
    Satisfied        = 2, // stock + on the way already meets the target
    SourceEmpty      = 3, // nothing at the source that isn't already spoken for
    WaitingForHauler = 4, // wants a trip, the pool is empty
    Unserviceable    = 5, // an end is gone or changed hands
}

// Where a route crew stands this think. Presentation only.
// Crosses the wire as an int: values are fixed, append-only. 0 = not yet
// looked at.
public enum RouteCrewState
{
    Walking    = 1, // on its way to the current stop (or regrouping there)
    Serving    = 2, // everyone is at the stop; the serve was sent this think
    MemberBusy = 3, // a member is tied up (a fight, a task the player gave)
    NoCrew     = 4, // every member is dead or gone
    CalledAway = 5, // M47: the player mustered or moved the crew's group; dismiss resumes it
}

public sealed record RouteCrewReport(int RouteId, int CrewId, RouteCrewState State, int CurrentStop, int Living);

public sealed record HaulJobReport(
    int JobId,
    int Position,        // 0 = front of the queue
    HaulJobState State,
    int Need,            // still wanted after what is on the way (0 when satisfied)
    int OnTheWay,        // amount already committed to this job
    int Haulers);        // haulers walking for it now, including any sent this think

// THE HAUL QUEUE DRIVER (docs/hauling-queue-and-routes.md).
//
// Each think, per owner: walk the queue front to back. A job that wants a
// trip, and has something at its source, takes the free hauler nearest its
// PICKUP tile and goes to the back of the line. The walk loops until the
// pool is empty or a full lap sends nobody, so a long hungry line can take
// several haulers in one think, one turn each.
//
// No priority, no held hands, no per-line cap. Weighting comes from need:
// a satisfied line is looked at and passed over.
//
// Same trust boundary as the automation substrate: pure reads of the world,
// then ordinary intents (RequeueHaulJobIntent + HaulIntent). The driver
// holds no sim state; what a hauler is doing for which job lives on its
// HaulPlan, so a restart resumes with nothing to rebuild.
public sealed class HaulingDriver
{
    private readonly HaulingConfig _cfg;
    private long _lastThink = long.MinValue;
    private readonly Dictionary<int, HaulJobReport> _reports = new();
    private readonly List<RouteCrewReport> _crewReports = new();

    public HaulingDriver(HaulingConfig? cfg = null) { _cfg = cfg ?? new HaulingConfig(); }

    // Last think's verdict per job id. Jobs added since then have no row.
    public IReadOnlyDictionary<int, HaulJobReport> Reports => _reports;
    // Last think's verdict per route crew, by (route, crew) id.
    public IReadOnlyList<RouteCrewReport> CrewReports => _crewReports;

    public void Think(Simulation sim, long now)
    {
        if (!_cfg.Enabled) return;
        if (_lastThink != long.MinValue && now - _lastThink < _cfg.ThinkPeriodTicks) return;
        _lastThink = now;

        var world = sim.World;
        _reports.Clear();
        _crewReports.Clear();
        RunRoutes(sim, now);
        if (world.HaulJobs.Count == 0) return;

        var ledger = Ledger.Read(world);

        // Owners in id order, each owner's line in (stamp, id) order.
        var lines = new SortedDictionary<int, List<HaulJob>>();
        foreach (var (_, job) in world.HaulJobs)
        {
            if (!lines.TryGetValue(job.OwnerId, out var line))
                lines[job.OwnerId] = line = new List<HaulJob>();
            line.Add(job);
        }
        foreach (var (owner, line) in lines)
        {
            // A defeated kingdom's intents are all rejected; sending them
            // would only grow the durable log. (SubstrateDriver precedent.)
            if (world.Players.TryGetValue(owner, out var p) && p.Defeated) continue;
            line.Sort((a, b) =>
            {
                var s = a.QueueStamp.CompareTo(b.QueueStamp);
                return s != 0 ? s : a.JobId.CompareTo(b.JobId);
            });
            RunLine(sim, owner, line, ledger, now);
        }
    }

    private void RunLine(Simulation sim, int owner, List<HaulJob> line, Ledger ledger, long now)
    {
        var world = sim.World;
        var free = Pool.Read(world, owner);
        var sent = new Dictionary<int, int>();   // job → haulers sent this think

        // Walk the line, sending a served job to the back. Each send uses up
        // a hauler, so this ends; "a full lap with no send" ends it when the
        // pool outlasts the work.
        var at = 0;
        var sinceSend = 0;
        while (free.Count > 0 && sinceSend < line.Count)
        {
            var job = line[at];
            if (TrySend(sim, job, free, ledger, now))
            {
                sent[job.JobId] = sent.GetValueOrDefault(job.JobId) + 1;
                line.RemoveAt(at);
                line.Add(job);
                sinceSend = 0;
                if (at >= line.Count) at = 0;
            }
            else
            {
                sinceSend++;
                at = (at + 1) % line.Count;
            }
        }

        // Report every job in its new position.
        for (var i = 0; i < line.Count; i++)
        {
            var job = line[i];
            var onTheWay = ledger.OnTheWay(job.JobId);
            var salvage = job.Kind == HaulJobKind.Salvage;
            // Salvage counts haulers, not amounts, and never reads its source.
            var need = salvage
                ? Math.Max(0, job.Target - ledger.Haulers(job.JobId))
                : Math.Max(0, Need(world, job, now) - onTheWay);
            var state = sent.ContainsKey(job.JobId) ? HaulJobState.Dispatched
                : !EndsHeld(world, job) ? HaulJobState.Unserviceable
                : need <= 0 ? HaulJobState.Satisfied
                : !salvage && ledger.Available(world, job, now) <= 0 ? HaulJobState.SourceEmpty
                : HaulJobState.WaitingForHauler;
            _reports[job.JobId] = new HaulJobReport(
                job.JobId, i, state, need, onTheWay, ledger.Haulers(job.JobId));
        }
    }

    // NAMED ROUTES (M47: a crew is a group on a route). Each crew's group marches in
    // formation to its current stop (one MoveGroupIntent, ForRoute), and once it has
    // formed up there the stop is served and the crew pointed at the next one (one
    // intent, so the two can't come apart). The group arrives together: there is no
    // straggler to wait for at the stop.
    //
    // Crews never stop and never wait on stock: an unservable stop is served
    // anyway (it moves nothing) and the loop carries on. A crew whose group the player
    // has called away (a muster, a move) is left alone until it is dismissed.
    private void RunRoutes(Simulation sim, long now)
    {
        var world = sim.World;
        foreach (var (_, route) in world.HaulRoutes)   // ascending id
        {
            if (world.Players.TryGetValue(route.OwnerId, out var p) && p.Defeated) continue;
            foreach (var crew in route.Crews)          // ascending id
            {
                var living = RouteCrews.Living(world, route, crew);
                var state = RunCrew(sim, route, crew, living, now);
                _crewReports.Add(new RouteCrewReport(
                    route.RouteId, crew.CrewId, state, crew.CurrentStop, living.Count));
            }
        }
    }

    private static RouteCrewState RunCrew(
        Simulation sim, HaulRoute route, RouteCrew crew, List<Unit> living, long now)
    {
        if (living.Count == 0 || !sim.World.Groups.TryGetValue(crew.GroupId, out var group)) return RouteCrewState.NoCrew;
        if (group.RouteSuspended) return RouteCrewState.CalledAway;

        var target = route.Stops[crew.CurrentStop].Tile;
        switch (group.State)
        {
            case Sim.Core.Groups.GroupState.Moving:
                return RouteCrewState.Walking;
            case Sim.Core.Groups.GroupState.Idle when group.Position == target:
                // Formed up at the stop. Someone still tied up (a fight, a delivery of
                // its own) holds the serve until it is free.
                if (living.Any(u => !IsFree(u))) return RouteCrewState.MemberBusy;
                sim.SubmitIntent(now, new ServeRouteStopIntent(route.RouteId, crew.CrewId, crew.CurrentStop, route.Revision)
                    { PlayerId = route.OwnerId });
                return RouteCrewState.Serving;
            case Sim.Core.Groups.GroupState.Idle:
                sim.SubmitIntent(now, new Sim.Core.Groups.MoveGroupIntent(group.Id, target, forRoute: true)
                    { PlayerId = route.OwnerId });
                return RouteCrewState.Walking;
            default:
                return RouteCrewState.MemberBusy;   // Forming: a newcomer is being called in
        }
    }

    // Free = an idle body with no in-flight anchors. Anchors, never Activity:
    // a marching unit can read Idle (the M16 pitfall).
    private static bool IsFree(Unit u) =>
        u.Activity == Activity.Idle
        && !u.IsWalking
        && u.HaulPlan is null
        && u.Goal is null
        && u.Pursuit is null
        && !u.IsEmbarked;

    // One turn at the front of the line. True = a hauler was sent.
    private static bool TrySend(Simulation sim, HaulJob job, Pool free, Ledger ledger, long now)
    {
        var world = sim.World;
        if (!EndsHeld(world, job)) return false;

        // M40 — salvage: keep up to Target haulers at work, each taking a full
        // load of whatever is there. The driver never peeks at the source: the
        // haulers find out when they arrive (docs/salvage.md).
        if (job.Kind == HaulJobKind.Salvage)
        {
            if (ledger.Haulers(job.JobId) >= job.Target) return false;
            var picked = free.Take(job.Source);
            ledger.Commit(job, picked.Id, picked.CargoCapacity);
            sim.SubmitIntent(now, new RequeueHaulJobIntent(job.JobId) { PlayerId = job.OwnerId });
            sim.SubmitIntent(now, new HaulIntent(
                picked.Id, job.Source, job.Dest, Resource.None, 0, job.JobId) { PlayerId = job.OwnerId });
            return true;
        }

        var need = Need(world, job, now) - ledger.OnTheWay(job.JobId);
        if (need <= 0) return false;

        // Looking costs no game time: an empty source sends nobody.
        var available = ledger.Available(world, job, now);
        if (available <= 0) return false;

        var hauler = free.Peek(job.Source);
        var amount = Math.Min(hauler.CargoCapacity, Math.Min(need, available));
        if (amount <= 0) return false;

        free.Remove(hauler);
        ledger.Commit(job, hauler.Id, amount);

        sim.SubmitIntent(now, new RequeueHaulJobIntent(job.JobId) { PlayerId = job.OwnerId });
        sim.SubmitIntent(now, new HaulIntent(
            hauler.Id, job.Source, job.Dest, job.Resource, amount, job.JobId) { PlayerId = job.OwnerId });
        return true;
    }

    // What the job still wants delivered, ignoring what is on the way.
    private static int Need(GameWorld world, HaulJob job, long now) => job.Kind switch
    {
        HaulJobKind.Standing => (int)Math.Max(0,
            job.Target - PredicateEvaluator.StoredAmount(world, job.Dest, job.Resource, now)),
        HaulJobKind.Once => Math.Max(0, job.Target - job.Delivered),
        // M40 — salvage wants haulers, not an amount: its need is its crew.
        HaulJobKind.Salvage => job.Target,
        _ => throw new InvalidOperationException(
            $"HaulingDriver has no case for HaulJobKind {(byte)job.Kind}"),
    };

    // Both ends still stand and are still the owner's (a razed or captured
    // end leaves the job in line, doing nothing, until the player clears it).
    private static bool EndsHeld(GameWorld world, HaulJob job) =>
        // M40 — a salvage source is nobody's by definition; only its end is held.
        (job.Kind == HaulJobKind.Salvage
            || (world.Structures.TryGetValue(job.Source, out var s) && s.OwnerId == job.OwnerId))
        && world.Structures.TryGetValue(job.Dest, out var d) && d.OwnerId == job.OwnerId;

    // How many haulers the owner has free right now. Pure read, for the HUD.
    // Haulers only: idle citizens are the overflow, not the crew.
    public static int CountFree(GameWorld world, int owner) => Pool.Read(world, owner).Haulers.Count;

    // THE POOL (docs/citizen-hauling.md): the owner's free bodies, in two tiers.
    // Haulers (cargo 25) are the crew; untrained citizens of ANY age (cargo 5)
    // are the overflow, taken only when no hauler is free, so idle hands are
    // never useless but a trained Hauler always wins the trip. No age gate on
    // purpose: hauling is what a child does until it is old enough to train.
    //
    // Free = empty and dormant (idle, not marching, not grouped, not claimed by
    // the AI substrate, not breeding, not on a route). Ascending id, so ties in
    // Nearest resolve canonically.
    private sealed class Pool
    {
        public readonly List<Unit> Haulers = new();
        public readonly List<Unit> Citizens = new();
        public int Count => Haulers.Count + Citizens.Count;

        public static Pool Read(GameWorld world, int owner)
        {
            var pool = new Pool();
            foreach (var (_, u) in world.Units)
            {
                if (u.OwnerId != owner) continue;
                var tier = u.Role switch
                {
                    UnitRole.Hauler => pool.Haulers,
                    UnitRole.None => pool.Citizens,
                    _ => null,
                };
                if (tier is null) continue;
                if (!u.Cargo.IsEmpty || u.HaulPlan is not null || u.Goal is not null) continue;
                if (!ClaimLedger.IsDormant(world, u)) continue;
                tier.Add(u);
            }
            return pool;
        }

        // The body that would take a trip from `to`: the nearest hauler, else
        // the nearest citizen.
        public Unit Peek(TileCoord to) => Nearest(Haulers.Count > 0 ? Haulers : Citizens, to);

        public Unit Take(TileCoord to)
        {
            var u = Peek(to);
            Remove(u);
            return u;
        }

        public void Remove(Unit u)
        {
            if (!Haulers.Remove(u)) Citizens.Remove(u);
        }
    }

    // Nearest to the pickup tile: Chebyshev distance, then y, x, id — the
    // selector's canonical order, so the choice is a total order.
    private static Unit Nearest(List<Unit> free, TileCoord to)
    {
        var best = free[0];
        for (var i = 1; i < free.Count; i++)
        {
            var u = free[i];
            var d = Chebyshev(u.Position, to).CompareTo(Chebyshev(best.Position, to));
            if (d < 0
                || (d == 0 && (u.Position.Y < best.Position.Y
                    || (u.Position.Y == best.Position.Y && (u.Position.X < best.Position.X
                        || (u.Position.X == best.Position.X && u.Id < best.Id))))))
                best = u;
        }
        return best;
    }

    private static int Chebyshev(TileCoord a, TileCoord b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    // What is already on the way, read once per think from the haulers'
    // plans and then kept current as this think sends more.
    private sealed class Ledger
    {
        private readonly Dictionary<int, int> _onTheWay = new();       // job → amount
        private readonly Dictionary<int, int> _haulers = new();        // job → bodies
        // Amount still to be LIFTED at a source — haulers walking there for
        // any job. Two jobs drawing on one farm must not both count its
        // buffer as theirs.
        private readonly Dictionary<(TileCoord, Resource), int> _toLift = new();

        public static Ledger Read(GameWorld world)
        {
            var l = new Ledger();
            foreach (var (_, u) in world.Units)
            {
                if (u.HaulPlan is not { } plan) continue;
                var planned = plan.Amount > 0 ? plan.Amount : u.CargoCapacity;
                if (plan.Phase == HaulPhase.ToSource)
                {
                    var key = (plan.SourceTile, plan.Resource);
                    l._toLift[key] = l._toLift.GetValueOrDefault(key) + planned;
                }
                if (plan.JobId == 0) continue;
                var amount = plan.Phase == HaulPhase.ToSource
                    ? planned
                    : u.Cargo.AmountOf(plan.Resource);
                l._onTheWay[plan.JobId] = l._onTheWay.GetValueOrDefault(plan.JobId) + amount;
                l._haulers[plan.JobId] = l._haulers.GetValueOrDefault(plan.JobId) + 1;
            }
            return l;
        }

        public int OnTheWay(int jobId) => _onTheWay.GetValueOrDefault(jobId);
        public int Haulers(int jobId) => _haulers.GetValueOrDefault(jobId);

        public int Available(GameWorld world, HaulJob job, long now) =>
            (int)PredicateEvaluator.StoredAmount(world, job.Source, job.Resource, now)
            - _toLift.GetValueOrDefault((job.Source, job.Resource));

        public void Commit(HaulJob job, int haulerId, int amount)
        {
            _onTheWay[job.JobId] = OnTheWay(job.JobId) + amount;
            _haulers[job.JobId] = Haulers(job.JobId) + 1;
            var key = (job.Source, job.Resource);
            _toLift[key] = _toLift.GetValueOrDefault(key) + amount;
        }
    }
}
