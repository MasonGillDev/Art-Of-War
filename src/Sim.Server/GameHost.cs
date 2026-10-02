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

    // C3 — the world's time of day (Atmosphere/WorldClock.cs). Set before Start().
    public Atmosphere.LightCycleConfig LightCycle
    {
        get => _projector.LightCycle;
        set => _projector.LightCycle = Atmosphere.WorldClock.Normalize(value);
    }
    // PACING IS NOT SIMULATION. This is the wall-clock rate at which the host feeds
    // ticks to a sim that has no idea time is passing unevenly — pausing stops the
    // host asking for ticks, it does not stop or alter a single sim rule. The tick
    // stream is byte-identical whatever the pace, which is why pace never appears in
    // the replay log.
    //
    // TWO-ACT PACING (docs/two-act-pacing.md): the pace is SCHEDULED. It is fast in
    // the prelude and drops at the world's landing tick (Sim.Core LandingConfig),
    // for everyone at once. Players do not set it. The /v2/pace endpoint remains a
    // dev/admin tool: it can pause, and it can hold an override rate until cleared.
    //
    // The schedule is replaced only before Start (PreludeTicksPerSecond). The
    // override is written by the HTTP thread and read by the clock thread;
    // volatile double is not a thing in C#, so it rides an Interlocked long via
    // BitConverter, NaN meaning "follow the schedule". No lock: a torn read would be
    // one frame of slightly-wrong pace, and taking _gate would block the HTTP thread
    // behind a whole tick's worth of AI thinking.
    private volatile PaceSchedule _schedule;
    private long _overrideBits = BitConverter.DoubleToInt64Bits(double.NaN);
    private volatile bool _paused;

    private double OverrideTicksPerSecond
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _overrideBits));
        set => Interlocked.Exchange(ref _overrideBits, BitConverter.DoubleToInt64Bits(value));
    }

    /// The pace schedule this host runs (fixed before Start). Rides genesis to the
    /// client so its countdown and its clock use the same numbers as the host.
    public PaceSchedule Schedule => _schedule;

    /// The prelude pace. Set before Start(); defaults to the constructor's pace, so a
    /// host nobody configures runs one pace throughout even in a world with a landing.
    public double PreludeTicksPerSecond
    {
        get => _schedule.PreludeTicksPerSecond;
        set => _schedule = _schedule with
        {
            PreludeTicksPerSecond = Math.Clamp(value, MinTicksPerSecond, MaxTicksPerSecond),
        };
    }

    /// The pace right now: the dev override if one is held, else the schedule at the
    /// current tick.
    public double CurrentTicksPerSecond
    {
        get
        {
            var over = OverrideTicksPerSecond;
            return double.IsNaN(over) ? _schedule.TicksPerSecondAt(Volatile.Read(ref _virtualTick)) : over;
        }
    }

    /// Host pacing bounds. The floor is above zero because "paused" is its own flag —
    /// a zero rate would be a second, silent way to pause. The ceiling exists because
    /// the clock loop sleeps 20ms, so past ~50 ticks/sec each wake has to run a burst
    /// of ticks and the AI drivers start dominating the frame.
    public const double MinTicksPerSecond = 0.25;
    public const double MaxTicksPerSecond = 32.0;
    private readonly Bandits.BanditDriver? _bandits;
    private readonly List<Ai.AiPlayerDriver> _ais = new();
    private readonly Automation.SubstrateDriver? _automation;
    // M36 — the haul queue (docs/hauling-queue-and-routes.md). Always on: a
    // world with no haul jobs makes Think a cheap no-op.
    private readonly Hauling.HaulingDriver _hauling = new();
    // M49 — Aggressive groups spot and charge what their owner can see.
    private readonly Groups.GroupStanceDriver _stances = new();

    // M25 — observability seam: which faction runs which brain (read-only;
    // the assignment test and smoke tooling read Kind/PlayerId off each
    // driver). The drivers themselves are only ever ticked by ClockLoop.
    public IReadOnlyList<Ai.AiPlayerDriver> AiDrivers => _ais;

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

    // Grave markers on witnessed death-loot tiles — the full lifecycle
    // (unit-map diff, cause exclusion, loot-linked retirement, per-player
    // seen gate) lives in GraveTracker; the host just drives it under _gate.
    private readonly GraveTracker _graveTracker = new();

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
        Automation.AutomationConfig? automationConfig = null,
        Action<Simulation>? setup = null)
    {
        // Spec-aware ctor: builds the world via Genesis AND rolls each genesis unit's
        // lifespan (death-by-age). The plain (GameWorld, seed) ctor would skip that and
        // leave starting units immortal.
        _sim = new Simulation(build.Spec, seed);
        // The battle sandbox's setup (Sandbox/SandboxWorld): what a GenesisSpec
        // can't say (composed people, gear, doctrine, marches), applied before
        // tick 0 and before anything below reads the world.
        setup?.Invoke(_sim);
        _projector = new ViewProjector(build);
        // `ticksPerSecond` is the pace from the landing on (or throughout, with no
        // landing). The prelude starts equal to it; PreludeTicksPerSecond changes that.
        var pace = Math.Clamp(ticksPerSecond, MinTicksPerSecond, MaxTicksPerSecond);
        _schedule = new PaceSchedule(pace, pace, _sim.World.LandingConfig.Tick);
        // M16 — the bandit brain rides the clock loop (same thread, same
        // lock); null when disabled.
        if (banditConfig is { Enabled: true })
            _bandits = new Bandits.BanditDriver(banditConfig);
        // M17 — one AI driver per non-human faction in the genesis spec
        // (the human is faction 0; bandits aren't a FactionStartSpec).
        // M25 — deterministic brain assignment: the HIGHEST RivalCount AI
        // faction ids run the Rival ladder, the rest stay Homesteaders.
        // Highest-first keeps faction 1 (the balance lab's baseline in every
        // pre-M25 test) a Homesteader. Rank by the ids actually present —
        // FindAiStart can skip a faction (no viable start), so ids aren't
        // guaranteed contiguous.
        if (aiConfig is { Enabled: true })
        {
            var aiIds = build.Spec.FactionStarts
                .Where(fs => fs.OwnerId != 0)
                .Select(fs => fs.OwnerId)
                .OrderBy(id => id)
                .ToList();
            var rivals = Math.Clamp(aiConfig.RivalCount, 0, aiIds.Count);
            for (var i = 0; i < aiIds.Count; i++)
            {
                // Rank among the rivals (0-based, ascending id) — the
                // personality table cycles on it, so rival #0 is always the
                // baseline Conqueror and a `--rivals 1` game changes no
                // tuned curve.
                var rank = i - (aiIds.Count - rivals);
                _ais.Add(rank >= 0
                    ? new Ai.AiPlayerDriver(aiIds[i], aiConfig, Ai.BrainKind.Rival,
                        Ai.RivalPersonalities.ForRivalRank(rank))
                    : new Ai.AiPlayerDriver(aiIds[i], aiConfig));
            }
        }
        // M18 — player standing orders. Always constructed (null config →
        // defaults): a world with no orders makes Think a cheap no-op, and
        // orders can arrive at any time over the wire.
        var autoCfg = automationConfig ?? new Automation.AutomationConfig();
        if (autoCfg.Enabled)
            _automation = new Automation.SubstrateDriver(autoCfg);
        // M20 — narrate returned scouts via Claude when a key is configured
        // (ANTHROPIC_API_KEY or the gitignored anthropic-key.txt); otherwise
        // reports ship as the raw claims sheet. Either way they reach the wire.
        var narrationOpts = Scouting.ScoutNarrationOptions.FromEnvironment();
        if (narrationOpts.Enabled)
            _narration = new Scouting.ScoutReportNarrationService(
                new Scouting.ClaudeReportNarrator(narrationOpts));
        // Baseline for the grave harvest's unit-map diff: the genesis roster.
        _graveTracker.SnapshotUnits(_sim.World);
        // Graves ride the PROJECTOR (not just the HTTP path) so the AI
        // brains see the same markers a human client renders — battlefield
        // salvage needs them, and the fairness contract wants one channel.
        _projector.GraveSource = _graveTracker;
        // Same trick for automation status: the driver's journal is what
        // lets a client's order dashboard say "waiting for a free hand"
        // instead of guessing from an empty crew list. Presentation-only.
        _projector.OrderSource = _automation?.Journal;
        _projector.HaulSource = _hauling;
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
            // Paused: advance `last` but not `accum`. Doing both is what keeps a
            // resume from time-warping — the sim never owes catch-up for wall-clock
            // that elapsed while it was stopped.
            //
            // The schedule advances THROUGH the landing: a span that crosses it runs
            // at the prelude pace up to the landing tick and at the slow pace after,
            // so the drop lands on exactly that tick however long this loop slept.
            if (!_paused)
            {
                var over = OverrideTicksPerSecond;
                accum = double.IsNaN(over)
                    ? _schedule.Advance(accum, now - last)
                    : accum + (now - last) * over;
            }
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
                _hauling.Think(_sim, _virtualTick);
                _stances.Think(_sim, _virtualTick);
                // Graves BEFORE rejections: the tracker reads the same
                // resolved-log window that HarvestRejections consumes (it
                // advances _resolvedCursor; the tracker's scan must not).
                _graveTracker.Harvest(_sim, _resolvedCursor);
                HarvestRejections();
                HarvestScoutReturns();
            }
            Thread.Sleep(20);
        }
    }

    /// Un-pause the clock (the battle sandbox's Play, Sandbox/SandboxHost).
    public void Resume() => _paused = false;

    public bool IsPaused => _paused;
    public long VirtualTick => _virtualTick;

    /// Current host pacing: paused, and the pace right now (the override, else the
    /// schedule at the current tick). Read by GET /v2/pace.
    public (bool Paused, double TicksPerSecond) GetPace() => (_paused, CurrentTicksPerSecond);

    /// Dev/admin pacing (players have no pace control: docs/two-act-pacing.md).
    /// A rate HOLDS an override, clamped, until cleared; null clears it and the
    /// schedule resumes. Returns the pace actually adopted, which may differ from
    /// the request because the rate is clamped.
    public (bool Paused, double TicksPerSecond) SetPace(bool paused, double? ticksPerSecond)
    {
        OverrideTicksPerSecond = ticksPerSecond is { } tps
            ? Math.Clamp(tps, MinTicksPerSecond, MaxTicksPerSecond)
            : double.NaN;
        _paused = paused;
        return GetPace();
    }

    /// Is a dev override holding the pace off its schedule?
    public bool PaceOverridden => !double.IsNaN(OverrideTicksPerSecond);

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
            // Order STATUS moves are the driver's voice, not the client's: a
            // wire client could otherwise advance its own circuit past a stop
            // (or, with a spoofed PlayerId, disable another player's order).
            // Set/Clear remain ordinary player intents.
            if (intent is Sim.Core.Automation.OrderStatusIntent)
                return Ack(false, "order-status intents are server-internal");
            // Automation substrate — claims are the driver's bookkeeping, not
            // the client's: a wire client could otherwise pin another order's
            // crew (claim-squatting) or free its own units from the ledger to
            // dodge arbitration. docs/automation-substrate.md, Layer 0.
            if (intent is Sim.Core.Automation.ClaimUnitIntent)
                return Ack(false, "claim intents are server-internal");
            // M36 — the haul queue's order is the driver's to move: a client
            // that could requeue would jump its own jobs to the front.
            if (intent is Sim.Core.Hauling.RequeueHaulJobIntent)
                return Ack(false, "haul-queue moves are server-internal");
            // A client serving its own stop could trade at a stop its crew
            // never reached.
            if (intent is Sim.Core.Hauling.ServeRouteStopIntent)
                return Ack(false, "route stops are served by the server");
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
            // Graves are attached inside Project (the projector's
            // GraveSource) — one channel for humans and brains alike.
        }
        return JsonSerializer.Serialize(dto, ServerJson.Options);
    }

    // GET /map/elevation: the full per-tile elevation grid for client-side terrain
    // synthesis. Generation-time, immutable data (terrain never changes), so unlike
    // BuildViewJson this reads nothing live and takes no _gate lock.
    public string BuildElevationJson() =>
        JsonSerializer.Serialize(_projector.BuildElevationDto(), ServerJson.Options);

    // ── v2 wire ───────────────────────────────────────────────────────────────
    // GET /v2/world: the static genesis payload (terrain + genesis biome grid).
    // Immutable generation-time data, so like BuildElevationJson it reads nothing
    // live and takes no _gate lock.
    // Genesis is immutable generation-time data, so this stays lock-free — but the
    // world's PopulationConfig is a readonly record struct set once at genesis, so
    // reading it here races nothing.
    public string BuildWorldJson()
    {
        var dto = _projector.BuildWorldDto(_sim.World.PopulationConfig, _sim.World.RoyaltyConfig, _sim.World.BiomeDegradationConfig);
        // The host's pace schedule (docs/two-act-pacing.md): static for the life of
        // the world, so it rides genesis. The client's countdown and clock read it.
        var s = _schedule;
        dto.Pace = new PaceScheduleDto
        {
            LandingTick = s.LandingTick,
            PreludeTicksPerSecond = s.PreludeTicksPerSecond,
            TicksPerSecond = s.TicksPerSecond,
        };
        return JsonSerializer.Serialize(dto, ServerJson.Options);
    }

    // GET /v2/view/{playerId}: the slim per-tick view. Identical lock discipline and
    // notice/report attachment to BuildViewJson — only the tile encoding differs.
    public string BuildViewV2Json(int playerId, bool reveal, bool revealOrders = false)
    {
        ViewV2Dto dto;
        lock (_gate)
        {
            dto = _projector.ProjectV2(_sim, _sim.Now, playerId, reveal, revealOrders);
            if (_notices.TryGetValue(playerId, out var list)) dto.Notices = list.ToArray();
            if (_scoutReports.TryGetValue(playerId, out var reps)) dto.ScoutReports = reps.ToArray();
            // The pace this view was produced at, so the client's clock runs at the
            // host's truth on every poll rather than a number read once at startup.
            dto.TicksPerSecond = CurrentTicksPerSecond;
            dto.Paused = _paused;
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
            // M25 — WORLD NEWS: wars, falls, and the end of the game reach
            // every living player's notice feed plus the console (the
            // headless run's only window). Diplomatic state is public
            // knowledge (docs/diplomacy-model.md), so broadcasting leaks
            // nothing. Fenced no-ops (peace voided the telegraph) carry a
            // Reject outcome and stay silent; applied events carry Applied.
            // (Until 2026-09-25 these two cases tested `Outcome is null`,
            // which never holds — Outcome defaults to Applied — so neither
            // broadcast ever fired.)
            switch (log[_resolvedCursor])
            {
                case Sim.Core.Diplomacy.WarBecomesEffectiveEvent w when w.Outcome.IsApplied:
                    Broadcast(w.At, $"WAR: factions {w.Pair.Lo} and {w.Pair.Hi} are now at war.");
                    continue;
                case Sim.Core.Sieges.PlayerDefeatedEvent pd when pd.Outcome.IsApplied:
                    Broadcast(pd.At, $"Faction {pd.OwnerId}'s castle has FALLEN — they are out of the game.");
                    continue;
                case Sim.Core.Landing.LandingEvent landing when landing.Outcome.IsApplied:
                    AnnounceLanding(landing.At);
                    continue;
                // M44 — every survey ends out loud, to its owner only (vein
                // knowledge is private; docs/stone-and-ore-land.md).
                case Sim.Core.Mining.SurveyReportEvent sr:
                    AddNotice(sr.OwnerId, sr.At, sr.Abandoned is { } why
                        ? $"survey at {sr.Target.X},{sr.Target.Y} abandoned — {why}"
                        : sr.Vein is { } v
                            ? $"ore vein found at {v.X},{v.Y} — a Mine can go there"
                            : $"survey at {sr.Target.X},{sr.Target.Y}: nothing in these slopes");
                    continue;
                case Sim.Core.Sieges.GameOverEvent over:
                    Broadcast(over.At, over.WinnerId is { } w2
                        ? $"GAME OVER — faction {w2} has won."
                        : "GAME OVER — mutual ruin; no one remains.");
                    continue;
            }
            if (log[_resolvedCursor] is not IntentEvent ie) continue;
            // A war DECLARATION is the telegraph itself — the whole point
            // is that everyone (especially the target) sees it coming.
            if (!ie.Outcome.IsRejected && ie.Intent is Sim.Core.Diplomacy.DeclareWarIntent dw)
            {
                var delay = _sim.World.Diplomacy.Config.Delay;
                Broadcast(ie.At, $"Faction {dw.DeclarerId} has declared war on faction {dw.TargetId} — " +
                    $"effective in {delay / Sim.Core.Time.Day} game-day(s).");
                continue;
            }
            // An APPLIED auto-disable is news the player must see: one of
            // their automations stopped. This is now RARE by design — a
            // labour shortage no longer disables anything, so reaching here
            // means the order genuinely cannot recover (a named crew that
            // died). See SubstrateDriver, "contention is not breakage".
            if (!ie.Outcome.IsRejected
                && ie.Intent is Sim.Core.Automation.OrderStatusIntent
                    { Op: Sim.Core.Automation.OrderStatusOp.Disable } disable)
            {
                AddNotice(disable.PlayerId, ie.At,
                    $"automation order {disable.OrderId} stopped — its crew can no longer do the job");
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

    // Two-act pacing — day X (docs/two-act-pacing.md). World news for everyone,
    // then each kingdom's own warning: which sides its war bands come from, and
    // when they strike. The sides are the fiction's sails on the horizon; the
    // bands themselves stay in the fog until someone sees them. Called under _gate.
    private void AnnounceLanding(long tick)
    {
        Broadcast(tick, "THE LANDING: war bands have come out of the fog. They march on every kingdom.");
        foreach (var (target, host) in _sim.World.LandingHosts)
        {
            var sides = host.Fronts.Select(f => SideOf(host.Seat, f.Approach)).ToList();
            var list = sides.Count switch
            {
                1 => sides[0],
                _ => string.Join(", ", sides.Take(sides.Count - 1)) + " and " + sides[^1],
            };
            var days = (host.AssaultTick - tick) / (double)Sim.Core.Time.Day;
            AddNotice(target, tick,
                $"{host.Fronts.Count} war band{(host.Fronts.Count == 1 ? "" : "s")} march on your castle from the {list}. " +
                $"They will gather in sight of your walls and strike together in {days:0.#} day{(days == 1 ? "" : "s")}.");
        }
    }

    // The compass side `approach` lies on, seen from `seat` (y grows north).
    private static string SideOf(Sim.Core.World.TileCoord seat, Sim.Core.World.TileCoord approach) =>
        (approach.X - seat.X, approach.Y - seat.Y) switch
        {
            (0, > 0) => "north",
            (> 0, 0) => "east",
            (0, < 0) => "south",
            _ => "west",
        };

    // M25 — world news goes to every real faction's notice feed (negative
    // sentinel owners have no clients) and the console. Called under _gate.
    private void Broadcast(long tick, string text)
    {
        Console.WriteLine($"[world] t{tick} {text}");
        foreach (var (id, _) in _sim.World.Players)
            if (id >= 0)
                AddNotice(id, tick, text);
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
