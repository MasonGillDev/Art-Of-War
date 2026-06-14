using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sim.Server.Scouting;

// M20 Phase 4 — the real narrator: a raw-HttpClient call to the Anthropic
// Messages API (POST /v1/messages). No SDK dependency; the wire shape is
// stable and small. Failure-tolerant by contract — any network error, non-2xx
// status, refusal, or empty body returns null, and the service falls back to
// the raw claims sheet. The sim is never blocked on this.
//
// The system prompt is the scout persona (ScoutPrompt.System); the user
// message is the OBSERVATIONS block (the canonical claims). The model is given
// nothing else — it cannot leak a true count it was never handed.
public sealed class ClaudeReportNarrator : IReportNarrator
{
    // One shared HttpClient (the documented reuse pattern — a new client per
    // call leaks sockets).
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly ScoutNarrationOptions _opts;

    public ClaudeReportNarrator(ScoutNarrationOptions opts) => _opts = opts;

    public async Task<string?> NarrateAsync(string systemPrompt, string observations, CancellationToken ct = default)
    {
        if (!_opts.Enabled) return null;

        // Minimal Messages API request: model + max_tokens + system + one user
        // message. No thinking config — this is short flavor text, not a
        // reasoning task. Property names are the snake_case the API expects.
        var body = JsonSerializer.Serialize(new
        {
            model = _opts.Model,
            max_tokens = _opts.MaxTokens,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = observations } },
        });

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _opts.Endpoint);
            req.Headers.Add("x-api-key", _opts.ApiKey);
            req.Headers.Add("anthropic-version", _opts.AnthropicVersion);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return ExtractText(json);
        }
        catch (Exception)
        {
            // Network failure, timeout, cancellation, malformed body — any of
            // these means "no prose this time"; the caller renders raw claims.
            return null;
        }
    }

    // Pull the concatenated text blocks out of a Messages API response:
    //   { "content": [ {"type":"text","text":"..."}, ... ], "stop_reason": "..." }
    // A "refusal" stop reason (or empty text) yields null → fallback.
    private static string? ExtractText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("stop_reason", out var sr) && sr.GetString() == "refusal")
            return null;
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                && block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                sb.Append(txt.GetString());
        }
        var text = sb.ToString().Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
