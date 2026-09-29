namespace Sim.Core.Battlefields;

// Which edges of a tile connect to which, on foot, worked out from the tile's
// footprint (docs/structure-footprints.md, "World movement"). World pathfinding
// asks this instead of the tile alone, so a castle is entered only through its
// gate and a canal is followed, not crossed.
//
// STATIC, NOT DYNAMIC: it reads the ground's shape (the footprint), never who is
// standing where. A defender in the castle gap is a battle matter; the gap is
// always a way in for pathing.
//
// Two sides, resolved when asked: FRIENDLY (the structure's owner and allies:
// walls and gates let them) and OTHER (everyone else). Diplomacy changing never
// invalidates a mask. Per side, 20 bits:
//   * pass (entry e, exit x), bit e * 4 + x: entering across edge e, the mover
//     can reach edge x and leave across it. e == x is a real crossing: in by
//     the castle gate and back out by it.
//   * stay (entry e), bit 16 + e: entering across e, there is somewhere to stand.
// Derived data, never saved: rebuild it from the structures any time.
public readonly record struct Crossings(uint Friendly, uint Other)
{
    private const uint All = (1u << 20) - 1;

    // A tile with nothing on it (or nothing that separates anything).
    public static readonly Crossings Anywhere = new(All, All);

    public bool IsAnywhere => Friendly == All && Other == All;

    public static Crossings Of(SubtileLayer layer) =>
        layer.IsOpen ? Anywhere : new Crossings(Side(layer, friendly: true), Side(layer, friendly: false));

    private static uint Side(SubtileLayer layer, bool friendly)
    {
        var mover = new BoardMover(friendly, Archer: false);
        uint bits = 0;
        foreach (var e in Headings.All)
        {
            var reach = BattlePathing.ReachableFrom(layer, mover, e);
            if (reach.Count > 0) bits |= 1u << (16 + (int)e);
            foreach (var x in Headings.All)
                if (reach.Any(s => s.IsOnEdgeRow(x) && layer.CanStep(s, s.Step(x), mover)))
                    bits |= 1u << ((int)e * 4 + (int)x);
        }
        return bits;
    }

    private uint Bits(bool friendly) => friendly ? Friendly : Other;

    // Entering across `entry` (null = already here with no known way in, e.g.
    // born on the tile: any way in counts), can the mover leave across `exit`?
    public bool CanPass(bool friendly, Heading? entry, Heading exit)
    {
        var b = Bits(friendly);
        if (entry is { } e) return (b & (1u << ((int)e * 4 + (int)exit))) != 0;
        foreach (var any in Headings.All)
            if ((b & (1u << ((int)any * 4 + (int)exit))) != 0) return true;
        return false;
    }

    // Entering across `entry`, is there somewhere to stand (or any way on)?
    public bool CanStay(bool friendly, Heading? entry)
    {
        var b = Bits(friendly);
        if (entry is { } e) return (b & (1u << (16 + (int)e))) != 0;
        return (b & (0xFu << 16)) != 0;
    }
}
