namespace Sim.Core.Progression;

// Append-only enums (serialized).
public enum OmenKind : byte
{
    // A bandit force marching on the owner's seat.
    Raid     = 1,
    // A band of newcomers walking in to join the owner (refugees, settlers).
    Refugees = 2,
    // A ruin in the fog, somewhere out in the bearing. No countdown: it is
    // there from the start, and the omen lasts until it is emptied.
    Rumour   = 3,
    // M39 — a bandit camp, rumoured with a search circle. Arrived from the
    // start (the camp stands); DueTick is when its first raid may ride;
    // Fulfilled when it is razed.
    Camp     = 4,
}

public enum OmenState : byte
{
    // Announced; the countdown runs to DueTick.
    Pending   = 1,
    // Happening: a raid's raiders are on the map (PartyIds); a rumour's ruin
    // stands at Target.
    Arrived   = 2,
    // ---- over (ResolvedTick says when) ----
    // A raid: every raider dead, or fled empty-handed.
    Repelled  = 3,
    // A raid: loot got away.
    Lost      = 4,
    // Newcomers arrived; a rumoured ruin was emptied.
    Fulfilled = 5,
    // It never came: no ground to arrive on, or the owner fell first.
    Fizzled   = 6,
}

// The eight compass points, clockwise from north. North is +Y: the prod
// client draws tile y along world +Z (forward), and its fortification and
// road dressers already call +y north. (The river edge mask names its bits
// the other way round; that is a sim-internal label, not a screen direction.)
public enum Bearing : byte
{
    North = 0, NorthEast = 1, East = 2, SouthEast = 3,
    South = 4, SouthWest = 5, West = 6, NorthWest = 7,
}

// M37 — an omen (docs/progression.md): something a milestone set in motion,
// announced to its owner, that the sim makes happen. It stays in
// GameWorld.Omens after it resolves, with its outcome: every milestone fires
// once, so a player has a handful of omens in a whole game, and the outcome is
// what the owner's view reports ("the raid was repelled").
//
// ANCHOR: (DueTick, DueSeq) is the pending OmenDueEvent while Pending, and
// is regenerated from here on restore (RegenerateQueue). A slip moves the
// anchor. Only State says whether it is live: Seq 0 is a real Seq.
//
// Mutated ONLY by Omens (raise, arrive, slip, resolve).
public sealed class Omen
{
    public int OmenId { get; init; }
    public int OwnerId { get; init; }
    public OmenKind Kind { get; init; }
    // The milestone that raised it; a raid's outcome counters are keyed on
    // it, so follow-up rows can hang off one particular threat.
    public int SourceMilestoneId { get; init; }
    // Raid / Refugees: where it is headed (the owner's seat when raised).
    // Rumour: where the ruin stands. Never on the wire for a rumour.
    public TileCoord Target { get; init; }
    // Where it comes from (or, for a rumour, which way to look).
    public Bearing From { get; init; }
    // Raiders or newcomers.
    public int Size { get; init; }
    // Rumour: the search area told to the owner — a circle that holds the
    // ruin, centred off it. Radius 0 for other kinds.
    public TileCoord AreaCenter { get; init; }
    public int AreaRadius { get; init; }
    // Raid: the war chest dropped when every raider is killed.
    public Resource ChestResource { get; init; }
    public int ChestAmount { get; init; }

    public OmenState State { get; internal set; } = OmenState.Pending;
    public bool IsLive => State is OmenState.Pending or OmenState.Arrived;
    // When it went from live to an outcome; 0 while live.
    public long ResolvedTick { get; internal set; }
    public long DueTick { get; internal set; }
    public long DueSeq { get; internal set; }
    public int Slips { get; internal set; }
    // Raid, once Arrived: the raiders still alive, ascending id.
    public List<int> PartyIds { get; } = new();
    // Raid: a raider got away carrying loot. Decides Repelled vs Lost.
    public bool Plundered { get; internal set; }
    // Raid: a raider got away at all (loot or not). No war chest then.
    public bool Escaped { get; internal set; }
}
