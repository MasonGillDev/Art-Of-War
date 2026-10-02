namespace Sim.Core.Hauling;

// Haul queue limits (docs/hauling-queue-and-routes.md). Constants enforced
// only when a job is set, so retuning them never invalidates a snapshot —
// the AutomationConstants precedent.
public static class HaulingConstants
{
    // Max haul jobs one player may have queued at once. Bounds the driver's
    // per-think pass and the snapshot.
    public const int MaxJobsPerPlayer = 64;
    // M40 — the most haulers one salvage job may keep at work.
    public const int MaxSalvageCrew = 6;

    // Named routes. Bounds the driver's work per think and the snapshot.
    public const int MaxRoutesPerPlayer = 32;
    public const int MaxStopsPerRoute = 16;
    public const int MaxRulesPerStop = 8;
    public const int MaxCrewsPerRoute = 8;
    public const int MaxMembersPerCrew = 12;
    // M45 — a route's name, in characters after trimming.
    public const int MaxRouteNameLength = 32;
}
