namespace Sim.Core.Diplomacy;

// Canonical key for a per-pair relationship. Always stores (min, max) so
// AreHostile(a, b) and AreHostile(b, a) hit the same row — directional
// drift would silently corrupt symmetry. Throws on a == b: a faction
// cannot be in a relationship with itself.
public readonly record struct FactionPair(int Lo, int Hi) : IComparable<FactionPair>
{
    public static FactionPair Of(int a, int b)
    {
        if (a == b)
            throw new InvalidOperationException(
                $"FactionPair: a faction cannot have a relationship with itself (id={a}).");
        return a < b ? new FactionPair(a, b) : new FactionPair(b, a);
    }

    public bool Contains(int playerId) => playerId == Lo || playerId == Hi;

    public int Other(int playerId)
    {
        if (playerId == Lo) return Hi;
        if (playerId == Hi) return Lo;
        throw new InvalidOperationException(
            $"FactionPair.Other: {playerId} is not in pair ({Lo},{Hi}).");
    }

    public int CompareTo(FactionPair other)
    {
        var c = Lo.CompareTo(other.Lo);
        return c != 0 ? c : Hi.CompareTo(other.Hi);
    }

    public override string ToString() => $"({Lo},{Hi})";
}
