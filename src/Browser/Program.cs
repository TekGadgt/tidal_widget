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
        string body;
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
            body = await reader.ReadToEndAsync();

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
        int length = (int)Math.Min(req.ContentLength64, int.MaxValue);
        var result = beat.Apply(length);
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
