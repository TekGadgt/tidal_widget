using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;
using TidalNowPlaying;

class Program
{
    const int    Port       = 8765;
    const int    PollMs     = 2000;
    const string AppName    = Config.AppName;
    const string AppFilter  = Config.AppFilter;
    const bool   FilterMode = Config.FilterMode;

    static volatile TrackInfo current = new();
    static readonly object trackLock = new();

    static async Task Main()
    {
        Console.WriteLine($"{AppName} → http://localhost:{Port}");
        Console.WriteLine($"  Widget:    http://localhost:{Port}/widget.html");
        Console.WriteLine($"  JSON feed: http://localhost:{Port}/now-playing");
        Console.WriteLine();

        _ = Task.Run(PollLoop);

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{Port}/");
        listener.Start();

        string widgetPath = Path.Combine(AppContext.BaseDirectory, "widget.html");

        while (true)
        {
            var ctx = await listener.GetContextAsync();
            _ = Task.Run(() => HandleRequest(ctx, widgetPath));
        }
    }

    static async Task PollLoop()
    {
        while (true)
        {
            try
            {
                var info = await GetMediaInfo();
                lock (trackLock)
                {
                    if (info.Title != current.Title || info.Artist != current.Artist || info.IsPlaying != current.IsPlaying)
                    {
                        current = info;
                        if (!string.IsNullOrEmpty(info.Title))
                            Console.WriteLine($"▶  {info.Artist} – {info.Title} [{(info.IsPlaying ? "playing" : "paused")}]");
                        else
                            Console.WriteLine("   (nothing playing)");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Poll error: {ex.Message}");
            }
            await Task.Delay(PollMs);
        }
    }

    static async Task<TrackInfo> GetMediaInfo()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        GlobalSystemMediaTransportControlsSession? session = null;

        if (FilterMode)
        {
            foreach (var s in manager.GetSessions())
            {
                string id = s.SourceAppUserModelId ?? "";
                if (id.Contains(AppFilter, StringComparison.OrdinalIgnoreCase))
                {
                    session = s;
                    break;
                }
            }
        }
        else
        {
            session = manager.GetCurrentSession();
        }

        if (session == null) return new();

        var props = await session.TryGetMediaPropertiesAsync();
        if (props == null) return new();

        var pb = session.GetPlaybackInfo();
        bool playing = pb?.PlaybackStatus ==
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        string art = "";
        try
        {
            var thumb = props.Thumbnail;
            if (thumb != null)
            {
                using var stream = await thumb.OpenReadAsync();
                var reader = new DataReader(stream);
                await reader.LoadAsync((uint)stream.Size);
                byte[] bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
                art = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
            }
        }
        catch { }

        return new TrackInfo
        {
            Title     = props.Title      ?? "",
            Artist    = props.Artist     ?? "",
            Album     = props.AlbumTitle ?? "",
            IsPlaying = playing,
            Art       = art,
        };
    }

    static void HandleRequest(HttpListenerContext ctx, string widgetPath)
    {
        try
        {
            var req  = ctx.Request;
            var resp = ctx.Response;
            resp.Headers.Add("Access-Control-Allow-Origin", "*");
            resp.Headers.Add("Cache-Control", "no-cache");

            if (req.Url?.AbsolutePath == "/now-playing")
            {
                TrackInfo info;
                lock (trackLock) { info = current; }

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
                resp.OutputStream.Write(buf);
            }
            else if (req.Url?.AbsolutePath is "/" or "/widget.html")
            {
                string html = File.Exists(widgetPath)
                    ? File.ReadAllText(widgetPath)
                    : "<p>widget.html not found — place it next to the .exe</p>";
                byte[] buf = Encoding.UTF8.GetBytes(html);
                resp.ContentType = "text/html; charset=utf-8";
                resp.ContentLength64 = buf.Length;
                resp.OutputStream.Write(buf);
            }
            else
            {
                resp.StatusCode = 404;
            }

            resp.Close();
        }
        catch { }
    }
}
