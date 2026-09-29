namespace Sim.Core.Bandits;

// M39 — bandit camp knobs (docs/bandit-camps.md). Genesis-set, immutable for
// the world's lifetime, snapshotted (v39): a camp's daily tick reads the
// recruit and muster rules, the driver reads the raid size, progression reads
// the placement ring. Every default is sized in the decision doc against the
// real combat numbers; each is a knob.
//
// Explicit parameterless constructor (the RoyaltyConfig pattern): `new
// CampConfig()` on a record struct with optional primary-constructor
// parameters would be all zeros.
public readonly record struct CampConfig(
    // The garrison a camp is raised with, and the most it regrows to (counting
    // its riders still out: the cap is the camp's whole strength).
    int GarrisonStart,
    int GarrisonCap,
    // One recruit per period while the garrison is below the cap and no
    // fight is on the camp's tile.
    long RecruitPeriodTicks,
    // A raid of RaidSize leaves at most once per period, only when no earlier
    // raid is still out, the hoard is below HoardCap, and at least MinHome
    // would stay behind.
    long RaidPeriodTicks,
    int RaidSize,
    int MinHome,
    // After the rumour, how long until the first raid may ride.
    long FirstRaidDelayTicks,
    // Raids stop while the hoard holds this much: an unanswered camp does
    // bounded harm, then sits fat on its takings.
    int HoardCap,
    // What the hoard starts with.
    int HoardStartIron,
    int HoardStartOre,
    // Where a rumoured camp is placed: this ring (Chebyshev tiles) around the
    // target's castle, in its wildest direction.
    int MinDistance,
    int MaxDistance,
    // How often a camp takes stock (recruit, muster, prune).
    long TickPeriodTicks)
{
    public CampConfig() : this(
        GarrisonStart: 6,
        GarrisonCap: 6,
        RecruitPeriodTicks: 3 * Time.Day,
        RaidPeriodTicks: 7 * Time.Day,
        RaidSize: 3,
        MinHome: 1,
        FirstRaidDelayTicks: 10 * Time.Day,
        HoardCap: 200,
        HoardStartIron: 30,
        HoardStartOre: 20,
        MinDistance: 20,
        MaxDistance: 40,
        TickPeriodTicks: Time.Day)
    { }
}
