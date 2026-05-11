using TidalNowPlaying;

const int    Port       = 8765;
const int    PollMs     = 2000;
const string AppName    = Config.AppName;
const string AppFilter  = Config.AppFilter;
const bool   FilterMode = Config.FilterMode;

Console.WriteLine($"{AppName} → http://127.0.0.1:{Port}");
Console.WriteLine($"  Widget:    http://127.0.0.1:{Port}/widget.html");
Console.WriteLine($"  JSON feed: http://127.0.0.1:{Port}/now-playing");
Console.WriteLine();

var poller = new SmtcPoller(PollMs, AppFilter, FilterMode, info =>
{
    if (!string.IsNullOrEmpty(info.Title))
        Console.WriteLine($"▶  {info.Artist} – {info.Title} [{(info.IsPlaying ? "playing" : "paused")}]");
    else
        Console.WriteLine("   (nothing playing)");
});

string widgetPath = Path.Combine(AppContext.BaseDirectory, "widget.html");
var server = new WidgetServer(Port, widgetPath, () => poller.Current);

var finished = await Task.WhenAny(server.RunAsync(), poller.RunAsync());
try { await finished; }
catch (Exception ex) { Console.WriteLine($"Fatal: {ex.Message}"); Environment.Exit(1); }
