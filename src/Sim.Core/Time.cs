namespace Sim.Core;

// THE CANONICAL CLOCK: 1 sim-tick = 1 game-minute.
//
// Every duration in the simulation is denominated in ticks. The constants
// below let durations be written as multiples of their natural unit
// (`9 * Time.Month`, `6 * Time.Hour`) so proportionality is structural —
// a config can't quietly claim a "year" is shorter than a "day" without
// it being obvious at the call site. See docs/time-and-scale.md.
//
// Calendar: 360-day year (12 months × 30 days). Clean integer factors,
// near-real, big enough that aging is legible against build / food /
// production cadences (which all live in minutes-to-hours).
//
// `int` is sufficient: Year = 518,400 ticks; a 60-year lifespan is
// 31,104,000 ticks — comfortably inside `int` headroom. Tick *counters*
// in the engine (`Sim.Now`, scheduled tick fields) are `long`; that
// remains correct because constants here widen freely.
//
// What is NOT here: real-world wall-clock pace. That's a separate
// orthogonal scaler (Sim.Server: TicksPerSecond) that uniformly stretches
// or compresses the in-fiction calendar against real time. Conflating the
// two is what produced the incoherent legacy values that this file
// retires.
public static class Time
{
    public const int Minute = 1;
    public const int Hour   = 60 * Minute;   //         60
    public const int Day    = 24 * Hour;     //      1,440
    public const int Week   =  7 * Day;      //     10,080
    public const int Month  = 30 * Day;      //     43,200
    public const int Year   = 12 * Month;    //    518,400
}
