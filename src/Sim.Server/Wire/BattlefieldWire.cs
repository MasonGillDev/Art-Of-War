using Sim.Core.Battlefields;

namespace Sim.Server.Wire;

// ── M41: open battlefields on the v2 view (docs/battlefield-grid.md §10,
//    docs/m41-status.md Phase 2). ─────────────────────────────────────────────────
//
// One BattlefieldDto per open board the player can see (build decision D5).
// Everyone on a visible board shows position, HP and morale; ORDERS, the step
// a unit will try next beat and why its last step failed are the unit's
// OWNER's alone (the other side never sees them, §5 "Hidden orders"). With
// ?reveal=1 (a dev switch; the battle test bed's director mode) every unit's
// orders are shown.
//
// Unity's JsonUtility can't read null objects or nullable numbers, so "none"
// is spelled out: HasLastTurn, HasNext, OrderKind 0.
public sealed class BattlefieldDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Turn { get; set; }
    public long NextTurnTick { get; set; }
    public bool Suspended { get; set; }
    public long RoundTicks { get; set; }
    public BattleUnitDto[] Units { get; set; } = [];
    public bool HasLastTurn { get; set; }
    public BattleTurnDto LastTurn { get; set; } = new();

    // What stands on the board (docs/structure-footprints.md): 16 subtiles,
    // row-major (index = y * 4 + x). Kinds holds each subtile's SubtileKind
    // (0 Open, 1 Blocked, 2 Wall, 3 Tower, 4 Gate, 5 Cover, 6 Water, 7 Bridge); Closed holds its
    // closed sides as bits, 1 << Heading (N 1, E 2, S 4, W 8). Both are empty
    // for a board of plain ground. LayerOwner is the structure's owner (walls,
    // gates and towers take it and its allies), meaningful only when
    // HasLayerOwner.
    public int[] Kinds { get; set; } = [];
    public int[] Closed { get; set; } = [];
    public bool HasLayerOwner { get; set; }
    public int LayerOwner { get; set; }
    // An archer's reach from each subtile, in moves; 0 = the normal 1 (a camp
    // tower: 2). Empty with Kinds.
    public int[] Reach { get; set; } = [];
}

// A 4×4 footprint (docs/structure-footprints.md): row-major (index = y * 4 + x).
//   Kinds  — SubtileKind: 0 Open, 1 Blocked, 2 Wall, 3 Tower, 4 Gate, 5 Cover, 6 Water, 7 Bridge
//   Closed — closed sides as bits, 1 << Heading (N 1, E 2, S 4, W 8)
//   Reach  — an archer's reach from the subtile, in moves; 0 = the normal 1
public sealed class FootprintDto
{
    public int[] Kinds { get; set; } = [];
    public int[] Closed { get; set; } = [];
    public int[] Reach { get; set; } = [];

    public static FootprintDto Of(SubtileLayer layer)
    {
        var all = Subtile.All().ToList();
        return new FootprintDto
        {
            Kinds = all.Select(c => (int)layer.KindAt(c)).ToArray(),
            Closed = all.Select(c => layer.ClosedSides(c).Aggregate(0, (m, h) => m | (1 << (int)h))).ToArray(),
            Reach = all.Select(layer.ReachStored).ToArray(),
        };
    }
}

// A kind's footprint as the build preview turns it: facing North, joining
// nothing (WorldDto.Footprints). Kinds absent from the list are plain ground.
public sealed class FootprintPatternDto
{
    public int Kind { get; set; }
    public FootprintDto Footprint { get; set; } = new();
}

// Subtile SX, SY: 0..3 on the board; one step outside an edge while waiting to
// come on in that lane. Sheltered units (overflow, D1) have no subtile.
public sealed class BattleUnitDto
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public int Role { get; set; }
    public int SX { get; set; }
    public int SY { get; set; }
    public bool Waiting { get; set; }
    public bool Sheltered { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Morale { get; set; }
    public bool InDuel { get; set; }
    public bool Ranged { get; set; }

    // Own units (or all, under reveal). Mine = false means the rest is blank.
    public bool Mine { get; set; }
    public int OrderKind { get; set; }          // 0 = no order (doctrine); else BattleOrderKind
    public int OrderX { get; set; }
    public int OrderY { get; set; }
    public int[] RouteX { get; set; } = [];     // the route's waypoints still ahead
    public int[] RouteY { get; set; } = [];
    public int SwapWith { get; set; }
    public int Doctrine { get; set; }           // DoctrineBehaviour
    public int WithdrawBelow { get; set; }
    public bool HasNext { get; set; }           // the step it will try next beat
    public int NextX { get; set; }
    public int NextY { get; set; }
    public int NextNote { get; set; }           // why it won't step (StepNote), when it won't
    public int LastNote { get; set; }           // why its last step failed (StepNote)
}

// What the turn just resolved did, for the client's playback. Moves, clashes,
// hits and deaths are public (they happened in plain sight on a visible board).
public sealed class BattleTurnDto
{
    public long Tick { get; set; }
    public int Turn { get; set; }
    public BattleMoveDto[] Moves { get; set; } = [];
    public BattleHitDto[] Hits { get; set; } = [];
    public BattleClashDto[] Clashes { get; set; } = [];
    public int[] Deaths { get; set; } = [];
}

public sealed class BattleMoveDto
{
    public int Id { get; set; }
    public int FromX { get; set; }
    public int FromY { get; set; }
    public int ToX { get; set; }
    public int ToY { get; set; }
}

// Kind: 1 duel, 2 clash, 3 arrow (HitKind).
public sealed class BattleHitDto
{
    public int Attacker { get; set; }
    public int Target { get; set; }
    public int Damage { get; set; }
    public int Kind { get; set; }
}

public sealed class BattleClashDto
{
    public int A { get; set; }
    public int B { get; set; }
}
