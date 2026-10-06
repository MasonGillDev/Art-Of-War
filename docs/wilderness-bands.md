# Wilderness bands: how far every tile lies from the kingdoms

**Status:** the field is built (2026-10-05): it is computed, stored and snapshotted. Nothing
reads it yet. Code: `src/Sim.Core/Wilderness/WildernessField.cs`, computed in
`Genesis.Build`, held on `GameWorld.Wilderness`, snapshot v54.

## The decision

At genesis, every tile gets a **remoteness**: the walking time, in game-minutes, from the
nearest **starting** castle. It uses the same foot movement cost units pay, rivers
included.

The reachable land is then cut into four **bands** by share, nearest first:

| Band | Share of reachable land |
|---|---|
| Settled | nearest 40% |
| Frontier | 40–70% |
| Wild | 70–90% |
| Deep | farthest 10% |

- Water has no band (`None`).
- Land no foot can reach (an island) is Deep.
- The field is **frozen**: nothing in the sim writes it after genesis.

The band is not stored. It is derived from the stored minutes and the three cut-offs, so
there is one source of truth. The shares are a `GenesisSpec.Wilderness` knob
(`WildernessConfig`).

What reads it: `world.Wilderness.BandAt(tile)` and `world.Wilderness.MinutesAt(tile)`.

## Why

This is the base for pushing players outward and making the world more dangerous
(the user, 2026-10-05):
- better loot and new resources deep in the fog;
- danger that grows with distance from the kingdoms;
- outposts in far lands.

Every one of those needs to know **how far out a tile is**, and all must agree.

### Choices and what lost

- **Walking time, not straight-line distance.** A valley behind a mountain range near you is
  wilder than open plain farther away, and the bands should say so. The same rule also makes
  pockets of wild land between kingdoms, not only a ring on the coast. Straight-line
  (Chebyshev) distance was simpler. It lost because it ignores terrain, and terrain is what
  makes a place remote.
- **Cut by share, not by fixed distance.** Shares give every map the same proportions
  whatever its size, seed or kingdom count. Measured on the default 252 map (seed 3301431)
  with fair start placement:
  - with 6 kingdoms, Frontier starts 1.3 game-days' walk out, Wild 2.1 and Deep 2.8;
  - with 16 kingdoms, Frontier starts at 0.8, Wild at 1.2 and Deep at 1.7.

  Fixed cut-offs ("Deep is 3+ days out") lost: they produce no Deep on a crowded map and
  mostly Deep on an empty one.
  The minutes are stored as well, so a rule that wants absolute distance can still read it.
- **Frozen at genesis, not live.** Kingdoms die, outposts rise and canals change the land.
  A live field would shift danger and loot under the player's feet: a hoard placed in the
  Deep would suddenly sit in the Frontier. Frozen means a band is a fact about the map. The
  planned way to make wild land safer is local, not a recomputation: an outpost suppresses
  danger around itself while the land keeps its band.
- **Measured from starting castles only.** Bandit camps, caches and later structures don't
  count. Starts are the only places every seat has at tick 0, and fair start placement
  (`docs/fair-start-placement.md`) makes them even.
- **Islands are Deep.** Land you can't walk to is the most remote land there is. Its minutes
  are null, so a rule that cares can tell an island from far mainland.
- **Remoteness is stored, not recomputed on restore.** A restore can't recompute it: a canal
  dug since genesis changes the grid the measure was taken on. The field costs 2 bytes per
  tile (about 127 KB on a 252² map, the same as the biome and river layers together).

### Determinism

- Integer Dijkstra over 4-neighbours from all castles at once. Shortest distances are unique,
  so the priority queue's tie order can't change the result.
- The cut-offs come from a sorted list of integers.
- It draws no random numbers.
- It is a pure read of the world at genesis. After that, only the snapshot writes it.
- It costs about 20–30 ms once per world on a 252² map.

## Shape

- `WildBand` (byte, append-only, order = remoteness): None 0, Settled 1, Frontier 2, Wild 3,
  Deep 4.
- `WildernessField`:
  - per-tile `ushort` minutes in (y, x) order;
  - markers `Water` (65535) and `BeyondReach` (65534);
  - distances capped at 65533 (about 45 game-days);
  - cut-offs `FrontierFrom` / `WildFrom` / `DeepFrom` in minutes;
  - `Empty` for a world built without genesis.
- **Snapshot v54:** after the route extras, a has-field flag, then the three cut-offs, then
  one `ushort` per tile.

## Future expansion

These are the uses the field was built for, each its own decision:
- loot caches tiered by band;
- bandit camps placed in Wild and Deep land, growing if left unfound;
- resource sites (old-growth timber in deep forest, rich veins in wild mountains);
- predators;
- outposts that suppress danger locally;
- the wire: send the band to the client, or reveal it only as the land is scouted.

Changing the shares is a config change: new worlds use them, existing worlds keep what they
measured.

## Acceptance

`WildernessTests`:
- **Remoteness is the walk from the nearest castle:** on a hand-built strip, with two castles
  and a river crossing.
- **Edge cases:** water has no band, an island is Deep, and a world without genesis has no
  field.
- **Shares on a generated world:** each band holds its share within 3 points, and the bands
  rise with distance.
- **Frozen:** the field survives a snapshot (the hashes match), and a later change to the land
  doesn't move it.
