namespace Sim.Core.Battlefields;

public enum HitKind : byte
{
    Duel = 1,
    Clash = 2,
    Arrow = 3,
}

// One move that happened. From off the board = came on; To off the board =
// left across that edge.
public readonly record struct BoardMove(int UnitId, Subtile From, Subtile To)
{
    public bool Entered => !From.IsOnBoard;
    public bool Left => !To.IsOnBoard;
}

public readonly record struct BoardHit(int AttackerId, int TargetId, int Damage, HitKind Kind);

// Everything one turn did, for the world to apply and the client to play back
// (docs/battlefield-grid.md §5, §10).
//
// Besiegers and StructureDamage (2026-10-01): the units that worked on the tile's
// structure this turn, and the HP it loses (docs/structure-footprints.md, Update
// 2026-10-01 "Siege from the board").
public sealed record TurnResult(
    BoardState After,
    IReadOnlyList<BoardMove> Moves,
    IReadOnlyDictionary<int, StepNote> Failed,
    IReadOnlyList<(int A, int B)> Clashes,
    IReadOnlyList<BoardHit> Hits,
    IReadOnlyList<int> Deaths,
    IReadOnlyList<int> Besiegers,
    int StructureDamage)
{
    public IEnumerable<int> LeftIds => Moves.Where(m => m.Left).Select(m => m.UnitId);
}

// M41 — one battlefield turn, resolved on the beat (§5 "Resolution, on the
// beat"). A PURE function: board + steps in, TurnResult out. The world reads
// the board from real units, runs this, then applies the result through its
// own primitives (health, death, world hops). Integer-only, no randomness, and
// order-independent: every step resolves together by the collision rules,
// ties between friends go to the lower unit id, never to who ordered first.
//
// `besieges` (2026-10-01): does a unit of this owner work on the tile's structure?
// The world answers it (hostile to the structure's owner, not a bandit); null when
// there is nothing on the tile to besiege. A unit that besieges and is NOT fighting
// a unit this turn — no duel, no clash, no target for its bow — and did not move
// deals its base damage to the structure (no morale: that is for fighting people).
public static class TurnResolver
{
    public static TurnResult Resolve(BoardState board, IReadOnlyDictionary<int, PlannedStep> steps, BattleConfig config,
        Func<int, bool>? besieges = null)
    {
        var failed = new Dictionary<int, StepNote>();
        var moves = new List<BoardMove>();

        // ---- 1. Leaving: off the board before anyone moves (§4.1). ----------
        var stay = new List<BoardUnit>();
        foreach (var u in board.Units)
        {
            if (u.OnBoard && steps.TryGetValue(u.Id, out var s) && s.To is { IsOnBoard: false } to)
            {
                if (board.CanStep(u, u.At, to)) { moves.Add(new BoardMove(u.Id, u.At, to)); continue; }
                failed[u.Id] = StepNote.CannotLeaveThere;
            }
            stay.Add(u);
        }

        // ---- 2. Moves, simultaneously, by the collision table. ---------------
        var pos = stay.ToDictionary(u => u.Id, u => u.At);
        var owner = stay.ToDictionary(u => u.Id, u => u.OwnerId);
        var target = new Dictionary<int, Subtile>();
        foreach (var u in stay)
            if (steps.TryGetValue(u.Id, out var s) && s.To is { IsOnBoard: true } to && to != u.At)
            {
                if (board.CanStep(u, u.At, to)) target[u.Id] = to;
                else failed[u.Id] = StepNote.NoWay;
            }
        var ok = new SortedSet<int>(target.Keys);
        var clashes = new List<(int A, int B)>();

        var changed = true;
        while (changed)
        {
            changed = false;

            // Two enemies trying to trade subtiles (each charging the other):
            // neither moves, and they clash.
            foreach (var a in ok.ToList())
            {
                if (!ok.Contains(a)) continue;
                foreach (var b in ok.ToList())
                {
                    if (b == a || !board.AreHostile(owner[a], owner[b])) continue;
                    if (target[a] != pos[b] || target[b] != pos[a]) continue;
                    ok.Remove(a);
                    ok.Remove(b);
                    clashes.Add((Math.Min(a, b), Math.Max(a, b)));
                    failed[a] = StepNote.Clashed;
                    failed[b] = StepNote.Clashed;
                    changed = true;
                    break;
                }
            }

            // At most one unit per owner per subtile after the moves. A unit
            // that stays keeps its subtile; of several arriving, the lowest id
            // gets it. A failed unit stays, which can fail others: repeat.
            var ending = new SortedDictionary<(int Owner, int Cell), List<int>>();
            foreach (var u in stay)
            {
                var cell = ok.Contains(u.Id) ? target[u.Id] : pos[u.Id];
                if (!cell.IsOnBoard) continue;
                var key = (u.OwnerId, cell.Index);
                if (!ending.TryGetValue(key, out var list)) ending[key] = list = new List<int>();
                list.Add(u.Id);
            }
            foreach (var ids in ending.Values)
            {
                if (ids.Count < 2) continue;
                var stayers = ids.Where(i => !ok.Contains(i)).ToList();
                var arriving = ids.Where(ok.Contains).OrderBy(i => i).ToList();
                var losers = stayers.Count > 0 ? arriving : arriving.Skip(1).ToList();
                foreach (var i in losers)
                {
                    ok.Remove(i);
                    failed[i] = StepNote.AllyInTheWay;
                    changed = true;
                }
            }
        }

        foreach (var u in stay)
            if (target.ContainsKey(u.Id) && !ok.Contains(u.Id) && !failed.ContainsKey(u.Id))
                failed[u.Id] = StepNote.AllyInTheWay;

        var moved = new List<BoardUnit>();
        foreach (var u in stay)
        {
            if (ok.Contains(u.Id))
            {
                moves.Add(new BoardMove(u.Id, u.At, target[u.Id]));
                moved.Add(u with { At = target[u.Id], CameFrom = u.At });
            }
            else moved.Add(u);
        }
        var afterMoves = board.With(moved);

        // ---- 3–5. Duels, clashes, morale, damage (collected, then applied). --
        var hits = new List<BoardHit>();
        var dueling = new HashSet<int>(afterMoves.OnBoard.Where(afterMoves.InDuel).Select(u => u.Id));
        var clashWith = new Dictionary<int, int>();
        foreach (var (a, b) in clashes)
        {
            // A duel comes first: a unit in a duel after the moves fights it,
            // not the clash.
            if (dueling.Contains(a) || dueling.Contains(b)) continue;
            clashWith[a] = b;
            clashWith[b] = a;
        }

        foreach (var u in afterMoves.OnBoard)
        {
            var dealt = u.Damage * Morale(afterMoves, u, config) / BattleConfig.SteadyMorale;
            if (dueling.Contains(u.Id))
                hits.Add(new BoardHit(u.Id, afterMoves.OpponentOf(u)!.Id, dealt, HitKind.Duel));
            else if (clashWith.TryGetValue(u.Id, out var foe))
                hits.Add(new BoardHit(u.Id, foe, dealt, HitKind.Clash));
            else if (u.Ranged && ArcherTarget(afterMoves, u) is { } t)
                hits.Add(new BoardHit(u.Id, t.Id, dealt, HitKind.Arrow));
        }

        var damage = new Dictionary<int, int>();
        foreach (var h in hits)
            damage[h.TargetId] = damage.GetValueOrDefault(h.TargetId) + h.Damage;

        // ---- 5b. The siege: whoever besieges, stood still and fought nobody works on
        //          the structure. Simultaneous with the rest: a besieger shot dead this
        //          turn still did its turn's work.
        var besiegers = new List<int>();
        var structureDamage = 0;
        if (besieges is not null)
        {
            var fighting = new HashSet<int>(hits.Select(h => h.AttackerId));
            foreach (var u in afterMoves.OnBoard)
            {
                if (!besieges(u.OwnerId) || ok.Contains(u.Id) || fighting.Contains(u.Id)) continue;
                besiegers.Add(u.Id);
                structureDamage += u.Damage;
            }
        }

        // ---- 6. The dead. ------------------------------------------------------
        var deaths = new List<int>();
        var survivors = new List<BoardUnit>();
        foreach (var u in afterMoves.Units)
        {
            var hp = u.Hp - damage.GetValueOrDefault(u.Id);
            if (hp <= 0) deaths.Add(u.Id);
            else survivors.Add(hp == u.Hp ? u : u with { Hp = hp });
        }

        return new TurnResult(afterMoves.With(survivors), moves, failed, clashes, hits, deaths, besiegers, structureDamage);
    }

    // §5 "Morale": 100 + LineSupport per friend alongside on this board.
    public static int Morale(BoardState board, BoardUnit u, BattleConfig config) =>
        BattleConfig.SteadyMorale + config.LineSupport * board.FriendsAlongside(u);

    // An archer not in a duel shoots the lowest-HP enemy it reaches, not one in
    // cover. Its reach is every subtile within Layer.ReachAt(its subtile) moves
    // (the four neighbours, unless it stands where the footprint gives more),
    // Blocked subtiles stopping the arrows. From a wall's Tower it also reaches
    // the two subtiles beside the one in front (docs/structure-footprints.md).
    // Ties: the nearer, then the order the search met them (N, E, S, W first),
    // then id.
    public static BoardUnit? ArcherTarget(BoardState board, BoardUnit archer)
    {
        var reach = InReach(board.Layer, archer.At, board.Layer.ReachAt(archer.At));
        if (board.Layer.TowerFront(archer.At) is { } front)
        {
            var ahead = archer.At.Step(front);
            reach.Add(ahead.Step((Heading)(((int)front + 3) % 4)));
            reach.Add(ahead.Step((Heading)(((int)front + 1) % 4)));
        }
        BoardUnit? best = null;
        (int Hp, int Rank, int Id) bestKey = default;
        for (var rank = 0; rank < reach.Count; rank++)
        {
            var q = reach[rank];
            if (!q.IsOnBoard || board.Layer.GivesCover(q)) continue;
            foreach (var e in board.OnBoard)
            {
                if (e.At != q || !board.AreHostile(archer.OwnerId, e.OwnerId)) continue;
                var key = (e.Hp, rank, e.Id);
                if (best is null || key.CompareTo(bestKey) < 0) { best = e; bestKey = key; }
            }
        }
        return best;
    }

    // The subtiles within `moves` orthogonal steps of `from`, nearest first and
    // N, E, S, W within a distance, never through a Blocked subtile or off the
    // board. `from` itself is not in it.
    public static List<Subtile> InReach(SubtileLayer layer, Subtile from, int moves)
    {
        var found = new List<Subtile>();
        var seen = new HashSet<Subtile> { from };
        var frontier = new List<Subtile> { from };
        for (var d = 0; d < moves && frontier.Count > 0; d++)
        {
            var next = new List<Subtile>();
            foreach (var at in frontier)
                foreach (var h in Headings.All)
                {
                    var q = at.Step(h);
                    if (!q.IsOnBoard || layer.KindAt(q) == SubtileKind.Blocked || !seen.Add(q)) continue;
                    next.Add(q);
                    found.Add(q);
                }
            frontier = next;
        }
        return found;
    }

    // Would a turn on this board do anything? False lets the world suspend an
    // idle board (§5 "Idle battlefields") instead of resolving empty turns. A
    // besieger standing on the tile is work: the structure loses HP every turn.
    public static bool HasWork(BoardState board, IReadOnlyDictionary<int, PlannedStep> steps, Func<int, bool>? besieges = null)
    {
        if (steps.Values.Any(s => s.To is not null)) return true;
        foreach (var u in board.OnBoard)
        {
            if (board.InDuel(u)) return true;
            if (u.Ranged && ArcherTarget(board, u) is not null) return true;
            if (besieges is not null && besieges(u.OwnerId)) return true;
        }
        return false;
    }
}
