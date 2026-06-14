using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Sim.Server.Scouting;

// M20 Phase 4 — report linting. The highest-stakes failure class is a NUMBER
// the LLM invented and the player then acts on (the audited "seven days" /
// "10-14" inventions). This catches it mechanically, no LLM-judge needed:
// every run of digits in the generated prose must also appear in the grounding
// text the model was given. A faithful report either spells numbers out
// ("a dozen", "two days" — not linted, too paraphrasable) or echoes the
// grounded digits; an invented "30" or "1466" has no grounding and fails.
//
// Word-number linting (catching "ten to fourteen" when the claim said
// "between six and twelve") is a deliberate future refinement — it risks
// false positives on legitimate paraphrase, so this pass stays conservative:
// it only rejects what it is certain is ungrounded.
public static class ReportLinter
{
    private static readonly Regex DigitRun = new(@"\d+", RegexOptions.Compiled);

    // True if every digit-run in `prose` also appears in `grounding` (the
    // OBSERVATIONS block the model was handed). Returns the first offending
    // token via `offending` when it fails, for diagnostics.
    public static bool NumeralsTraceToGrounding(string prose, string grounding, out string? offending)
    {
        var allowed = new HashSet<string>();
        foreach (Match m in DigitRun.Matches(grounding)) allowed.Add(m.Value);

        foreach (Match m in DigitRun.Matches(prose))
        {
            if (!allowed.Contains(m.Value))
            {
                offending = m.Value;
                return false;
            }
        }
        offending = null;
        return true;
    }

    public static bool NumeralsTraceToGrounding(string prose, string grounding) =>
        NumeralsTraceToGrounding(prose, grounding, out _);
}
