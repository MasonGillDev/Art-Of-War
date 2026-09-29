# Progression milestones — the ledger

One row per milestone: its name, when it happens, and what it does. **Add a row whenever
a milestone is added or changed.** The rules behind them are in `docs/progression.md`.

Every milestone fires **once per player**, is **hidden** until it fires (the player never
sees this list), and applies only to **human seats**. Numbers are the current defaults;
each is a knob in `ProgressionConfig` (`src/Sim.Core/Progression/`).

## Live

| # | Name | When it happens | What it does |
|---|---|---|---|
| 1 | Reprisal | 6 soldiers or archers trained (ever; losses don't undo it) | A raid of 6 bandits is announced 7 days ahead, from the wildest direction, and marches on your castle. Kill every raider and their war chest (10 iron) drops where the last one fell. |
| 2 | Word spreads | The Reprisal raid is beaten (every raider dead, or fled empty-handed) | 4 refugees are announced 2 days ahead and walk in from your settled side. |
| 3 | Far horizons | 1,500 tiles explored | A ruin (20 iron, 2 swords) is placed in unexplored land in the wildest direction; you are told the direction and a search circle (radius 8), never the tile. |
| 4 | A good home | 3 houses completed | 2 settlers are announced 2 days ahead and walk in from your settled side. **The Lodge becomes buildable** (and with it, scouts). |
| 5 | Smoke on the horizon | Population 25 | A bandit camp stands 20–40 tiles out in your wildest direction: 6 guards, a hoard (30 iron, 20 ore). You are told the direction and a search circle, never the tile. Its raiders (3 at a time, weekly) ride after 10 days, steal, and carry their loot home to the hoard. |
| 6 | The camp burns | You raze the camp Smoke on the horizon raised (kill its guard, then stand on it until it burns) | 3 freed captives walk home. The camp's hoard (the start plus everything it stole) spills where it stood. |

## Planned

From `docs/secrets-and-progression-proposal.md`; numbers are proposals until built.

| Name | When it happens | What it does |
|---|---|---|
| First find | Your first scout comes home with a charted secret | A wanderer follows the scout home (1 person). |
| Ironworks | 100 iron smelted | A smith arrives, bringing 2 swords. |
| The warlord | The camp razed **and** 12 soldiers or archers trained | A host of 16 bandits is announced 10 days ahead; beat it for the warlord's hoard (40 iron, 4 swords) and 6 refugees. |
| Merchants' road | A road of yours reaches the Paved stage | A travelling merchant sets up a stall beside your castle for a few days with posted barter rates: the first lesson in trade. |
| The wide world | 6,000 tiles explored (about 17% of the land) | A wreck is rumoured on an island or the coast (reaching it needs a Dock). |
| A market town | Population 60 | 6 settlers. |
| Long live the king | Your first succession (the founding king dies, around day 141–231) | 3 loyal retainers (soldiers) arrive. |
