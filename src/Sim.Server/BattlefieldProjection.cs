using Sim.Core.Battlefields;
using Sim.Core.Combat;
using Sim.Core.Engine;
using Sim.Core.World;
using Sim.Server.Wire;

namespace Sim.Server;

// M41 — projects open battlefields onto a player's v2 view (Wire/BattlefieldWire.cs,
// docs/battlefield-grid.md §10). A PURE READ: it builds a board the same way the
// turn does and asks the planner what each unit would try next beat, without
// touching any state. Must be called under the host lock, like the rest of the
// projector.
//
// What a player sees (build decision D5):
//   * boards on tiles they can see (every board, under revealMap);
//   * everyone on such a board: subtile, HP, morale, whether duelling, and the walkers
//     waiting across its edge for room (Waiting, in the lane outside);
//   * their OWN units' orders, next step and last failure only — the enemy's
//     orders never reach the wire. revealOrders (a dev switch, never a play mode)
//     shows all. The two are separate on purpose (docs/battle-sandbox.md, "Fog, not
//     orders"): lifting the fog must not also give away what the other side plans.
internal static class BattlefieldProjection
{
    public static BattlefieldDto[] Project(Simulation sim, int playerId, bool revealMap, bool revealOrders,
        IReadOnlySet<TileCoord> visible)
    {
        var world = sim.World;
        if (world.Battlefields.Count == 0) return [];
        var list = new List<BattlefieldDto>();
        foreach (var bf in world.Battlefields.Values)
        {
            if (!revealMap && !visible.Contains(bf.Tile)) continue;
            list.Add(ToDto(sim, bf, playerId, revealOrders));
        }
        return list.ToArray();
    }

    private static BattlefieldDto ToDto(Simulation sim, Battlefield bf, int playerId, bool revealOrders)
    {
        var world = sim.World;
        var slotted = world.Units.Values.Where(u => u.Board is { } s && s.Tile == bf.Tile && u.Subtile is not null).ToList();
        var board = Battlefields.ReadBoard(sim, bf.Tile, slotted);
        var preview = Battlefields.PlanPreview(sim, bf);
        var cfg = world.CombatConfig.Battle;

        var units = new List<BattleUnitDto>();
        foreach (var u in slotted)
        {
            var slot = u.Board!;
            var onBoard = board.Get(u.Id);
            var spec = UnitCombatCatalog.Spec(u.Role);
            var dto = new BattleUnitDto
            {
                Id = u.Id,
                OwnerId = u.OwnerId,
                Role = (int)u.Role,
                SX = u.Subtile!.Value.X,
                SY = u.Subtile.Value.Y,
                Waiting = false,       // M42: nobody waits in an outside lane any more
                Sheltered = false,     // M42: nobody is sheltered any more
                Hp = u.Health,
                MaxHp = CombatRules.MaxHealth(u, sim.Now),
                Morale = onBoard is not null ? TurnResolver.Morale(board, onBoard, cfg) : BattleConfig.SteadyMorale,
                InDuel = onBoard is not null && board.InDuel(onBoard),
                Ranged = spec.Ranged,
            };
            if (revealOrders || u.OwnerId == playerId)
            {
                dto.Mine = u.OwnerId == playerId;
                var doctrine = u.Doctrine ?? BattleDoctrine.DefaultFor(u.Role);
                dto.Doctrine = (int)doctrine.Behaviour;
                dto.WithdrawBelow = doctrine.WithdrawBelow;
                dto.LastNote = (int)slot.LastNote;
                if (slot.Order is { } o)
                {
                    dto.OrderKind = (int)o.Kind;
                    dto.OrderX = o.Destination.X;
                    dto.OrderY = o.Destination.Y;
                    dto.SwapWith = o.SwapWith;
                    var ahead = o.Waypoints.Skip(o.NextWaypoint).ToArray();
                    dto.RouteX = ahead.Select(w => w.X).ToArray();
                    dto.RouteY = ahead.Select(w => w.Y).ToArray();
                }
                if (preview.TryGetValue(u.Id, out var step))
                {
                    dto.HasNext = step.To is not null;
                    if (step.To is { } to) { dto.NextX = to.X; dto.NextY = to.Y; }
                    dto.NextNote = (int)step.Note;
                }
            }
            units.Add(dto);
        }

        // M43 (docs/fix-combat-m43.md): walkers waiting at this board's edge for room. They are
        // not on the board (they can't act or be hit) but they are in plain sight across the
        // edge they will come in by: in the lane of the subtile they step onto, just outside it.
        foreach (var u in world.Units.Values)
        {
            if (u.WaitingToEnter != bf.Tile || u.SubtileRoute is not { Count: > 0 } route || u.Subtile is null) continue;
            if (Battlefields.EdgeToward(bf.Tile, u.Position) is not { } edge) continue;
            var arrival = route[0].Sub;
            var lane = edge is Heading.North or Heading.South ? arrival.X : arrival.Y;
            var outside = Subtile.OutsideLane(edge, lane);
            var spec = UnitCombatCatalog.Spec(u.Role);
            var waiting = new BattleUnitDto
            {
                Id = u.Id, OwnerId = u.OwnerId, Role = (int)u.Role,
                SX = outside.X, SY = outside.Y,
                Waiting = true,
                Hp = u.Health, MaxHp = CombatRules.MaxHealth(u, sim.Now),
                Morale = BattleConfig.SteadyMorale,
                Ranged = spec.Ranged,
            };
            if (revealOrders || u.OwnerId == playerId)
            {
                waiting.Mine = u.OwnerId == playerId;
                var doctrine = u.Doctrine ?? BattleDoctrine.DefaultFor(u.Role);
                waiting.Doctrine = (int)doctrine.Behaviour;
                waiting.WithdrawBelow = doctrine.WithdrawBelow;
            }
            units.Add(waiting);
        }

        var result = new BattlefieldDto
        {
            X = bf.Tile.X,
            Y = bf.Tile.Y,
            Turn = bf.TurnNumber,
            NextTurnTick = bf.Suspended ? 0 : bf.NextTurnTick,
            Suspended = bf.Suspended,
            RoundTicks = world.CombatConfig.RoundIntervalTicks,
            Units = units.ToArray(),
        };
        var layer = Battlefields.LayerFor(world, bf.Tile);
        if (!layer.IsOpen)
        {
            var all = Subtile.All().ToList();   // row-major: index = y * 4 + x
            result.Kinds = all.Select(c => (int)layer.KindAt(c)).ToArray();
            result.Closed = all.Select(c => layer.ClosedSides(c).Aggregate(0, (m, h) => m | (1 << (int)h))).ToArray();
            result.Reach = all.Select(layer.ReachStored).ToArray();
        }
        if (layer.Owner is { } lo) { result.HasLayerOwner = true; result.LayerOwner = lo; }
        if (bf.LastTurn is { } last)
        {
            result.HasLastTurn = true;
            result.LastTurn = new BattleTurnDto
            {
                Tick = last.Tick,
                Turn = last.TurnNumber,
                Moves = last.Result.Moves.Select(m => new BattleMoveDto
                {
                    Id = m.UnitId, FromX = m.From.X, FromY = m.From.Y, ToX = m.To.X, ToY = m.To.Y,
                }).ToArray(),
                Hits = last.Result.Hits.Select(h => new BattleHitDto
                {
                    Attacker = h.AttackerId, Target = h.TargetId, Damage = h.Damage, Kind = (int)h.Kind,
                }).ToArray(),
                Clashes = last.Result.Clashes.Select(c => new BattleClashDto { A = c.A, B = c.B }).ToArray(),
                Deaths = last.Result.Deaths.ToArray(),
                Besiegers = last.Result.Besiegers.ToArray(),
                StructureDamage = last.Result.StructureDamage,
            };
        }
        return result;
    }
}
