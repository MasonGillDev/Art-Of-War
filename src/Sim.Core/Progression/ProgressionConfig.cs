namespace Sim.Core.Progression;

// M37 — world-level progression knobs (docs/progression.md). Set at genesis,
// immutable for the world's lifetime, serialized in the snapshot. Same shape
// as RoyaltyConfig. The milestone ROWS are code (MilestoneCatalog); their
// numbers live here so tests derive from them and the user can retune without
// touching the rows.
//
// Every default is a starting point for hand play, not a tuned answer.
public readonly record struct ProgressionConfig(
    // ---- Reprisal: you armed yourself, the bandits noticed ----
    // Soldiers + archers trained (ever) before the raid is announced.
    int ReprisalTrained,
    // Warning between the announcement and the raid.
    long ReprisalWarningTicks,
    // Raiders. Sized so that many trained soldiers, at home and warned, win
    // (Bandit 25/3 vs Soldier 30/3 before arms).
    int ReprisalRaidSize,
    // The raiders' war chest: dropped where the last one falls when every
    // raider is killed (none escaped). The payout for standing and fighting.
    Resource ReprisalChestResource,
    int ReprisalChestAmount,

    // ---- Word spreads: the reprisal was beaten, and people heard ----
    int WordSpreadsRefugees,
    long WordSpreadsWarningTicks,

    // ---- Far horizons: you have seen enough of the world to hear rumours ----
    // Tiles explored before the rumour.
    int FarHorizonsTiles,
    // What the ruin holds.
    int FarHorizonsIron,
    int FarHorizonsSwords,
    // The rumour's search area: a circle of this radius (tiles) that holds the
    // ruin somewhere inside, never at its centre. Big enough that a scout has
    // to search, small enough that it knows where to aim.
    int RumourAreaRadius,

    // ---- M39: Smoke on the horizon (a camp) / The camp burns ----
    // Population at which a bandit camp is rumoured: a town worth robbing.
    int SmokePopulation,
    // Captives freed when the camp is razed; they walk home.
    int CampCaptives,

    // ---- A good home: houses built, settlers come ----
    int GoodHomeHouses,
    int GoodHomeSettlers,
    long GoodHomeWarningTicks,

    // ---- Omens ----
    // The ring (Chebyshev tiles from the seat) an arrival or a ruin is searched
    // in, nearest first, inside the omen's bearing.
    int OmenSpawnMinDistance,
    int OmenSpawnMaxDistance,
    // A raid with no dark tile in its bearing slips this long, at most
    // OmenMaxSlips times; after that darkness is waived (the distance floor
    // never is).
    long OmenSlipTicks,
    int OmenMaxSlips)
{
    public ProgressionConfig() : this(
        ReprisalTrained: 6,
        ReprisalWarningTicks: 7 * Time.Day,
        ReprisalRaidSize: 6,
        ReprisalChestResource: Resource.Bronze,
        ReprisalChestAmount: 10,
        WordSpreadsRefugees: 4,
        WordSpreadsWarningTicks: 2 * Time.Day,
        FarHorizonsTiles: 1500,
        FarHorizonsIron: 20,
        FarHorizonsSwords: 2,
        RumourAreaRadius: 8,
        SmokePopulation: 25,
        CampCaptives: 3,
        GoodHomeHouses: 3,
        GoodHomeSettlers: 2,
        GoodHomeWarningTicks: 2 * Time.Day,
        OmenSpawnMinDistance: 16,
        OmenSpawnMaxDistance: 40,
        OmenSlipTicks: Time.Day,
        OmenMaxSlips: 3)
    { }
}
