# M25 — Rival: the offensive AI (full conquest)

M17 gave the world AI players that build economies and defend themselves;
M24 gave the world sieges, defeat, and a winner. What's missing from
"playing against the AI" is an AI that *attacks*: the Rival, phase 3 of
the AI-players ladder (docs/ai-players.md), whose stated blocker —
"conquest waits on win conditions" — fell with M24. The user's call:
**full conquest**. The Rival plays to win — it scouts enemies, declares
war, raids supply lines, besieges castles, and will raze the human's
castle and end the game.

No Sim.Core changes. Combat is presence-based (marching onto a hostile
tile starts it), sieges auto-resolve against undefended structures, and
the diplomacy intents (`DeclareWarIntent`, `ProposeRelationshipIntent`,
`RespondToProposalIntent`) have existed since M6/M8. M25 is Sim.Server
brain work plus one wire addition: diplomacy on the ViewDto — which the
human client needs anyway, because the war telegraph IS the fairness
mechanism and the human must see it coming too.

## Locked decisions

1. **Composition, not a fork.** Pre-locked by docs/m17-defender-spec.md:
   the Rival is the same rung vocabulary in a different order plus
   Rival-only rungs. The two-layer think loop (strategic ladder →
   logistics background) extracted move-only into `BrainCore`; both
   brains implement `IBrain` and delegate. Rival ladder:
   `Defend → Eat → Build → Train → Muster(offense) → War → Conquer →
   Raid → Probe → Grow → Scout`. Defend stays on top (don't lose your
   own castle while the army's away); Eat outranks everything martial
   (ledger #11, "Sparta starves", is structural, not a tuning choice).
   War/Conquer/Raid fire only when policy is active, so a peacetime
   Rival curves exactly like a Homesteader.
2. **Enemy-intel perception is a pre-pass, not a rung** — it hangs on
   BrainCore's `perceive` hook and runs every think regardless of which
   rung claims (the same reasoning that put threat-memory updates at the
   top of DefendRung).
3. **Brain selection**: `--rivals K` of `--ai N` (`AiConfig.RivalCount`,
   default 0 — aggression is opt-in). The host assigns the Rival ladder
   to the **highest K AI faction ids**, deterministically; faction 1 —
   the balance lab's baseline in every pre-M25 test — stays a
   Homesteader so the golden curves don't move.
4. **Individual MoveIntent convergence, not groups.** `EquipUnitIntent`
   rejects grouped units, solo intents are rejected on grouped units
   (M5), and group hop cost scales with member crowding. The
   BanditDriver precedent and M17's "group sorties deferred" both point
   the same way. Groups revisit when a measured failure demands them.
5. **Intel is droppable memory** (`KnownEnemyStructures`,
   `KnownEnemyCastles`): refreshed on sight, cleared when the tile is
   re-observed absent/Rubble — the exact `SightedHostiles` discipline.
   A restarted server re-scouts; observation stays ground truth.
6. **War policy**: one war at a time. Declare only when the colony can
   afford it (population floor, food runway healthy, garrison at
   quota) and the fight is picked, not stumbled into (fresh intel on a
   non-defeated enemy castle; own military power ×100 ≥ estimate ×
   `WarAdvantageRatioPercent`). Target = nearest known castle — march
   time is the real cost. Enemy power is ESTIMATED from visible units
   priced via `UnitCombatCatalog` (rule constants — a human reads the
   same numbers off the UI), floored by `AssumedGarrisonPower` (never
   plan against zero).
7. **The telegraph is embraced, not gamed.** DeclareWar's pending window
   (`DiplomacyConfig.Delay`) is the Rival's mobilization phase — muster,
   craft, assemble happen inside it. **No shot before `AreHostile` is
   true**: no combat contact, no neutral looting (even though
   `LoadCargoIntent` would physically allow it). Pinned by headline
   test.
8. **Attack economics**: offensive budget = `pop /
   OffensePopulationPerSoldier` ON TOP of the peacetime garrison quota,
   clamped by the existing wartime ceiling (`WarPopulationPerSoldier`,
   ledger #11). Campaign soldiers are cross-think designations wired
   into the shared designation set, so every existing selector — and
   Defend's stand-down — skips them for free (ledger #6 machinery,
   reused verbatim). Equipment this milestone: shields only (wood +
   stone, both already in the Rival's economy); the sword/ore economy
   is staged out exactly like M17 staged equipment.
9. **Campaigns are observation-driven state machines** (`ConquerRung`):
   PREP (designate + equip; the garrison never marches) → ASSEMBLE at
   the Barracks rally (bounds arrival spread — multi-round combat
   punishes trickle-in) → MARCH (orders only to idle-still units; the
   Idle-while-moving replay pin from docs/bandits.md) → SIEGE (park on
   the castle tile; M24's combat trigger does the rest — defenders
   shield the structure, so kill-defenders-first is automatic) →
   observe Rubble / `Defeated` → next target or stand down. WITHDRAW
   when campaign power falls below `RetreatBelowPercent` of the visible
   defense or the home colony hits famine (the Sparta abort).
10. **Peace**: target defeated → pick the next target (full conquest
    loops to GameOverEvent). Losing → propose Neutral, and accept
    incoming peace proposals. Winning → ignore proposals (lazy expiry
    handles them).
11. **Raids**: when war is effective but no campaign is in March/Siege,
    `RaidPartySize` soldiers hit the nearest known enemy
    extractor/stockpile, `LoadCargoIntent` the stock, walk it home,
    unload, un-designate. `RaidPartySize: 0` doubles as the doctrine
    A/B switch (raid-first vs siege-rush) for the balance lab.

## New AiConfig knobs

| Knob | Default | Why |
|---|---|---|
| `RivalCount` | 0 | aggression is opt-in; 0 = the pre-M25 world |
| `CampaignPopulationFloor` | 30 | young colonies homestead; early curves untouched |
| `WarAdvantageRatioPercent` | 150 | wars are picked, not stumbled into |
| `AttackOvermatchPercent` | 150 | declaration is strategic, GO is operational |
| `RetreatBelowPercent` | 100 | no death spirals; parity lost = campaign over |
| `OffensePopulationPerSoldier` | 6 | the offense budget scales with the society that pays it (ledger #11) |
| `RaidPartySize` | 3 | every standing job has a bound (ledger #9); 0 = A/B switch |
| `AssumedGarrisonPower` | 12 | never plan against zero — fog hides garrisons |
| `IntelStaleTicks` | 6 days | a week-old castle sighting is not a war plan |
| `ProbeLegBudget` | 24 | scouting pressure is bounded like every job (ledger #9) |

## Phases

1. **Diplomacy on the wire + the brain seam** — ViewDto gains
   Factions/Relationships/PendingWars/IncomingProposals (public
   knowledge mirrored from PlayerView; sentinel owners never listed);
   `IBrain`/`BrainCore` extraction; `--rivals` plumbed host-to-config;
   deterministic highest-ids assignment.
2. **Enemy intel + threat extension** — AiMemory intel fields;
   EnemyIntel perceive pre-pass; ThinkContext diplomacy digests;
   DefendRung's hostile test extends to declared-war factions (the
   pre-announced seam at DefendRung.UpdateThreatMemory — Homesteader
   victims defend against Rivals for free); ProbeRung.
3. **RivalBrain + WarRung + the offense budget** (MusterRung ctor flag).
4. **ConquerRung** — assemble → equip → march → besiege → raze. This
   phase alone delivers full conquest; raids come after because the
   siege is the win condition and mechanically simpler than the raid
   loop.
5. **RaidRung + the balance lab campaign + the replay headline.**
6. **Docs, determinism audit, smoke.**

## Explicitly deferred

Alliances-with-benefits, multi-front wars (one-war-at-a-time is a
constant, not a knob), fortifications/walls, repair, castle *capture*
(razing only — M24's stance), ranged combat, the sword/ore economy,
loot-pile recovery on the wire (still not on the wire for anyone),
Homesteader-initiated diplomacy, rival coalitions, difficulty handicaps.

## Headline tests

- `Rival_RazesUndefendedCastle_GameOverFires` — two factions, only the
  Rival driven; horizon computed from the world's `DiplomacyConfig.Delay`
  + march cost + `ceil(castleHP / campaignPower)` × combat round period —
  all config-derived, never hard-coded ticks.
- `Rival_DeclaresWarBeforeAttacking` — THE telegraph pin: the first
  hostile contact happens at or after the war's EffectiveTick, and the
  DeclareWarIntent precedes it in the durable log.
- `Rival_DoesNotStarveItsOwnColony` — the Sparta pin: a campaigning
  Rival's colony survives the match without starvation deaths.
- `RivalVsHomesteader_WarEndsDecisively` — no forever-war; the lab
  report prints the curve.
- `Rival_ReplayFromIntentLog_HashesMatch` — bandits + Homesteader +
  Rival all driving, chronological interleave (docs/bandits.md replay
  discipline), `Snapshot.Hash` equality.
- `Brain_TouchesOnlyTheView` stays green over the whole namespace — the
  fairness sweep covers every new rung by construction.

## Update 2026-07-05 — SHIPPED (all six phases, 812 + 37 green)

All phases landed in one pass; the full suite (798 pre-existing + 18 new,
minus 4 superseded counts) and the persistence suite stayed green at every
phase boundary. Deviations from the plan above, each forced by a measured
or reasoned failure:

- **Ladder: War ABOVE Muster** (spec sketch had Muster first). Declaring
  is what flips `AtWarOrMobilizing`, which is what tells Muster's
  drawdown the surplus is a WAR CHEST — the sketched order let the demob
  walk the army to the School before WarRung ever got a think (caught
  dry-running the lab bench on day 0).
- **Ladder: Raid ABOVE Conquer** (sketch had Conquer first). Conquer's
  PREP sweeps every free surplus soldier into the campaign roster; a
  party below it can never form. Ledger #5, relearned: priority is who
  reserves first, not rung order.
- **Raids: extractors only.** `LoadCargoIntent` must NAME the resource
  and enemy holdings are fogged — an extractor's kind names its output
  through `StructureCatalog` (rule knowledge); a stockpile's contents
  name nothing. Storage/castle raids wait for observable loot.
  Dry buffers are learned by observation (two consecutive empty-handed
  thinks standing on the target — the ZeroBufferThinks discipline).
- **`AcceptPeaceRung` on the Homesteader** (not in the plan). A losing
  Rival sues for peace; a defender that can never answer makes every
  war unendable by treaty. Responding is not the deferred
  "Homesteader-initiated diplomacy" — it's table stakes. Top of the
  ladder; fires only when a Neutral offer is pending.
- **Shields deferred after all** (the plan said shields-only this
  milestone). The decisive-war lab ended conclusively with bare
  soldiers, so equipment joins the sword/ore economy in the deferred
  list — the exact M17 staging rule: equipment waits until the lab
  shows bare squads losing.

**Post-ship fix 2026-07-06 — the mobilization interlock** (found by the
first live playtest: "they aren't battling each other"). The advantage
gate priced the STANDING army, but the peacetime quota deliberately caps
that below the bar, and the offense budget that would raise it waited on
a declared war — a chicken-and-egg that kept every organic Rival at
peace forever. The integration tests had masked it by injecting
soldiers. Fix: the declaration gate prices MOBILIZABLE power (the
wartime ceiling at bare-soldier rates — declaration is strategic, "we
can raise an army that wins in the telegraph window"), while
ConquerRung's GO gate still re-checks the REAL assembled army before
anyone marches (operational). Pinned by
`Rivals_GoToWar_Organically_LabReport`: two Rivals + a Homesteader,
default knobs, no injections — first declaration day 82, first combat
day 115, one Rival conquered outright by day 250. Same fix ships with
world-news notices (GameHost broadcasts declarations, effective wars,
falls, and game-over to every player's notice feed + the console) so
wars are observable without curling the view JSON.

Lab outcomes: the campaign machine razes an undefended castle and fires
`GameOverEvent` end-to-end under the full 30-day default telegraph (the
headline); a 12-soldier war chest against a stock Homesteader ends
decisively within 40 days (recall doctrine's civilian shield wall holds
the castle for a while — then falls); a peacetime Rival's 60-day curve
carries no famine and no starvation deaths (the Sparta pin); the replay
hash holds with bandits + Homesteader + Rival interleaved on one clock.

## Open question (flagged during Phase 1, user decision)

`DiplomacyConfig.Delay` is `1 * Time.Month` (30 game-days) in code while
its own comment claims "6 game-hours, matching the original design doc"
(src/Sim.Core/Diplomacy/DiplomacyConfig.cs). A month of telegraph
dominates how war feels — the victim gets 30 days of visible warning.
All M25 tests compute from the world's actual config, so either value
works mechanically; the knob should be settled during the Phase 5 lab.

## References

- docs/ai-players.md — the arbitration ledger (12 lessons every new
  rung must respect), phase ladder, Rival as phase 3.
- docs/m17-defender-spec.md — the composition pre-lock, Muster/Defend
  rungs the Rival reuses.
- docs/sieges-and-conquest.md — M24 mechanics the Rival drives
  (structure HP, defender shielding, PlayerDefeated → GameOver).
- docs/bandits.md — the intent-driver pitfalls (Idle-while-moving,
  replay interleaving) inherited by every driver.
- docs/diplomacy-model.md — relationships, the telegraph, proposals.
