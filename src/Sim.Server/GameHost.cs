using System.Diagnostics;
using System.Text.Json;
using Sim.Core.Engine;
using Sim.Core.Intents;
using Sim.Persistence;
using Sim.Server.Wire;

namespace Sim.Server;

// Owns the authoritative Simulation, the single lock that serializes all sim access
// (Simulation is not thread-safe), and the background clock that maps wall-clock time
// to sim ticks so movement plays out in real time. Exposes exactly the two operations
// the HTTP layer needs: submit an intent, build a view.
public sealed class GameHost : IDisposable
{
    private readonly Simulation _sim;
    private readonly ViewProjector _projector;
    private readonly double _ticksPerSecond;

    private readonly object _gate = new();
    private long _virtualTick;        // current virtual tick; read/written only under _gate
    private volatile bool _running;
    private Thread? _clock;

    // Per-player rolling window of resolution-time rejections, harvested from the
    // engine's ResolvedLog after each Run. All access under _gate.
    private const int MaxNoticesPerPlayer = 20;
    private int _resolvedCursor;      // how far into sim.ResolvedLog we've harvested
    private long _nextNoticeId = 1;
    private readonly Dictionary<int, List<NoticeDto>> _notices = new();

    public GameHost(WorldBuild build, ulong seed, double ticksPerSecond)
    {
        // Spec-aware ctor: builds the world via Genesis AND rolls each genesis unit's
        // lifespan (death-by-age). The plain (GameWorld, seed) ctor would skip that and
        // leave starting units immortal.
        _sim = new Simulation(build.Spec, seed);
        _projector = new ViewProjector(build);
        _ticksPerSecond = ticksPerSecond;
    }

    public void Start()
    {
        if (_clock != null) return;
        _running = true;
        _clock = new Thread(ClockLoop) { IsBackground = true, Name = "sim-clock" };
        _clock.Start();
    }

    public void Stop() => _running = false;
    public void Dispose() => Stop();

    private void ClockLoop()
    {
        var sw = Stopwatch.StartNew();
        var last = sw.Elapsed.TotalSeconds;
        var accum = 0.0;
        while (_running)
        {
            var now = sw.Elapsed.TotalSeconds;
            accum += (now - last) * _ticksPerSecond;
            last = now;
            lock (_gate)
            {
                _virtualTick = (long)accum;
                _sim.Run(until: _virtualTick);
                HarvestRejections();
            }
            Thread.Sleep(20);
        }
    }

    // POST /intent: parse the {typeName,payload} envelope, rebuild the Intent via the
    // engine's registry, queue it. Validation is at resolution time, so this ack only
    // confirms the intent was accepted onto the queue.
    public string SubmitEnvelopeJson(string body)
    {
        try
        {
            var env = JsonSerializer.Deserialize<IntentEnvelopeDto>(body, ServerJson.Options);
            if (env is null || string.IsNullOrEmpty(env.TypeName))
                return Ack(false, "missing typeName");

            var intent = IntentJson.Deserialize(env.TypeName, env.Payload ?? "{}");
            lock (_gate)
            {
                var at = Math.Max(_sim.Now, _virtualTick);
                _sim.SubmitIntent(at, intent);
            }
            return Ack(true, "");
        }
        catch (Exception e) { return Ack(false, e.Message); }
    }

    // GET /view/{playerId}: project the fog-filtered (or revealed) view under the lock,
    // since it reads live sim state.
    public string BuildViewJson(int playerId, bool reveal)
    {
        ViewDto dto;
        lock (_gate)
        {
            dto = _projector.Project(_sim, _sim.Now, playerId, reveal);
            if (_notices.TryGetValue(playerId, out var list)) dto.Notices = list.ToArray();
        }
        return JsonSerializer.Serialize(dto, ServerJson.Options);
    }

    // Scan newly-resolved events for rejected PLAYER intents and record a per-player
    // notice. Only IntentEvents carry a PlayerId; consequence-event rejections (e.g. a
    // haul pickup finding an empty source) aren't attributed here. Called under _gate
    // right after Run, so it sees every resolution exactly once via _resolvedCursor.
    private void HarvestRejections()
    {
        var log = _sim.ResolvedLog;
        for (; _resolvedCursor < log.Count; _resolvedCursor++)
        {
            if (log[_resolvedCursor] is not IntentEvent ie || !ie.Outcome.IsRejected) continue;
            var pid = ie.Intent.PlayerId;
            if (!_notices.TryGetValue(pid, out var list))
            {
                list = new List<NoticeDto>();
                _notices[pid] = list;
            }
            list.Add(new NoticeDto
            {
                Id = _nextNoticeId++,
                Tick = ie.At,
                Text = $"{ie.Intent.Describe()} — {ie.Outcome.Reason}",
            });
            if (list.Count > MaxNoticesPerPlayer) list.RemoveRange(0, list.Count - MaxNoticesPerPlayer);
        }
    }

    private static string Ack(bool accepted, string reason) =>
        JsonSerializer.Serialize(new AckDto { Accepted = accepted, Reason = reason }, ServerJson.Options);
}
