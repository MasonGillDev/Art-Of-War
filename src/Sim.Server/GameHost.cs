using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
    private readonly Bandits.BanditDriver? _bandits;
    private readonly List<Ai.AiPlayerDriver> _ais = new();
    private readonly Automation.AutomationDriver? _automation;

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

    // M20 — scout reports. When a mission returns, claims are compiled under
    // the lock (a fast pure read) and a raw report is deposited immediately;
    // narration (the slow Claude call) runs off the lock and updates the prose
    // in place when it lands. Off-sim, presentation-only — never hashed.
    private const int MaxReportsPerPlayer = 20;
    private long _nextReportId = 1;
    private readonly Scouting.ScoutReportNarrationService? _narration;
    private readonly Dictionary<int, List<ScoutReportDto>> _scoutReports = new();
    private readonly HashSet<(int scoutId, long dispatchTick)> _handledMissions = new();

    public GameHost(WorldBuild build, ulong seed, double ticksPerSecond,
        Bandits.BanditConfig? banditConfig = null, Ai.AiConfig? aiConfig = null,
        Automation.AutomationConfig? automationConfig = null)
    {
        // Spec-aware ctor: builds the world via Genesis AND rolls each genesis unit's
        // lifespan (death-by-age). The plain (GameWorld, seed) ctor would skip that and
        // leave starting units immortal.
        _sim = new Simulation(build.Spec, seed);
        _projector = new ViewProjector(build);
        _ticksPerSecond = ticksPerSecond;
        // M16 — the bandit brain rides the clock loop (same thread, same
        // lock); null when disabled.
        if (banditConfig is { Enabled: true })
            _bandits = new Bandits.BanditDriver(banditConfig);
        // M17 — one AI driver per non-human faction in the genesis spec
        // (the human is faction 0; bandits aren't a FactionStartSpec).
        // M25 — each gets a seeded PERSONALITY (Homesteader/Opportunist/Warlord)
        // derived deterministically from (seed, ownerId): a twin-run reproduces
        // the same cast. Tests/the balance lab build drivers directly and so
        // stay Homesteader; only the live host deals the war-capable postures.
        if (aiConfig is { Enabled: true })
            foreach (var fs in build.Spec.FactionStarts)
                if (fs.OwnerId != 0)
                    _ais.Add(new Ai.AiPlayerDriver(fs.OwnerId,
                        aiConfig with { Personality = Ai.RivalDoctrine.AssignPersonality(seed, fs.OwnerId) }));
        // M18 — player standing orders. Always constructed (null config →
        // defaults): a world with no orders makes Think a cheap no-op, and
        // orders can arrive at any time over the wire.
        var autoCfg = automationConfig ?? new Automation.AutomationConfig();
        if (autoCfg.Enabled)
            _automation = new Automation.AutomationDriver(autoCfg);
        // M20 — narrate returned scouts via Claude when a key is configured
        // (ANTHROPIC_API_KEY or the gitignored anthropic-key.txt); otherwise
        // reports ship as the raw claims sheet. Either way they reach the wire.
        var narrationOpts = Scouting.ScoutNarrationOptions.FromEnvironment();
        if (narrationOpts.Enabled)
            _narration = new Scouting.ScoutReportNarrationService(
                new Scouting.ClaudeReportNarrator(narrationOpts));
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
                // M16/M17 — the NPC brains read the freshly-advanced world and
                // submit their intents (they resolve on the next Run). Same
                // thread, under the lock: their pure reads can never race the sim.
                _bandits?.Think(_sim, _virtualTick);
                foreach (var ai in _ais) ai.Think(_sim, _projector, _virtualTick);
                // M18 — player standing orders: same contract as the NPC
                // brains (pure reads + ordinary intents, under the lock).
                _automation?.Think(_sim, _virtualTick);
                HarvestRejections();
                HarvestScoutReturns();
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
            // M16 — the bandit faction is SERVER-INTERNAL: no wire client may
            // speak as it (any PlayerId) or invoke its spawn/despawn intents
            // (any claimed id). The in-process driver submits below this gate.
            // First server-internal intent class — docs/intent-authorization.md.
            if (intent.PlayerId == Sim.Core.Bandits.BanditConstants.OwnerId
                || intent is Sim.Core.Bandits.SpawnBanditPartyIntent
                || intent is Sim.Core.Bandits.DespawnBanditPartyIntent)
                return Ack(false, "bandit-faction intents are server-internal");
            // M18 — cursor moves are the AutomationDriver's voice, not the
            // client's: a wire client could otherwise skip its own order's
            // steps (or, with a spoofed PlayerId, wedge another player's
            // order). Set/Clear remain ordinary player intents.
            if (intent is Sim.Core.Automation.AdvanceOrderCursorIntent)
                return Ack(false, "order-cursor intents are server-internal");
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
            if (_scoutReports.TryGetValue(playerId, out var reps)) dto.ScoutReports = reps.ToArray();
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
            if (log[_resolvedCursor] is not IntentEvent ie) continue;
            // M18 — an APPLIED auto-disable is news the player must see:
            // their standing order stopped (retry budget exhausted). The
            // failing action's rejections were already noticed above this
            // in the log; this line says "and the engine gave up."
            if (!ie.Outcome.IsRejected
                && ie.Intent is Sim.Core.Automation.AdvanceOrderCursorIntent
                    { Op: Sim.Core.Automation.CursorOp.Disable } disable)
            {
                AddNotice(disable.PlayerId, ie.At,
                    $"standing order {disable.OrderId} auto-disabled after repeated failures");
                continue;
            }
            if (!ie.Outcome.IsRejected) continue;
            AddNotice(ie.Intent.PlayerId, ie.At, $"{ie.Intent.Describe()} — {ie.Outcome.Reason}");
        }
    }

    // M20 — detect missions that have just reached Returned (each handled once
    // by (scout id, dispatch tick)). Compile claims under the lock, deposit a
    // raw report + a "scout returned" notice immediately, then kick the slow
    // Claude narration off the lock; when it lands it swaps the prose in place.
    // Called under _gate, right after the drivers think.
    private void HarvestScoutReturns()
    {
        foreach (var (scoutId, m) in _sim.World.ScoutMissions)
        {
            if (m.State != Sim.Core.Scouting.ScoutMissionState.Returned) continue;
            if (!_handledMissions.Add((scoutId, m.DispatchTick))) continue;

            var name = ScoutName(scoutId);
            var report = Scouting.ClaimsCompiler.Compile(_sim.World, m);
            var dto = ToReportDto(report, _nextReportId++, name);
            AddReport(m.OwnerId, dto);
            AddNotice(m.OwnerId, _sim.Now, $"{name} has returned with a report.");

            if (_narration is null) continue;
            // Fire-and-forget: narrate off the lock, then update the prose.
            var owner = m.OwnerId;
            var reportId = dto.Id;
            _ = Task.Run(async () =>
            {
                var narrated = await _narration.NarrateReportAsync(report, name).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_scoutReports.TryGetValue(owner, out var list))
                    {
                        var d = list.Find(r => r.Id == reportId);
                        if (d is not null) { d.Prose = narrated.Prose; d.Status = (int)narrated.Status; }
                    }
                }
            });
        }
    }

    private void AddReport(int playerId, ScoutReportDto dto)
    {
        if (!_scoutReports.TryGetValue(playerId, out var list))
        {
            list = new List<ScoutReportDto>();
            _scoutReports[playerId] = list;
        }
        list.Add(dto);
        if (list.Count > MaxReportsPerPlayer) list.RemoveRange(0, list.Count - MaxReportsPerPlayer);
    }

    private static ScoutReportDto ToReportDto(Scouting.ScoutReport report, long id, string name) => new()
    {
        Id = id,
        ScoutUnitId = report.ScoutUnitId,
        ScoutName = name,
        DispatchTick = report.DispatchTick,
        ReturnTick = report.ReturnTick,
        // Starts as the raw claims sheet (shown instantly); narration swaps it.
        Prose = Scouting.ReportText.RawFallback(report),
        Status = (int)Scouting.ReportStatus.RawFallback,
        Claims = report.Claims.Select(c => new ScoutClaimDto
        {
            Sequence = c.Sequence,
            Kind = (int)c.Kind,
            Certainty = (int)c.Certainty,
            Text = c.Text,
            HasAnchor = c.Anchor.HasValue,
            AnchorX = c.Anchor?.X ?? 0,
            AnchorY = c.Anchor?.Y ?? 0,
            Novel = c.Novel,
        }).ToArray(),
    };

    // A stable persona name per scout id — Maddox who survived three missions
    // reads differently than a green recruit. Deterministic, presentation-only.
    private static string ScoutName(int scoutId)
    {
        var names = new[] { "Maddox", "Ren", "Coll", "Brannon", "Hew", "Garrick", "Tam", "Osric", "Wat", "Joss" };
        return names[((scoutId % names.Length) + names.Length) % names.Length];
    }

    private void AddNotice(int playerId, long tick, string text)
    {
        if (!_notices.TryGetValue(playerId, out var list))
        {
            list = new List<NoticeDto>();
            _notices[playerId] = list;
        }
        list.Add(new NoticeDto { Id = _nextNoticeId++, Tick = tick, Text = text });
        if (list.Count > MaxNoticesPerPlayer) list.RemoveRange(0, list.Count - MaxNoticesPerPlayer);
    }

    private static string Ack(bool accepted, string reason) =>
        JsonSerializer.Serialize(new AckDto { Accepted = accepted, Reason = reason }, ServerJson.Options);
}
