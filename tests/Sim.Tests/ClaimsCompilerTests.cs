using System.Linq;
using Sim.Core.Scouting;
using Sim.Core.World;
using Sim.Server.Scouting;

namespace Sim.Tests;

// M20 Phase 2 — the claims compiler is the truth oracle. These pins cover the
// LEXICAL pass directly (bearings, ride-time, banding, day-stamps as pure
// functions) and the SEMANTIC pass through the worked Maddox fixture
// (Appendix B of the spec, adapted to real enums): every one of the five
// audited hand-translation drifts is now something the compiler gets right by
// construction.
public class ClaimsCompilerTests
{
    private static readonly ScoutReportConstants Cfg = new();

    // ---- lexical pass: pure functions, pinned -------------------------

    [Theory]
    [InlineData(0, -5, "north")]
    [InlineData(5, 0, "east")]
    [InlineData(0, 7, "south")]
    [InlineData(-4, 0, "west")]
    [InlineData(5, -5, "north-east")]
    [InlineData(10, 14, "south-east")]   // the camp's bearing from home
    [InlineData(10, 3, "east")]          // shallow angle → cardinal, not diagonal
    public void Bearing_IsPureIntegerCompass(int dx, int dy, string expected)
        => Assert.Equal(expected, Lexicon.Bearing(dx, dy, Cfg));

    [Fact]
    public void RideTime_BucketsTilesIntoTravelersTerms()
    {
        // Derived from RideTicksPerTile + the Time vocabulary, never raw tiles.
        Assert.Equal("a short ride", Lexicon.RideTime(1, Cfg));     // 30 ticks
        Assert.Equal("half a day's ride", Lexicon.RideTime(21, Cfg)); // 630 ticks
        Assert.Equal("a day's ride", Lexicon.RideTime(60, Cfg));    // 1800 ticks
        Assert.Equal("2 days' ride", Lexicon.RideTime(100, Cfg));   // 3000 ticks
    }

    [Fact]
    public void CountPhrase_ExactWhenClose_BandedWhenPoor()
    {
        Assert.Equal("9 of them", Lexicon.CountPhrase(9, 70, Cfg)); // quality >= 67 → exact
        Assert.Equal("a lone man", Lexicon.CountPhrase(1, 90, Cfg));
        // Poor vision → a band, and the true count (9) is NOT in the phrase.
        var banded = Lexicon.CountPhrase(9, 33, Cfg);
        Assert.Equal("between 6 and 12", banded);
        Assert.DoesNotContain("9", banded);
    }

    [Fact]
    public void DayStamp_IsJourneyRelative()
    {
        Assert.Equal("on the first day out", Lexicon.DayStamp(200, 0));
        Assert.Equal("on the second day", Lexicon.DayStamp(2000, 0));
        Assert.Equal("on the third day", Lexicon.DayStamp(3000, 0));
    }

    // ---- semantic pass: the worked fixture ----------------------------

    // Home keep at (40,100), owner 0. A near-straight north-east journey; an
    // empty outbound stretch, then House Ashford (owner 1) making camp by a
    // half-raised dwelling at (52,118), glimpsed at range from (50,114).
    private static (GameWorld world, ScoutMission mission) BuildFixture()
    {
        var spec = new GenesisSpec
        {
            Width = 140, Height = 140,
            FactionStarts = new[]
            {
                new FactionStartSpec
                {
                    OwnerId = 0,
                    CastlePosition = new TileCoord(40, 100),
                    UnitSpawns = new[] { new UnitSpawn(14, new TileCoord(41, 101), UnitRole.Scout) },
                },
            },
        };
        var world = Genesis.Build(spec);

        var mission = new ScoutMission { ScoutUnitId = 14, OwnerId = 0, DispatchTick = 0 };

        // Empty outbound legs (mountain then grass) — compress into one claim.
        foreach (var (c, t) in new[]
        {
            (new TileCoord(41, 101), 200L),
            (new TileCoord(43, 104), 1000L),
            (new TileCoord(45, 107), 1800L),
            (new TileCoord(47, 110), 2400L),
        })
            mission.Legs.Add(new ObservationLeg { Tick = t, Center = c, Radius = 6 });

        // The sighting leg: nine Ashford soldiers, idle, on one tile beside a
        // dwelling at 40% (2 days' work, of a 5-day build), seen from ~4 off.
        var soldiers = Enumerable.Range(320, 9)
            .Select(id => new SightedUnit(id, 1, UnitRole.Soldier, Activity.Idle))
            .ToList();
        var leg = new ObservationLeg { Tick = 3000, Center = new TileCoord(50, 114), Radius = 6 };
        leg.Sightings.Add(new Sighting
        {
            Tile = new TileCoord(52, 118),
            Biome = Biome.Grassland,
            Units = soldiers,
            Structure = new SightedStructure
            {
                Kind = StructureKind.ConstructionSite,
                TargetKind = StructureKind.House,
                OwnerId = 1,
                ProgressTicks = 2880,        // 2 days
                BuildDurationTicks = 7200,   // → 40%
            },
        });
        mission.Legs.Add(leg);

        return (world, mission);
    }

    [Fact]
    public void Fixture_Force_GeometryComputed_CountBanded_DayStamped()
    {
        var (world, mission) = BuildFixture();
        var report = ClaimsCompiler.Compile(world, mission, Cfg);

        var force = Assert.Single(report.Claims, c => c.Kind == ClaimKind.Force);
        Assert.Equal(ClaimCertainty.Estimated, force.Certainty); // banded → estimated

        // (1) Geometry is computed, not narrated: bearing + ride-time, not tiles.
        Assert.Contains("south-east", force.Text);
        Assert.Contains("half a day's ride", force.Text);
        // (2) The count is a band; the true number (9) never appears.
        Assert.Contains("between 6 and 12", force.Text);
        Assert.DoesNotContain("9 of them", force.Text);
        // (4) A journey-relative day stamp is present.
        Assert.Contains("third day", force.Text);
        // Observed posture (Idle → at a halt), stated as fact.
        Assert.Contains("at a halt", force.Text);
        // The map pin is the sighted tile.
        Assert.Equal(new TileCoord(52, 118), force.Anchor);
    }

    [Fact]
    public void Fixture_Impression_IsTaggedSeparately()
    {
        var (world, mission) = BuildFixture();
        var report = ClaimsCompiler.Compile(world, mission, Cfg);

        // (3) "Making camp / entrenching" is an Impression claim, never blended
        //     into the force fact line.
        var impression = Assert.Single(report.Claims, c => c.Kind == ClaimKind.Impression);
        Assert.Equal(ClaimCertainty.Impression, impression.Certainty);
        Assert.Contains("digging in", impression.Text);
    }

    [Fact]
    public void Fixture_Structure_HasTargetKind_PercentAndWork()
    {
        var (world, mission) = BuildFixture();
        var report = ClaimsCompiler.Compile(world, mission, Cfg);

        var s = Assert.Single(report.Claims, c => c.Kind == ClaimKind.Structure);
        Assert.Contains("dwelling being raised by House Ashford", s.Text);
        Assert.Contains("40 in the hundred", s.Text);
        Assert.Contains("2 days' work", s.Text);
    }

    [Fact]
    public void Fixture_ClosureUnknowns_AreEmitted()
    {
        var (world, mission) = BuildFixture();
        var report = ClaimsCompiler.Compile(world, mission, Cfg);

        var unknowns = report.Claims.Where(c => c.Kind == ClaimKind.Unknown).ToList();
        Assert.All(unknowns, u => Assert.Equal(ClaimCertainty.NotObserved, u.Certainty));
        Assert.Contains(unknowns, u => u.Text.Contains("intends"));            // intent
        Assert.Contains(unknowns, u => u.Text.Contains("full strength"));      // true number (banded)
        Assert.Contains(unknowns, u => u.Text.Contains("fort or a staging camp")); // purpose
    }

    [Fact]
    public void Fixture_NegativeClaim_ClosesTheReport()
    {
        var (world, mission) = BuildFixture();
        var report = ClaimsCompiler.Compile(world, mission, Cfg);

        // (5) The country beyond the turnaround is an explicit NOT-OBSERVED
        //     claim, and it is the LAST thing in the report (the limits block).
        var last = report.Claims[^1];
        Assert.Equal(ClaimKind.NotObserved, last.Kind);
        Assert.Contains("dark to me", last.Text);
        Assert.Contains("south-east", last.Text);
    }

    [Fact]
    public void Fixture_EmptyGround_CompressesTheOutboundStretch()
    {
        var (world, mission) = BuildFixture();
        var report = ClaimsCompiler.Compile(world, mission, Cfg);

        var empties = report.Claims.Where(c => c.Kind == ClaimKind.EmptyGround).ToList();
        Assert.Single(empties);                       // four empty legs → one claim
        Assert.Contains("empty", empties[0].Text);
    }

    [Fact]
    public void Fixture_JourneyFirst_LimitsLast()
    {
        var (world, mission) = BuildFixture();
        var report = ClaimsCompiler.Compile(world, mission, Cfg);

        bool IsLimit(Claim c) => c.Kind is ClaimKind.Unknown or ClaimKind.NotObserved;
        var maxJourney = report.Claims.Where(c => !IsLimit(c)).Max(c => c.Sequence);
        var minLimit = report.Claims.Where(IsLimit).Min(c => c.Sequence);
        Assert.True(maxJourney < minLimit, "every limit/unknown must follow every journey claim");
    }

    [Fact]
    public void Fixture_FogHonest_OnlyTheSeenFactionIsNamed()
    {
        var (world, mission) = BuildFixture();
        var report = ClaimsCompiler.Compile(world, mission, Cfg);

        var all = string.Join("\n", report.CanonicalSentences);
        Assert.Contains("House Ashford", all);
        // No other house leaks in — the compiler invents no factions.
        foreach (var house in new[] { "Blackwood", "Hollin", "Marsh", "Vance", "Dunmore" })
            Assert.DoesNotContain(house, all);
    }

    [Fact]
    public void Compile_IsDeterministic()
    {
        var (w1, m1) = BuildFixture();
        var (w2, m2) = BuildFixture();
        Assert.Equal(
            ClaimsCompiler.Compile(w1, m1, Cfg).CanonicalSentences,
            ClaimsCompiler.Compile(w2, m2, Cfg).CanonicalSentences);
    }
}
