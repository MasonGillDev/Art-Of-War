namespace Sim.Core.Bandits;

// M16 — the bandit faction (docs/m16-bandits-spec.md). In the core's
// eyes bandits are just another faction: a reserved OwnerId whose units
// move, fight, carry, and die through entirely existing machinery. What
// makes the faction bandit-SHAPED is three exemptions wired off this id:
//
//   * hostile to every faction always (Diplomacy.AreHostile special
//     case — no relationship rows, no war telegraph, no peace);
//   * exempt from civilization (no castle → the food/famine machinery
//     never engages; no Houses → no breeding; spawn path skips the
//     lifespan roll → no death-by-age);
//   * no remembered map (Sight.Reveal skips this owner — bandits hunt
//     from LIVE sight only, View.VisibleTiles; their longer-term
//     "memory" is the server-side driver's ephemeral problem).
//
// The id is out-of-band BELOW all player ids (players are 0..N, assigned
// at genesis). Genesis rejects any FactionStartSpec claiming it, and the
// wire rejects any envelope claiming it (GameHost) — only the in-process
// bandit driver may speak as this faction.
public static class BanditConstants
{
    public const int OwnerId = -1;

    // Spawn placement (validated in core at resolve time — the driver
    // proposes, the sim disposes). MinSpawnDistance is Chebyshev tiles
    // (= km) from ANY player unit or structure, seen or not: darkness
    // alone isn't enough, because economic structures (extractors,
    // stockpiles) cast no vision — without the distance floor a party
    // could materialize ON an unwatched lumber camp. 10 km of warning
    // also means a raid is always a march, never a jump-scare.
    public const int MinSpawnDistance = 10;

    // Party-size ceiling — a sanity clamp on the driver, not a balance
    // knob (the driver's own config decides actual sizes).
    public const int MaxPartySize = 8;
}
