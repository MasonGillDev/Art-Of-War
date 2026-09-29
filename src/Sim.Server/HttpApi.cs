using System.Net;
using System.Text;
using System.Text.Json;

namespace Sim.Server;

// HTTP transport: an HttpListener loop that routes the endpoints to the GameHost.
// Knows nothing about the sim beyond the host's methods.
//   v1 (debug client — FROZEN, do not change shape):
//     GET  /view/{playerId}[?reveal=1]
//     GET  /map/elevation   — full per-tile elevation grid (client-side terrain erosion)
//   v2 (production client — Wire/WireV2.cs):
//     GET  /v2/world              — static genesis payload, fetched ONCE per session
//     GET  /v2/view/{playerId}[?reveal=1]  — slim per-tick view (fog runs, no tile arrays)
//   both:
//     POST /intent
//     GET/POST /v2/pace           — host pause + speed override, a DEV/ADMIN tool (the
//                                   pace is scheduled: docs/two-act-pacing.md). NOT an
//                                   intent: pacing is a property of the host clock,
//                                   never of the sim.
public sealed class HttpApi : IDisposable
{
    private GameHost _host;
    private readonly HttpListener _listener = new();

    // M41 — the battle test bed (Scenarios/ScenarioHost) serves /v2/dev/* and
    // swaps the host when it loads a scenario. Null in every ordinary game.
    public Scenarios.ScenarioHost? Dev { get; set; }

    // Requests are handled one at a time on the listener thread, so a swap made
    // from a dev route never races another request.
    public void SwapHost(GameHost host) => _host = host;

    public HttpApi(GameHost host, int port)
    {
        _host = host;
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    // Blocks the calling thread until Stop() / Dispose() tears the listener down.
    public void Run()
    {
        _listener.Start();
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (HttpListenerException) { break; }    // listener stopped
            catch (ObjectDisposedException) { break; }  // listener disposed
            Handle(ctx);
        }
    }

    public void Stop() { if (_listener.IsListening) _listener.Stop(); }
    public void Dispose() => _listener.Close();

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var path = req.Url!.AbsolutePath;

            if (req.HttpMethod == "GET" && path == "/v2/world")
            {
                WriteJson(ctx, 200, _host.BuildWorldJson());
                return;
            }

            if (req.HttpMethod == "GET" && path.StartsWith("/v2/view/"))
            {
                if (!int.TryParse(path["/v2/view/".Length..], out var v2pid))
                {
                    WriteJson(ctx, 400, "{\"error\":\"bad playerId\"}");
                    return;
                }
                WriteJson(ctx, 200, _host.BuildViewV2Json(v2pid, req.QueryString["reveal"] == "1"));
                return;
            }

            if (req.HttpMethod == "GET" && path.StartsWith("/view/"))
            {
                if (!int.TryParse(path["/view/".Length..], out var pid))
                {
                    WriteJson(ctx, 400, "{\"error\":\"bad playerId\"}");
                    return;
                }
                var reveal = req.QueryString["reveal"] == "1"; // dev mode: ignore fog
                WriteJson(ctx, 200, _host.BuildViewJson(pid, reveal));
                return;
            }

            if (req.HttpMethod == "GET" && path == "/map/elevation")
            {
                WriteJson(ctx, 200, _host.BuildElevationJson());
                return;
            }

            // C2 — host pacing. Pause and speed are a property of the HOST, not the
            // simulation: they change how fast wall-clock time is fed to the tick
            // loop and nothing else. Deliberately NOT an intent, because an intent
            // is a durable world event and this is not one — it never enters the
            // replay log, and a replay of this game runs at whatever pace the
            // replayer chooses.
            if (path == "/v2/pace")
            {
                if (req.HttpMethod == "GET")
                {
                    WriteJson(ctx, 200, PaceJson(_host.GetPace()));
                    return;
                }
                if (req.HttpMethod == "POST")
                {
                    using var paceReader = new StreamReader(req.InputStream, req.ContentEncoding);
                    var paceBody = paceReader.ReadToEnd();
                    PaceDto? want;
                    try { want = JsonSerializer.Deserialize<PaceDto>(paceBody, ServerJson.Options); }
                    catch (JsonException) { want = null; }
                    if (want is null)
                    {
                        WriteJson(ctx, 400, "{\"error\":\"malformed pace body\"}");
                        return;
                    }
                    WriteJson(ctx, 200, PaceJson(_host.SetPace(want.Paused, want.TicksPerSecond)));
                    return;
                }
            }

            if (Dev is not null && path.StartsWith("/v2/dev/"))
            {
                using var devReader = new StreamReader(req.InputStream, req.ContentEncoding);
                if (Dev.Handle(ctx, req.HttpMethod, path, devReader.ReadToEnd())) return;
            }

            if (req.HttpMethod == "POST" && path == "/intent")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = reader.ReadToEnd();
                WriteJson(ctx, 200, _host.SubmitEnvelopeJson(body));
                return;
            }

            WriteJson(ctx, 404, "{\"error\":\"not found\"}");
        }
        catch (Exception e)
        {
            try { WriteJson(ctx, 500, $"{{\"error\":{JsonSerializer.Serialize(e.Message)}}}"); }
            catch { /* client gone */ }
        }
    }

    // The pace body — a dev/admin tool: players have no pace control, the pace is
    // scheduled (docs/two-act-pacing.md). Request: Paused, plus TicksPerSecond to
    // HOLD an override (omit it or send null to return to the schedule). Response:
    // the pace actually running, and whether an override holds it. The clamp is
    // visible rather than silent.
    private sealed class PaceDto
    {
        public bool Paused { get; set; }
        public double? TicksPerSecond { get; set; }
        public bool Overridden { get; set; }
    }

    private string PaceJson((bool Paused, double TicksPerSecond) pace) =>
        JsonSerializer.Serialize(
            new PaceDto { Paused = pace.Paused, TicksPerSecond = pace.TicksPerSecond, Overridden = _host.PaceOverridden },
            ServerJson.Options);

    internal static void WriteJson(HttpListenerContext ctx, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }
}
