using Sim.Core.Engine;
using Sim.Server.Scenarios;

namespace Sim.Tests.Battlefields;

// M41 — the battle test bed's scenario library (scenarios/battle/*.scenario) stays
// loadable and still produces a battle: each file parses, builds its small real
// world, and within a few game-hours a battlefield opens. Keeps the Unity test bed
// from rotting when the sim moves.
public class ScenarioLibraryTests
{
    // Walk up from THIS source file (the test binaries may be built anywhere).
    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private static string LibraryDir()
    {
        var dir = Path.GetDirectoryName(Here());
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "scenarios", "battle")))
            dir = Path.GetDirectoryName(dir);
        return dir is null ? "" : Path.Combine(dir, "scenarios", "battle");
    }

    public static IEnumerable<object[]> Files()
    {
        var dir = LibraryDir();
        if (dir.Length == 0) yield break;
        foreach (var f in Directory.GetFiles(dir, "*.scenario").OrderBy(f => f, StringComparer.Ordinal))
            yield return new object[] { Path.GetFileName(f) };
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryScenario_BuildsAndOpensABattle(string file)
    {
        var scenario = ScenarioFile.Load(Path.Combine(LibraryDir(), file));
        var (build, setup, seed) = ScenarioHost.Prepare(scenario);
        var sim = new Simulation(build.Spec, seed);
        setup(sim);
        var opened = false;
        for (long t = 0; t <= 12 * Sim.Core.Time.Hour && !opened; t += 10)
        {
            sim.Run(until: t);
            opened = sim.World.Battlefields.Count > 0;
        }
        Assert.True(opened, $"{file}: no battle opened within 12 game-hours");

        // And the sides actually meet on it: both have a unit on a board at
        // once within a few turns (an attacker kept outside a wall never does).
        var turn = sim.World.CombatConfig.RoundIntervalTicks;
        var met = false;
        var end = sim.Now + 6 * turn;
        for (var at = sim.Now + 10; !met && at <= end; at += 10)
        {
            sim.Run(until: at);
            met = sim.World.Units.Values
                .Where(u => u.Board is { OnBoard: true })
                .GroupBy(u => u.Board!.Tile)
                .Any(g => g.Select(u => u.OwnerId).Distinct().Count() > 1);
        }
        Assert.True(met, $"{file}: the sides never stood on a board together");
    }

    [Fact]
    public void TheLibraryIsThere() => Assert.NotEmpty(Files());

    [Fact]
    public void ABadLine_NamesItsFileAndLine()
    {
        var e = Assert.Throws<FormatException>(() => ScenarioFile.Parse("broken", "unit green soldier 0 0"));
        Assert.Contains("broken: line 1", e.Message);
    }
}
