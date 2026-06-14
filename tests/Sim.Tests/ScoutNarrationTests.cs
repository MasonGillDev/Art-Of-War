using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sim.Core.Engine;
using Sim.Core.Persistence;
using Sim.Core.Scouting;
using Sim.Core.World;
using Sim.Server.Scouting;

namespace Sim.Tests;

// M20 Phase 4 — the LLM narration layer. NO test calls the real Anthropic API
// (cost): every narrator here is a fake. Pins: the numeral-tracing linter, the
// service's narrate→lint→retry→fallback orchestration, and the headline
// PRESENTATION WALL — the LLM is pure presentation, so the sim hash and the
// claims are identical whether narration is off, on, or failing; only the
// prose differs.
public class ScoutNarrationTests
{
    // ---- fake narrators ----------------------------------------------

    private sealed class FixedNarrator : IReportNarrator
    {
        private readonly string? _prose;
        public int Calls;
        public FixedNarrator(string? prose) => _prose = prose;
        public Task<string?> NarrateAsync(string s, string o, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_prose);
        }
    }

    private sealed class SequenceNarrator : IReportNarrator
    {
        private readonly Queue<string?> _replies;
        public SequenceNarrator(params string?[] replies) => _replies = new Queue<string?>(replies);
        public Task<string?> NarrateAsync(string s, string o, CancellationToken ct = default)
            => Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : null);
    }

    // A digit-free report passes the linter trivially.
    private const string GoodProse =
        "My lord, I rode the eastern road as you bid and am returned. " +
        "Armed men of a rival house hold the open grass, raising a hall beside them. " +
        "Beyond, my path did not carry me; that country is dark to me.";

    // ---- the linter --------------------------------------------------

    [Fact]
    public void Lint_PassesGroundedDigits_RejectsInvented()
    {
        const string grounding = "between 6 and 12, on the third day, about 40 in the hundred done";
        Assert.True(ReportLinter.NumeralsTraceToGrounding("I saw 6, maybe 12, by the 40th part.", grounding));
        // Spelled-out numbers are never linted.
        Assert.True(ReportLinter.NumeralsTraceToGrounding("a dozen men, two days' work", grounding));
        // An invented digit-run fails and is reported.
        Assert.False(ReportLinter.NumeralsTraceToGrounding("a host of 3000 men", grounding, out var bad));
        Assert.Equal("3000", bad);
    }

    // ---- the service: orchestration ----------------------------------

    private static (GameWorld world, Simulation sim) ReturnedMission()
    {
        var spec = new GenesisSpec
        {
            Width = 60, Height = 60,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(10, 10),
                    UnitSpawns = new[] { new UnitSpawn(5, new TileCoord(12, 12), UnitRole.Scout) },
                },
            },
        };
        var world = Genesis.Build(spec);
        world.AddStructure(new Lodge(new TileCoord(11, 11)) { OwnerId = 0 });
        world.AddStructure(new ConstructionSite(new TileCoord(23, 20), StructureKind.House) { OwnerId = 1 })
            .ProgressTicks = 600;
        var sim = new Simulation(world, seed: 1);
        sim.SubmitIntent(0, new DispatchScoutIntent(
            5, new List<TileCoord> { new(20, 12), new(20, 20) }, ScoutReturnRule.WaypointsExhausted));
        sim.Run();
        return (world, sim);
    }

    [Fact]
    public async Task Disabled_FallsBackToRawClaims()
    {
        var (world, sim) = ReturnedMission();
        var service = new ScoutReportNarrationService(narrator: null);
        var result = await service.NarrateAsync(world, sim.World.ScoutMissions[5]);

        Assert.Equal(ReportStatus.RawFallback, result.Status);
        Assert.Equal(ReportText.RawFallback(result.Report), result.Prose);
        Assert.NotEmpty(result.Report.Claims);
    }

    [Fact]
    public async Task ValidProse_IsNarrated()
    {
        var (world, sim) = ReturnedMission();
        var narrator = new FixedNarrator(GoodProse);
        var service = new ScoutReportNarrationService(narrator);
        var result = await service.NarrateAsync(world, sim.World.ScoutMissions[5]);

        Assert.Equal(ReportStatus.Narrated, result.Status);
        Assert.Equal(GoodProse, result.Prose);
        Assert.Equal(1, narrator.Calls); // succeeded first try, no retry
    }

    [Fact]
    public async Task FailedNarration_RetriesOnce_ThenFallsBack()
    {
        var (world, sim) = ReturnedMission();
        var narrator = new FixedNarrator(null); // always fails
        var service = new ScoutReportNarrationService(narrator);
        var result = await service.NarrateAsync(world, sim.World.ScoutMissions[5]);

        Assert.Equal(ReportStatus.RawFallback, result.Status);
        Assert.Equal(2, narrator.Calls); // one retry before giving up
    }

    [Fact]
    public async Task TransientFailure_ThenSuccess_IsNarrated()
    {
        var (world, sim) = ReturnedMission();
        var narrator = new SequenceNarrator(null, GoodProse); // fail, then succeed
        var service = new ScoutReportNarrationService(narrator);
        var result = await service.NarrateAsync(world, sim.World.ScoutMissions[5]);

        Assert.Equal(ReportStatus.Narrated, result.Status);
        Assert.Equal(GoodProse, result.Prose);
    }

    [Fact]
    public async Task InventedNumber_FailsLint_FallsBack()
    {
        var (world, sim) = ReturnedMission();
        // Plausible prose, but with an invented troop count.
        var narrator = new FixedNarrator("My lord, I counted a host of 3000 men on the eastern grass.");
        var service = new ScoutReportNarrationService(narrator);
        var result = await service.NarrateAsync(world, sim.World.ScoutMissions[5]);

        Assert.Equal(ReportStatus.RawFallback, result.Status); // lint rejected the invention
    }

    // ---- the presentation wall (headline) ----------------------------

    [Fact]
    public async Task Report_PresentationWall()
    {
        var (world, sim) = ReturnedMission();
        var mission = sim.World.ScoutMissions[5];
        var hashBefore = Snapshot.Hash(sim);

        var off = await new ScoutReportNarrationService(null).NarrateAsync(world, mission);
        var on = await new ScoutReportNarrationService(new FixedNarrator(GoodProse)).NarrateAsync(world, mission);
        var failing = await new ScoutReportNarrationService(new FixedNarrator(null)).NarrateAsync(world, mission);

        // The sim is untouched by narration, no matter the narrator.
        Assert.Equal(hashBefore, Snapshot.Hash(sim));

        // The CLAIMS are identical across all three — the canonical artifact
        // does not depend on the LLM.
        Assert.Equal(off.Report.CanonicalSentences, on.Report.CanonicalSentences);
        Assert.Equal(off.Report.CanonicalSentences, failing.Report.CanonicalSentences);

        // Only the prose differs: enabled gives narrated prose; off and failing
        // both render the identical raw claims sheet.
        Assert.Equal(ReportStatus.Narrated, on.Status);
        Assert.Equal(GoodProse, on.Prose);
        Assert.Equal(off.Prose, failing.Prose);
        Assert.NotEqual(on.Prose, off.Prose);
    }
}
