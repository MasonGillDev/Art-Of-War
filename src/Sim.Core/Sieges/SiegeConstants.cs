namespace Sim.Core.Sieges;

// M24 — sieges & conquest (docs/sieges-and-conquest.md). Rubble is the
// destroyed-structure tile occupant produced by CombatRoundEvent when a
// structure's Health drops to zero. It has no faction — out-of-band BELOW
// every other reserved owner id (players 0..N, bandits -1, caches -2),
// mirroring the BanditConstants / CacheConstants pattern.
//
// The "destroyed" id is load-bearing in three places:
//   * Iteration over "player N's structures" naturally skips rubble (no
//     player ever has id -3). A player whose castle is razed doesn't keep
//     accidentally appearing as the rubble's "owner."
//   * Combat-start gates on diplomacy hostility, and Diplomacy treats any
//     non-player owner as inert. A rubble pile draws no further attacks.
//   * Snapshot / view code that already special-cases -1 and -2 needs no
//     new arm for rubble — it falls through the "not a living player"
//     branch like the others.
public static class SiegeConstants
{
    // Owner id stamped onto every Rubble structure. Distinct from bandits
    // (-1) and caches (-2) so the wire / driver / iteration code can tell
    // a destroyed pile apart from those.
    public const int RubbleOwnerId = -3;
}
