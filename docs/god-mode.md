# God mode (`--god 1`): free, instant placement for the human seat

## The decision

A `--god 1` host flag marks **player 0** as a god: every structure that
player places (sites, wall lines, canals) passes the ordinary placement
validation and then **completes on the spot** — no materials hauled, no
builders assigned, no build duration. It is a test-harness switch for
walking the full game quickly. AI factions never get it.

The flag is **sim state**, not a server-side check: `Player.GodMode`, set at
genesis from `FactionStartSpec.GodMode`, serialized in the snapshot (v34).

## Why

**Where the flag lives.** The intent log replays without the server options.
If god mode were a `GameHost` rewrite of incoming intents, replaying a god
game (or recovering one from a snapshot + log) would rebuild a *different*
world: the placements would land as pending sites instead of standing
structures, and the hash would diverge. Putting the flag on the `Player`
row makes it part of the world the log was recorded against, so replay and
recovery reproduce a god run exactly like any other. This is the same
reasoning as the fertility launch switch living in `BiomeDegradationConfig`.

*Rejected: host-side interception.* Cheaper (no snapshot bump) but breaks
the determinism contract for exactly the runs it is meant to help test.

*Rejected: a Simulation-level rule object.* Would also need snapshotting
to survive recovery, and `Player` already holds per-faction switches
(`Defeated`, `KingUnitId`); one more bool is the smallest canonical home.

**What "instant" means.** The three placement intents already validate and
insert a `ConstructionSite`. God mode then calls `Construction.Complete`
on that site immediately. To make that possible without a second copy of
"what a finished build means", the success body of `BuildCompleteEvent`
was extracted into `Sim.Core.Logistics.Construction.Complete` — site
removal, builder release, canal flooding (with its load-bearing
catch-up-before-flood order), rubble clearing, the built structure with
claim transfer, manning, vision, dock arming and house move-in. The event
keeps only its three gates (site exists, fencing token, prerequisites) and
calls the helper. God mode and the ordinary route therefore cannot drift.

*Rejected: bypassing validation too.* A god still cannot build on water,
on a claimed tile, or on a Mountain farm. Keeping validation means a god
run still exercises the real placement rules; anyone wanting to test
"place anywhere" needs a second, separate switch.

*Rejected: making Castle / Tower placeable.* The catalog's
`IsPlayerBuildable` gate is untouched; god mode is about cost and time,
not about which kinds exist for a player.

**Cost.** Nothing is deducted because nothing was ever hauled. The site's
`Required` table is never consulted.

## What it does NOT do

- Does not touch training, hauling, combat, or diplomacy.
- Does not blind the AI: the god player's free castle sprint looks like a
  very rich rival. Balance readouts from a god run mean nothing.
- Does not flip mid-game. The flag is genesis-set and immutable, like
  `RoyaltyConfig`.

## Acceptance tests

`tests/Sim.Tests/GodModeTests.cs`:

- god placement → structure stands, no site remains, owner carried;
- ordinary player on the same world → pending site, prerequisites unmet;
- validation unchanged (biome reject leaves the world untouched);
- claiming kinds transfer the claim to the built extractor;
- wall line → every segment stands; canal → path floods on placement;
- snapshot round-trip keeps the flag, hash equal, restored god still instant;
- twin-run hash match with a god and an ordinary player interleaved.

## Future expansion

- `FactionStartSpec.GodMode` is per faction, so a lab that wants a god AI
  (e.g. to stress the siege code against a fully walled rival at day 1)
  only needs to set the flag on that faction's spec.
- A "place anywhere" switch, if ever wanted, is a second bool checked in
  the same three intents *before* validation — it does not touch
  `Construction`.
- Instant training / instant hauling would be siblings of this switch,
  each on its own bool, each reusing the existing completion path the way
  this one reuses `Construction.Complete`.
