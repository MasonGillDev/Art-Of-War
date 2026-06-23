# M25 — The Rival: war-capable AI players

> Spec doc (the *what*). The engineering *how* lives in `docs/architecture.md`;
> the design rationale is in the decision doc `docs/ai-rival.md`.
>
> **Status: shipped 2026-06-23.** All seven phases landed; headline green
> (`RivalHeadlineTests`); full suite green. See `docs/ai-rival.md`.

## The gap

M17 gave us the **Homesteader** — a fair, view-only economy brain that builds,
feeds, grows, and *defends against bandits*. M24 gave us the **win condition** —
razing a Castle defeats a player. Between them sits an empty space: **no AI ever
initiates conflict with another player.** The Homesteader's own `DefendRung`
names the missing piece — *"Rival extends this to declared-war factions via the
view's diplomacy data."* M25 builds that Rival.

The Rival is the next evolution of the opponent: it builds on the Homesteader
(does not replace it) and adds the capacity to **decide to go to war**, **raise
an offensive army**, and **march on and raze enemy structures** — including the
special structures that decide the game.

## Locked decisions (from the 2026-06-23 design Q&A)

1. **Per-AI personalities, seeded at genesis.** Each AI faction is born with a
   posture, assigned deterministically from the world seed + faction id (a pure,
   reproducible function — the driver is ephemeral and re-derives it on restart).
   Three postures:
   - **Homesteader** — today's brain exactly. Peaceful; defends its turf; never
     initiates an offensive war. `AiPersonality.Homesteader` is the **default**,
     so every existing scenario and test is unchanged.
   - **Opportunist** — a predator of weakness and a taker of land. Initiates
     limited wars when it sees an opening or is crowded; sues for peace once the
     grievance is settled or a campaign turns against it.
   - **Warlord** — plays the M24 win condition. Masses armies, sieges and razes
     enemy Castles, presses to eliminate rivals.

2. **Belligerence is symmetric.** The Rival treats *every* hostile faction the
   same — other AIs and the human player alike. AIs war each other; emergent
   multipolar conflict is the intended texture, not a bug.

3. **Four casus belli, gated by personality:**
   - **Encroachment** — a rival builds a structure or claims land inside/near
     the AI's territory.
   - **Retaliation** — a rival declares war on the AI, or its units are seen
     attacking the AI's people/holdings.
   - **Opportunism** — the AI's mustered force materially outweighs a visible
     rival garrison near a rival objective.
   - **Land/resource hunger** — the AI is boxed in (land bank below floor, no
     free pockets) and a neighbor holds land it needs.

   Homesteaders act on none of the offensive triggers (retaliation reduces to
   *defense*, not counter-invasion). Opportunists act on all four with
   conservative thresholds. Warlords act on all four aggressively and, absent any
   trigger, manufacture one against the nearest reachable rival.

4. **Strike doctrine — full campaigns, not punitive raids.** When an army
   mobilizes it prosecutes the war by priority:
   **military structures** (Barracks/Tower — decapitate force projection) →
   **economy** (extractors/farms — collapse the war's fuel) →
   **Castle** (the decisive, win-condition blow). Target selection is bounded by
   what the AI can actually *see* (fog) and by a force-parity commit gate (never
   trickle a squad into a siege it can't sustain).

## What gets built

- **Wire**: project the *public* diplomatic state (`Factions`,
  `Relationships`, `PendingWars`) — already on `PlayerView` — onto the
  `ViewDto`, so both the human client and the AI brain learn who is hostile
  through the **same fair channel**.
- **Personalities**: `AiPersonality` enum, deterministic seeding, `AiConfig`
  posture knobs.
- **Perception**: `DefendRung` threat memory broadened from "bandits only" to
  "bandit **or** a faction I'm at war with," with enemy combat power estimated
  from unit *role* via the combat catalog (enemy `Power` is hidden in the fair
  view — a human estimates the same way).
- **`RivalRung`**: the offensive brain — casus-belli evaluation, war
  declaration, campaign bookkeeping, army formation, march, siege, and (for
  limited postures) suing for peace.
- **Offensive muster**: `MusterRung` raises to a *campaign* quota while a
  campaign is active, then the standing army funds the field army.

## What's reused (don't reinvent)

- `Diplomacy.AreHostile` / `DeclareWarIntent` (telegraphed, delayed, rejects
  duplicates) / `ProposeRelationshipIntent` + `RespondToProposalIntent` (peace).
- The M7 combat engine and M24 siege (`CombatRoundEvent` folds siege damage;
  razing a Castle schedules `PlayerDefeatedEvent` → `GameOverEvent`). The AI
  emits **movement**; combat and siege are automatic on hostile contact.
- The M5 group primitive (`FormGroupIntent` / `MoveGroupIntent`) for field
  armies.
- The Homesteader's whole machinery: the strict ladder, `ThinkContext`
  reservations/designations, `AiMemory` ephemeral hints, the `DecisionTrace`
  ring buffer.

## Fairness contract (unchanged, must hold)

The brain sees **only** the projected `ViewDto` — pinned by
`AiPlayerTests.Brain_TouchesOnlyTheView`, which sweeps the whole `Sim.Server.Ai`
namespace for any `GameWorld`/`Simulation` parameter. New diplomacy view data is
*public knowledge* (per `docs/diplomacy-model.md`), so exposing it to the brain
exposes nothing a human client doesn't already render. Enemy positions, holdings,
activity, and exact power stay fogged/private exactly as today.

## Headline test (the contract)

A multi-faction match with at least one warring personality runs to a
**deterministic hash**, and:

1. **Twin-run** — same seed, same scenario, `Snapshot.Hash` equal across two
   runs that include a declared war, a marched army, and a siege.
2. **Replay-from-intent-log** — driverless replay of the durable intent log
   reproduces the live hash (the M16/M17 proof, re-earned for a war).
3. **Mid-siege recovery** — snapshot mid-siege, restore, finish; the hash equals
   the uninterrupted run (M24's siege-recovery property, now driven by the AI).

Plus a behavioral gate: an encroachment/opportunity scenario in which a
non-Homesteader AI **declares war, mobilizes, marches, and damages an enemy
structure** — and a regression gate that **Homesteaders never declare war** and
the M17 balance-lab survival tests still pass.

## Out of scope (deferred)

- Equipment-aware army composition (archers, shield/sword loadouts for offense)
  beyond what Muster already trains — the campaign army is bare soldiers first,
  same staging as M17's standing army; richer composition waits for the lab to
  show bare squads losing sieges.
- Multi-front coordination (one AI prosecuting two campaigns at once) — one
  active campaign per AI in M25.
- Alliances as a war tool (ganging up, coordinated declarations) — `Ally` stays
  inert per `docs/diplomacy-model.md`.
- Naval invasions (boats carrying an army across water) — campaigns are
  overland in M25; the M12 embark seam is left untouched.
- Loot/raid economy as a *goal* (raiding caravans for profit) — razing is
  destruction, not theft, in M25.

## Phased plan

| Phase | Delivers | Done when |
|---|---|---|
| 0 | Diplomacy on the `ViewDto` | human + AI both see relationships/pending wars; pure-read; round-trip green |
| 1 | Personalities (enum + deterministic seeding) | assignment is deterministic; Homesteader == today (M17 tests green) |
| 2 | Hostile-faction perception | an AI defends turf against an invading enemy faction |
| 3 | `RivalRung` — casus belli + declare war | encroachment/opportunity → non-Homesteader declares; Homesteader never does |
| 4 | Offensive war machine | a warring AI masses, marches, and sieges/razes an enemy structure |
| 5 | War termination | Warlord razes a Castle (defeats a player); limited posture sues for peace |
| 6 | Headline determinism + smoke + docs | twin-run + replay + mid-siege recovery green; decision doc + audit + roadmap updated |
