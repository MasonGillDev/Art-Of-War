# Loot Caches (M23)

## Decision

**Random loot caches are scattered across the world at genesis, only ever on
tiles no player has seen.** A cache is a **dedicated, unowned structure**
(`StructureKind.Cache`) holding a rolled bundle of resources and/or gear. It is
discovered by exploring into its tile, **vanishes back into the fog** when no
player can see it, and is **looted cargo-capped** by a unit standing on it
(`LootCacheIntent`): the unit takes one named resource up to its cargo space,
the remainder stays, and the cache is removed once empty. First unit there wins.

Locked choices (from the design discussion):

| Aspect | Decision |
|---|---|
| Form | Dedicated unowned `Cache : StorageStructure` (owner `-2`) |
| Spawn | **Genesis scatter only** — rolled once with the sim's `Rng`, frozen in the save |
| Placement | Only tiles **no player has ever seen** (outside every starting-vision disc) |
| Visibility | Appears only while genuinely in vision; **vanishes** otherwise |
| Looting | Cargo-capped; remainder persists, re-lootable; removed when emptied |
| Contents | Resources **and** gear (sword/bow/shield) |

## Why

### Why this is mostly reuse, not new machinery

The whole feature leans on parts that already exist, which is why it's low-risk:

- **The structure + its loot** ride `StorageStructure` (Holdings + `Withdraw`)
  and the existing structure snapshot payload (`WriteStorage`/`ReadStorage`).
- **Looting** is the M16 cargo atom: `LoadCargoIntent` already does a
  cargo-capped, ownership-unchecked withdraw from a `StorageStructure` on the
  unit's tile, so a `Cache` is lootable by construction. `LootCacheIntent` is
  that logic plus the one cache-specific step — remove the cache when emptied —
  kept as its own verb so cache rules don't leak into the general cargo path.
- **Genesis RNG ordering** mirrors the lifespan rolls: the scatter runs once in
  the `Simulation` spec-ctor with the sim's owned `Rng`, after the lifespan
  rolls, so a `Count == 0` config is a pure no-op that consumes no `Rng` and
  leaves every existing scenario bit-identical.
- **The fog behavior is free.** `View.BuildPlayerView` shows a structure only
  when `OwnerId == viewer || tile is visible`. The cache's owner `-2` never
  equals a player, so it surfaces exactly while a player can see its tile and
  drops out of view otherwise — the "vanishes back into the fog" rule with no
  extra code. (Structures aren't remembered, so there's no lingering marker.)

### Why an unowned `-2` owner

A cache belongs to no faction, but a structure needs an `OwnerId`. Reusing a
player id would make the cache "theirs" (always visible to them, enemy to
others); reusing the bandit id `-1` would make it hostile. So caches get their
own out-of-band sentinel `CacheConstants.OwnerId = -2`, below the bandit id —
mirroring `BanditConstants`. No `Player` row is registered for it, so it never
appears as a faction and engages no diplomacy / population / food machinery (a
cache has no units). The id is what makes the fog-vanish behavior fall out for
free.

### Why genesis-only, and why the fog gate is the load-bearing rule

Genesis scatter keeps the determinism story trivial: roll positions and loot
once from the seeded `Rng`, freeze the resulting `Cache` structures into the
snapshot. No driver, no events, no anchors → recovery-clean (restore loads the
caches; it never re-scatters). At genesis "no player has ever seen it" is simply
"outside every player's starting-vision disc," which is the overwhelming
majority of the map. The scatter is fully deterministic: candidate tiles
enumerated in canonical `(y, x)` order, selected by a partial Fisher-Yates draw,
loot rolled — all from the one seeded `Rng`. (Ongoing respawns were considered
and deferred; the M16 bandit "spawn-in-darkness" driver is the proven template
if they're ever wanted — see Future expansion.)

### Why cargo-capped, single-resource looting

The engine's "everything is physical" rule already models cargo as a single
resource type up to a role-based capacity. Looting honors it: a unit takes one
named resource up to its free space; a big haul (or a mixed "40 wood + a sword"
cache) needs a hauler or several trips, and a rival can grab the leftovers
between trips. That makes a fat cache a *logistics* reward and keeps contention
alive, rather than letting a lone scout teleport a fortune home. Because cargo
is single-type, `LootCacheIntent` names which resource to take — and a cache's
contents are revealed to whoever can *see* it (the `ViewProjector`, same "a
visible structure's contents are public" stance as M15 claims) so the player can
choose. The contents stay fogged with the cache itself.

### Why a dedicated `LootCacheIntent` rather than just `LoadCargoIntent`

`LoadCargoIntent` would loot a cache fine (it's a `StorageStructure`), but it
doesn't remove the cache when emptied, and folding cache-removal into the
general cargo verb would couple unrelated concerns. A dedicated verb reads as
the player's intent ("loot this cache"), owns the remove-when-empty step, and
can be gated to caches only.

## Future expansion

- **Ongoing respawns.** A server-side `CacheDriver` following the M16 bandit
  pattern (pure reads in, durable `SpawnCacheIntent` out, resolve-time
  re-validation that the tile is still dark) would let the map keep rewarding
  exploration. The fog gate would relax from "never seen" to "not currently
  visible to anyone" so late-game pristine tiles don't run out.
- **Guarded caches / ruins.** A cache could spawn with a defending mob (the
  bandit faction), turning the richest finds into a fight — the "ruins" half of
  the design doc's "things in the fog."
- **Rarer / structured loot tables.** Weighted tables, unique items, biome- or
  distance-scaled value (deeper / higher = better).
- **AI looting.** The Homesteader/brain already receives caches and their
  contents through the same view; teaching a rung to detour for a nearby cache
  is an AI-behavior addition, not a sim change.
- **A "discovered" marker.** If play wants less rush-or-lose, a per-player
  remembered-cache marker (the M22 RememberedTerrain trick, applied to
  structures) would let a glimpsed cache stay on the map.

## Acceptance tests

`tests/Sim.Tests/CachesTests.cs`:

- A cache on a visible tile appears in the view; on a fogged tile it is absent
  (vanishes when unseen); it round-trips through the snapshot with its loot.
- Looting is cargo-capped (Hauler takes 25 of 100, remainder 75 persists);
  emptying the cache removes it; gear loots; wrong-resource / not-on-tile reject;
  first-come-first-served (the second looter finds it gone).
- Scatter places exactly `Count` unowned, looted caches; **every cache is on a
  tile unseen by all players at genesis** (the constraint); it avoids water;
  `Count == 0` produces none; twin-run gives identical placement + loot.

## Headline determinism test

> **`CachesTests.Caches_TwinRun_HashesMatch`** — two identical genesis scenarios
> (same seed + spec, caches scattered) produce equal `Snapshot.Hash`. Combined
> with `Scatter_EveryCache_IsUnseenByAllPlayers_AtGenesis` (the spawn-in-fog
> invariant), this is the M23 contract.

## References

- `docs/architecture.md` §8 ("things-in-the-fog" — the system this is),
  §2.8 (no new anchors → recovery-clean).
- `docs/m16-bandits-spec.md` / `docs/bandits.md` (spawn-in-darkness + the
  driver template a respawn layer would reuse; `BanditConstants` the owner
  sentinel mirrors).
- `docs/high-terrain-visibility.md` (the complement: visible peaks to race,
  hidden caches to discover).
- `Sim.Core/Caches/*` (CacheConstants, CacheConfig, CacheScatter,
  LootCacheIntent), `Sim.Core/World/Structure.cs` (`Cache`),
  `Sim.Core/Logistics/LoadCargoIntent.cs` (the cargo atom it mirrors).
