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
public sealed class HttpApi : IDisposable
{
    private readonly GameHost _host;
    private readonly HttpListener _listener = new();

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

    private static void WriteJson(HttpListenerContext ctx, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }
}
