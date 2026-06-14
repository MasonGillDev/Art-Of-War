using System.Linq;
using System.Text;
using Sim.Core; // Time

namespace Sim.Server.Scouting;

// M20 Phase 4 — renders a ScoutReport two ways: the OBSERVATIONS block handed
// to the LLM (the grounding text), and the raw claims sheet shown when there
// is no LLM or it fails. Both are pure functions of the claims — flavor on
// top of verifiable ground truth, never a substitute for it.
public static class ReportText
{
    // The user message for the narrator: a fog-honest header plus the ordered
    // canonical sentences, split into what was seen and the limits of the
    // report (mirrors the prompt's STRUCTURE). This is ALSO the grounding text
    // the linter checks the prose against — any number here is fair game; only
    // numbers NOT here are inventions.
    public static string ObservationsBlock(ScoutReport report, string scoutName)
    {
        var days = DaysAfield(report);
        var sb = new StringBuilder();
        sb.Append("SCOUT: ").Append(scoutName)
          .Append(", returned after ").Append(days).Append(days == 1 ? " day afield" : " days afield").Append('\n');
        sb.Append("(report fog-honest: contains only what this scout's vision touched)\n\n");

        var journey = report.Claims.Where(c => !IsLimit(c)).ToList();
        var limits = report.Claims.Where(IsLimit).ToList();

        sb.Append("OBSERVATIONS (in order along the path):\n");
        if (journey.Count == 0)
            sb.Append("- An empty ride. Nothing of note crossed my path.\n");
        else
            foreach (var c in journey) sb.Append("- ").Append(c.Text).Append('\n');

        if (limits.Count > 0)
        {
            sb.Append("\nNOT OBSERVED / UNKNOWN:\n");
            foreach (var c in limits) sb.Append("- ").Append(c.Text).Append('\n');
        }
        return sb.ToString();
    }

    // The raw fallback view — the claims sheet, shown verbatim when there is no
    // narrated prose. Always available, always honest.
    public static string RawFallback(ScoutReport report) =>
        string.Join("\n", report.Claims.Select(c => c.Text));

    private static bool IsLimit(Claim c) => c.Kind is ClaimKind.Unknown or ClaimKind.NotObserved;

    private static long DaysAfield(ScoutReport report)
    {
        var ticks = report.ReturnTick - report.DispatchTick;
        var days = ticks / Time.Day;
        return days < 1 ? 1 : days;
    }
}
