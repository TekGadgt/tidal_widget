using System.Net;
using System.Text;
using System.Text.Json;

namespace TidalNowPlaying;

public class WidgetServer
{
    private readonly int port;
    private readonly string widgetPath;
    private readonly Func<TrackInfo> getCurrent;
    private readonly Func<HttpListenerContext, Task>? postHandler;
    private readonly CancellationTokenSource cts = new();

    public WidgetServer(
        int port,
        string widgetPath,
        Func<TrackInfo> getCurrent,
        Func<HttpListenerContext, Task>? postHandler = null)
    {
        this.port = port;
        this.widgetPath = widgetPath;
        this.getCurrent = getCurrent;
        this.postHandler = postHandler;
    }

    public async Task RunAsync()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        cts.Token.Register(() => { try { listener.Stop(); } catch { } });

        try
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = Task.Run(() => HandleRequest(ctx));
            }
        }
        finally
        {
            try { listener.Close(); } catch { }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var req  = ctx.Request;
            var resp = ctx.Response;
            string path   = req.Url?.AbsolutePath ?? "";
            string method = req.HttpMethod;

            resp.Headers.Add("Cache-Control", "no-cache");

            string origin = req.Headers["Origin"] ?? "";
            bool fromExtension = origin.StartsWith("chrome-extension://", StringComparison.Ordinal);

            // CORS headers are granted only to the extension. Same-origin requests
            // (widget polling /now-playing from the local server itself) don't have
            // an Origin header and don't need CORS. Public web origins get no
            // approval, so the browser blocks them.
            if (fromExtension)
            {
                resp.Headers.Add("Access-Control-Allow-Origin", origin);
                resp.Headers.Add("Vary", "Origin");
            }

            if (method == "OPTIONS" && (path == "/ingest" || path == "/heartbeat"))
            {
                if (fromExtension)
                {
                    resp.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
                    resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                    resp.Headers.Add("Access-Control-Allow-Private-Network", "true");
                    resp.Headers.Add("Access-Control-Max-Age", "600");
                    resp.StatusCode = 204;
                }
                else
                {
                    // Reject preflights from anyone else — no CORS approval given.
                    resp.StatusCode = 403;
                }
                resp.Close();
                return;
            }

            if (method == "GET" && path == "/now-playing")
            {
                var info = getCurrent();
                string json = JsonSerializer.Serialize(new {
                    title      = info.Title,
                    artist     = info.Artist,
                    album      = info.Album,
                    is_playing = info.IsPlaying,
                    art        = info.Art,
                });
                byte[] buf = Encoding.UTF8.GetBytes(json);
                resp.ContentType = "application/json";
                resp.ContentLength64 = buf.Length;
                await resp.OutputStream.WriteAsync(buf);
                resp.Close();
                return;
            }

            if (method == "GET" && (path == "/" || path == "/widget.html"))
            {
                string html = File.Exists(widgetPath)
                    ? await File.ReadAllTextAsync(widgetPath)
                    : "<p>widget.html not found — place it next to the executable</p>";
                byte[] buf = Encoding.UTF8.GetBytes(html);
                resp.ContentType = "text/html; charset=utf-8";
                resp.ContentLength64 = buf.Length;
                await resp.OutputStream.WriteAsync(buf);
                resp.Close();
                return;
            }

            if (method == "POST" && postHandler != null)
            {
                try { await postHandler(ctx); }
                finally { try { ctx.Response.Close(); } catch { } }
                return;
            }

            resp.StatusCode = 404;
            resp.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Request error: {ex.Message}");
            try { ctx.Response.Close(); } catch { }
        }
    }

    public void Stop() => cts.Cancel();
}
