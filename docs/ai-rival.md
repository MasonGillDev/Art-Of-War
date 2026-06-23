# The Rival — war-capable AI players (M25)

## The decision

The AI opponent gains the capacity to **initiate and prosecute war**. Built on
the M17 Homesteader (not replacing it), each AI faction is born with a seeded
**personality** that decides how warlike it is, and a new **`RivalRung`** gives
the brain a casus belli, a war declaration, and a field army that marches on and
sieges enemy structures — ending, for a Warlord, in razing the enemy Castle.

Concretely:

- **Personalities** (`AiPersonality`, on the ephemeral `AiConfig`): `Homesteader`
  (peaceful — today's brain, the default), `Opportunist` (limited wars of gain),
  `Warlord` (plays the M24 win condition). Assigned deterministically from
  `(worldSeed, ownerId)` by `RivalDoctrine.AssignPersonality`.
- **Casus belli** (gated by personality): **encroachment**, **retaliation**,
  **opportunism**, **land hunger**.
- **Strike doctrine**: military → economy → Castle.
- **War termination**: a Warlord presses to conquest; an Opportunist sues for
  peace when sated; any non-Warlord accepts an olive branch.

The AI remains **out-of-sim**: it reads only the projected `ViewDto` and emits
ordinary intents (`DeclareWarIntent`, `MoveIntent`, `ProposeRelationshipIntent`,
`RespondToProposalIntent`). It is the fourth instance of the M16 driver shape
(bandits → AI economy → automation → **rival war**).

## Why

### Personality as ephemeral config, not sim state

A faction's posture could have been stored on the `Player` (serialized,
append-only enum). We chose to derive it as a **pure function of `(worldSeed,
ownerId)`** held only on the ephemeral `AiConfig`, for three reasons:

1. **Determinism for free.** Twin-runs reproduce the same cast because the same
   seed yields the same assignment; replay-from-intent-log doesn't run the brain
   at all (the intents are durable). No snapshot field, no append-only enum
   obligation, no migration.
2. **Fairness is unaffected.** Personality is a knob the brain reads like build
   costs — it is not world state the brain could "cheat" from. The reflection
   pin (`Brain_TouchesOnlyTheView`) is untouched.
3. **The default is invisible.** `Homesteader = 0` is the default, so every
   existing scenario, test, and the whole M17 balance lab keep today's behavior
   bit-for-bit. The host deals the war-capable postures; tests opt in explicitly.

Rejected: **storing posture on `Player`.** It would buy nothing (the assignment
is reproducible without it) and cost a serialized field + an append-only enum on
the hot snapshot path.

### The war DECISION lives in a perception pass; the army DIRECTION is a low ladder rung

The Homesteader is a strict priority ladder (first rung to emit claims the
think). Two facts pulled the Rival's two halves to opposite ends of it:

- **The declaration must be timely.** If "should I go to war?" were an ordinary
  rung, a busy economy (Eat/Build/Muster all claiming) would starve it for many
  thinks. So casus-belli evaluation + the declaration run in a **perception
  pass** the brain executes every think *before* the ladder — exactly how the
  Defend rung runs its threat memory first. Declaring consumes no units, so its
  intents ship no matter who claims the think.
- **The army must come second to survival.** Marching a field army is the most
  preemptable thing the colony does — you feed and arm before you march, and you
  never march a starving colony. So `RivalRung.TryClaim` (form-up + march +
  siege) sits **below Muster** in the ladder: Eat, Build, Train, and Muster all
  outrank it, and it yields the think whenever there's no order to give.

Rejected: **one war rung in the ladder.** Placed high, it starves the economy;
placed low, the declaration is starved. Splitting decision (perception) from
direction (low rung) gets both right.

### Diplomacy reaches the brain through the fair, public channel

The brain learns who is hostile from new `ViewDto` fields (`Factions`,
`Relationships`, `PendingWars`, `IncomingProposals`) projected from the world's
`Diplomacy` aggregate. This is **not a privilege**: diplomatic posture is
*public knowledge* (`docs/diplomacy-model.md`) — a human client renders the same
data on its diplomacy screen. Fog still hides positions, holdings, and unit
activity. The Rival sees exactly what a human sees, no more.

Rejected: **giving the AI a back channel to `world.Diplomacy`.** It would break
the structural fairness contract (`Brain_TouchesOnlyTheView`) for data that is
already public anyway.

### Enemy strength is estimated from role, not read

A unit's true `EffectivePower` is private (own-only, like `Activity`). For force
parity — the gate on sorties, opportunism, and the march commit — the brain
estimates an enemy unit's power from its **visible role** via the combat catalog
(a *rule*, like build costs). This is exactly how a human reads "that's a
soldier, about 3 power." Reading the true value would be a fog cheat.

### Strike doctrine: military → economy → Castle

When a column has a choice of visible targets it hits **military structures**
first (decapitate force projection), then **economy** (collapse the war's fuel),
then the **Castle** (the decisive, win-condition blow — last, because razing it
defeats the player, M24). The doctrine emerges naturally as the army advances:
it razes whatever frontier it can see, and reaches the keep last. This was the
user's explicit choice (full campaigns, not punitive raids).

### Peace reuses the M6 bilateral handshake

War termination needs no new core mechanism. A limited aggressor proposes
`Neutral` via `ProposeRelationshipIntent` once its grievance is settled (nothing
of the enemy's left in sight, no trespasser in its border) or its army is spent;
the other side ends the war by accepting (`RespondToProposalIntent`). Acceptance
is personality-gated: a Homesteader and an Opportunist take the olive branch; a
Warlord lets every offer lapse and plays for the kill. A campaign stands down
when `Bookkeep` reads the pair back to Neutral (peace) or the target Defeated
(conquest).

### Determinism: the AI stays out-of-sim

Like every driver since M16, the Rival is an ephemeral brain: pure reads in,
ordinary intents out. It adds **zero** sim mutation points and **zero** anchors —
the war it wages is carried entirely by existing, already-recovery-clean systems
(M6 diplomacy anchors, M7 combat, M24 siege). The headline is the M16/M17 proof
re-earned for a war: a full-`AiPlayerDriver` Warlord-vs-Homesteader campaign is
deterministic across **twin-run** and **replay-from-intent-log**
(`RivalHeadlineTests`).

## Future expansion

- **Hunting a distant enemy.** Today a war needs a *visible* enemy structure to
  set an objective, so the Rival fights neighbours it can see (contact via
  scouting/encroachment). A Warlord that knows a faction exists but can't find it
  holds. Directed war-scouting (march toward the last-known bearing) is the
  natural next step. *Deferred, documented limitation.*
- **Group-based field armies.** The army marches as individual `MoveIntent`s
  today; the M5 group primitive (`FormGroupIntent`/`MoveGroupIntent`) would let a
  column move and arrive as one concentrated body. The commit gate and
  designation seams are already in place.
- **Equipment-aware composition.** The campaign army is bare soldiers (M14's
  staging). Archers and shield/sword loadouts for offense layer on once the lab
  shows bare squads losing sieges.
- **Multi-front war.** One campaign per AI today (`AiMemory.CampaignTarget`). A
  list would let a Warlord prosecute several at once.
- **Alliances as a war tool.** `Ally` is still inert (`docs/diplomacy-model.md`);
  coordinated declarations and shared-vision war-bands are a later milestone.
- **Naval invasion.** Campaigns are overland; the M12 embark seam is untouched.
  Carrying an army across water reuses boats wholesale.

## Acceptance tests

- `RivalWireTests` — diplomacy is public on the wire (both viewers identical),
  live (declared → pending → Enemy), and pure-read.
- `RivalPersonalityTests` — seeded assignment is deterministic, varied, and
  Homesteader-by-default.
- `RivalDefenseTests` — the defender repels an enemy *faction*, power estimated
  from role; a neutral faction is no threat.
- `RivalDeclarationTests` — the four casus belli, Homesteader pacifism, the
  no-double-declare guard, famine-blocks-war, defeated-rival-is-no-target.
- `RivalCampaignTests` — strike-doctrine objective, commit gate, reinforcement;
  and the real-sim proofs that a campaign **razes an enemy structure** and a
  Warlord **razes a Castle and defeats the player**.
- `RivalPeaceTests` — accept (Homesteader/Opportunist) vs refuse (Warlord),
  Opportunist sues when sated, campaign ends on peace.
- `RivalHeadlineTests` — **the contract**: an AI-driven war is deterministic
  (twin-run + replay-from-intent-log), asserted non-trivial.

## Reference

Realizes the design doc's "AI opponents" line and the `DefendRung`'s own
foreshadowing ("Rival extends this to declared-war factions via the view's
diplomacy data"). Spec: `docs/m25-rival-spec.md`. Composes M6 diplomacy, M7
combat, M14 military, M17 the Homesteader, and M24 sieges. Host smoke:
`dotnet run --project src/Sim.Host -- --rival`.
