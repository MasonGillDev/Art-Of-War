namespace Sim.Core.Progression;

// M37 — one row of the catalog: fires ONCE per player, the first time When
// holds. Id is serialized (the ledger's fired set, an omen's source) and must
// never be reused for a different row. Name is for logs and tests only and
// never reaches the wire (milestones are surprises).
public sealed record Milestone(int Id, string Name, Condition When, IReadOnlyList<Effect> Then);

// M37 — the milestones (docs/progression.md). A code table like
// StructureCatalog: the rows are fixed code, their numbers come from
// ProgressionConfig. GameWorld.Milestones holds the rows built from the
// world's config.
//
// Rules for a row:
//   * It fires once. A bigger moment is a NEW row with its own flavour, never
//     the same row with bigger numbers.
//   * A threat is a test the milestone says the player can pass: telegraphed,
//     and beating it pays out.
//   * Ids are append-only: a retired row keeps its id reserved.
public static class MilestoneCatalog
{
    // You armed yourself; the bandits noticed. A raid is announced a week
    // ahead and marches on your seat. Kill them all for their war chest.
    public const int Reprisal = 1;
    // You beat the reprisal, and people heard: refugees come to live under
    // your protection.
    public const int WordSpreads = 2;
    // You have seen enough of the world to hear its rumours: a ruin stands in
    // the fog, out in the wildest direction.
    public const int FarHorizons = 3;
    // Your houses are a town now: settlers come to join it, and a settled
    // people starts looking outward: the Lodge (and with it scouts) is known.
    public const int AGoodHome = 4;
    // M39 — your town is worth robbing: smoke rises from a bandit camp out in
    // the wildest direction, and its raiders will ride.
    public const int SmokeOnTheHorizon = 5;
    // M39 — you razed that camp: the captives it held walk home.
    public const int TheCampBurns = 6;

    public static IReadOnlyList<Milestone> For(ProgressionConfig cfg) => new[]
    {
        new Milestone(Reprisal, "Reprisal",
            Condition.AtLeast(cfg.ReprisalTrained,
                ProgressKey.Trained(UnitRole.Soldier), ProgressKey.Trained(UnitRole.Archer)),
            new Effect[]
            {
                new Threat(OmenKind.Raid, cfg.ReprisalRaidSize, cfg.ReprisalWarningTicks,
                    cfg.ReprisalChestResource, cfg.ReprisalChestAmount),
            }),
        new Milestone(WordSpreads, "Word spreads",
            Condition.AtLeast(1, ProgressKey.Repelled(Reprisal)),
            new Effect[] { new Arrival(cfg.WordSpreadsRefugees, cfg.WordSpreadsWarningTicks) }),
        new Milestone(FarHorizons, "Far horizons",
            Condition.Gauge(ProgressGauge.TilesExplored, cfg.FarHorizonsTiles),
            new Effect[]
            {
                new Rumour(new[] { (Resource.Iron, cfg.FarHorizonsIron), (Resource.Sword, cfg.FarHorizonsSwords) }),
            }),
        new Milestone(AGoodHome, "A good home",
            Condition.AtLeast(cfg.GoodHomeHouses, ProgressKey.Completed(StructureKind.House)),
            new Effect[]
            {
                new Arrival(cfg.GoodHomeSettlers, cfg.GoodHomeWarningTicks),
                // 2026-09-24 (the user): the Lodge arrives with a good home.
                new Unlock(StructureKind.Lodge),
            }),
        new Milestone(SmokeOnTheHorizon, "Smoke on the horizon",
            Condition.Gauge(ProgressGauge.Population, cfg.SmokePopulation),
            new Effect[] { new CampRumour() }),
        new Milestone(TheCampBurns, "The camp burns",
            Condition.AtLeast(1, ProgressKey.CampRazed(SmokeOnTheHorizon)),
            new Effect[] { new Arrival(cfg.CampCaptives, Time.Day) }),
    };
}
