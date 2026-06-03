namespace Sim.Persistence;

// Configurable snapshot-trigger policy. Host calls ShouldSnapshot after
// each batch of work; when true, the host takes a snapshot and calls
// Reset.
//
// Two independent counters: ticks since last snapshot AND intents since
// last snapshot. Snapshot if EITHER threshold is crossed. Long quiet
// periods (lots of ticks, no intents) still trigger; bursty intent
// sequences (many intents in a short tick span) also trigger.
//
// Defaults match docs/m4-status.md: 5000 ticks OR 100 intents.
public sealed class SnapshotCadence
{
    private readonly int _everyNTicks;
    private readonly int _everyMIntents;
    private long _ticksSinceLast;
    private int _intentsSinceLast;

    public SnapshotCadence(int everyNTicks = 5000, int everyMIntents = 100)
    {
        if (everyNTicks < 1) throw new ArgumentOutOfRangeException(nameof(everyNTicks));
        if (everyMIntents < 1) throw new ArgumentOutOfRangeException(nameof(everyMIntents));
        _everyNTicks = everyNTicks;
        _everyMIntents = everyMIntents;
    }

    public int EveryNTicks => _everyNTicks;
    public int EveryMIntents => _everyMIntents;

    // Accumulate progress since the last snapshot. Called by the host
    // after each Run batch (with how many ticks elapsed) and after each
    // SubmitIntentDurable (with +1 intent).
    public void AccumulateTicks(long elapsed)
    {
        if (elapsed < 0) throw new ArgumentOutOfRangeException(nameof(elapsed));
        _ticksSinceLast += elapsed;
    }

    public void AccumulateIntent() => _intentsSinceLast++;

    public bool ShouldSnapshot() =>
        _ticksSinceLast >= _everyNTicks || _intentsSinceLast >= _everyMIntents;

    public void Reset()
    {
        _ticksSinceLast = 0;
        _intentsSinceLast = 0;
    }
}
