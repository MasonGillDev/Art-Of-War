# King & Dynasty — a mortal protagonist, not a win condition

**Status:** locked design, not scheduled.
**Depends on:** M8 population (aging, breeding, fixed `DeathTick`), combat/death
events, the One Stop Rule, the chronicler/narration layer, companion-app
notifications, and the battle layer (for the radius buff to shine).
**Sim cost:** thin. The dynasty is a crown on machinery that already exists — a
flagged unit, a derived heir, succession events fired from existing death
events.

## The decision

Every realm starts with a **king**: an ordinary unit, flagged royal, who ages
from `BornTick`, rolls a lifespan at birth, and dies of age or violence like
anyone else. He carries a **military, radius-based, positional buff** — troops
within R tiles (world grid) / R sub-tiles (battlefield grid). Exactly one
**heir-apparent** exists at any time — the eldest living child of the current
king, purely derived, never stored — carrying a **small, differently flavoured
buff attached to the position, not the blood**.

The king's death is **not** a loss condition. It removes the buffs until
succession resolves; the realm dims and persists. Killing an enemy *line* is
the real decapitation campaign.

## Why

The game has depth everywhere and a heart nowhere. The king concentrates
identity into one mortal, killable, breedable unit — a protagonist inside the
player's own realm. "My king fell at the ford and his daughter was crowned at
fourteen" is a memory; no aggregate system produces memories like that. The
dynasty gives the chronicler, the battle layer, the breeding system and enemy
war-planning a shared apex object.

Secondary motivations, all confirmed:

- **Day-one stakes.** From tick zero the player owns something precious,
  aging and losable — stakes before the first enemy appears, and a one-unit
  tutorial for the game's deepest loop (protect what is mortal; plan for
  generational turnover).
- **War gets an objective with a shape.** "Kill the line" is a campaign with a
  beginning and an end, legible to both sides.
- **The Alexander fantasy.** Fielding the king is a real, tempting, never
  mandatory gamble.

### Rejected: regicide as a win/loss condition

Recorded so we don't re-litigate it. The original idea — killing the king is a
second win condition — loses on three counts:

- It is a **single point of catastrophic failure** in a game whose entire
  architecture rejects single points of catastrophic failure (persistence,
  telegraphed war, automation, the app).
- In an async persistent world it makes **offline decapitation the dominant
  strategy**, resurrecting the exact offline-vulnerability problem the design
  exists to solve.
- It **murders its own fantasy**: if the king's death is game over, no rational
  player ever fields him, and the buff designed to reward boldness becomes a
  museum piece.

The royal-line version keeps everything cool and converts the fatal flaw into
stakes-with-recovery. Killing an enemy king stays devastating and prestigious;
killing the line is brutal, slow, visible and fair.

### Why the buff is a radius, not a global

A global (or economic) buff makes the king a **lockbox item** — the optimal
play is to never move him and the mechanic disappears into a number. A
positional radius buff makes him **a piece on the board**, and a reason to open
the battle layer. Fielding him is opt-in risk; the default posture — and the
default doctrine/automation setting — is *king stays behind walls*, with
"never field unless I am present" an authorable standing order.

### Why the heir is core, not seasoning

Being royal grants nothing; being **the** heir does. Because the position is
singular by construction, the buff **cannot be farmed** no matter how many
royal children exist. The heir's effect is small and flavoured differently from
the king's (if the king is a combat radius, the heir is e.g. a minor
morale/rally effect) so king + heir in one army reads as *two characters
present*, not one number turned up.

Transfer is instant and automatic: heir dies → next eldest becomes
heir-apparent → buff moves. No player action, no gap, pure function of line
state. Fielding the heir is the design's best dilemma — a small edge today
against the succession tomorrow. King and heir in the same battle is the all-in
posture.

### Why the line is narrow

Royal children beyond the heir-apparent carry **no buffs**; their value is
succession itself. Royal blood is a liability you protect, not a resource you
multiply — precious-and-scarce is a story, numerous-and-productive is a
spreadsheet.

**Narrow line rule (anti-bloat, structural):** only the current king's own
children are royal. On succession only the new king's children join the line;
siblings' branches lapse to commoners. (Acceptable gentler variant: line =
king + his children + the previous king's surviving children, nothing deeper.)
The dynasty is a thread, not a tree — permanently small, precious, huntable.

**Spam-breeding is already structurally impossible**: breeding occupies both
parents for the full gestation (the king is serially bottlenecked — one House
at a time), costs food, ends at fertility 40, and children are fifteen years of
mouths before they can do anything. A maximally dedicated reign yields perhaps
a dozen royal births — and a king who spent his reign in the bedroom instead of
on the battlefield. No balance duct tape required.

### Succession, minority, interregnum

- **Succession event** fires from existing death events (`DeathByAge`, combat
  death via the `OnUnitRemoved` pathway). The crown passes to the
  heir-apparent.
- **Interregnum:** between a king's death and a functioning successor the
  King's Buff is simply **gone** — clean and legible, no regency half-state.
- **Minority:** an heir who inherits under age 15 is a child monarch and
  provides **no King's Buff until majority** — a known, visible window of
  weakness enemies can plan around. Telegraphed stakes, on brand. Note the
  emergent double crisis: a newly crowned child king has no children, so no
  heir-apparent exists, so *both* buffs are absent — this falls out of the
  position-based definition with zero extra rules.
- **Line extinction:** the realm persists but is buffless. Recovery follows the
  canal principle — **a new dynasty can be founded, priced monumentally**.
  Nothing is truly unrecoverable; recovery costs enough that prevention
  matters.

### Death while the player is away

Accepted as the nature of the game, and handled by systems already built:

- **Old-age death is never a surprise to the sim** (`DeathTick` is fixed at
  birth). The exact tick is not exposed, but the game telegraphs ("the king is
  in his final years") and the **companion app warns about unsecured succession
  with years of lead time**. A peaceful transition with a grown heir needs no
  player present. The sting lands only on players who ignored a long, loud
  telegraph — the fairness doctrine working as intended.
- **Violent death in absence** only occurs under an opted-into risk posture
  (king fielded via doctrine) — a legitimate consequence of a legitimate
  gamble.
- **The chronicler makes unwitnessed deaths land emotionally.** Returning to
  "the king fell at the eastern ford; his son is crowned" is the gut-punch
  delivered properly — exactly the job the narration layer was built for.

## Composition

- **Battle layer:** the radius buff is positional — the king is a sub-tile
  piece; protecting or hunting him is battlefield geometry. Doctrine covers his
  posture when the player is absent.
- **Scouting:** *which child is the heir* and *where the king rides* are
  intelligence products. Royal targets give enemy scouts something worth
  hunting and your walls something precious to hold.
- **Chronicler/narration:** coronations, minorities, regicides, extinctions —
  a content generator for the narrative layer at zero additional sim cost.
- **Automation:** royal postures are standing orders; the dynasty obeys the
  core promise — brains beforehand, sleep soundly, attention at crises.
- **Naming/history:** "the Battle of Greywater Ford, where King X fell" —
  dynasties give named history its protagonists.

## Balance targets (balance-lab tests to write)

- **King temptation zone:** same army, king vs. no king — with-king should win
  *often but not always*. Tempting always, mandatory never. Sweep buff values
  to find it.
- **Heir stacking check:** king + heir stacked must not become a must-have
  doomstack — same sweep, one more column.
- **Dynastic bad-luck floor:** short-lifespan rolls and late heirs must produce
  rocky reigns, never unwinnable openings. Twin-run cohorts across lifespan
  seeds.
- **Opening safety:** early-game regicide (bandit raid, rush) must not be
  possible before the war telegraph and starting defences can function. The
  king's day-one position is safe by default.
- **Upkeep weight:** default dynasty management must be light (breed him, keep
  heirs behind walls — automatable), with attention demanded only at crisis
  moments. If dynasty upkeep reads as a chore treadmill, cut scope rather than
  add features.

## Test implications

Succession determinism (twin-run across king deaths of both kinds); heir
derivation is pure and order-independent; One Stop Rule interaction (king dies
mid-gestation — breeding stops, existing rule); interregnum and minority
buff-absence exactly bounded by events; the narrow-line rule holds across three
generations; line extinction and refounding round-trip through
snapshot/restore (`FormatVersion` bump when built).

## Future expansion — open questions to settle before build

1. Exact buff contents and radii (king: combat; heir: morale/rally?) — the
   balance lab decides magnitudes.
2. Minority age: training age (15), or its own threshold?
3. Narrow-line variant: strict (king's children only) vs. gentle (+ the
   previous king's children)?
4. New-dynasty founding: cost, mechanism, and whether the new house starts
   buffless and scales up over time.
5. Queens/consorts: does the king's breeding partner have any mechanical
   identity, or remain a normal unit? (Lean: normal unit — scope discipline.)
6. Does the heir-apparent marker leak through fog (enemies always know who the
   heir is), or is it scout-discoverable intel? (Lean: discoverable — it feeds
   the intelligence game.)
7. Visual/UI treatment: how the king and heir read on the world map, the
   battlefield, and in the city-builder ambient layer (the palace as a
   district?).

---

*One line: every realm starts with a mortal king whose radius buff makes
fielding him a real gamble; one position-based heir-apparent carries a small
distinct buff that can never be farmed; succession, minority windows and a
narrow royal line turn breeding into dynasty and war into campaigns against a
bloodline — with death softened from game-over into stakes-with-recovery,
telegraphed by the app and narrated by the chronicler.*

## Update 2026-08-20 — build spec, and two corrections

Planning for the first build milestone lives in `docs/m30-king-dynasty-spec.md`
(scope: line + succession + the king's aura). Reading the code corrected two
assumptions above:

- **Parentage must be stored.** `BirthEvent` discards the parent ids today, so
  "eldest living child of the current king" is not derivable from current
  state. The heir stays derived (as designed); the *inputs* to that derivation
  did not exist and must be added (`Unit.ParentAId` / `ParentBId`).
- **Royalty needs no royal flag.** With stored parentage plus one stored
  `Player.KingUnitId`, "is royal" is a pure predicate — and the narrow-line
  rule of §5 then costs nothing at succession. This settles open question #3
  in favour of the **strict** variant; the gentle variant remains a
  one-field addendum.

The King's Buff is also pinned as a **pure-read aura** rather than an instance
of the existing stored `Buff` (which is a 2-slot equipment loadout) — see the
spec for why, and for the `CombatRules.EffectivePower` signature change that
pays for it.
