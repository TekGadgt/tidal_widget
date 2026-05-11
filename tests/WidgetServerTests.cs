using System.Net.Http.Json;
using System.Text.Json;

namespace TidalNowPlaying.Tests;

public class WidgetServerTests : IAsyncDisposable
{
    private readonly WidgetServer server;
    private readonly HttpClient http;
    private readonly string baseUrl;
    private readonly CancellationTokenSource cts = new();
    private readonly Task serverTask;

    public WidgetServerTests()
    {
        int port = FreeTcpPort();
        baseUrl = $"http://127.0.0.1:{port}";
        var info = new TrackInfo { Title = "Spaceman", Artist = "Hardwell", Album = "Spaceman",
                                   IsPlaying = true, Art = "data:image/jpeg;base64,XYZ" };
        server = new WidgetServer(port, "nonexistent-widget.html", () => info);
        serverTask = server.RunAsync();
        http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    [Fact]
    public async Task NowPlaying_ReturnsExpectedJsonShape()
    {
        var resp = await http.GetAsync("/now-playing");
        resp.EnsureSuccessStatusCode();

        string json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Spaceman",     root.GetProperty("title").GetString());
        Assert.Equal("Hardwell",     root.GetProperty("artist").GetString());
        Assert.Equal("Spaceman",     root.GetProperty("album").GetString());
        Assert.True(root.GetProperty("is_playing").GetBoolean());
        Assert.Equal("data:image/jpeg;base64,XYZ", root.GetProperty("art").GetString());
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        server.Stop();
        http.Dispose();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
    }

    private static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
