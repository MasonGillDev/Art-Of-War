using System;
using System.IO;

namespace Sim.Server.Scouting;

// M20 Phase 4 — narration config (endpoint, model, key, budget). Off by
// default: with no API key the feature is fully playable on raw-claims
// reports. Presentation-only — none of this touches the determinism hash.
public sealed class ScoutNarrationOptions
{
    // The Anthropic Messages API endpoint.
    public string Endpoint { get; init; } = "https://api.anthropic.com/v1/messages";

    // anthropic-version header value.
    public string AnthropicVersion { get; init; } = "2023-06-01";

    // Model id. Default is Opus 4.8 (the most capable widely-available model);
    // for cost, a scout report is short flavor text and "claude-haiku-4-5" is
    // the natural cheap swap — a one-line change here, no code edits.
    public string Model { get; init; } = "claude-opus-4-8";

    // A scout report is a few short paragraphs; this is ample.
    public int MaxTokens { get; init; } = 1024;

    // The API key. Empty = narration disabled (raw-claims fallback). Resolved
    // by FromEnvironment below; never hard-code a key.
    public string ApiKey { get; init; } = "";

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);

    // The local, gitignored key file (repo root). Searched upward from the
    // working dir and the binary dir so it's found whether you run from the
    // repo root or the build output. First real (non-comment, non-blank) line
    // is the key.
    public const string KeyFileName = "anthropic-key.txt";

    // Resolve the key from, in order: the ANTHROPIC_API_KEY environment
    // variable, then the local key file. Either way the secret stays out of
    // source control.
    public static ScoutNarrationOptions FromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            key = ReadKeyFile();
        return new ScoutNarrationOptions { ApiKey = (key ?? "").Trim() };
    }

    private static string? ReadKeyFile()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, KeyFileName);
                if (!File.Exists(path)) continue;
                try { return KeyFromText(File.ReadAllText(path)); }
                catch (IOException) { return null; }
            }
        }
        return null;
    }

    private static string? KeyFromText(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            // An untouched placeholder counts as "not set" → stays disabled.
            if (line.StartsWith("sk-ant-REPLACE", StringComparison.Ordinal)) return null;
            return line;
        }
        return null;
    }
}
