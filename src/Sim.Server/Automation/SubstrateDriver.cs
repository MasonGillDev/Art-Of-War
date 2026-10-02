using Sim.Core.Automation;
using Sim.Core.Engine;
using Sim.Core.Logistics;
using Sim.Core.Vision;
using Sim.Core.World;

namespace Sim.Server.Automation;

// THE SUBSTRATE DRIVER (docs/automation-substrate.md, Layer 3) — the brain
// that runs player automation on the universal Order record. Fourth
// instance of the M16 driver shape (BanditDriver → AiPlayerDriver →
// AutomationDriver → this): pure fog-filtered reads in, ordinary durable
// intents out, EPHEMERAL brain. Order definitions, status, and claims are
// all sim state, so a restarted server resumes cold with nothing to
// recover, and replaying the intent log without this driver reproduces the
// same world byte-for-byte.
//
// THREADING: Think() runs on the sim-owning thread inside the GameHost
// clock-loop lock, right after Run. Never blocks, never sleeps, self-gates
// to one pass per ThinkPeriodTicks.
//
// SEQUENTIAL EVALUATION WITH VISIBLE PENDING CLAIMS — the decision that
// makes the whole substrate work (docs/automation-substrate.md §4):
// orders are evaluated in (Priority, OrderId) order, and a claim made
// earlier in the pass is visible to every order later in the pass. Intents
// resolve AFTER the think, so the driver tracks its own pending claims in
// `_pendingClaims` for the remainder of the pass. That is what makes
//   * pool contention resolve at claim time (first in canonical order wins,
//     the rest see a smaller pool — no collisions, no races), and
//   * in-flight-aware quotas honest (a second producer reads the target as
//     already covered and stands down instead of duplicating work).
//
// FOG: each owner's visibility is computed ONCE per think (View.VisibleTiles,
// a pure read) and shared by all their orders — automation reads only what
// its owner can see.
public sealed class SubstrateDriver
{
    private readonly AutomationConfig _cfg;
    private readonly OrderJournal _journal;
    private long _lastThink = long.MinValue;

    // Claims submitted during THIS pass, not yet resolved into the ledger.
    // Cleared at the start of every think — intents from the previous pass
    // have resolved by then, so the ledger is the truth again.
    //
    // Kept as unit → ORDER, not just a set of units, because pending claims
    // have to be visible to two different readers: the SELECTOR (so a later
    // order does not pick a body an earlier one just took) and the QUOTA
    // PREDICATE (so a later order counts the trainee an earlier one just
    // committed and reads its target as already covered). Tracking only the
    // unit ids fixed the first and left the second racing — two schools
    // both training for the same missing slot.
    private readonly Dictionary<int, int> _pendingClaims = new();
    private readonly HashSet<int> _pendingUnits = new();

    public SubstrateDriver(AutomationConfig cfg, OrderJournal? journal = null)
    {
        _cfg = cfg;
        _journal = journal ?? new OrderJournal();
    }

    public OrderJournal Journal => _journal;

    public void Think(Simulation sim, long now)
    {
        if (!_cfg.Enabled) return;
        if (_lastThink != long.MinValue && now - _lastThink < _cfg.ThinkPeriodTicks) return;
        _lastThink = now;

        var world = sim.World;
        // Housekeeping before the early return, so clearing the LAST order
        // still retires its journal row.
        _journal.PruneTo(world.Orders.Keys);
        if (world.Orders.Count == 0) return;

        _pendingClaims.Clear();
        _pendingUnits.Clear();

        // CANONICAL EVALUATION ORDER: priority first (the player's own
        // triage — a lower number wins a contested unit and is the last to
        // be suspended), then order id. Two runs over the same world
        // evaluate in the same sequence and submit the same intents.
        var ordered = new List<Order>(world.Orders.Values);
        ordered.Sort((a, b) =>
        {
            var p = a.Priority.CompareTo(b.Priority);
            return p != 0 ? p : a.OrderId.CompareTo(b.OrderId);
        });

        var visibility = new Dictionary<int, HashSet<TileCoord>>();
        foreach (var order in ordered)
        {
            if (!order.Enabled) continue;
            // A DEFEATED kingdom's machine is switched off. Every intent from
            // a defeated player is rejected at resolution, so a driver that
            // kept thinking for one would submit rejected work forever —
            // including its own housekeeping, which is why a dead player's
            // claims could never be reaped. Rejected intents still consume
            // Seq (and Seq is hashed), so this is not merely wasted effort:
            // it is unbounded noise in the durable log of a finished player.
            if (world.Players.TryGetValue(order.OwnerId, out var owner) && owner.Defeated)
                continue;
            if (!visibility.TryGetValue(order.OwnerId, out var visible))
            {
                visible = View.VisibleTiles(world, order.OwnerId);
                visibility[order.OwnerId] = visible;
            }
            RunOrder(sim, order, visible, now);
        }
    }

    // THE TRIGGER GATES NEW COMMITMENTS, NOT THE WHOLE RECIPE.
    //
    // Two reasons, both learned from the staged Breed recipe:
    //   1. REAPING must happen regardless. An in-flight claim whose errand
    //      has landed has to be released even while the order is otherwise
    //      idle, or pulled units leak out of the pool forever.
    //   2. COMMITTED WORK FOLLOWS THROUGH (user, 2026-08-06). If the
    //      population target is reached while a breeding pair is still
    //      walking, they finish and breed — a tiny overshoot instead of an
    //      about-face. Gating the whole recipe would make pairs oscillate:
    //      walk out, turn around, walk back, forever.
    private void RunOrder(Simulation sim, Order order, IReadOnlySet<TileCoord> visible, long now)
    {
        var triggerMet = PredicateEvaluator.IsMet(sim.World, order, visible, now, _pendingClaims);

        if (order.Program == ProgramKind.Routine)
        {
            RunRoutine(sim, order, visible, now);
            return;
        }

        switch (order.Recipe)
        {
            case RecipeKind.Haul:
                RunHaul(sim, order, triggerMet, now);
                return;
            case RecipeKind.Breed:
                RunBreed(sim, order, triggerMet, now);
                return;
            case RecipeKind.Train:
                RunErrand(sim, order, triggerMet, now, order.SourceTile, Errand.Train);
                return;
            case RecipeKind.Staff:
                RunErrand(sim, order, triggerMet, now, order.SubjectTile, Errand.Staff);
                return;
            default:
                // Set-time validation rejects unknown recipes.
                throw new InvalidOperationException(
                    $"SubstrateDriver has no case for RecipeKind {(byte)order.Recipe} — " +
                    "was a recipe row added without a driver case?");
        }
    }

    // BORROW-AND-RETURN — the in-flight claim lifecycle.
    //
    // A Pull order BORROWS a hand for as long as it has work and RETURNS it
    // when satisfied. It does NOT release after every errand: releasing on
    // completion and re-pulling next think would rotate through the whole
    // labour pool, borrowing a different body for every trip — churn with
    // no benefit and a constantly shifting picture on the map.
    //
    // So a claim is released only when the unit is genuinely no longer
    // wanted: the order is satisfied (its trigger went false), or the unit
    // is gone. Everything else keeps its hands.
    //
    // `releaseIdle` is the "satisfied" signal. It never releases a unit
    // that is still mid-errand — walking home laden is not being finished
    // with.
    private static void ReleasePulledHands(Simulation sim, Order order, long now, bool releaseIdle)
    {
        var world = sim.World;
        foreach (var unitId in ClaimLedger.UnitsOf(world, order.OrderId))
        {
            if (ClaimLedger.ClaimOf(world, unitId) is not { Purpose: ClaimPurpose.InFlight })
                continue;

            // Gone (died / captured): the claim is dead weight either way.
            if (!world.Units.TryGetValue(unitId, out var u) || u.OwnerId != order.OwnerId)
            {
                sim.SubmitIntent(now, ClaimUnitIntent.Release(unitId, order.OrderId, order.OwnerId));
                continue;
            }
            // A stalled hand is released whatever the trigger says: it keeps
            // its cargo (a laden unit can be retasked) and the order is free
            // to pull another.
            if ((releaseIdle && IsFreeForWork(u) && u.CargoAmount == 0) || IsStalled(u))
                sim.SubmitIntent(now, ClaimUnitIntent.Release(unitId, order.OrderId, order.OwnerId));
        }
    }

    // Return every held hand that is idle and empty EXCEPT `keep` (the one
    // being dispatched this think). A pull line never benches bodies: what
    // it holds is what is walking for it.
    private static void ReleaseIdleSurplus(Simulation sim, Order order, long now, Unit? keep)
    {
        var world = sim.World;
        foreach (var unitId in ClaimLedger.UnitsOf(world, order.OrderId))
        {
            if (keep is not null && unitId == keep.Id) continue;
            if (ClaimLedger.ClaimOf(world, unitId) is not { Purpose: ClaimPurpose.InFlight }) continue;
            if (!world.Units.TryGetValue(unitId, out var u) || u.OwnerId != order.OwnerId) continue;
            if ((IsFreeForWork(u) && u.CargoAmount == 0) || IsStalled(u))
                sim.SubmitIntent(now, ClaimUnitIntent.Release(unitId, order.OrderId, order.OwnerId));
        }
    }

    // A hand this order already holds and that is ready for another errand.
    // Checked BEFORE pulling from the pool so a working line reuses its own
    // borrowed hauler trip after trip.
    private static Unit? HeldAndFree(GameWorld world, Order order)
    {
        foreach (var unitId in ClaimLedger.UnitsOf(world, order.OrderId))
        {
            if (!world.Units.TryGetValue(unitId, out var u)) continue;
            if (u.OwnerId != order.OwnerId) continue;
            if (IsFreeForWork(u) && u.CargoAmount == 0) return u;
        }
        return null;
    }

    // THE HAUL RECIPE — "keep the subject stocked from the source."
    //
    // Feature parity with the M18 supply line, minus the step program: no
    // cursor, no dispatch fence, no per-step retry. The order is
    // LEVEL-TRIGGERED — it looks at the world, and if the level is low and
    // a hand is free, it sends a trip. A trip in progress simply means the
    // hand isn't free next think, so there is nothing to fence.
    private void RunHaul(Simulation sim, Order order, bool triggerMet, long now)
    {
        var world = sim.World;

        // Give back borrowed hands the moment the line is satisfied (and
        // always give back dead ones) — the claim lifecycle that stops a
        // Pull line from draining the labour pool one body per firing.
        if (order.CrewMode == CrewMode.Pull)
            ReleasePulledHands(sim, order, now, releaseIdle: !triggerMet);

        if (!triggerMet)
        {
            // Waiting is the NORMAL resting state of a healthy order, not a
            // failure — it never burns retry budget. (Only fruitless
            // FIRINGS count toward auto-disable, or every idle supply line
            // would eventually disable itself.)
            _journal.Add(now, order, JournalOutcome.Waiting);
            return;
        }

        // OBSERVE, DON'T ASSUME — the same pre-check RunRoutine's Load stop
        // makes. Sending a hauler to a source that holds nothing walks them
        // all the way there for a guaranteed-rejected pickup, and the player
        // watches their caravan pace back and forth doing nothing. The
        // destination being hungry is only half the question; the source
        // having something to give is the other half.
        if (PredicateEvaluator.StoredAmount(world, order.SourceTile, order.Resource, now) <= 0)
        {
            // BLOCKED, not Waiting: the trigger is met — the destination is
            // hungry — and the machine cannot serve it because the shelf it
            // draws from is bare. That is news (your chain is broken one
            // link upstream), where Waiting means "all is well, nothing to
            // do". It still never burns retry budget: a farm buffer cycling
            // empty is ordinary and recovers on its own.
            // A line that cannot send anyone must not keep anyone: hands
            // standing idle at a dry source are hoarded from every other
            // order. Those still walking home laden stay on the books.
            if (order.CrewMode == CrewMode.Pull)
                ReleasePulledHands(sim, order, now, releaseIdle: true);
            _journal.Add(now, order, JournalOutcome.Blocked,
                $"source {order.SourceTile.X},{order.SourceTile.Y} has no {order.Resource}");
            return;
        }

        // Find a free hand. Named crews are the caravan model (these units,
        // this line); Pull reuses the hand it already borrowed, and only
        // reaches into the pool when it hasn't got one.
        Unit? hand = null;
        if (order.CrewMode == CrewMode.Named)
        {
            foreach (var unitId in order.NamedCrew)   // ascending — canonical
            {
                // Presence in world.Units IS liveness (death removes the row);
                // DeathTick is the pre-rolled old-age date every genesis unit
                // carries, and reading it as "dead" skipped entire named crews
                // on the real server while passing every hand-built test.
                if (!world.Units.TryGetValue(unitId, out var u)) continue;
                if (u.OwnerId != order.OwnerId) continue;
                // ANCHORS ARE THE TRUTH, NEVER Activity (the M16 pitfall: a
                // marching unit reads Idle). A crew member mid-trip is busy.
                if (!IsFreeForWork(u)) continue;
                hand = u;
                break;
            }
        }
        else
        {
            // HOLD ONLY WHAT IS WORKING. A pull line's held set is the bodies
            // in flight plus the one it dispatches this think — never a bench.
            // The first cut fell through to the pool whenever its hauler was
            // mid-trip and released nothing while its trigger stayed met, so a
            // single line borrowed a fresh body every think and hoarded every
            // hauler in reach: a castle line held four while the house line
            // beside it read "no free hauler in reach" with two of them
            // standing idle, claimed, invisible to every other selector.
            //
            // So: reuse a held free hand; any OTHER held hand that is idle and
            // empty goes back to the pool this think (it is re-borrowable
            // next think, by this order or a hungrier one). Reach into the
            // pool only when every held hand is mid-trip AND the line is
            // under its in-flight cap — throughput still scales with trip
            // length, which the keystone lab depends on, but bounded.
            hand = HeldAndFree(world, order);
            ReleaseIdleSurplus(sim, order, now, keep: hand);
            if (hand is null
                && ClaimLedger.HeldBy(world, order.OrderId, ClaimPurpose.InFlight) < _cfg.MaxPulledHands)
                hand = SelectorResolver.First(world, order, order.Selector, now, _pendingUnits);
        }

        if (hand is null)
        {
            // A dead or permanently-busy crew is how automation ERODES
            // under attrition (docs/automation-as-core-game.md law 4): the
            // order keeps trying, burns its budget, and goes quiet — which
            // is the news the morning report must carry.
            // CONTENTION IS NOT BREAKAGE — but DEATH IS.
            //
            // "No free hands right now" must never walk an order toward
            // auto-disable: labour shortages are transient and recover on
            // their own, and an order that killed itself over one busy
            // afternoon would be permanently dead by the time the player
            // woke up (found by the 160-day lab: three of five orders had
            // disabled themselves while the colony slept). That is news for
            // the morning report, not a fault.
            //
            // A NAMED crew with nobody left alive is the opposite case: the
            // order can never work again, because a named crew is standing
            // membership that no pull will refill. That is law 4 of
            // docs/automation-as-core-game.md — kill the crew and the order
            // goes dark — and it must still report itself.
            if (order.CrewMode == CrewMode.Named && !AnyCrewAlive(world, order))
            {
                _journal.Add(now, order, JournalOutcome.NoCrew, "crew is dead");
                BumpRetryOrDisable(sim, order, now);
                return;
            }

            // BUSY ON THIS ORDER'S OWN WORK IS NOT A SHORTAGE.
            //
            // A hand that isn't free because it is mid-trip FOR THIS LINE is
            // progress, and a one-hauler line spends most of its life that
            // way. Reporting NoCrew there made a perfectly healthy supply
            // line scream "short of hands" on every think between trips —
            // and NoCrew is the dashboard's alarm state, the one that means
            // "make more people or raise my priority". It has to mean only
            // that, or the player learns to ignore it.
            //
            // So: alive-but-busy is WAITING (work in flight, like the
            // errand recipe's "hand travelling"), and NoCrew is reserved
            // for a line that wants to work and has genuinely nobody.
            // A STATUE IS NOT A TRIP. A held hand with an obligation and no
            // leg (the combat-pin strand) reads "busy" to every anchor test
            // above; naming it here is the sentence the player needed, and
            // the release sweep has already let it go for next think.
            if (StalledHand(world, order) is { } stalled)
            {
                _journal.Add(now, order, JournalOutcome.Blocked,
                    $"unit {stalled.Id} stalled carrying {stalled.CargoAmount} {stalled.CargoResource} " +
                    $"at ({stalled.Position.X},{stalled.Position.Y})");
                return;
            }

            if (HoldsLiveHands(world, order))
            {
                _journal.Add(now, order, JournalOutcome.InFlight, "crew is on the trip");
                return;
            }

            // Contention, honestly reported: a Pull line whose pool is dry.
            // Still never auto-disables — labour shortages are transient and
            // an order that killed itself over one busy afternoon would be
            // dead by the time the player woke up (the 160-day lab lesson).
            _journal.Add(now, order, JournalOutcome.NoCrew, "no free hauler in reach");
            return;
        }

        // A hauler must start empty (HaulIntent's own rule) — a laden hand
        // is not a failure of this order, just not usable this think.
        if (hand.CargoAmount > 0)
        {
            _journal.Add(now, order, JournalOutcome.Blocked,
                $"unit {hand.Id} is carrying {hand.CargoAmount} {hand.CargoResource}");
            BumpRetryOrDisable(sim, order, now);
            return;
        }

        // CLAIM-ON-COMMIT: for a newly pulled hand, claim BEFORE dispatching
        // so every order later in this pass sees a smaller pool. A hand we
        // already hold is skipped — it is claimed already.
        if (order.CrewMode == CrewMode.Pull && !ClaimLedger.IsClaimed(world, hand.Id))
        {
            sim.SubmitIntent(now, new ClaimUnitIntent(
                hand.Id, order.OrderId, ClaimOp.Claim, ClaimPurpose.InFlight)
                { PlayerId = order.OwnerId });
            _pendingClaims[hand.Id] = order.OrderId;
            _pendingUnits.Add(hand.Id);
        }

        sim.SubmitIntent(now, new HaulIntent(
            hand.Id, order.SourceTile, order.SubjectTile, order.Resource)
            { PlayerId = order.OwnerId });
        sim.SubmitIntent(now, new OrderStatusIntent(order.OrderId, OrderStatusOp.MarkFired)
            { PlayerId = order.OwnerId });

        _journal.Add(now, order, JournalOutcome.Fired,
            $"unit {hand.Id} hauling {order.Resource} " +
            $"{order.SourceTile.X},{order.SourceTile.Y} → {order.SubjectTile.X},{order.SubjectTile.Y}");
    }

    // THE BREED RECIPE — "keep this house making children."
    //
    // The first STAGED recipe: claim two fertile adults, walk both to the
    // house, and begin only once both are standing there. It holds NO
    // driver state to do it — every think it re-derives its stage by
    // reading its own claims and the units' anchors. The world is the
    // state; that is why the brain stays ephemeral and replay stays free.
    //
    // What it deliberately does NOT do is ask whether the kingdom can
    // afford another mouth. That would be goal-AI (a search over the whole
    // food economy). Instead the player's trigger reads THIS HOUSE's
    // larder, which a supply line keeps stocked — so breeding self-limits
    // to exactly what logistics physically delivers, and the composition
    // does the thinking. docs/automation-as-core-game.md law 3.
    private void RunBreed(Simulation sim, Order order, bool triggerMet, long now)
    {
        var world = sim.World;
        var houseTile = order.SubjectTile;

        // ---- stage 0: reap ----
        // Release anyone who can no longer serve: dead, no longer ours,
        // aged out of the fertility window between claim and arrival (a
        // real case — the walk takes days and the window has an edge), or
        // ALREADY BREEDING.
        //
        // That last case is why release happens HERE and not at the moment
        // we submit BeginBreeding: an intent's outcome isn't known when the
        // driver submits it, so releasing on the ASSUMPTION that breeding
        // started would hand the pair back even when the intent was
        // rejected (an empty larder, a house occupied in the same tick).
        // Observing `GetActiveBreedingFor` next think is the level-triggered
        // truth — if breeding really began, let them go; if it didn't, we
        // still hold them and simply try again.
        var cfg = world.PopulationConfig;
        var held = new List<Unit>();
        foreach (var unitId in ClaimLedger.UnitsOf(world, order.OrderId))
        {
            if (ClaimLedger.ClaimOf(world, unitId) is not { Purpose: ClaimPurpose.InFlight })
                continue;
            if (!world.Units.TryGetValue(unitId, out var u)
                || u.OwnerId != order.OwnerId
                || Sim.Core.Population.Population.GetActiveBreedingFor(world, unitId) is not null
                || !Sim.Core.Population.Population.CanBreed(u, now, cfg))
            {
                sim.SubmitIntent(now, ClaimUnitIntent.Release(unitId, order.OrderId, order.OwnerId));
                continue;
            }
            held.Add(u);
        }

        // ---- stage 1: advance — both home and idle? begin. ----
        // COMMITTED WORK FOLLOWS THROUGH: this runs whether or not the
        // trigger still holds. A pair that is already walking finishes the
        // job (user, 2026-08-06) rather than turning around at the door.
        if (held.Count >= 2)
        {
            var a = held[0];
            var b = held[1];
            var bothHome =
                a.Position == houseTile && b.Position == houseTile
                && IsFreeForWork(a) && IsFreeForWork(b);

            if (bothHome)
            {
                sim.SubmitIntent(now, new Sim.Core.Population.BeginBreedingIntent(houseTile, a.Id, b.Id)
                    { PlayerId = order.OwnerId });
                // No release here — see stage 0. The pair is handed back
                // next think, once breeding is OBSERVED to have started.
                sim.SubmitIntent(now, new OrderStatusIntent(order.OrderId, OrderStatusOp.MarkFired)
                    { PlayerId = order.OwnerId });
                _journal.Add(now, order, JournalOutcome.Fired,
                    $"breeding units {a.Id} + {b.Id} at {houseTile.X},{houseTile.Y}");
                return;
            }

            // Walking. Nudge anyone who has stopped short (a rejected move,
            // a shove off the tile) and otherwise let them travel.
            foreach (var u in held)
                if (IsFreeForWork(u) && u.Position != houseTile)
                    sim.SubmitIntent(now, new Sim.Core.Movement.MoveIntent(u.Id, houseTile)
                        { PlayerId = order.OwnerId });
            _journal.Add(now, order, JournalOutcome.InFlight, "pair travelling");
            return;
        }

        // ---- stage 2: commit — recruit up to a pair ----
        // NEW commitments are gated on the trigger (population target, the
        // house's own larder, whatever the player wired).
        if (!triggerMet)
        {
            // Not breeding right now: hand back a lone candidate rather
            // than parking them at a house indefinitely.
            foreach (var u in held)
                if (IsFreeForWork(u))
                    sim.SubmitIntent(now, ClaimUnitIntent.Release(u.Id, order.OrderId, order.OwnerId));
            _journal.Add(now, order, JournalOutcome.Waiting);
            return;
        }

        // An occupied house cannot take another pair — wait for the birth.
        if (world.Structures.TryGetValue(houseTile, out var s)
            && s is Sim.Core.World.House house && house.Occupation is not null)
        {
            _journal.Add(now, order, JournalOutcome.Waiting, "house occupied");
            return;
        }

        var exclude = new HashSet<int>(_pendingUnits);
        foreach (var u in held) exclude.Add(u.Id);
        var candidates = SelectorResolver.Resolve(world, order, order.Selector, now, exclude);

        var wanted = 2 - held.Count;
        if (candidates.Count < wanted)
        {
            // Not enough fertile adults free. THE SELF-THROTTLE: breeding
            // runs at the speed of spare people, never faster. Contention,
            // not breakage — never burns the retry budget (see RunHaul).
            _journal.Add(now, order, JournalOutcome.NoCrew,
                $"need {wanted} more fertile adult(s), {candidates.Count} available");
            return;
        }

        for (var i = 0; i < wanted; i++)
        {
            var pick = candidates[i];
            sim.SubmitIntent(now, ClaimUnitIntent.Claim(pick.Id, order.OrderId, ClaimPurpose.InFlight, order.OwnerId));
            _pendingClaims[pick.Id] = order.OrderId;
            _pendingUnits.Add(pick.Id);
            if (pick.Position != houseTile)
                sim.SubmitIntent(now, new Sim.Core.Movement.MoveIntent(pick.Id, houseTile)
                    { PlayerId = order.OwnerId });
        }
        _journal.Add(now, order, JournalOutcome.Fired,
            $"recruited {wanted} to {houseTile.X},{houseTile.Y}");
    }

    // THE ROUTINE PROGRAM — a crew walking a circuit.
    //
    // Go to the current stop; do the stop's action; when its departure
    // conditions hold, advance the cursor and head for the next one,
    // wrapping at the end. This is the trade-route / patrol primitive, and
    // the only program that reads a stored cursor — see RecipeKind.Routine
    // for why the world alone cannot say which stop a caravan is on.
    //
    // The crew moves as a body: every member walks to the same stop, and
    // the circuit only advances once the whole crew has arrived and acted.
    // That is what makes a caravan-with-escort a single thing rather than
    // several units that happen to share a route.
    private void RunRoutine(Simulation sim, Order order, IReadOnlySet<TileCoord> visible, long now)
    {
        var world = sim.World;
        if (order.Steps.Count == 0) return;

        var cursor = Math.Clamp(order.CurrentStep, 0, order.Steps.Count - 1);
        var step = order.Steps[cursor];

        // Living crew. A Routine's crew is Named, so a dead member is gone
        // for good — the circuit runs on whoever is left, and stops when
        // nobody is (law 4).
        var crew = new List<Unit>();
        foreach (var unitId in order.NamedCrew)
            if (world.Units.TryGetValue(unitId, out var u) && u.OwnerId == order.OwnerId)
                crew.Add(u);

        if (crew.Count == 0)
        {
            _journal.Add(now, order, JournalOutcome.NoCrew, "crew is dead");
            BumpRetryOrDisable(sim, order, now);
            return;
        }

        // ---- M29: PATROL POSTURE (docs/patrols.md) ----
        //
        // Runs BEFORE the walk, so a patrol engages anywhere along its route
        // rather than only when parked at a stop.
        if (order.EngageRadius > 0)
        {
            // A chase already running owns the crew. Report and stand back:
            // the sim re-paths it every tile, and the anchor releases itself
            // on catch / leash / lost sight, after which the walk phase below
            // simply marches the survivors back to the current stop. The
            // cursor never moved, so the circuit resumes where it left off.
            foreach (var u in crew)
            {
                if (u.Pursuit is not { } chase) continue;
                _journal.Add(now, order, JournalOutcome.InFlight,
                    $"in pursuit of unit {chase.TargetUnitId}");
                return;
            }

            if (FindQuarry(world, order, crew, visible) is { } quarry)
            {
                // THE PARTY BREAKS AS A BODY — no splitting, no "soldiers
                // engage while haulers flee". That would be tactical AI; put
                // a farmer on a patrol and the farmer dies.
                foreach (var u in crew)
                    sim.SubmitIntent(now, new Sim.Core.Combat.EngageUnitIntent(
                        u.Id, quarry.Target.Id, quarry.LeashTile, order.LeashRadius)
                        { PlayerId = order.OwnerId });
                sim.SubmitIntent(now, new OrderStatusIntent(order.OrderId, OrderStatusOp.MarkFired)
                    { PlayerId = order.OwnerId });
                _journal.Add(now, order, JournalOutcome.Fired,
                    $"engaging unit {quarry.Target.Id} at {quarry.Target.Position.X},{quarry.Target.Position.Y}");
                return;
            }
        }

        // ---- walk ----
        var allPresent = true;
        foreach (var u in crew)
        {
            if (u.Position == step.Tile) continue;
            allPresent = false;
            if (IsFreeForWork(u))
                sim.SubmitIntent(now, new Sim.Core.Movement.MoveIntent(u.Id, step.Tile)
                    { PlayerId = order.OwnerId });
        }
        if (!allPresent)
        {
            _journal.Add(now, order, JournalOutcome.InFlight,
                $"bound for stop {cursor} ({step.Tile.X},{step.Tile.Y})");
            return;
        }

        // ---- act ----
        var acting = false;
        foreach (var u in crew)
        {
            if (!IsFreeForWork(u)) { acting = true; continue; }   // mid-transfer
            switch (step.Action)
            {
                // Only reach for a load that is actually THERE. Submitting a
                // doomed LoadCargoIntent every think would spam the log with
                // rejections and report the stop as working when it is in
                // fact waiting for stock — observe first, act second.
                case RoutineAction.Load when u.CargoAmount == 0
                        && PredicateEvaluator.StoredAmount(world, step.Tile, step.Resource, now) > 0:
                    sim.SubmitIntent(now, new Sim.Core.Logistics.LoadCargoIntent(u.Id, step.Resource)
                        { PlayerId = order.OwnerId });
                    acting = true;
                    break;
                case RoutineAction.Unload when u.CargoAmount > 0:
                    sim.SubmitIntent(now, new Sim.Core.Logistics.UnloadCargoIntent(u.Id)
                        { PlayerId = order.OwnerId });
                    acting = true;
                    break;
            }
        }
        if (acting)
        {
            _journal.Add(now, order, JournalOutcome.Fired,
                $"{step.Action} at stop {cursor} ({step.Tile.X},{step.Tile.Y})");
            return;
        }

        // ---- depart? ----
        foreach (var p in step.DepartWhen)
        {
            if (PredicateEvaluator.IsMet(world, order, p, visible, now, _pendingClaims)) continue;
            // Holding at the stop is legitimate waiting — a caravan parked
            // until its load is worth carrying is the point of the feature,
            // not a stall, so it never burns the retry budget.
            _journal.Add(now, order, JournalOutcome.Waiting,
                $"held at stop {cursor} awaiting departure conditions");
            return;
        }

        sim.SubmitIntent(now, new OrderStatusIntent(order.OrderId, OrderStatusOp.AdvanceStep, cursor)
            { PlayerId = order.OwnerId });
        _journal.Add(now, order, JournalOutcome.Fired, $"departing stop {cursor}");
    }

    // The two single-hand errands. Both are "pull somebody, walk them
    // somewhere, do one thing to them there" — identical staging, different
    // destination and final verb — so they share one implementation rather
    // than two near-copies that drift apart.
    private enum Errand { Train, Staff }

    // TRAIN — "keep N of this profession."   Pull an untrained adult, walk
    //         them to the School, train them. Quota conditions count
    //         in-flight trainees, so two schools on one quota do not race.
    // STAFF — "keep this farm at N hands."   Pull a matching worker, walk
    //         them to the extractor, assign them.
    //
    // Staff never trains a substitute when the right role is scarce: it
    // says so and waits. Train is the player's answer, and the two compose
    // through the shared labour pool without either knowing the other
    // exists (law 3 — compose through the world, never order-to-order).
    private void RunErrand(
        Simulation sim, Order order, bool triggerMet, long now, TileCoord destination, Errand errand)
    {
        var world = sim.World;

        // ---- stage 0: reap ----
        // An errand is DONE when its effect is observable on the unit: the
        // trainee now holds the role, the worker is now Working. Observing
        // the outcome (rather than assuming the intent applied) means a
        // rejection just leaves the unit held and retried next think.
        var held = new List<Unit>();
        foreach (var unitId in ClaimLedger.UnitsOf(world, order.OrderId))
        {
            if (ClaimLedger.ClaimOf(world, unitId) is not { Purpose: ClaimPurpose.InFlight })
                continue;
            if (!world.Units.TryGetValue(unitId, out var u) || u.OwnerId != order.OwnerId)
            {
                sim.SubmitIntent(now, ClaimUnitIntent.Release(unitId, order.OrderId, order.OwnerId));
                continue;
            }
            var done = errand switch
            {
                Errand.Train => u.Role == order.SubjectRole,
                Errand.Staff => u.Activity == Activity.Working && u.Assignment == destination,
                _ => false,
            };
            if (done)
            {
                sim.SubmitIntent(now, ClaimUnitIntent.Release(unitId, order.OrderId, order.OwnerId));
                continue;
            }
            held.Add(u);
        }

        // ---- stage 1: advance — at the destination? do the thing. ----
        // Runs regardless of the trigger: committed work follows through.
        if (held.Count > 0)
        {
            var u = held[0];
            if (u.Position == destination && IsFreeForWork(u))
            {
                Sim.Core.Intents.Intent verb = errand == Errand.Train
                    ? new Sim.Core.Population.TrainUnitIntent(u.Id, order.SubjectRole)
                        { PlayerId = order.OwnerId }
                    : new Sim.Core.Logistics.AssignWorkersIntent(destination, new[] { u.Id })
                        { PlayerId = order.OwnerId };
                sim.SubmitIntent(now, verb);
                sim.SubmitIntent(now, new OrderStatusIntent(order.OrderId, OrderStatusOp.MarkFired)
                    { PlayerId = order.OwnerId });
                _journal.Add(now, order, JournalOutcome.Fired,
                    $"{errand} unit {u.Id} at {destination.X},{destination.Y}");
                return;
            }
            if (IsFreeForWork(u))
                sim.SubmitIntent(now, new Sim.Core.Movement.MoveIntent(u.Id, destination)
                    { PlayerId = order.OwnerId });
            _journal.Add(now, order, JournalOutcome.InFlight, "hand travelling");
            return;
        }

        // ---- stage 2: commit — recruit one hand ----
        if (!triggerMet)
        {
            _journal.Add(now, order, JournalOutcome.Waiting);
            return;
        }

        var pick = SelectorResolver.First(world, order, order.Selector, now, _pendingUnits);
        if (pick is null)
        {
            // Contention, not breakage (see RunHaul) — never auto-disables.
            // For Staff this is the honest "nobody of that trade is free"
            // report that tells the player to raise a Train quota.
            _journal.Add(now, order, JournalOutcome.NoCrew,
                errand == Errand.Train ? "no untrained adult free" : "no matching worker free");
            return;
        }

        sim.SubmitIntent(now, ClaimUnitIntent.Claim(pick.Id, order.OrderId, ClaimPurpose.InFlight, order.OwnerId));
        _pendingClaims[pick.Id] = order.OrderId;
            _pendingUnits.Add(pick.Id);
        if (pick.Position != destination)
            sim.SubmitIntent(now, new Sim.Core.Movement.MoveIntent(pick.Id, destination)
                { PlayerId = order.OwnerId });
        _journal.Add(now, order, JournalOutcome.Fired,
            $"recruited unit {pick.Id} for {errand}");
    }

    // What a patrol has decided to chase, and the route anchor its leash is
    // measured from.
    private readonly record struct Quarry(Unit Target, TileCoord LeashTile);

    // THE ENGAGE SELECTOR (docs/patrols.md) — a WHERE clause, never a ranking.
    //
    // Eligible = hostile, visible to the owner, on a tile (not embarked), and
    // within EngageRadius of ANY crew member (a party spread along a leg still
    // covers its ground). Nearest wins; ties break on the substrate's canonical
    // (distance, y, x, id) order so two runs over one world pick the same
    // target. No weighing of threat, value or winnability — that is the
    // tactical judgement the player is supposed to be making.
    //
    // HOSTILITY IS THE DIPLOMACY LAYER'S WORD. Bandits are hostile to all;
    // rival kingdoms only once war is actually declared. A patrol must never
    // be able to start one by walking into a neutral's scout.
    private static Quarry? FindQuarry(
        GameWorld world, Order order, List<Unit> crew, IReadOnlySet<TileCoord> visible)
    {
        Unit? best = null;
        var bestDist = int.MaxValue;

        foreach (var (_, candidate) in world.Units)   // ascending id — canonical
        {
            if (candidate.IsEmbarked) continue;
            if (!world.Diplomacy.AreHostile(order.OwnerId, candidate.OwnerId)) continue;
            // THE FOG CONTRACT: automation reads only what its owner can see.
            if (!visible.Contains(candidate.Position)) continue;

            var dist = int.MaxValue;
            foreach (var u in crew)
            {
                var d = Sim.Core.Combat.PursuitRules.Chebyshev(u.Position, candidate.Position);
                if (d < dist) dist = d;
            }
            if (dist > order.EngageRadius) continue;

            // THE LEASH IS PART OF THE DECISION, not just of the chase. A
            // target already outside the leash would be released on the very
            // first step, so engaging it would submit a doomed intent every
            // think — visible thrash and log noise for a chase that can never
            // happen. Refuse it here instead.
            var anchor = NearestStop(order, candidate.Position);
            if (order.LeashRadius > 0
                && Sim.Core.Combat.PursuitRules.Chebyshev(candidate.Position, anchor) > order.LeashRadius)
                continue;

            if (best is null
                || dist < bestDist
                || (dist == bestDist && CanonicalCompare(candidate, best) < 0))
            {
                best = candidate;
                bestDist = dist;
            }
        }

        return best is null ? null : new Quarry(best, NearestStop(order, best.Position));
    }

    // The leash anchors to the NEAREST STOP, which makes the bound a corridor
    // around the whole route rather than a bubble around one stop — otherwise
    // a patrol marching a long leg could not chase anything in the middle of
    // it. Anchoring to the route (rather than to where the chase started) is
    // what stops a target hovering at the boundary from dragging the party
    // across the map in leash-sized increments.
    private static TileCoord NearestStop(Order order, TileCoord to)
    {
        var best = order.Steps[0].Tile;
        var bestDist = Sim.Core.Combat.PursuitRules.Chebyshev(best, to);
        foreach (var s in order.Steps)
        {
            var d = Sim.Core.Combat.PursuitRules.Chebyshev(s.Tile, to);
            if (d < bestDist) { bestDist = d; best = s.Tile; }
        }
        return best;
    }

    // Canonical tie-break: (y, x, id) — the substrate's standard total order,
    // so equidistant targets resolve identically on every run.
    private static int CanonicalCompare(Unit a, Unit b)
    {
        var c = a.Position.Y.CompareTo(b.Position.Y);
        if (c != 0) return c;
        c = a.Position.X.CompareTo(b.Position.X);
        return c != 0 ? c : a.Id.CompareTo(b.Id);
    }

    // Does this order have anybody on its books RIGHT NOW — a named crew
    // member still breathing, or a pulled hand it has already claimed?
    //
    // This is what separates "my people are busy doing this job" (in-flight
    // work, report Waiting) from "I want to work and there is nobody"
    // (a real shortage, report NoCrew). Callers use it only after failing to
    // find a FREE hand, so a true answer means the hands exist and are busy.
    private static bool HoldsLiveHands(GameWorld world, Order order)
    {
        if (order.CrewMode == CrewMode.Named) return AnyCrewAlive(world, order);
        foreach (var unitId in ClaimLedger.UnitsOf(world, order.OrderId))
            if (world.Units.ContainsKey(unitId)) return true;
        return false;
    }

    // The lowest-id held (or named) hand that IsStalled, else null.
    private static Unit? StalledHand(GameWorld world, Order order)
    {
        IEnumerable<int> ids = order.CrewMode == CrewMode.Named
            ? order.NamedCrew
            : ClaimLedger.UnitsOf(world, order.OrderId);
        Unit? found = null;
        foreach (var unitId in ids)
        {
            if (!world.Units.TryGetValue(unitId, out var u) || u.OwnerId != order.OwnerId) continue;
            if (IsStalled(u) && (found is null || u.Id < found.Id)) found = u;
        }
        return found;
    }

    // Does this named crew still have anyone left? Distinguishes an order
    // whose hands are merely BUSY from one whose hands are in the ground.
    private static bool AnyCrewAlive(GameWorld world, Order order)
    {
        foreach (var unitId in order.NamedCrew)
            if (world.Units.TryGetValue(unitId, out var u) && u.OwnerId == order.OwnerId)
                return true;
        return false;
    }

    // A hand that is neither free nor going anywhere: a haul obligation with
    // no travel leg and no pending arrival. Combat pins used to produce these
    // (docs/combat-pin-strands-hauls.md); the sim now resumes them on combat
    // end, and this is the belt to that brace — an order must never report a
    // statue as a trip. Hauling is the right gate for this driver: Working
    // and Building have no travel leg by design, and a goal walker is not a
    // driver hand.
    private static bool IsStalled(Unit u) =>
        !IsFreeForWork(u)
        && !u.IsWalking
        && u.Activity == Activity.Hauling
        && !u.IsEmbarked;

    // Free = idle body with no in-flight anchors. Anchors, never Activity.
    private static bool IsFreeForWork(Unit u) =>
        u.Activity == Activity.Idle
        && !u.IsWalking
        && u.HaulPlan is null
        && !u.IsEmbarked;

    // THE BOUNDED-JOB RULE (M16's "wedge forever" fix, inherited): an order
    // that fires fruitlessly forever must eventually stop and TELL the
    // player, rather than silently churning the intent log until someone
    // notices. Only fruitless firings count — waiting is free.
    private void BumpRetryOrDisable(Simulation sim, Order order, long now)
    {
        if (order.RetryCount + 1 >= _cfg.RetryBudget)
        {
            sim.SubmitIntent(now, new OrderStatusIntent(order.OrderId, OrderStatusOp.Disable)
                { PlayerId = order.OwnerId });
            _journal.Add(now, order, JournalOutcome.Suspended, "retry budget exhausted");
            return;
        }
        sim.SubmitIntent(now, new OrderStatusIntent(order.OrderId, OrderStatusOp.BumpRetry)
            { PlayerId = order.OwnerId });
    }
}
