namespace Sim.Core.Caches;

// M23 — loot caches (docs/loot-caches.md). A cache is an UNOWNED structure —
// a discoverable treasure that belongs to no faction — so it needs an OwnerId
// that is no player and not the bandit faction. Out-of-band BELOW the bandit
// id, mirroring BanditConstants: players are 0..N, bandits -1, caches -2.
//
// The unowned id is load-bearing for the fog behavior: View.BuildPlayerView
// shows a structure only when `OwnerId == viewer || tile is visible`, and -2
// never equals a player, so a cache appears the moment a player can see its
// tile and VANISHES back into the fog when they look away — exactly the
// rush-or-lose behavior, with no extra code. Nothing registers a Player row
// for this id, so it never appears as a faction and engages no diplomacy /
// population / food machinery (a cache has no units).
public static class CacheConstants
{
    public const int OwnerId = -2;
}
