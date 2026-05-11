using System.Net;
using System.Text;
using System.Text.Json;
using TidalNowPlaying;

const int Port = 8765;

Console.WriteLine($"Tidal Now Playing (Browser) → http://127.0.0.1:{Port}");
Console.WriteLine($"  Widget:    http://127.0.0.1:{Port}/widget.html");
Console.WriteLine($"  JSON feed: http://127.0.0.1:{Port}/now-playing");
Console.WriteLine();

string widgetPath = Path.Combine(AppContext.BaseDirectory, "widget.html");

var state    = new State();
var clock    = new SystemClock();
var fetcher  = new ArtFetcher();
var ingest   = new IngestHandler(state, fetcher, clock);
var beat     = new HeartbeatHandler(state, clock);
var idle     = new IdleTimer(state, clock, TimeSpan.FromSeconds(10));

string? lastLoggedTitle = null;

async Task PostRouter(HttpListenerContext ctx)
{
    var req  = ctx.Request;
    var resp = ctx.Response;
    string path = req.Url?.AbsolutePath ?? "";

    if (path == "/ingest")
    {
        const int MaxBodyBytes = 16 * 1024;

        // Reject early if the declared length already exceeds the cap.
        if (req.ContentLength64 > MaxBodyBytes)
        {
            resp.StatusCode = 413;
            resp.Close();
            return;
        }

        // Read incrementally, capping at MaxBodyBytes + 1 so we can detect oversize
        // bodies without buffering arbitrary amounts (defends against missing or
        // spoofed Content-Length, and against chunked encoding).
        using var mem = new MemoryStream();
        var buf = new byte[8 * 1024];
        long total = 0;
        int read;
        while ((read = await req.InputStream.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            total += read;
            if (total > MaxBodyBytes)
            {
                resp.StatusCode = 413;
                resp.Close();
                return;
            }
            mem.Write(buf, 0, read);
        }
        string body = req.ContentEncoding.GetString(mem.ToArray());

        var result = await ingest.ApplyAsync(body);
        resp.StatusCode = result.StatusCode;
        resp.Close();

        if (result.StatusCode == 200)
        {
            var info = state.Current;
            if (!string.IsNullOrEmpty(info.Title) && info.Title != lastLoggedTitle)
            {
                Console.WriteLine($"◀  {info.Artist} – {info.Title} [{(info.IsPlaying ? "playing" : "paused")}]");
                lastLoggedTitle = info.Title;
            }
            else if (string.IsNullOrEmpty(info.Title) && lastLoggedTitle != null)
            {
                Console.WriteLine("   (nothing playing)");
                lastLoggedTitle = null;
            }
        }
        else if (result.StatusCode == 400)
        {
            Console.WriteLine("⚠  /ingest: malformed body (state cleared)");
            lastLoggedTitle = null;
        }
        return;
    }

    if (path == "/heartbeat")
    {
        long rawLen = req.ContentLength64;

        if (rawLen > 0)
        {
            // Explicitly non-empty per the Content-Length header.
            resp.StatusCode = beat.Apply((int)Math.Min(rawLen, int.MaxValue)).StatusCode;
            resp.Close();
            return;
        }

        // rawLen <= 0: header absent / unknown / chunked. Probe the stream — if any
        // body bytes show up, treat as non-empty and reject. Otherwise 0.
        int observed = 0;
        if (rawLen < 0)
        {
            var probe = new byte[1];
            int n = await req.InputStream.ReadAsync(probe, 0, 1);
            if (n > 0) observed = 1;
        }

        var result = beat.Apply(observed);
        resp.StatusCode = result.StatusCode;
        resp.Close();
        return;
    }

    resp.StatusCode = 404;
    resp.Close();
}

var server = new WidgetServer(Port, widgetPath, () => state.Current, PostRouter);

var idleCts = new CancellationTokenSource();
_ = idle.RunAsync(idleCts.Token);

try
{
    await server.RunAsync();
}
catch (HttpListenerException ex) when (ex.ErrorCode == 5 || ex.ErrorCode == 48 || ex.ErrorCode == 98)
{
    Console.Error.WriteLine($"Port {Port} is already in use — close the other Now Playing instance, or stop the process bound to that port.");
    Environment.Exit(1);
}
