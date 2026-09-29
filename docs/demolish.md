# Demolish: pulling your own structure down

## The decision

`DemolishStructureIntent(tile)` removes one of the issuing player's own
standing structures **instantly, with no unit involved and no refund**. The
tile is left **empty** (no rubble), buildable again in the same tick, and
whatever the structure held lands on the tile as a ground pile. It reuses
the raze pipeline through a shared `SiegeDamage.Teardown`, minus the wreckage.

Refused for: rubble and caches (nobody's), another player's structure, a
construction site still under way, and the castle.

## Why

**Removal is clean, not razing.** A demolish that left rubble would need a
second clearing step before the ground was usable, which defeats the
purpose (swapping a house for a gate, say). Razing leaves rubble because
conquest is supposed to cost the victor a day of labour to reclaim; tidying
your own yard is not conquest.

**One removal path.** Razing already decides three subtle things: the
fertility catch-up on a producing extractor's claims (anchor discipline,
architecture §2.5), the vault spill (holdings, an extractor's buffer and
input store, a site's delivered materials), and the release of units still
walking toward the tile. Rather than copy those into a second intent, the
body of `RazeStructure` moved into `SiegeDamage.Teardown(sim, structure,
leaveRubble, reason)`. Raze calls it with rubble and then schedules the
castle defeat; demolish calls it without rubble. The two cannot drift.

*Rejected: a separate demolish implementation.* Would have re-derived the
catch-up order and the spill table and got one of them subtly wrong later.

**What demolish adds that razing does not.** Razing never releases the
workers standing on the tile or the residents of a house: the units keep
their activity and fall out later through fencing. That is tolerable for
wreckage nobody asked for, but a deliberate demolish should leave no
dangling state. So, while the structure still stands (both helpers need it
present to fix their counts), demolish releases every worker and builder
posted there via `WorkAssignment.Release` and moves a house's residents back
to the castle via `Population.SetHome`, both in unit-id order. Razing was
left as it was, so existing siege hashes do not move.

**Combat state.** A besieged wall or gate closes its siege the way a breach
does (`CombatStates.Remove`; the pending round event stales on its anchor).
A unit fight on an ordinary structure's tile is left alone: the next round
finds no siege target and, if no hostile pair remains, ends itself.

**No refund.** Keeps the rule simple and closes the build-demolish loop as
an exploit vector (place, demolish, place elsewhere at no cost). A partial
material refund is a one-line addition inside the intent if wanted later.

**Sites are refused, not cancelled.** Cancelling a job in progress is its
own decision: it has to dissolve bound builders and manners, decide what
happens to delivered materials, and unreserve claims and canal paths.
Deferred rather than half-done here.

**The castle is refused.** Losing it is defeat by definition
(`PlayerDefeatedEvent`); a voluntary path to that would be surrender, which
is a diplomacy decision, not a tidy-up verb.

## What ships

- `Sim.Core/Sieges/DemolishStructureIntent.cs` — the intent.
- `SiegeDamage.Teardown` — the shared removal; `RazeStructure` now wraps it.
- `IntentJson` registry row; the host's gate lets it through as an
  ordinary player intent (nothing new goes on the view: the pile shows
  through the existing `Piles`, the structure simply disappears).
- Client (`Art Of War(prod)`): `DemolishPayload`, `IntentFactory.Demolish`,
  `OrderIssuer.CanDemolish/Demolish`, a "Demolish" verb on the HUD card
  and the world-UI wheel for own structures, `Glyphs.Demolish`
  (`demolish.png` is owed by the artist; assets-or-nothing, so the wheel
  shows the label alone until then).

## Acceptance tests

`tests/Sim.Tests/DemolishTests.cs`: own stockpile gone with no rubble,
holdings on the ground, tile buildable the same tick; every refusal leaves
the hash untouched; cache refused; extractor releases its worker, frees its
claim, spills its buffer; a producing extractor's soil damage persists;
house residents go home to the castle; a besieged wall closes its siege;
snapshot round-trip and twin-run hash.

## Future expansion

- **Cancel site** — a sibling intent on `ConstructionSite` only, reusing
  `Teardown(leaveRubble: false)` for the delivered-materials spill.
- **Partial refund** — spill a fraction of the catalog cost instead of
  nothing; one table lookup inside the intent.
- **AI use** — a Rival that wants to re-plan its district can issue this
  like any other intent; no brain does yet.
