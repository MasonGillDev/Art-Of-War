# Bandit camps: raiders with a home, a hoard, and an address

## The decision

A **bandit camp** is a bandit-owned structure in the fog. Its garrison stands guard; its
raiders ride out on a schedule, steal, and **carry the loot home to the camp's hoard**
instead of vanishing with it. Raze the camp and the hoard spills on the ground for you to
collect.

For now a camp is raised by progression, one per human seat: the **Smoke on the
horizon** milestone (population 25). It is rumoured with a search circle, like the
ruin, and its raiders ride after a warning. Razing it fires **The camp burns**, which
brings home captives. World-wide camps that replace today's anonymous "prosperity"
spawns are a later step.

## Why

- **The user chose it first** (2026-09-24) from the secrets and progression proposal.
- **It is M16's designed follow-up.** `docs/bandits.md`: "camps (destroyable spawn
  structures with hoards) are the richer loop — they give exploration a payoff and the
  military an objective … the despawn valve and driver FSM were built so camps slot in
  by swapping the flee destination and the spawn site source."
- **It deepens five systems at once:**
  - the bandit driver (a home to flee to, a garrison to keep);
  - sieges (the camp is razed like any structure);
  - piles (the hoard spills, as a razed castle's vault does);
  - patrols (the answer while you are away);
  - scouting (a camp is a secret: "smoke").
- **It gives progression a threat with an address.** A problem you can go and solve,
  rather than weather.

### Numbers, and why they are these

From the combat replay in `docs/secrets-and-progression-proposal.md` (section 1.3, real
stats, real round rule):

| | Value | Why |
|---|---|---|
| Garrison | 6 bandits (cap 6) | 6 swordsmen win the assault with 5 standing; 8 bare soldiers with 6; 6 bare soldiers win but lose 4. The camp asks you to arm, the same lesson as the reprisal. |
| Recruits | +1 every 3 days, up to the cap | Killing raiders weakens the camp: it regrows about 2.3 a week and a raid takes 3. |
| Raid | 3 bandits every 7 days, only when no earlier raid is still out and at least 1 would stay home | A 4-soldier patrol beats 3 bandits with 3 standing, even while you sleep (the async rule: anything that can reach you while you are away is at most 3 bandits). |
| First raid | 10 days after the rumour (1 hour real) | Time to raise a Barracks and train 4 (Barracks 100 wood + 20 stone; training is instant). |
| Hoard | starts 30 iron + 20 ore; raids stop while it holds 200 or more | 200 is about 4–5 full raids (15 each). An unanswered camp does bounded harm, then sits fat on your goods, which is the reason to go and take them back. |
| Health | 300 | 17 h of uncontested siege for 6 bare soldiers (18 power), 8 h for 6 swordsmen. |
| Placement | 20–40 tiles from your castle, in the wildest direction, on unexplored ground, at least 10 tiles from anyone | The rumour ruin's placement, at camp range. |
| The camp burns | 3 captives walk home | People are the strongest reward (proposal section 1.2); they were taken on raids. |

### Alternatives considered

- **The driver decides musters (brain-side state).** Lost. The driver's state is
  disposable by design, and a raid in flight must survive a restart without being sent
  twice. The **sim** keeps who is out raiding (the camp's durable raider list) and when
  the next muster is due (an anchored daily event). The driver only plays the raid out.
- **Camps spawn fresh raiders for every raid.** Lost. The garrison is a population that
  raids draw from and recruits refill, so answering raids is how you weaken a camp.
- **No hoard cap.** Lost. An unanswered camp would raid through a whole night (80
  game-days, about 11 raids). The cap bounds the harm to about 4–5 raids, which the
  vision's "presence buys finesse, never survival" demands.
- **Camps as world scenery from genesis.** Deferred. A per-player camp raised by
  progression is a telegraphed, sized test. World camps change bandit pressure for
  everyone, so they are their own step.

## Shape

- **Sim.Core:**
  - `StructureKind.BanditCamp` (23); `BanditCamp : StorageStructure` (the hoard) with
    `TargetOwnerId`, `SourceMilestoneId`, a durable `Raiders` list, `LastRaidTick`,
    `LastRecruitTick`, and a daily `CampTickEvent` anchor.
  - `CampConfig` (genesis-set, snapshotted).
  - `Camps`, the single mutation point: raise, recruit, muster, prune the dead, raze.
  - Omen kind `Camp` (the rumour); the `CampRumour` effect.
  - `ProgressStat.CampRazed` (Sub = the source milestone), credited to every hostile
    owner standing on the tile when it falls.
  - Rows 5 (Smoke on the horizon) and 6 (The camp burns).
  - `Charts.HintFor(BanditCamp)` = Smoke.
  - Snapshot v39.
- **Sim.Server:**
  - `BanditDriver`: garrison parties stay home; camp raiders are recognised from the
    camp's durable list and march on the target's seat; loaded raiders go home and
    unload into the hoard, then stand guard; a razed camp's survivors become ordinary
    raiders.
  - Bandit pressure no longer counts structures of owners below zero.
- **Wire:** the camp omen (the direction, a search circle, days until the first raid);
  the camp as an ordinary hostile structure while in sight.
- **Prod client:**
  - A tile recipe (campfire, awnings as tents, chests and crates for the hoard, barrels,
    axes stuck in the ground).
  - The camp is never painted on the map (only a scout's chart marks it).
  - HUD line and toasts; the search circle on the map; a bubble row saying how to raze.

## Acceptance tests

- **Placement:** at population 25 a camp stands in the wildest direction 20–40 tiles
  out, on unexplored ground, with 6 bandits, the starting hoard, and a camp omen whose
  circle holds it.
- **Recruiting:** the garrison regrows by 1 per recruit period to the cap, never above
  it, and not while a fight is on the tile.
- **Mustering:** 3 raiders leave on schedule; never while a raid is out, while the hoard
  is at its cap, or if nobody would stay home.
- **Returning:** raiders carry loot home and the hoard grows by it.
- **Razing:** killing the garrison and besieging the camp razes it. The hoard spills,
  the razing player's `CampRazed` goes up, the omen resolves, and The camp burns sends
  3 captives home.
- **Bandit pressure:** caches, idols, rubble and camps no longer count toward it.
- **Determinism:** twin-run and snapshot/restore mid-raid and mid-siege land on the
  same hash.

## Future expansion

- **World camps** (several per map at genesis, in the rim far from every castle) that
  own all bandit spawning, retiring "prosperity" pressure.
- **Tracking raiders home** as a scouting clue.
- **Captives inside the hoard**, freed on razing.
- **The warlord** (a later milestone): camp razed and 12 trained → a host of 16.
- **Camps that move** when their region fills with people.

## Update 2026-09-24 — built

- **The garrison cap counts riders still out**, so a raid cannot swell the camp.
- **`BanditCamp.RaidDeparted`** (durable, set in `MoveArrivalEvent` when a raider steps
  off the camp) tells a returning raider from a fresh one. A raider back home with nothing
  to unload stands guard and leaves the list at the camp's next daily tick.
- **The driver keeps no camp state.** Every think it re-derives the garrison (bandits on
  a camp tile) and the raid (the camp's `Raiders`) from the sim, so a restart changes
  nothing.
- **A camp's health is public while it is in sight**, like a wall's: burning it is a
  siege (`ViewProjector.IsFortificationKind`).
- **The prod client** draws it from a tile recipe (campfire, awnings, chest and crates,
  barrels, logs, torches, axes in the ground). The map never paints it (it is a secret);
  a scout's chart marks it with a red diamond. Its rumour's search circle is red.
