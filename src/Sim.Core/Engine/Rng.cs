namespace Sim.Core.Engine;

// xorshift64 — small, fast, fully deterministic across runtimes.
// We own the bits because System.Random's algorithm has changed between
// .NET versions, and replay must survive runtime upgrades.
public sealed class Rng
{
    private ulong _state;

    public Rng(ulong seed)
    {
        _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    public ulong State => _state;

    internal void SetState(ulong state)
    {
        // Used only by snapshot restore.
        _state = state == 0 ? 0x9E3779B97F4A7C15UL : state;
    }

    public ulong NextUInt64()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        _state = x;
        return x;
    }

    public int NextInt(int boundExclusive)
    {
        if (boundExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(boundExclusive));
        return (int)(NextUInt64() % (ulong)boundExclusive);
    }
}
