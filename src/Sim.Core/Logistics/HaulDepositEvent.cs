namespace Sim.Core.Logistics;

// Fires when a hauler arrives at the destination tile of a haul. Transfers
// what it can from the hauler's cargo into the dest structure, then returns
// the hauler to Idle.
//
// Cross-system hook: if the destination is a ConstructionSite and conditions
// become newly met by this deposit, fires StartOrResume on the site
// (Phase C). This is the first real (non-test) caller of that path.
//
// Fencing: ExpectedEpoch is captured at schedule time (from the pickup
// event). If the hauler's AssignmentEpoch differs on fire, the unit was
// retasked between scheduling and now — this event is stale, no-op without
// mutation.
//
// Fail-clean per docs/intent-validation.md: any non-fencing precondition
// miss leaves the world unchanged except that the hauler is returned to
// Idle (otherwise they'd be stuck Hauling forever holding cargo).
public sealed class HaulDepositEvent : ScheduledEvent
{
    public int HaulerId { get; }
    public TileCoord DestTile { get; }
    public byte ExpectedEpoch { get; }

    public HaulDepositEvent(int haulerId, TileCoord destTile, byte expectedEpoch)
    {
        HaulerId = haulerId;
        DestTile = destTile;
        ExpectedEpoch = expectedEpoch;
    }

    public override void Apply(Simulation sim)
    {
        var world = sim.World;
        if (!world.Units.TryGetValue(HaulerId, out var hauler))
        {
            Outcome = IntentOutcome.Reject($"hauler {HaulerId} does not exist");
            return;
        }

        // Fencing: stale event from a previous task; no-op.
        if (hauler.AssignmentEpoch != ExpectedEpoch)
        {
            Outcome = IntentOutcome.Reject("stale (epoch mismatch)");
            return;
        }

        if (hauler.Activity != Activity.Hauling)
        {
            Outcome = IntentOutcome.Reject("hauler is not Hauling");
            return;
        }
        // M28 — a boat's stop is the dest dock's SLIP, not the dock tile.
        if (!HaulStops.AtStop(world, hauler, DestTile))
        {
            hauler.TrySetActivity(Activity.Idle);
            Outcome = IntentOutcome.Reject($"hauler not at dest {DestTile.X},{DestTile.Y}");
            return;
        }
        if (hauler.CargoAmount == 0 || hauler.CargoResource == Resource.None)
        {
            hauler.TrySetActivity(Activity.Idle);
            Outcome = IntentOutcome.Reject("hauler has no cargo");
            return;
        }
        if (!world.Structures.TryGetValue(DestTile, out var dest))
        {
            hauler.TrySetActivity(Activity.Idle);
            Outcome = IntentOutcome.Reject($"no structure at dest {DestTile.X},{DestTile.Y}");
            return;
        }

        // Deposit via the shared primitive — it owns the Castle food catch-up /
        // famine re-eval (M13) and the construction-site start hook. Overflow that
        // doesn't fit stays on the hauler (unchanged from before the refactor).
        //
        // M36 — cargo can be mixed, so the trip delivers only ITS resource
        // (the plan's). A deposit with no plan (a hand-scheduled event)
        // delivers everything aboard, which is what it did when a unit could
        // carry only one resource.
        if (hauler.HaulPlan is { Resource: Resource.None })
        {
            // M40 — a salvage trip delivers everything aboard (a mixed load).
            foreach (var r in new List<Resource>(hauler.Cargo.Items.Keys))
                DeliverAll(sim, hauler, dest, r);
        }
        else if (hauler.HaulPlan is { } plan)
        {
            var delivered = DeliverAll(sim, hauler, dest, plan.Resource);
            CreditJob(sim.World, hauler, plan, delivered);
        }
        else
            foreach (var r in new List<Resource>(hauler.Cargo.Items.Keys))
                DeliverAll(sim, hauler, dest, r);

        // M4 Phase A: haul complete; clear the on-unit anchor.
        hauler.HaulPlan = null;
        hauler.TrySetActivity(Activity.Idle);
        Sim.Core.Groups.GroupMuster.OnFreed(sim, hauler);   // M46: delivered, it answers its group's muster
    }

    private static int DeliverAll(Simulation sim, Unit hauler, Structure dest, Resource resource)
    {
        var deposited = CargoTransfer.DepositInto(sim, dest, resource, hauler.Cargo.AmountOf(resource));
        return hauler.Cargo.Take(resource, deposited);
    }

    // M36 — a trip for a Once job counts toward its total; the job leaves the
    // queue when the total is met. A cleared job (or one that changed hands)
    // simply isn't credited: the trip still delivered, the job is just gone.
    private static void CreditJob(GameWorld world, Unit hauler, HaulPlan plan, int delivered)
    {
        if (plan.JobId == 0 || delivered <= 0) return;
        if (!world.HaulJobs.TryGetValue(plan.JobId, out var job)) return;
        if (job.OwnerId != hauler.OwnerId || job.Kind != Sim.Core.Hauling.HaulJobKind.Once) return;
        job.Delivered += delivered;
        if (job.Delivered >= job.Target) world.HaulJobs.Remove(job.JobId);
    }

    public override string Describe() =>
        $"HaulDeposit(hauler={HaulerId} @ {DestTile.X},{DestTile.Y})";
}
