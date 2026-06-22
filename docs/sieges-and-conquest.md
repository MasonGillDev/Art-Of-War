# Sieges & Conquest (M24)

## Decision

**Structures take siege damage from hostile units on their tile; razing a
player's Castle defeats them and (if at most one player remains) ends the
game.** HP is a flat field on every `Structure`, sourced from a new
`StructureCatalog.BaseHealth`. The damage step is folded into the existing
`CombatRoundEvent` (after unit damage); defenders shield by *start-of-round*
presence; a destroyed structure becomes `StructureKind.Rubble` on the same
tile (owner sentinel `-3`, indestructible). A razed Castle schedules a
`PlayerDefeatedEvent` at the same tick; that event in turn schedules a
`GameOverEvent` if at most one non-bandit / non-cache player remains
undefeated.

Locked choices:

| Aspect | Decision |
|---|---|
| HP location | `StructureSpec.BaseHealth` (catalog), `Structure.Health` (runtime). `BaseHealth = 0` → indestructible. |
| Damage step | Folded into `CombatRoundEvent.Apply` AFTER unit damage; no new event type. |
| Defender shielding | START-of-round presence — a defender's death this round still costs the attackers a round. |
| Damage value | Sum of attackers' `EffectivePower` (same shape as unit damage; no new balance knob). |
| Destruction | Swap structure → `Rubble` (owner `-3`, indestructible, persists forever, blocks placement). No decay, no rebuild-over. |
| Combat-start broadening | Combat triggers on hostile UNIT pair OR a hostile destructible structure with at least one attacker on the tile. |
| Castle razing → defeat | `Player.Defeated = true`. Existing Units & non-Castle Structures persist as inert. No mass-kill, no faction transfer. |
| Defeated-player intents | Rejected at `IntentEvent.Apply` BEFORE per-intent `Resolve` runs. Single gate. |
| Game over | Fires when ≤1 non-defeated player remains. The event is a pure marker — sim does not halt; host reads `ResolvedLog`. |
| Repair | None. HP is one-way (until a future milestone). |
| Bandits | Exempt from siege as ATTACKERS — M16 explicitly defers structure damage by the bandit faction. Bandits still raid via `LoadCargoIntent`. |
| Snapshot | FormatVersion bump `18 → 19`. Adds `Structure.Health` (read before `AddStructure` runs so auto-init no-ops) and `Player.Defeated`. Zero new anchors. |

## Why

### Why HP on the base `Structure`, not per-class

Every kind eventually wants HP. Subclass-specific HP fields would scatter
the same field across `Castle`, `Stockpile`, `Extractor`, `Barracks`,
`House`, `Tower`, `Dock`, `School`, `Lodge`, and every future structure.
The siege math also needs a uniform read — `if structure.Health <= 0`
should not have to know what kind it's looking at. So `Health` lives on
the base, with `StructureCatalog.BaseHealth` as the per-kind seed. The
`Cache` / `Canal` / `Rubble` "indestructible" cases fall out of
`BaseHealth = 0` without any subclass code: `SiegeableStructureOn` returns
null whenever `Health <= 0`, and the round naturally skips them.

This mirrors the M7 unit-HP shape exactly: `Unit.Health` is on the base,
`UnitCombatCatalog.BaseHealth` seeds it, `AddUnit` auto-fills from the
catalog when the field is the sentinel zero (used by snapshot restore to
skip auto-init for damaged units). The Phase A change is one new field on
the base and an `InitStructureHealthIfFresh` mirror of
`InitCombatStatsIfFresh`.

### Why damage is folded into `CombatRoundEvent`, not a new event type

The user's spec gave two options: extend `GatherForcesOnTile` to include
structures as combatants, or extend `CombatRoundEvent` to deal damage to
structures after unit resolution. We chose the second.

`GatherForcesOnTile` returns `IDictionary<int, List<Unit>>` — units, not
structures. Including structures would change the return type, change
every caller, and entangle the "force size" rollup with the very
different "is the structure alive?" question. The unit-damage math runs
unchanged; siege damage is a separate, gated step that runs after.

Adding a new `SiegeRoundEvent` was the other option considered. We
rejected it because every siege round happens INSIDE a combat tile that
already has a `CombatRoundEvent` scheduled and anchored. Two events on
the same tile means two anchors, two fencing tokens, two recovery
branches. The folded step reuses the existing anchor for free.

### Why defender shielding is start-of-round, not post-damage

The alternative (check defenders AFTER unit damage applies in the same
round) lets the attackers kill the last defender AND chip the wall in
the same blow. That's plausible but defenders' deaths become cheap — a
unit dying in round N has the same effect on the structure as a unit who
was never there.

Start-of-round shielding gives the defender's death meaning: their
presence costs the attackers a round of structure damage. The attacker's
math is "kill the defenders, then start chipping," which matches the
fiction and gives the defender's positioning real value.

### Why Rubble is a new kind, not a `Destroyed` flag

A `Destroyed` flag on the existing structure would leave a Castle still
typed `Castle` with `Health = 0`. Every code path that does `s is
Castle` (food consumption, breeding, owner iteration) would now have to
also check `!s.Destroyed`. The flag would leak into every consumer.

`Rubble` as its own kind makes the consumer code free: the cast simply
fails, the food event fences cleanly, the AI brain doesn't think it
still owns a castle. The cost is one new enum value and one new (very
small) class.

The persists-forever + blocks-placement choice is the simplest possible
behavior. The existing `world.Structures.ContainsKey(tile)` guard in
`PlaceSiteIntent` and `PlaceCanalIntent` rejects placement on rubble for
free — no new code in either intent. Decay over time and rebuild-over
were considered (see "Future expansion" below) and explicitly deferred.

### Why the `-3` owner sentinel for Rubble

Out-of-band BELOW every other reserved owner id (players `0..N`,
bandits `-1`, caches `-2`), mirroring `BanditConstants` and
`CacheConstants`. This is load-bearing in three places:

* Iteration over "player N's structures" naturally skips rubble (no
  player ever has id `-3`). A player whose castle was razed doesn't
  keep accidentally appearing as the rubble's owner.
* Diplomacy treats any non-player owner as inert. A rubble pile draws
  no further attacks.
* Snapshot / view code that already special-cases `-1` and `-2` doesn't
  need a new arm for rubble — it falls through the "not a living player"
  branch like the others.

### Why castle destruction → `PlayerDefeatedEvent`, not inline mutation

`SiegeDamage.RazeStructure` could set `player.Defeated = true` directly
when it sees the razed structure is a Castle. We chose to schedule a
`PlayerDefeatedEvent` at `sim.Now` instead, for one reason: the
transition is **observable** state the host / replay / audit wants to
see in `ResolvedLog`. Without the event, code that needs to know "the
tick player N fell" has to scan `Player.Defeated` every tick. With the
event, the resolved log is the single source of truth.

The event also gives us idempotency: a second `PlayerDefeatedEvent` for
an already-defeated player rejects cleanly. This defends against future
edge cases like "two castles razed at the same tick" (multi-castle
support is deferred, but the gate is free).

Same justification as `FamineCheckEvent` / `BirthEvent` /
`DeathByAgeEvent`: observable state transitions become events.

### Why defeated-player intents reject at `IntentEvent`, not per-intent

There are ~30 intent classes. Adding a `if (player.Defeated)` check at
the top of each `Resolve` would be 30 places to remember, 30 places to
miss, 30 places where future intents could omit the check. The
`IntentEvent.Apply` wrapper is the single gate that every intent
funnels through (Sim.Server bandit driver intents included). One check
there mutes the entire defeated-player intent surface.

The trade-off: the rejection reason becomes generic ("player N is
defeated"). The intent never reaches its own `Resolve`, so its own
specific rejection messages are skipped. We accept this — the gate fires
ONLY for defeated players, and "you are defeated" is the only meaningful
explanation at that point.

### Why bandits are exempt from siege

M16's spec (`docs/m16-bandits-spec.md` / `docs/bandits.md`) explicitly
defers structure damage by the bandit faction. The bandit driver is
designed around the raid-and-flee loop (`LoadCargoIntent`), not
besiegement. Without the exemption a bandit party walking onto an
unguarded outpost would auto-start a siege, get pinned in combat, and
never close their raid loop.

The exemption is localized to two seams:
* `CombatRules.AnyHostileToStructure` skips `BanditConstants.OwnerId`
  when checking whether the unit set can siege.
* `CombatRoundEvent.Apply` skips bandit-owned attackers when summing
  siege damage.

Player-vs-player and player-vs-future-faction sieges are unaffected.
When bandit camps land (deferred per M16), the exemption can be
generalized — or removed, if the design wants raiders to also burn the
posts they steal from.

### Why no repair

The user's spec is one-way: HP only goes down. Repair (slow regen,
worker-driven, or both) opens up a balance surface that's bigger than
the milestone needs: repair speed vs. siege speed vs. resource cost
becomes a tuning loop. We chose to ship the one-way siege first and
re-open repair after the system has been played.

The implementation is repair-ready: `Structure.Health` is a simple
mutable int and `RazeStructure` is the only writer that drives it to
zero. A future `RepairIntent` would just add additive writes from a
single new mutation point. No siege code needs to change.

### Why no decay on rubble, why no rebuild-over

Decay would add a new lazy-catch-up surface (read `LastDecayTick`,
compute "is the rubble still here at now?"). That's a real cost and the
spec is silent. Persists-forever is simpler today and easy to convert
to decay later — replace the rubble lookup with a lazy "exists at now"
check, no caller needs to change.

Rebuild-over would mean placement rules special-case rubble (allow same
kind, cheaper cost, etc.). Again, a balance surface that we don't need
to design now. The persistent rubble pile is **the cost of losing
ground** — you lose the tile until a future milestone lets you reclaim
it.

### Why scheduled events for `PlayerDefeatedEvent` / `GameOverEvent`
### instead of `RegenerateQueue` anchors

Both events are scheduled at `sim.Now` and fire that same tick. The
queue never carries them across a snapshot. So there's no anchor field,
no `RegenerateQueue.From` branch, no recovery rebuild — the M4 anchor
discipline simply doesn't apply.

The flip side: a snapshot taken EXACTLY between the
`PlayerDefeatedEvent` scheduling and its execution would lose the
event. We avoid this because `Player.Defeated` itself is serialized:
the "did the player lose?" state survives, and the IntentEvent gate
keeps doing its job. The event re-firing on restore would be a future
"observable-tick precision" feature; for now, the persisted flag is
the contract.

## Future expansion

Open seams the design leaves room for:

1. **Repair**. The single mutation point in `RazeStructure` is the only
   writer that lowers HP; a `RepairIntent` (worker-driven, resource-
   costing) or a regen-when-no-combat lazy catch-up adds the inverse
   direction without disturbing siege.

2. **Rubble decay / clearable rubble**. Replace the persistent-Rubble
   lookup with a lazy "exists at now" check; add a `ClearRubbleIntent`
   for explicit removal. Placement rules read through the same gate, so
   no consumer changes.

3. **Allied units shield friendly structures**. Today only units owned
   by the structure's owner count as defenders. A future change to
   `hadDefendersAtStart` could treat all non-hostile units as defenders
   — `forces.Keys.Any(o => !diplomacy.AreHostile(o, siegeTarget.OwnerId))`
   — for an alliance fortification rule.

4. **Bandit-camp sieges**. When bandit camps land (the M16-deferred
   feature), bandits will own destructible structures. The
   `AnyHostileToStructure` exemption is one line to flip; player-led
   bandit-camp razing becomes available with no new code.

5. **Multi-castle players**. The current defeat condition fires when
   the player's ONE castle is razed (genesis seeds one per player). The
   `PlayerDefeatedEvent` already idempotency-fences; a future
   multi-castle policy ("defeat when ALL castles razed") only changes
   when the event gets scheduled — the event itself is reusable.

6. **Surrender intent**. A defeated player whose castle still stands
   could opt to surrender voluntarily. Mechanically: a
   `SurrenderIntent` resolves into a `PlayerDefeatedEvent`. The gate
   and the game-over logic are already there.

7. **Captured castles**. The user's M24 decision was that castle
   destruction = defeat. A future "capture" variant (hostile units hold
   the castle for N ticks → ownership transfers) would land as a
   sibling to `RazeStructure` — set `castle.OwnerId = captor` instead
   of swapping to Rubble. The gate logic + game-over rollup work
   unchanged.

## Acceptance tests

The milestone's pinned contracts:

* `StructureHealthTests` — `BaseHealth` flows from catalog to `Health`
  on placement; damaged HP round-trips through snapshot; `FormatVersion`
  is `>= 19`.
* `RubbleTests` — `Rubble` is indestructible, owner sentinel `-3`;
  `PlaceSiteIntent` and `PlaceCanalIntent` reject on rubble tiles;
  rubble round-trips through snapshot.
* `SiegeDamageTests` — combat starts on lone-attacker + hostile
  destructible structure; defenders shield at round start;
  undefended castle takes damage over rounds; razing swaps to Rubble;
  indestructible kinds are not siege targets.
* `PlayerDefeatTests` — razing a castle fires `PlayerDefeatedEvent`;
  defeated players' intents reject at `IntentEvent`; with ≤1 player
  remaining `GameOverEvent` fires with the winner's id;
  `Player.Defeated` round-trips through snapshot.
* `SiegeHeadlineTests` — twin-run hash equality across a full
  siege-to-game-over scenario (M0 contract); mid-siege snapshot →
  restore → finish-the-siege hash equality (M4 contract).
