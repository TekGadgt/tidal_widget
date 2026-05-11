# Browser Extension Source Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Chrome MV3 extension + cross-platform companion C# server so the Tidal Now Playing OBS widget can be driven from a browser tab, in addition to the existing Windows SMTC path.

**Architecture:** The existing Windows SMTC build stays untouched in behavior. A new `TidalNowPlaying.Browser` C# project (plain `net8.0`, cross-platform) exposes the same `/widget.html` + `/now-playing` plus new `POST /ingest` and `POST /heartbeat` endpoints. A Chrome MV3 content script reads `navigator.mediaSession.metadata` from `tidal.com`, POSTs state changes + a 5s heartbeat, and emits a clear on `pagehide` via `sendBeacon`. Server holds in-memory state, fetches album art (https-only, 5 MB / 5s caps), and clears state on a 10s idle timeout. Both binaries share `Shared/WidgetServer.cs` and `Shared/TrackInfo.cs` via compile-included source.

**Tech Stack:** C# / .NET 8, `System.Net.HttpListener`, `System.Net.Http.HttpClient`, `System.Text.Json`, xUnit for tests. Chrome Manifest V3 (vanilla JS content script). GitHub Actions for build + release.

**Source spec:** `docs/superpowers/specs/2026-05-10-browser-extension-source-design.md` (read this first; the plan implements it verbatim).

**Development environment caveat:** This work is being done on macOS. The existing Windows csproj targets `net8.0-windows10.0.19041.0` (WinRT) and cannot be built locally on Mac. Phase A's refactor of the Windows path is verified by pushing to CI; the Browser code is fully developable + testable locally on Mac.

---

## File Structure

**Created:**
- `src/Shared/TrackInfo.cs` — shared `TrackInfo` record (moved from existing `Program.cs`).
- `src/Shared/WidgetServer.cs` — `HttpListener` loop + `/now-playing` + `/widget.html` + `OPTIONS` preflight. Both csprojs include this.
- `src/SmtcPoller.cs` — Windows-only SMTC polling logic extracted from existing `Program.cs`.
- `src/Browser/TidalNowPlaying.Browser.csproj` — new cross-platform server project.
- `src/Browser/Program.cs` — wires `WidgetServer` + `IngestHandler` + `HeartbeatHandler` + `ArtFetcher` + idle timer.
- `src/Browser/IngestHandler.cs` — handles `POST /ingest`, updates `TrackInfo`, kicks off art fetch.
- `src/Browser/HeartbeatHandler.cs` — handles `POST /heartbeat`, refreshes `lastIngestAt`.
- `src/Browser/ArtFetcher.cs` — https-only fetch with size cap, timeout, retries, single-entry cache, in-flight cancellation.
- `src/Browser/State.cs` — shared mutable state holder (`current: TrackInfo`, `lastIngestAt: DateTime`) used by all handlers.
- `tests/TidalNowPlaying.Browser.Tests.csproj` — xUnit test project.
- `tests/FakeHttpMessageHandler.cs` — small `HttpMessageHandler` fake for `ArtFetcher` tests.
- `tests/ArtFetcherTests.cs`
- `tests/IngestHandlerTests.cs`
- `tests/HeartbeatHandlerTests.cs`
- `tests/WidgetServerTests.cs` — covers `/now-playing` JSON shape, OPTIONS preflight.
- `tests/IdleTimeoutTests.cs`
- `extension/manifest.json` — MV3 manifest.
- `extension/content.js` — state-tracking, polling, POST'ing content script.
- `extension/icons/icon16.png`, `icon48.png`, `icon128.png` — placeholder solid-color PNGs (16/48/128).

**Modified:**
- `src/Program.cs` — slimmed to wire `WidgetServer` + `SmtcPoller`.
- `src/TidalNowPlaying.csproj` — adds `<Compile Remove="Browser/**/*.cs" />`, drops `<RuntimeIdentifier>`.
- `.github/workflows/build.yml` — existing matrix passes `-r win-x64` on CLI; new `test-browser`, `build-browser-server`, `package-extension`, `release` jobs.
- `README.md` — adds the Browser-source install instructions and the release URL pattern.

---

## Phase A — Extract Shared/ from existing Windows code

> **CI verification gate:** Phase A only touches the Windows code path. The Mac dev machine cannot build `net8.0-windows10.0.19041.0`. After Task A4, push the branch and verify the existing `build` matrix in GitHub Actions still produces all three SMTC artifacts before proceeding to Phase B.

### Task A1: Extract `TrackInfo` to `Shared/TrackInfo.cs`

**Files:**
- Create: `src/Shared/TrackInfo.cs`
- Modify: `src/Program.cs` (remove `TrackInfo` record at bottom of file)

- [ ] **Step 1: Create the shared file**

Write `src/Shared/TrackInfo.cs`:

```csharp
namespace TidalNowPlaying;

public record TrackInfo
{
    public string Title     { get; init; } = "";
    public string Artist    { get; init; } = "";
    public string Album     { get; init; } = "";
    public bool   IsPlaying { get; init; } = false;
    public string Art       { get; init; } = "";
}
```

- [ ] **Step 2: Remove the duplicate `TrackInfo` record from `src/Program.cs`**

Delete lines 176-183 of `src/Program.cs` (the `record TrackInfo { ... }` block at the bottom of the file).

- [ ] **Step 3: Add `using TidalNowPlaying;` to `src/Program.cs`**

At the top of `src/Program.cs`, add:

```csharp
using TidalNowPlaying;
```

(Place it with the other `using` directives.)

- [ ] **Step 4: Commit**

```bash
git add src/Shared/TrackInfo.cs src/Program.cs
git commit -m "extract TrackInfo to Shared/ for cross-build reuse"
```

---

### Task A2: Extract `WidgetServer` to `Shared/WidgetServer.cs`

This consolidates the `HttpListener` loop and the `/now-playing` + `/widget.html` routes into a class both builds can share. `OPTIONS` preflight handling for the (Browser-only) POST endpoints lives here too, since headers must be sent uniformly.

**Files:**
- Create: `src/Shared/WidgetServer.cs`
- Modify: `src/Program.cs` (remove `HandleRequest`, replace `listener` setup with `WidgetServer`)

- [ ] **Step 1: Create the shared file**

Write `src/Shared/WidgetServer.cs`:

```csharp
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

            resp.Headers.Add("Access-Control-Allow-Origin", "*");
            resp.Headers.Add("Cache-Control", "no-cache");

            if (method == "OPTIONS" && (path == "/ingest" || path == "/heartbeat"))
            {
                resp.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
                resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                resp.Headers.Add("Access-Control-Max-Age", "600");
                resp.StatusCode = 204;
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
                await postHandler(ctx);
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
```

- [ ] **Step 2: Commit (just the new file for now; Program.cs adjustment is Task A4)**

```bash
git add src/Shared/WidgetServer.cs
git commit -m "add Shared/WidgetServer for HttpListener routing"
```

---

### Task A3: Extract SMTC polling to `src/SmtcPoller.cs`

**Files:**
- Create: `src/SmtcPoller.cs`

- [ ] **Step 1: Create the polling class**

Write `src/SmtcPoller.cs`:

```csharp
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TidalNowPlaying;

public class SmtcPoller
{
    private readonly int    pollMs;
    private readonly string appFilter;
    private readonly bool   filterMode;
    private readonly Action<TrackInfo> onChange;
    private TrackInfo current = new();
    private readonly object trackLock = new();

    public SmtcPoller(int pollMs, string appFilter, bool filterMode, Action<TrackInfo> onChange)
    {
        this.pollMs     = pollMs;
        this.appFilter  = appFilter;
        this.filterMode = filterMode;
        this.onChange   = onChange;
    }

    public TrackInfo Current
    {
        get { lock (trackLock) { return current; } }
    }

    public async Task RunAsync()
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
                        onChange(info);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Poll error: {ex.Message}");
            }
            await Task.Delay(pollMs);
        }
    }

    private async Task<TrackInfo> GetMediaInfo()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        GlobalSystemMediaTransportControlsSession? session = null;

        if (filterMode)
        {
            foreach (var s in manager.GetSessions())
            {
                string id = s.SourceAppUserModelId ?? "";
                if (id.Contains(appFilter, StringComparison.OrdinalIgnoreCase))
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
}
```

- [ ] **Step 2: Commit**

```bash
git add src/SmtcPoller.cs
git commit -m "extract SMTC polling to SmtcPoller"
```

---

### Task A4: Slim `Program.cs` + update existing csproj

**Files:**
- Modify: `src/Program.cs` (replace with thin wire-up)
- Modify: `src/TidalNowPlaying.csproj` (add `Compile Remove`, drop `RuntimeIdentifier`)

- [ ] **Step 1: Rewrite `src/Program.cs` as a thin wire-up**

Replace the entire contents of `src/Program.cs` with:

```csharp
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

await Task.WhenAny(server.RunAsync(), poller.RunAsync());
```

- [ ] **Step 2: Update `src/TidalNowPlaying.csproj`**

Replace its contents with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <AssemblyName>$(AppTarget)NowPlaying</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Compile Remove="Browser/**/*.cs" />
  </ItemGroup>
</Project>
```

(Differences: dropped `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` — passed on the CLI now; added `<Compile Remove="Browser/**/*.cs" />` to keep the Windows build from pulling in the upcoming Browser sources.)

- [ ] **Step 3: Update `.github/workflows/build.yml` to pass `-r win-x64`**

In the existing `build` job, change the `Publish ${{ matrix.target }}` step:

```yaml
- name: Publish ${{ matrix.target }}
  run: |
    dotnet publish src/TidalNowPlaying.csproj `
      -c Release `
      -r win-x64 `
      --self-contained true `
      -p:PublishSingleFile=true `
      -p:AppTarget=${{ matrix.target }} `
      -o publish/${{ matrix.target }}
```

(The `-r win-x64` line replaces the implicit RID that used to come from the csproj.)

- [ ] **Step 4: Commit**

```bash
git add src/Program.cs src/TidalNowPlaying.csproj .github/workflows/build.yml
git commit -m "slim Program.cs to wire-up; pass RID on CLI; exclude Browser/"
```

- [ ] **Step 5: Push and verify CI**

```bash
git push -u origin HEAD
```

Open GitHub Actions in a browser. Wait for the `build` matrix (Tidal/Spotify/Any) to complete. Expected: all three jobs green, all three artifacts produced.

If any job fails, **stop and diagnose** before moving to Phase B — the refactor must not have regressed the existing Windows path.

---

## Phase B — Browser project scaffolding

### Task B1: Create the Browser csproj

**Files:**
- Create: `src/Browser/TidalNowPlaying.Browser.csproj`

- [ ] **Step 1: Write the csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <AssemblyName>TidalNowPlaying-Browser</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>TidalNowPlaying</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="..\Shared\**\*.cs" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add a stub `Program.cs` so the project can build**

Create `src/Browser/Program.cs`:

```csharp
Console.WriteLine("TidalNowPlaying.Browser stub — wired up in Phase H");
```

- [ ] **Step 3: Verify the project compiles**

Run:

```bash
dotnet build src/Browser/TidalNowPlaying.Browser.csproj
```

Expected: `Build succeeded.` No errors.

- [ ] **Step 4: Commit**

```bash
git add src/Browser/
git commit -m "scaffold TidalNowPlaying.Browser csproj"
```

---

### Task B2: Create xUnit test project

**Files:**
- Create: `tests/TidalNowPlaying.Browser.Tests.csproj`

- [ ] **Step 1: Generate the xUnit project via the CLI**

Run from the repo root:

```bash
mkdir -p tests
cd tests
dotnet new xunit -n TidalNowPlaying.Browser.Tests --force
mv TidalNowPlaying.Browser.Tests/* .
rmdir TidalNowPlaying.Browser.Tests
rm UnitTest1.cs 2>/dev/null || true
cd ..
```

- [ ] **Step 2: Add project reference to the Browser project**

Run:

```bash
dotnet add tests/TidalNowPlaying.Browser.Tests.csproj reference src/Browser/TidalNowPlaying.Browser.csproj
```

- [ ] **Step 3: Add `InternalsVisibleTo` so tests can reach `internal` members if needed**

Replace `src/Browser/TidalNowPlaying.Browser.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <AssemblyName>TidalNowPlaying-Browser</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>TidalNowPlaying</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="..\Shared\**\*.cs" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="TidalNowPlaying.Browser.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Run the empty test suite to verify scaffolding**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj
```

Expected: `Passed! - Failed: 0, Passed: 0, Skipped: 0, Total: 0`. (Zero tests, zero failures — the suite runs.)

- [ ] **Step 5: Commit**

```bash
git add tests/ src/Browser/TidalNowPlaying.Browser.csproj
git commit -m "add xUnit test project for Browser server"
```

---

### Task B3: Create the shared mutable state holder

`State` is the thread-safe holder for `current: TrackInfo` and `lastIngestAt: DateTime`. Both handlers and the idle timer touch it.

**Files:**
- Create: `src/Browser/State.cs`

- [ ] **Step 1: Write the state class**

```csharp
namespace TidalNowPlaying;

public class State
{
    private readonly object gate = new();
    private TrackInfo current = new();
    private DateTime lastIngestAt = DateTime.MinValue;

    public TrackInfo Current
    {
        get { lock (gate) { return current; } }
    }

    public DateTime LastIngestAt
    {
        get { lock (gate) { return lastIngestAt; } }
    }

    public void Update(TrackInfo info, DateTime now)
    {
        lock (gate)
        {
            current = info;
            lastIngestAt = now;
        }
    }

    public void TouchHeartbeat(DateTime now)
    {
        lock (gate) { lastIngestAt = now; }
    }

    public void Clear()
    {
        lock (gate) { current = new TrackInfo(); }
    }

    public void ClearArt()
    {
        lock (gate) { current = current with { Art = "" }; }
    }

    public void SetArt(string art)
    {
        lock (gate) { current = current with { Art = art }; }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/Browser/State.cs
git commit -m "add State holder for Browser server"
```

---

## Phase C — `ArtFetcher` (TDD)

### Task C1: `FakeHttpMessageHandler` for tests

**Files:**
- Create: `tests/FakeHttpMessageHandler.cs`

- [ ] **Step 1: Write the fake handler**

```csharp
using System.Net;

namespace TidalNowPlaying.Tests;

public class FakeHttpMessageHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Calls { get; } = new();

    private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond)
    {
        this.respond = respond;
    }

    public static FakeHttpMessageHandler AlwaysReturn(byte[] body, string mediaType = "image/jpeg")
        => new((req, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            return Task.FromResult(resp);
        });

    public static FakeHttpMessageHandler AlwaysFail()
        => new((_, _) => throw new HttpRequestException("simulated failure"));

    public static FakeHttpMessageHandler FailThenSucceed(int failTimes, byte[] body)
        => new((req, callIndex) =>
        {
            if (callIndex < failTimes) throw new HttpRequestException("simulated failure");
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(resp);
        });

    public static FakeHttpMessageHandler Slow(TimeSpan delay)
        => new(async (req, _) =>
        {
            await Task.Delay(delay);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int idx = Calls.Count;
        Calls.Add(request);
        return await respond(request, idx);
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add tests/FakeHttpMessageHandler.cs
git commit -m "add FakeHttpMessageHandler for ArtFetcher tests"
```

---

### Task C2: `ArtFetcher` — happy path

**Files:**
- Create: `src/Browser/ArtFetcher.cs`
- Create: `tests/ArtFetcherTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/ArtFetcherTests.cs`:

```csharp
using System.Text;

namespace TidalNowPlaying.Tests;

public class ArtFetcherTests
{
    [Fact]
    public async Task FetchAsync_ValidHttpsUrl_ReturnsBase64DataUrl()
    {
        byte[] body = Encoding.UTF8.GetBytes("fake-image-bytes");
        var handler = FakeHttpMessageHandler.AlwaysReturn(body);
        var fetcher = new ArtFetcher(handler);

        string? result = await fetcher.FetchAsync("https://example.com/art.jpg");

        Assert.NotNull(result);
        Assert.StartsWith("data:image/jpeg;base64,", result);
        string expectedBase64 = Convert.ToBase64String(body);
        Assert.Equal($"data:image/jpeg;base64,{expectedBase64}", result);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: build failure — `ArtFetcher` type doesn't exist yet.

- [ ] **Step 3: Write minimal `ArtFetcher`**

`src/Browser/ArtFetcher.cs`:

```csharp
namespace TidalNowPlaying;

public class ArtFetcher
{
    private readonly HttpClient http;

    public ArtFetcher(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler());
    }

    public async Task<string?> FetchAsync(string url)
    {
        using var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
        return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Browser/ArtFetcher.cs tests/ArtFetcherTests.cs
git commit -m "ArtFetcher: fetch https URL → base64 data URL"
```

---

### Task C3: `ArtFetcher` — reject non-https schemes

**Files:**
- Modify: `src/Browser/ArtFetcher.cs`
- Modify: `tests/ArtFetcherTests.cs`

- [ ] **Step 1: Add failing tests**

Append to `tests/ArtFetcherTests.cs` (inside the same class):

```csharp
    [Theory]
    [InlineData("http://example.com/art.jpg")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/art.jpg")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public async Task FetchAsync_NonHttpsScheme_ReturnsNullWithoutFetching(string url)
    {
        var handler = FakeHttpMessageHandler.AlwaysReturn(new byte[] { 1, 2, 3 });
        var fetcher = new ArtFetcher(handler);

        string? result = await fetcher.FetchAsync(url);

        Assert.Null(result);
        Assert.Empty(handler.Calls);
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: 5 new test cases fail (current impl tries to fetch any URL).

- [ ] **Step 3: Add scheme validation to `ArtFetcher`**

Replace `FetchAsync` in `src/Browser/ArtFetcher.cs`:

```csharp
    public async Task<string?> FetchAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return null;

        using var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
        return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
    }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: 6 passed (1 from C2 + 5 from C3).

- [ ] **Step 5: Commit**

```bash
git add src/Browser/ArtFetcher.cs tests/ArtFetcherTests.cs
git commit -m "ArtFetcher: reject non-https URLs (SSRF mitigation)"
```

---

### Task C4: `ArtFetcher` — 5 MB size cap

**Files:**
- Modify: `src/Browser/ArtFetcher.cs`
- Modify: `tests/ArtFetcherTests.cs`

- [ ] **Step 1: Add failing test**

Append to `ArtFetcherTests`:

```csharp
    [Fact]
    public async Task FetchAsync_ResponseExceedsSizeCap_ReturnsNull()
    {
        byte[] big = new byte[6 * 1024 * 1024]; // 6 MB > 5 MB cap
        var handler = FakeHttpMessageHandler.AlwaysReturn(big);
        var fetcher = new ArtFetcher(handler);

        string? result = await fetcher.FetchAsync("https://example.com/big.jpg");

        Assert.Null(result);
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: this test fails (current impl reads the whole body unconditionally).

- [ ] **Step 3: Add size-cap streaming read to `ArtFetcher`**

Replace `FetchAsync` again:

```csharp
    private const long MaxBytes = 5 * 1024 * 1024;

    public async Task<string?> FetchAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return null;

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) return null;

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var buf = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            if (buf.Length + read > MaxBytes) return null;
            buf.Write(buffer, 0, read);
        }
        return "data:image/jpeg;base64," + Convert.ToBase64String(buf.ToArray());
    }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: 7 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Browser/ArtFetcher.cs tests/ArtFetcherTests.cs
git commit -m "ArtFetcher: 5 MB response size cap"
```

---

### Task C5: `ArtFetcher` — 5 second timeout

**Files:**
- Modify: `src/Browser/ArtFetcher.cs`
- Modify: `tests/ArtFetcherTests.cs`

- [ ] **Step 1: Add failing test**

Append to `ArtFetcherTests`:

```csharp
    [Fact]
    public async Task FetchAsync_RequestExceedsTimeout_ReturnsNull()
    {
        var handler = FakeHttpMessageHandler.Slow(TimeSpan.FromSeconds(10));
        // single zero-delay retry slot so AttemptOnce actually runs once and engages the timeout
        var fetcher = new ArtFetcher(handler, timeout: TimeSpan.FromMilliseconds(200), retryDelays: new[] { TimeSpan.Zero });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? result = await fetcher.FetchAsync("https://example.com/slow.jpg");
        sw.Stop();

        Assert.Null(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Took {sw.Elapsed} — timeout did not trip.");
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: test fails (no timeout yet — and constructor signature doesn't accept `timeout` or `retryDelays`).

- [ ] **Step 3: Extend `ArtFetcher` constructor and add timeout**

Replace `ArtFetcher` in `src/Browser/ArtFetcher.cs`:

```csharp
namespace TidalNowPlaying;

public class ArtFetcher
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly HttpClient http;
    private readonly TimeSpan timeout;
    private readonly TimeSpan[] retryDelays;

    public ArtFetcher(
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null,
        TimeSpan[]? retryDelays = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler());
        this.timeout = timeout ?? TimeSpan.FromSeconds(5);
        this.retryDelays = retryDelays ?? new[]
        {
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
        };
    }

    public async Task<string?> FetchAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return null;

        return await AttemptOnceAsync(url);
    }

    private async Task<string?> AttemptOnceAsync(string url)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var buf = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                if (buf.Length + read > MaxBytes) return null;
                buf.Write(buffer, 0, read);
            }
            return "data:image/jpeg;base64," + Convert.ToBase64String(buf.ToArray());
        }
        catch
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: 8 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Browser/ArtFetcher.cs tests/ArtFetcherTests.cs
git commit -m "ArtFetcher: 5s per-attempt timeout"
```

---

### Task C6: `ArtFetcher` — retry schedule

**Files:**
- Modify: `src/Browser/ArtFetcher.cs`
- Modify: `tests/ArtFetcherTests.cs`

- [ ] **Step 1: Add failing tests**

Append to `ArtFetcherTests`:

```csharp
    [Fact]
    public async Task FetchAsync_FailsTwiceThenSucceeds_ReturnsResult()
    {
        byte[] body = new byte[] { 0xFF, 0xD8, 0xFF };
        var handler = FakeHttpMessageHandler.FailThenSucceed(failTimes: 2, body: body);
        // tight retry delays so test finishes quickly
        var fetcher = new ArtFetcher(handler,
            timeout: TimeSpan.FromSeconds(5),
            retryDelays: new[] { TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1) });

        string? result = await fetcher.FetchAsync("https://example.com/flaky.jpg");

        Assert.NotNull(result);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task FetchAsync_AllAttemptsFail_ReturnsNull()
    {
        var handler = FakeHttpMessageHandler.AlwaysFail();
        var fetcher = new ArtFetcher(handler,
            timeout: TimeSpan.FromSeconds(5),
            retryDelays: new[] { TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1) });

        string? result = await fetcher.FetchAsync("https://example.com/dead.jpg");

        Assert.Null(result);
        Assert.Equal(3, handler.Calls.Count);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: 2 new tests fail (current impl makes only 1 attempt).

- [ ] **Step 3: Add retry loop to `ArtFetcher`**

Replace `FetchAsync` in `src/Browser/ArtFetcher.cs`:

```csharp
    public async Task<string?> FetchAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return null;

        foreach (var delay in retryDelays)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            var result = await AttemptOnceAsync(url);
            if (result != null) return result;
        }
        return null;
    }
```

(The retry schedule waits *before* each attempt — the initial 250 ms also debounces rapid track changes, as discussed in the spec's *Art handling* section.)

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: 10 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Browser/ArtFetcher.cs tests/ArtFetcherTests.cs
git commit -m "ArtFetcher: retry schedule 250ms / 1s / 3s"
```

---

### Task C7: `ArtFetcher` — URL caching

**Files:**
- Modify: `src/Browser/ArtFetcher.cs`
- Modify: `tests/ArtFetcherTests.cs`

- [ ] **Step 1: Add failing tests**

Append to `ArtFetcherTests`:

```csharp
    [Fact]
    public async Task FetchAsync_SameUrlTwice_OnlyFetchesOnce()
    {
        byte[] body = new byte[] { 1, 2, 3 };
        var handler = FakeHttpMessageHandler.AlwaysReturn(body);
        var fetcher = new ArtFetcher(handler, retryDelays: new[] { TimeSpan.Zero });

        string? first  = await fetcher.FetchAsync("https://example.com/art.jpg");
        string? second = await fetcher.FetchAsync("https://example.com/art.jpg");

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task FetchAsync_DifferentUrl_FetchesAgain()
    {
        byte[] body = new byte[] { 1, 2, 3 };
        var handler = FakeHttpMessageHandler.AlwaysReturn(body);
        var fetcher = new ArtFetcher(handler, retryDelays: new[] { TimeSpan.Zero });

        await fetcher.FetchAsync("https://example.com/a.jpg");
        await fetcher.FetchAsync("https://example.com/b.jpg");

        Assert.Equal(2, handler.Calls.Count);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: first test fails (no caching → 2 calls instead of 1).

- [ ] **Step 3: Add single-entry cache to `ArtFetcher`**

Inside `ArtFetcher`, add fields and cache check at the start of `FetchAsync`:

```csharp
    private readonly object cacheLock = new();
    private string? cachedUrl;
    private string? cachedResult;

    public async Task<string?> FetchAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return null;

        lock (cacheLock)
        {
            if (url == cachedUrl && cachedResult != null) return cachedResult;
        }

        foreach (var delay in retryDelays)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            var result = await AttemptOnceAsync(url);
            if (result != null)
            {
                lock (cacheLock)
                {
                    cachedUrl    = url;
                    cachedResult = result;
                }
                return result;
            }
        }
        return null;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: 12 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Browser/ArtFetcher.cs tests/ArtFetcherTests.cs
git commit -m "ArtFetcher: single-entry URL cache"
```

---

### Task C8: `ArtFetcher` — in-flight cancellation on new URL

**Files:**
- Modify: `src/Browser/ArtFetcher.cs`
- Modify: `tests/ArtFetcherTests.cs`

- [ ] **Step 1: Add failing test**

Append to `ArtFetcherTests`:

```csharp
    [Fact]
    public async Task FetchAsync_NewUrlWhileOldInFlight_CancelsOldAndProceeds()
    {
        // The first call uses a "slow" handler (10s). The second call uses a different URL.
        // The first call should return null because the second call cancels it.
        var slowHandler = FakeHttpMessageHandler.Slow(TimeSpan.FromSeconds(10));
        var fetcher = new ArtFetcher(slowHandler,
            timeout: TimeSpan.FromSeconds(30),
            retryDelays: new[] { TimeSpan.Zero });

        var firstTask = fetcher.FetchAsync("https://example.com/slow-a.jpg");
        await Task.Delay(50); // let first call start
        var secondTask = fetcher.FetchAsync("https://example.com/slow-b.jpg");
        await Task.Delay(50); // let cancellation propagate

        string? firstResult = await firstTask;
        Assert.Null(firstResult);
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: this test hangs or fails (no cancellation between concurrent fetches yet).

- [ ] **Step 3: Add in-flight cancellation to `ArtFetcher`**

Replace the `ArtFetcher` class body in `src/Browser/ArtFetcher.cs`:

```csharp
namespace TidalNowPlaying;

public class ArtFetcher
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly HttpClient http;
    private readonly TimeSpan timeout;
    private readonly TimeSpan[] retryDelays;

    private readonly object gate = new();
    private string? cachedUrl;
    private string? cachedResult;
    private CancellationTokenSource? inFlight;

    public ArtFetcher(
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null,
        TimeSpan[]? retryDelays = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler());
        this.timeout = timeout ?? TimeSpan.FromSeconds(5);
        this.retryDelays = retryDelays ?? new[]
        {
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
        };
    }

    public async Task<string?> FetchAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return null;

        CancellationToken outerToken;
        lock (gate)
        {
            if (url == cachedUrl && cachedResult != null) return cachedResult;
            inFlight?.Cancel();
            inFlight = new CancellationTokenSource();
            outerToken = inFlight.Token;
        }

        foreach (var delay in retryDelays)
        {
            if (outerToken.IsCancellationRequested) return null;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, outerToken); }
                catch (OperationCanceledException) { return null; }
            }

            var result = await AttemptOnceAsync(url, outerToken);
            if (outerToken.IsCancellationRequested) return null;
            if (result != null)
            {
                lock (gate)
                {
                    cachedUrl    = url;
                    cachedResult = result;
                }
                return result;
            }
        }
        return null;
    }

    private async Task<string?> AttemptOnceAsync(string url, CancellationToken outerToken)
    {
        using var perAttempt = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        perAttempt.CancelAfter(timeout);
        var token = perAttempt.Token;

        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if (!resp.IsSuccessStatusCode) return null;

            using var stream = await resp.Content.ReadAsStreamAsync(token);
            using var buf = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (buf.Length + read > MaxBytes) return null;
                buf.Write(buffer, 0, read);
            }
            return "data:image/jpeg;base64," + Convert.ToBase64String(buf.ToArray());
        }
        catch
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run all `ArtFetcher` tests to verify they pass**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter ArtFetcherTests
```

Expected: 13 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Browser/ArtFetcher.cs tests/ArtFetcherTests.cs
git commit -m "ArtFetcher: cancel in-flight fetch when new URL arrives"
```

---

## Phase D — `IngestHandler` (TDD)

### Task D1: `IngestHandler` — valid POST updates state

**Files:**
- Create: `src/Browser/IngestHandler.cs`
- Create: `tests/IngestHandlerTests.cs`

`IngestHandler` operates on an `HttpListenerContext`, which is awkward to construct in tests. Instead, the handler is structured so the parse/apply logic is callable directly from tests — the HttpListener-binding glue is a thin shim.

- [ ] **Step 1: Write the failing test**

`tests/IngestHandlerTests.cs`:

```csharp
namespace TidalNowPlaying.Tests;

public class IngestHandlerTests
{
    [Fact]
    public async Task ApplyAsync_ValidBody_UpdatesStateAndRefreshesLastIngestAt()
    {
        var state = new State();
        var fetcher = new ArtFetcher(FakeHttpMessageHandler.AlwaysReturn(new byte[] { 1 }),
                                     retryDelays: new[] { TimeSpan.Zero });
        var clock = new TestClock(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));
        var handler = new IngestHandler(state, fetcher, clock);

        string body = """
            {"title":"Spaceman","artist":"Hardwell","album":"Spaceman",
             "is_playing":true,"art_url":""}
            """;

        var result = await handler.ApplyAsync(body);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("Spaceman", state.Current.Title);
        Assert.Equal("Hardwell", state.Current.Artist);
        Assert.True(state.Current.IsPlaying);
        Assert.Equal(clock.Now, state.LastIngestAt);
    }
}
```

- [ ] **Step 2: Add a `TestClock` shim**

Create `tests/TestClock.cs`:

```csharp
namespace TidalNowPlaying.Tests;

public class TestClock : TidalNowPlaying.IClock
{
    public DateTime Now { get; set; }
    public TestClock(DateTime initial) { Now = initial; }
    public DateTime UtcNow() => Now;
    public void Advance(TimeSpan d) => Now = Now.Add(d);
}
```

- [ ] **Step 3: Run test to verify it fails**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter IngestHandlerTests
```

Expected: build failure — `IngestHandler`, `IClock`, `IngestResult` don't exist.

- [ ] **Step 4: Add `IClock` interface**

Create `src/Browser/IClock.cs`:

```csharp
namespace TidalNowPlaying;

public interface IClock
{
    DateTime UtcNow();
}

public class SystemClock : IClock
{
    public DateTime UtcNow() => DateTime.UtcNow;
}
```

- [ ] **Step 5: Implement `IngestHandler`**

Create `src/Browser/IngestHandler.cs`:

```csharp
using System.Text.Json;

namespace TidalNowPlaying;

public record IngestResult(int StatusCode);

public class IngestHandler
{
    private const int MaxBodyBytes = 16 * 1024;

    private readonly State state;
    private readonly ArtFetcher fetcher;
    private readonly IClock clock;
    private string lastSeenArtUrl = "";

    public IngestHandler(State state, ArtFetcher fetcher, IClock clock)
    {
        this.state   = state;
        this.fetcher = fetcher;
        this.clock   = clock;
    }

    public async Task<IngestResult> ApplyAsync(string body)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
            return new IngestResult(413);

        IngestPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<IngestPayload>(body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        }
        catch
        {
            state.Clear();
            state.TouchHeartbeat(clock.UtcNow());
            return new IngestResult(400);
        }
        if (payload is null)
        {
            state.Clear();
            state.TouchHeartbeat(clock.UtcNow());
            return new IngestResult(400);
        }

        string newArtUrl = payload.ArtUrl ?? "";
        bool   artChanged = newArtUrl != lastSeenArtUrl;

        var info = new TrackInfo
        {
            Title     = payload.Title  ?? "",
            Artist    = payload.Artist ?? "",
            Album     = payload.Album  ?? "",
            IsPlaying = payload.IsPlaying,
            Art       = artChanged ? "" : state.Current.Art,
        };
        state.Update(info, clock.UtcNow());

        if (artChanged)
        {
            lastSeenArtUrl = newArtUrl;
            if (!string.IsNullOrEmpty(newArtUrl))
            {
                _ = Task.Run(async () =>
                {
                    string? art = await fetcher.FetchAsync(newArtUrl);
                    if (art != null) state.SetArt(art);
                });
            }
        }

        return new IngestResult(200);
    }

    private class IngestPayload
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public bool IsPlaying { get; set; }
        public string? ArtUrl { get; set; }
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter IngestHandlerTests
```

Expected: 1 passed.

- [ ] **Step 7: Commit**

```bash
git add src/Browser/IClock.cs src/Browser/IngestHandler.cs tests/IngestHandlerTests.cs tests/TestClock.cs
git commit -m "IngestHandler: parse + apply valid POST"
```

---

### Task D2: `IngestHandler` — malformed JSON clears state, returns 400

**Files:**
- Modify: `tests/IngestHandlerTests.cs`

- [ ] **Step 1: Add failing test**

Append to `IngestHandlerTests`:

```csharp
    [Fact]
    public async Task ApplyAsync_MalformedJson_ClearsStateAndReturns400()
    {
        var state = new State();
        // Pre-populate state with a real track
        state.Update(new TrackInfo { Title = "Old", Artist = "X", IsPlaying = true, Art = "data:..." },
                     new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc));

        var fetcher = new ArtFetcher(FakeHttpMessageHandler.AlwaysFail(),
                                     retryDelays: new[] { TimeSpan.Zero });
        var clock = new TestClock(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));
        var handler = new IngestHandler(state, fetcher, clock);

        var result = await handler.ApplyAsync("{not valid json");

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("", state.Current.Title);
        Assert.Equal("", state.Current.Artist);
        Assert.False(state.Current.IsPlaying);
        Assert.Equal(clock.Now, state.LastIngestAt);
    }
```

- [ ] **Step 2: Run test to verify it passes**

The Task D1 implementation already handles this case (the `catch` block). Run:

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter IngestHandlerTests
```

Expected: 2 passed. (No new code needed — verifying the D1 impl is correct.)

- [ ] **Step 3: Commit**

```bash
git add tests/IngestHandlerTests.cs
git commit -m "IngestHandler: test malformed JSON clears state"
```

---

### Task D3: `IngestHandler` — body > 16 KB returns 413, no state change

**Files:**
- Modify: `tests/IngestHandlerTests.cs`

- [ ] **Step 1: Add failing test**

Append to `IngestHandlerTests`:

```csharp
    [Fact]
    public async Task ApplyAsync_BodyExceeds16KB_Returns413AndDoesNotTouchState()
    {
        var state = new State();
        var initialTime = new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc);
        state.Update(new TrackInfo { Title = "Untouched" }, initialTime);

        var fetcher = new ArtFetcher(FakeHttpMessageHandler.AlwaysFail(),
                                     retryDelays: new[] { TimeSpan.Zero });
        var clock = new TestClock(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));
        var handler = new IngestHandler(state, fetcher, clock);

        string huge = new string('x', 17 * 1024); // 17 KB > 16 KB cap

        var result = await handler.ApplyAsync(huge);

        Assert.Equal(413, result.StatusCode);
        Assert.Equal("Untouched", state.Current.Title);
        Assert.Equal(initialTime, state.LastIngestAt);
    }
```

- [ ] **Step 2: Run test to verify it passes**

The D1 impl already returns 413 for oversize bodies and doesn't touch state on that branch. Verify:

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter IngestHandlerTests
```

Expected: 3 passed.

- [ ] **Step 3: Commit**

```bash
git add tests/IngestHandlerTests.cs
git commit -m "IngestHandler: test 16 KB body cap"
```

---

### Task D4: `IngestHandler` — art_url unchanged → no refetch

**Files:**
- Modify: `tests/IngestHandlerTests.cs`

- [ ] **Step 1: Add failing test**

Append to `IngestHandlerTests`:

```csharp
    [Fact]
    public async Task ApplyAsync_SameArtUrlAcrossPosts_DoesNotRefetch()
    {
        var state = new State();
        var fakeHandler = FakeHttpMessageHandler.AlwaysReturn(new byte[] { 1, 2, 3 });
        var fetcher = new ArtFetcher(fakeHandler, retryDelays: new[] { TimeSpan.Zero });
        var clock = new TestClock(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));
        var handler = new IngestHandler(state, fetcher, clock);

        string body = """
            {"title":"A","artist":"B","album":"","is_playing":true,
             "art_url":"https://example.com/art.jpg"}
            """;

        await handler.ApplyAsync(body);
        await Task.Delay(100); // let async fetch resolve
        await handler.ApplyAsync(body);
        await Task.Delay(100);

        Assert.Single(fakeHandler.Calls);
    }
```

- [ ] **Step 2: Run test to verify it passes**

The D1 impl tracks `lastSeenArtUrl` per-handler, so identical art_url across POSTs skips the refetch. ArtFetcher's cache also catches it. Verify:

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter IngestHandlerTests
```

Expected: 4 passed.

- [ ] **Step 3: Commit**

```bash
git add tests/IngestHandlerTests.cs
git commit -m "IngestHandler: test unchanged art_url skips refetch"
```

---

### Task D5: `IngestHandler` — art_url changed → `current.Art` cleared immediately

**Files:**
- Modify: `tests/IngestHandlerTests.cs`

- [ ] **Step 1: Add failing test**

Append to `IngestHandlerTests`:

```csharp
    [Fact]
    public async Task ApplyAsync_ArtUrlChanged_ClearsCurrentArtImmediately()
    {
        var state = new State();
        // Pre-populate state with a current track and stale art
        state.Update(new TrackInfo { Title = "Old", Art = "data:image/jpeg;base64,STALE" },
                     new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc));

        // Slow fetcher so the new art doesn't populate during the assertion
        var fetcher = new ArtFetcher(FakeHttpMessageHandler.Slow(TimeSpan.FromSeconds(10)),
                                     timeout: TimeSpan.FromSeconds(30),
                                     retryDelays: new[] { TimeSpan.Zero });
        var clock = new TestClock(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));
        var handler = new IngestHandler(state, fetcher, clock);

        string body = """
            {"title":"New","artist":"X","album":"","is_playing":true,
             "art_url":"https://example.com/different.jpg"}
            """;

        await handler.ApplyAsync(body);

        Assert.Equal("New", state.Current.Title);
        Assert.Equal("", state.Current.Art);
    }
```

- [ ] **Step 2: Run test to verify it passes**

The D1 impl sets `Art = ""` synchronously when `artChanged`. Verify:

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter IngestHandlerTests
```

Expected: 5 passed.

- [ ] **Step 3: Commit**

```bash
git add tests/IngestHandlerTests.cs
git commit -m "IngestHandler: test art_url change clears art immediately"
```

---

## Phase E — `HeartbeatHandler` (TDD)

### Task E1: `HeartbeatHandler` — empty body refreshes timestamp

**Files:**
- Create: `src/Browser/HeartbeatHandler.cs`
- Create: `tests/HeartbeatHandlerTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/HeartbeatHandlerTests.cs`:

```csharp
namespace TidalNowPlaying.Tests;

public class HeartbeatHandlerTests
{
    [Fact]
    public void Apply_EmptyBody_RefreshesLastIngestAtAndReturns204()
    {
        var state = new State();
        var prev = new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc);
        state.Update(new TrackInfo { Title = "X" }, prev);

        var clock = new TestClock(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));
        var handler = new HeartbeatHandler(state, clock);

        var result = handler.Apply(bodyLength: 0);

        Assert.Equal(204, result.StatusCode);
        Assert.Equal(clock.Now, state.LastIngestAt);
        Assert.Equal("X", state.Current.Title); // current unchanged
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter HeartbeatHandlerTests
```

Expected: build error — `HeartbeatHandler` doesn't exist.

- [ ] **Step 3: Implement `HeartbeatHandler`**

`src/Browser/HeartbeatHandler.cs`:

```csharp
namespace TidalNowPlaying;

public record HeartbeatResult(int StatusCode);

public class HeartbeatHandler
{
    private readonly State state;
    private readonly IClock clock;

    public HeartbeatHandler(State state, IClock clock)
    {
        this.state = state;
        this.clock = clock;
    }

    public HeartbeatResult Apply(int bodyLength)
    {
        if (bodyLength != 0) return new HeartbeatResult(400);
        state.TouchHeartbeat(clock.UtcNow());
        return new HeartbeatResult(204);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter HeartbeatHandlerTests
```

Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Browser/HeartbeatHandler.cs tests/HeartbeatHandlerTests.cs
git commit -m "HeartbeatHandler: empty body refreshes timestamp"
```

---

### Task E2: `HeartbeatHandler` — non-empty body rejected

**Files:**
- Modify: `tests/HeartbeatHandlerTests.cs`

- [ ] **Step 1: Add failing test**

Append to `HeartbeatHandlerTests`:

```csharp
    [Fact]
    public void Apply_NonEmptyBody_Returns400AndDoesNotRefresh()
    {
        var state = new State();
        var prev = new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc);
        state.Update(new TrackInfo { Title = "X" }, prev);

        var clock = new TestClock(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));
        var handler = new HeartbeatHandler(state, clock);

        var result = handler.Apply(bodyLength: 42);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal(prev, state.LastIngestAt); // not refreshed
        Assert.Equal("X", state.Current.Title);
    }
```

- [ ] **Step 2: Run test to verify it passes**

The E1 implementation already returns 400 for non-zero body. Verify:

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter HeartbeatHandlerTests
```

Expected: 2 passed.

- [ ] **Step 3: Commit**

```bash
git add tests/HeartbeatHandlerTests.cs
git commit -m "HeartbeatHandler: reject non-empty body"
```

---

## Phase F — Idle timeout (TDD)

### Task F1: `IdleTimer` clears `current` after 10s of no POSTs

**Files:**
- Create: `src/Browser/IdleTimer.cs`
- Create: `tests/IdleTimerTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/IdleTimerTests.cs`:

```csharp
namespace TidalNowPlaying.Tests;

public class IdleTimerTests
{
    [Fact]
    public void Tick_ElapsedExceedsTimeout_ClearsCurrent()
    {
        var state = new State();
        var t0 = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        state.Update(new TrackInfo { Title = "X", IsPlaying = true }, t0);

        var clock = new TestClock(t0);
        var timer = new IdleTimer(state, clock, TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(11));
        timer.Tick();

        Assert.Equal("", state.Current.Title);
        Assert.False(state.Current.IsPlaying);
    }

    [Fact]
    public void Tick_ElapsedWithinTimeout_LeavesCurrentAlone()
    {
        var state = new State();
        var t0 = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        state.Update(new TrackInfo { Title = "X", IsPlaying = true }, t0);

        var clock = new TestClock(t0);
        var timer = new IdleTimer(state, clock, TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(5));
        timer.Tick();

        Assert.Equal("X", state.Current.Title);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter IdleTimerTests
```

Expected: build error — `IdleTimer` doesn't exist.

- [ ] **Step 3: Implement `IdleTimer`**

`src/Browser/IdleTimer.cs`:

```csharp
namespace TidalNowPlaying;

public class IdleTimer
{
    private readonly State state;
    private readonly IClock clock;
    private readonly TimeSpan timeout;

    public IdleTimer(State state, IClock clock, TimeSpan timeout)
    {
        this.state   = state;
        this.clock   = clock;
        this.timeout = timeout;
    }

    public void Tick()
    {
        if (clock.UtcNow() - state.LastIngestAt > timeout)
        {
            state.Clear();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }
            Tick();
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter IdleTimerTests
```

Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Browser/IdleTimer.cs tests/IdleTimerTests.cs
git commit -m "IdleTimer: clear current when no ingest in 10s"
```

---

## Phase G — `WidgetServer` tests (CORS preflight + JSON shape)

### Task G1: `WidgetServer` — `/now-playing` JSON shape test

**Files:**
- Create: `tests/WidgetServerTests.cs`

Tests start a real `HttpListener` on a random port and assert against actual responses. This is the cleanest way to test the routing without mocking `HttpListenerContext`.

- [ ] **Step 1: Write the failing test**

`tests/WidgetServerTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it passes**

The A2 implementation of `WidgetServer` already serves `/now-playing` correctly. Verify:

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter WidgetServerTests
```

Expected: 1 passed.

- [ ] **Step 3: Commit**

```bash
git add tests/WidgetServerTests.cs
git commit -m "WidgetServer: test /now-playing JSON shape"
```

---

### Task G2: `WidgetServer` — `OPTIONS /ingest` preflight returns expected headers

**Files:**
- Modify: `tests/WidgetServerTests.cs`

- [ ] **Step 1: Add failing test**

Append to `WidgetServerTests`:

```csharp
    [Theory]
    [InlineData("/ingest")]
    [InlineData("/heartbeat")]
    public async Task OptionsPreflight_ReturnsCorsHeadersAnd204(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Options, path);
        var resp = await http.SendAsync(req);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("*",                  resp.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("POST, OPTIONS",      resp.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Equal("Content-Type",       resp.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.Equal("600",                resp.Headers.GetValues("Access-Control-Max-Age").Single());
    }
```

- [ ] **Step 2: Run test to verify it passes**

`WidgetServer.HandleRequest` already routes OPTIONS preflight for `/ingest` and `/heartbeat` (Task A2). Verify:

```bash
dotnet test tests/TidalNowPlaying.Browser.Tests.csproj --filter WidgetServerTests
```

Expected: 3 passed (1 from G1 + 2 from G2's Theory).

- [ ] **Step 3: Commit**

```bash
git add tests/WidgetServerTests.cs
git commit -m "WidgetServer: test OPTIONS preflight on POST endpoints"
```

---

## Phase H — Wire up Browser/Program.cs

### Task H1: Replace stub `Browser/Program.cs` with the real wire-up

**Files:**
- Modify: `src/Browser/Program.cs`

- [ ] **Step 1: Replace the stub**

```csharp
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
```

- [ ] **Step 2: Build to verify it compiles**

```bash
dotnet build src/Browser/TidalNowPlaying.Browser.csproj
```

Expected: `Build succeeded.`

- [ ] **Step 3: Smoke-test the server locally on Mac**

```bash
dotnet run --project src/Browser/TidalNowPlaying.Browser.csproj
```

In another terminal:

```bash
curl -s http://127.0.0.1:8765/now-playing
```

Expected: `{"title":"","artist":"","album":"","is_playing":false,"art":""}` (empty state — nothing has POSTed to `/ingest`).

```bash
curl -i -X OPTIONS http://127.0.0.1:8765/ingest
```

Expected: `HTTP/1.1 204 No Content` with `Access-Control-Allow-*` headers.

```bash
curl -i -X POST -H "Content-Type: application/json" \
  -d '{"title":"Test","artist":"A","album":"B","is_playing":true,"art_url":""}' \
  http://127.0.0.1:8765/ingest
```

Expected: `HTTP/1.1 200 OK`. The server console should print `◀  A – Test [playing]`.

```bash
curl -s http://127.0.0.1:8765/now-playing
```

Expected: `{"title":"Test","artist":"A","album":"B","is_playing":true,"art":""}`.

Stop the server with Ctrl-C.

- [ ] **Step 4: Commit**

```bash
git add src/Browser/Program.cs
git commit -m "wire up Browser server: ingest + heartbeat + idle + art fetch"
```

---

## Phase I — Chrome extension

### Task I1: Create `extension/manifest.json`

**Files:**
- Create: `extension/manifest.json`

- [ ] **Step 1: Write the manifest**

```json
{
  "manifest_version": 3,
  "name": "Tidal Now Playing",
  "version": "0.1.0",
  "description": "Forwards Tidal web playback state to the local Tidal Now Playing companion server.",
  "icons": {
    "16": "icons/icon16.png",
    "48": "icons/icon48.png",
    "128": "icons/icon128.png"
  },
  "host_permissions": [
    "http://127.0.0.1/*"
  ],
  "content_scripts": [
    {
      "matches": [
        "https://listen.tidal.com/*",
        "https://tidal.com/*"
      ],
      "js": ["content.js"],
      "run_at": "document_idle",
      "all_frames": false
    }
  ]
}
```

- [ ] **Step 2: Commit**

```bash
git add extension/manifest.json
git commit -m "extension: MV3 manifest (Tidal hosts + localhost permission)"
```

---

### Task I2: Create placeholder icons

**Files:**
- Create: `extension/icons/icon16.png`
- Create: `extension/icons/icon48.png`
- Create: `extension/icons/icon128.png`

- [ ] **Step 1: Generate solid-color PNG placeholders via ImageMagick**

```bash
mkdir -p extension/icons
for size in 16 48 128; do
  magick -size ${size}x${size} xc:'#0d1216' "extension/icons/icon${size}.png" \
    || convert -size ${size}x${size} xc:'#0d1216' "extension/icons/icon${size}.png"
done
ls -la extension/icons/
```

Expected: three `icon{16,48,128}.png` files, ~100-500 bytes each.

If ImageMagick isn't installed and the user prefers, fetch any 1×1-extended dark PNG, or check in a known dark-square PNG from elsewhere. The point is that v1 icons exist; design polish is out of scope.

- [ ] **Step 2: Commit**

```bash
git add extension/icons/
git commit -m "extension: placeholder icons (16/48/128)"
```

---

### Task I3: Create `extension/content.js`

**Files:**
- Create: `extension/content.js`

- [ ] **Step 1: Write the content script**

```javascript
// Tidal Now Playing — content script.
// Reads navigator.mediaSession.metadata + the page's <audio> element, then:
//   - POSTs /ingest when state changes
//   - POSTs /heartbeat every 5 s
//   - sendBeacon /ingest cleared on pagehide

const SERVER = 'http://127.0.0.1:8765';
const POLL_MS = 500;
const HEARTBEAT_MS = 5000;

let lastSent = { state: 'none', title: '', artist: '', album: '', isPlaying: false, artUrl: '' };

function findAudio() {
  return document.querySelector('audio');
}

function pickArtUrl(metadata) {
  if (!metadata || !metadata.artwork || metadata.artwork.length === 0) return '';
  // Prefer the largest artwork available.
  const sorted = [...metadata.artwork].sort((a, b) => {
    const sa = parseInt((a.sizes || '0x0').split('x')[0], 10) || 0;
    const sb = parseInt((b.sizes || '0x0').split('x')[0], 10) || 0;
    return sb - sa;
  });
  return sorted[0].src || '';
}

function snapshot() {
  try {
    const md = navigator.mediaSession && navigator.mediaSession.metadata;
    const audio = findAudio();
    const audioReady = !!(audio && audio.src);
    if (!md || !audioReady) {
      return { state: 'none', title: '', artist: '', album: '', isPlaying: false, artUrl: '' };
    }
    return {
      state: 'track',
      title: md.title || '',
      artist: md.artist || '',
      album: md.album || '',
      isPlaying: !!(audio && !audio.paused),
      artUrl: pickArtUrl(md),
    };
  } catch (e) {
    console.warn('[TidalNowPlaying] snapshot error:', e);
    return { state: 'none', title: '', artist: '', album: '', isPlaying: false, artUrl: '' };
  }
}

function snapshotsDiffer(a, b) {
  return a.state !== b.state
      || a.title !== b.title
      || a.artist !== b.artist
      || a.album !== b.album
      || a.isPlaying !== b.isPlaying
      || a.artUrl !== b.artUrl;
}

function payloadFromSnapshot(s) {
  return {
    title: s.title,
    artist: s.artist,
    album: s.album,
    is_playing: s.isPlaying,
    art_url: s.artUrl,
  };
}

const CLEARED_PAYLOAD = { title: '', artist: '', album: '', is_playing: false, art_url: '' };

function postIngest(payload) {
  fetch(`${SERVER}/ingest`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
    keepalive: false,
  }).catch(() => { /* silently ignore; next event or heartbeat retries */ });
}

function postHeartbeat() {
  fetch(`${SERVER}/heartbeat`, { method: 'POST' }).catch(() => { /* ignore */ });
}

function tick() {
  const cur = snapshot();

  if (cur.state === 'track') {
    if (snapshotsDiffer(cur, lastSent)) {
      postIngest(payloadFromSnapshot(cur));
      lastSent = cur;
    }
  } else {
    if (lastSent.state === 'track') {
      postIngest(CLEARED_PAYLOAD);
      lastSent = cur;
    }
  }
}

// Polling loop.
setInterval(tick, POLL_MS);

// Heartbeat loop.
setInterval(postHeartbeat, HEARTBEAT_MS);

// React faster to play/pause by recomputing on audio events.
function attachAudioListeners(audio) {
  if (!audio || audio._tidalListenersAttached) return;
  audio._tidalListenersAttached = true;
  audio.addEventListener('play', tick);
  audio.addEventListener('pause', tick);
}
const audioObserver = new MutationObserver(() => attachAudioListeners(findAudio()));
audioObserver.observe(document.documentElement, { childList: true, subtree: true });
attachAudioListeners(findAudio());

// Send a cleared ingest on tab close / navigation away.
window.addEventListener('pagehide', () => {
  navigator.sendBeacon(
    `${SERVER}/ingest`,
    new Blob([JSON.stringify(CLEARED_PAYLOAD)], { type: 'application/json' })
  );
});

console.log('[TidalNowPlaying] content script loaded');
```

- [ ] **Step 2: Commit**

```bash
git add extension/content.js
git commit -m "extension: content script (polling, heartbeat, pagehide clear)"
```

---

### Task I4: Manual E2E test against running server

This is a manual checklist; no automation.

- [ ] **Step 1: Start the server**

```bash
dotnet run --project src/Browser/TidalNowPlaying.Browser.csproj
```

Leave it running. Note the server's console.

- [ ] **Step 2: Load the extension in Chrome**

1. Open `chrome://extensions`.
2. Enable **Developer mode** (top right).
3. Click **Load unpacked** → select the `extension/` folder in this repo.
4. Verify the extension appears with no manifest errors.

- [ ] **Step 3: Open the widget**

In a new tab, open `http://127.0.0.1:8765/widget.html`. It should render blank (no track yet).

- [ ] **Step 4: Open Tidal and start playing**

1. Navigate to `https://listen.tidal.com` (log in if needed).
2. Open the DevTools Console in the Tidal tab; you should see `[TidalNowPlaying] content script loaded`.
3. Play any track.

Expected outcomes:
- Server console prints `◀  <Artist> – <Title> [playing]` within ~1 second.
- The widget tab populates with title/artist/album and album art.

- [ ] **Step 5: Pause / play**

Pause the track in Tidal.

Expected: widget switches to the paused state (no equalizer animation) within ~0.5s. Server console may print an updated line.

Resume play. Widget animates again.

- [ ] **Step 6: Skip track**

Click "Next" in Tidal.

Expected: widget art briefly clears (server clears `current.Art` on URL change), then re-populates with the new track's art within a few hundred ms. No mismatched art ever shown.

- [ ] **Step 7: Player teardown (metadata populated → null)**

In the Tidal tab, navigate to a non-player page within Tidal (e.g., your library settings) so the audio element unmounts.

Expected: widget hides immediately (server received cleared `/ingest`).

- [ ] **Step 8: Close the Tidal tab**

Close the tab.

Expected: widget hides immediately (`pagehide` → `sendBeacon` fired the cleared `/ingest`).

- [ ] **Step 9: Idle timeout (kill-9 simulation)**

Restart Tidal playback, then disable the extension at `chrome://extensions` mid-track.

Expected: widget remains showing the current track for ~10s after disabling, then hides (idle timeout kicked in).

- [ ] **Step 10: Re-enable for cleanup**

Re-enable the extension. Stop the server with Ctrl-C.

- [ ] **Step 11: No commit needed**

This is purely a verification step. If any of steps 4-9 misbehave, file a follow-up before continuing.

---

## Phase J — CI workflow updates

### Task J1: Add `test-browser` job

**Files:**
- Modify: `.github/workflows/build.yml`

- [ ] **Step 1: Add the job at the end of the workflow (before the existing `build` job)**

Append the following job to `.github/workflows/build.yml` (keeping the existing `build` matrix job intact):

```yaml
  test-browser:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
      - uses: actions/setup-dotnet@v5
        with:
          dotnet-version: '8.0.x'
      - name: Restore + test
        run: dotnet test tests/TidalNowPlaying.Browser.Tests.csproj -c Release --logger "console;verbosity=normal"
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/build.yml
git commit -m "ci: add test-browser job"
```

---

### Task J2: Add `build-browser-server` job

**Files:**
- Modify: `.github/workflows/build.yml`

- [ ] **Step 1: Append the matrix job**

```yaml
  build-browser-server:
    runs-on: ubuntu-latest
    needs: [test-browser]
    strategy:
      matrix:
        rid: [win-x64, osx-arm64, linux-x64]
    steps:
      - uses: actions/checkout@v6
      - uses: actions/setup-dotnet@v5
        with:
          dotnet-version: '8.0.x'
      - name: Publish ${{ matrix.rid }}
        run: |
          dotnet publish src/Browser/TidalNowPlaying.Browser.csproj \
            -c Release -r ${{ matrix.rid }} \
            --self-contained true -p:PublishSingleFile=true \
            -o publish/browser/${{ matrix.rid }}
      - name: Bundle widget.html
        run: cp widget.html publish/browser/${{ matrix.rid }}/widget.html
      - uses: actions/upload-artifact@v5
        with:
          name: TidalNowPlaying-Browser-${{ matrix.rid }}
          path: publish/browser/${{ matrix.rid }}/
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/build.yml
git commit -m "ci: add build-browser-server matrix (win/mac/linux)"
```

---

### Task J3: Add `package-extension` job

**Files:**
- Modify: `.github/workflows/build.yml`

- [ ] **Step 1: Append the job**

```yaml
  package-extension:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
      - name: Zip extension
        run: cd extension && zip -r ../tidal-extension.zip .
      - uses: actions/upload-artifact@v5
        with:
          name: tidal-extension
          path: tidal-extension.zip
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/build.yml
git commit -m "ci: add package-extension job"
```

---

### Task J4: Add `release` job (rolling latest, per-pass, concurrency-controlled)

**Files:**
- Modify: `.github/workflows/build.yml`

- [ ] **Step 1: Append the release job**

```yaml
  release:
    runs-on: ubuntu-latest
    needs: [build, build-browser-server, package-extension]
    if: always()
    concurrency:
      group: release-${{ github.ref }}
      cancel-in-progress: true
    permissions:
      contents: write
    steps:
      - uses: actions/download-artifact@v5
        with: { path: artifacts }
      - name: Stage release zips
        run: |
          mkdir -p release
          for dir in artifacts/*/; do
            name=$(basename "$dir")
            if [ "$name" = "tidal-extension" ]; then
              cp "$dir"*.zip "release/${name}.zip"
            else
              case "$name" in
                *Browser-osx-*|*Browser-linux-*)
                  find "$dir" -maxdepth 1 -type f ! -name '*.html' -exec chmod +x {} \;
                  ;;
              esac
              (cd "$dir" && zip -r "../../release/${name}.zip" .)
            fi
          done
          ls -la release/
      - uses: softprops/action-gh-release@v3
        with:
          tag_name: latest
          name: Latest build
          prerelease: false
          make_latest: true
          files: release/*.zip
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/build.yml
git commit -m "ci: add rolling 'latest' release job"
```

- [ ] **Step 3: Push and verify CI**

```bash
git push
```

Open GitHub Actions. Wait for the full pipeline:
- `build` matrix (existing): all three SMTC artifacts pass.
- `test-browser`: green, all unit tests pass.
- `build-browser-server` matrix: all three RIDs pass.
- `package-extension`: green, zip uploaded.
- `release`: green; check the Releases tab — `latest` release should now contain 7 zips.

Download one of the browser server zips for your OS (`TidalNowPlaying-Browser-osx-arm64.zip` if you're on Apple Silicon), unzip, and confirm the binary has the executable bit set (`ls -l` should show `-rwxr-xr-x` or similar).

If anything fails, **stop and diagnose** before Phase K.

---

## Phase K — Documentation

### Task K1: Update `README.md` for the browser path

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Append a new section before the "Building from source" section**

After the existing "Endpoints" section, insert:

```markdown
## Browser source (alternative to SMTC)

If you listen to Tidal in a browser tab instead of the Windows desktop app, use the browser-source build:

1. From the latest GitHub Release, download:
   - `TidalNowPlaying-Browser-<your-platform>.zip` — server binary + `widget.html`. Available for `win-x64`, `osx-arm64`, and `linux-x64`.
   - `tidal-extension.zip` — the Chrome MV3 extension.
2. Unzip the server build. Run the executable (`TidalNowPlaying-Browser` or `TidalNowPlaying-Browser.exe`). Keep it running while streaming.
3. Unzip the extension. In Chrome (or any Chromium browser), open `chrome://extensions`, enable Developer mode, click **Load unpacked**, and select the unzipped `extension/` folder.
4. Configure OBS the same way as the SMTC build: a Browser Source pointing at `http://127.0.0.1:8765/widget.html`, width 420, height 110.
5. Play something in your `listen.tidal.com` tab — the widget appears automatically.

Direct release download URLs (always point at the latest main build):
- https://github.com/<owner>/<repo>/releases/latest/download/TidalNowPlaying-Browser-win-x64.zip
- https://github.com/<owner>/<repo>/releases/latest/download/TidalNowPlaying-Browser-osx-arm64.zip
- https://github.com/<owner>/<repo>/releases/latest/download/TidalNowPlaying-Browser-linux-x64.zip
- https://github.com/<owner>/<repo>/releases/latest/download/tidal-extension.zip

Known limitations:
- Single Tidal tab at a time. If multiple Tidal tabs are playing, the server shows the most recent update.
- macOS/Linux binaries are not code-signed — first-launch warnings may appear; `xattr -d com.apple.quarantine TidalNowPlaying-Browser` on macOS if Gatekeeper blocks it.
```

- [ ] **Step 2: Update the "Building from source" section to point at releases**

Replace the existing "Building from source" section with:

```markdown
## Building from source

Push to GitHub and the Actions workflow builds all variants automatically. The `latest` GitHub Release is updated on every push to `main` with seven artifacts:

- `TidalNowPlaying.zip`, `SpotifyNowPlaying.zip`, `NowPlaying.zip` — existing Windows + SMTC builds.
- `TidalNowPlaying-Browser-win-x64.zip`, `TidalNowPlaying-Browser-osx-arm64.zip`, `TidalNowPlaying-Browser-linux-x64.zip` — cross-platform browser-source server builds.
- `tidal-extension.zip` — the Chrome MV3 extension.

Each release file is a self-contained zip — unzip and run.
```

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: add browser-source install + new release URLs to README"
```

---

## Self-Review Checklist

After implementing, sanity-check the plan against the spec:

- [ ] Both products in repo: existing Windows SMTC build untouched in behavior; new `TidalNowPlaying.Browser` exists. (Phase A + Phase H)
- [ ] `Shared/TrackInfo.cs` and `Shared/WidgetServer.cs` exist and are compile-included by both csprojs. (A1, A2, B1)
- [ ] Existing csproj has `<Compile Remove="Browser/**/*.cs" />`. (A4)
- [ ] Existing csproj's `<RuntimeIdentifier>` dropped; CLI passes `-r win-x64`. (A4)
- [ ] Server handles `OPTIONS /ingest` and `OPTIONS /heartbeat` with documented CORS headers. (A2, G2)
- [ ] `POST /ingest` body cap = 16 KB (returns 413). (D3)
- [ ] Malformed `/ingest` body clears state, refreshes lastIngestAt, returns 400. (D2)
- [ ] `POST /heartbeat` empty body → 204, refreshes lastIngestAt only. Non-empty → 400, no refresh. (E1, E2)
- [ ] `ArtFetcher` rejects non-https URLs. (C3)
- [ ] `ArtFetcher` enforces 5 MB cap and 5 s timeout. (C4, C5)
- [ ] `ArtFetcher` retries on 250 ms / 1 s / 3 s schedule. (C6)
- [ ] `ArtFetcher` single-entry URL cache. (C7)
- [ ] `ArtFetcher` cancels in-flight fetch when new URL arrives. (C8)
- [ ] Idle timeout (10 s) clears `current` to empty TrackInfo. (F1)
- [ ] Extension manifest is MV3, has `host_permissions: ["http://127.0.0.1/*"]`, content scripts match `tidal.com`/`listen.tidal.com`. (I1)
- [ ] Content script handles populated→null transition (POSTs cleared) and pagehide (sendBeacon). (I3)
- [ ] No `service_worker.js` in the extension. (I1, I3)
- [ ] CI: `test-browser`, `build-browser-server`, `package-extension`, `release` jobs all present. (J1-J4)
- [ ] Release job has `if: always()`, `concurrency`, chmod for non-Windows binaries. (J4)
- [ ] README updated. (K1)

---

## Out of scope (deferred to future iterations)

These are explicitly listed in the spec as non-goals:

- Track position / duration / progress-bar data.
- Spotify Web / YouTube Music / generic mediaSession extensions.
- Chrome Web Store publication.
- Firefox / Safari ports.
- Multi-tab deduplication.
- Authentication on `/ingest` (localhost-only is the chosen threat model).
- Host allowlist for art_url (only scheme + size + timeout hardening was selected).
