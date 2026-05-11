# Browser Extension Source — Design

**Date:** 2026-05-10
**Status:** Approved (pending spec-file review)

## Summary

Add a second data-source path to the Tidal Now Playing widget: a Chrome (Manifest V3) extension that scrapes a Tidal web tab and pushes Now-Playing state to a new cross-platform companion server. The existing Windows + SMTC path is preserved unchanged. Both paths serve the same `widget.html` and the same `/now-playing` JSON, so the OBS Browser Source setup is identical for either deployment.

## Goals

- Let users who play Tidal in a browser tab (rather than the Tidal desktop app on Windows) drive the same OBS overlay.
- Ship a cross-platform server binary (Win/Mac/Linux) for the new path, so the existing Windows-only constraint doesn't apply.
- Keep the existing SMTC build path untouched. No regressions to today's behavior.
- Keep `widget.html` and the `/now-playing` JSON contract identical between the two paths, so OBS setup, styling, and end-user experience are the same regardless of source.

## Non-goals

- Track position / progress-bar data. The two paths stay at parity with today's fields (`title`, `artist`, `album`, `is_playing`, `art`). Position is a future extension point — see *Future considerations*.
- Support for multiple browser-based music services in v1 (no Spotify Web / YT Music / generic mediaSession variants). Tidal-only.
- Authentication on the local HTTP endpoints. Localhost-bound is sufficient for the threat model.
- Publishing the extension to the Chrome Web Store. Load-unpacked from a release zip is the distribution model.
- Firefox / Safari support. Chromium-only (Chrome, Edge, Brave, etc.).
- Multi-tab deduplication. If two Tidal tabs are open and playing, last write wins; this is documented as a known limitation.

## Architecture

```
┌────────────────────────────────────────────────┐
│  Chrome tab (tidal.com)                        │
│  ┌──────────────────────────────────────────┐  │
│  │ content.js                               │  │
│  │  - reads navigator.mediaSession          │  │
│  │  - delegates network I/O to SW via       │  │
│  │    chrome.runtime.sendMessage(           │  │
│  │      {type: 'ingest'|'heartbeat', …})    │  │
│  └──────────────────────────────────────────┘  │
└──────────────────────┬─────────────────────────┘
                       │ runtime.sendMessage
                       ▼
┌────────────────────────────────────────────────┐
│  service_worker.js (chrome-extension:// origin)│
│  - host_permissions: http://127.0.0.1/*        │
│  - performs the actual fetches; SW origin is   │
│    exempt from Private Network Access checks   │
└──────────────────────┬─────────────────────────┘
                       │ fetch (POST)
                       ▼
   ┌────────────────────────────────────────┐
   │ TidalNowPlaying.Browser                │
   │  HttpListener 127.0.0.1 + localhost    │
   │   /ingest    (POST + OPTIONS preflight │
   │              with Allow-Private-Network)│
   │   /heartbeat (POST + OPTIONS preflight)│
   │   /now-playing (GET, JSON)             │
   │   /widget.html (GET)                   │
   │                                        │
   │  - holds current TrackInfo             │
   │  - fetches art URL (https, capped      │
   │    size, timeout) → base64 → in-memory │
   │  - 10s idle timeout clears current     │
   └─────────────────┬──────────────────────┘
                     │ GET /widget.html, /now-playing
                     ▼
   ┌────────────────────────────────────────┐
   │ OBS Browser Source                     │
   │  polls /now-playing                    │
   └────────────────────────────────────────┘
```

Two independent products in the same repo:

- **`TidalNowPlaying`** (existing) — Windows-only, reads SMTC. Untouched in behavior.
- **`TidalNowPlaying.Browser`** (new) — cross-platform (`net8.0`), receives state via HTTP POST from the extension. Same widget.html, same `/now-playing` JSON shape.

The two binaries are not designed to run simultaneously on the same host. Users pick the build that matches how they listen to Tidal.

## Components & Repo Layout

```
tidal_widget/
├── widget.html                          (unchanged — both builds serve this)
├── README.md
├── .github/workflows/
│   └── build.yml                        (existing matrix kept; new jobs added)
│
├── docs/superpowers/specs/
│   └── 2026-05-10-browser-extension-source-design.md   (this doc)
│
├── src/
│   ├── Shared/                          (compile-included by both csprojs)
│   │   ├── TrackInfo.cs                 (record — extracted from Program.cs)
│   │   └── WidgetServer.cs              (HttpListener + /now-playing + /widget.html)
│   │
│   ├── TidalNowPlaying.csproj           (existing — net8.0-windows10.0.19041.0)
│   ├── Program.cs                       (slimmed — wires SmtcPoller + WidgetServer)
│   ├── SmtcPoller.cs                    (extracted polling logic from current Program.cs)
│   ├── Config.cs                        (existing, build-time generated)
│   │
│   └── Browser/
│       ├── TidalNowPlaying.Browser.csproj   (new — plain net8.0)
│       ├── Program.cs                       (wires Ingest + ArtFetcher + WidgetServer + idle timer)
│       ├── IngestHandler.cs                 (POST /ingest → updates current TrackInfo)
│       ├── HeartbeatHandler.cs              (POST /heartbeat → refreshes lastIngestAt)
│       └── ArtFetcher.cs                    (URL → bytes → base64; retry + cache)
│
└── extension/
    ├── manifest.json                    (MV3, host_permissions: http://127.0.0.1/*, background.service_worker)
    ├── content.js                       (reads mediaSession; sends runtime messages to SW; pagehide → cleared message)
    ├── service_worker.js                (receives messages; performs the actual fetches from extension origin)
    └── icons/                           (16/48/128 png — placeholder ok for v1)
```

### Responsibility split

- **`Shared/WidgetServer.cs`** — owns the HttpListener loop and the two read-only routes (`/now-playing`, `/widget.html`). Both binaries reuse it so the widget URL and JSON contract cannot drift.
- **`Shared/TrackInfo.cs`** — shared record. Same JSON serialization for both paths.
- **Windows side** — `Program.cs` becomes a tiny wire-up file; SMTC polling moves into `SmtcPoller.cs`. Outward behavior identical to today.
- **Browser side** — `Program.cs` wires up the ingest + heartbeat handlers, the `ArtFetcher`, the `WidgetServer`, and the 10-second idle timer. Handlers are separated by responsibility.
- **Extension `content.js`** — reads `navigator.mediaSession.metadata` on a 500 ms tick. Decides "track" vs "none" state, computes the payload, and asks the service worker to deliver it via `chrome.runtime.sendMessage`. Also runs a 5 s heartbeat timer and a `pagehide` handler (best-effort cleared message). Does **not** issue `fetch` directly — see `service_worker.js`.
- **Extension `service_worker.js`** — receives `{type: 'ingest', payload}` and `{type: 'heartbeat'}` messages and performs the actual `fetch()` to the local server. The SW runs in the extension's own origin (`chrome-extension://...`), where the manifest's `host_permissions: ["http://127.0.0.1/*"]` grants an exemption from Chrome's Private Network Access policy. Content-script fetches do **not** get that exemption (they run in the page's network context), which is why the SW is required even though it adds the MV3 service-worker lifecycle back into the design. The SW returns a response to each message via `return true; sendResponse(...)` so it stays alive until the fetch resolves. No `chrome.tabs.onRemoved` tab-tracking — the page's `pagehide` handler (best-effort) plus the server's 10 s idle timeout are sufficient.

## Data Flow & Contracts

### JSON contracts

`GET /now-playing` response — **unchanged from today**:

```json
{
  "title": "Spaceman",
  "artist": "Hardwell",
  "album": "Spaceman",
  "is_playing": true,
  "art": "data:image/jpeg;base64,/9j/4AAQ..."
}
```

`POST /ingest` request body — **new**:

```json
{
  "title": "Spaceman",
  "artist": "Hardwell",
  "album": "Spaceman",
  "is_playing": true,
  "art_url": "https://resources.tidal.com/.../640x640.jpg"
}
```

Field-name asymmetry (`art_url` in, `art` out) is deliberate: the server's job is to transform `art_url` → fetched bytes → base64 data URL → `art`. Keeps wire payload small and the responsibility explicit.

`POST /heartbeat` request body — **new, empty**:

Zero bytes. No JSON to parse. Returns `204 No Content`.

A "cleared" state (tab close, no track playing) is signaled via `POST /ingest` with all string fields empty and `is_playing: false`. It is *not* a separate endpoint.

### Extension → server triggers

`content.js` (runs on `listen.tidal.com` and `tidal.com`) maintains an in-memory `lastSentState` snapshot to decide when an `/ingest` message is warranted. It does **not** issue `fetch` directly — all outbound network I/O is delegated to `service_worker.js` via `chrome.runtime.sendMessage`. (See *Cross-origin / Private Network Access* below for the reason.)

On each 500 ms tick the content script computes the *current* state:

- `state = 'track'` if `navigator.mediaSession.metadata` is non-null. **The `<audio>` element is intentionally not consulted for this gate** — Tidal's player uses Media Source Extensions, so the `<audio>`/`<video>` element typically has no `src` attribute even when a track is loaded. `mediaSession.metadata` is the authoritative "track loaded" signal that Tidal sets via the MSE pipeline.
- `state = 'none'` otherwise.

Then:

- If `state == 'track'`: build the payload `{title, artist, album, is_playing, art_url}` from `mediaSession.metadata`. `is_playing` is derived from (in priority order) `navigator.mediaSession.playbackState`, then the audio element's `paused` flag if an audio/video element exists, then a default of `true`. If any field differs from `lastSentState`, send `{type: 'ingest', payload}` to the service worker and update `lastSentState`.
- If `state == 'none'` **and** `lastSentState` was `'track'` (metadata transitioned populated → null): send a cleared ingest message (all string fields empty, `is_playing: false`) and update `lastSentState` to `'none'`. Without this, the heartbeats would keep `lastIngestAt` fresh and the server's idle timeout would never fire — leaving the widget pinned to the last track when the user closes Tidal's player without closing the tab.
- If `state == 'none'` **and** `lastSentState` was already `'none'` (page just loaded, no track ever played): nothing sent.

Independently of the poll:

- `setInterval(5000)` → send `{type: 'heartbeat'}` to the service worker regardless of state.
- Audio element `play`/`pause` events (when an `<audio>` or `<video>` element is present) → trigger an out-of-band recompute so play/pause reacts faster than 500 ms.
- `window.addEventListener('pagehide', ...)` → send a cleared ingest message. This is **best-effort**: the message is async and may not be delivered before the page is fully torn down, especially for clean tab closes where the content script process dies quickly. In practice the 10 s server-side idle timeout is the reliable fallback (the widget hides ~10 s after the tab closes rather than immediately). Earlier drafts used `navigator.sendBeacon`, but sendBeacon to loopback is also subject to Private Network Access and gets blocked the same way as `fetch`, so the message-passing path is uniform.

All messages are fire-and-forget. Service worker failures (server down, network error) are silently ignored; the next event or heartbeat retries.

`service_worker.js` registers `chrome.runtime.onMessage` and, on each incoming message, does the actual `fetch()` against `http://127.0.0.1:8765/<endpoint>`. It returns `true` from the listener so MV3 keeps the SW alive until the fetch resolves and `sendResponse` is called. No state is held in the SW (the content script owns `lastSentState`); the SW is purely a network proxy. This means SW suspension/restart in MV3 doesn't lose state.

### Cross-origin handling (CORS preflight + Private Network Access)

The Tidal page is HTTPS; the local server is HTTP on `127.0.0.1`. Chrome treats `127.0.0.1` as a *potentially trustworthy* origin, so the **mixed-content** prohibition is waived for HTTPS → loopback fetches. CORS is still enforced.

Two browser policies interact here:

1. **Standard CORS.** A `POST` with `Content-Type: application/json` is not a "simple request" and triggers a preflight `OPTIONS`. The server must respond with the matching `Access-Control-Allow-*` headers.
2. **Private Network Access (PNA).** Chrome blocks any fetch from a public origin (anywhere reachable over the public internet, including `https://tidal.com`) to a private address space (loopback `127.0.0.1`, link-local, RFC 1918) unless the server explicitly opts in by returning `Access-Control-Allow-Private-Network: true` on the preflight. Without this header the browser rejects the request with *"Permission was denied for this request to access the `loopback` address space"* before the actual POST is even attempted.

`WidgetServer` inspects the `Origin` header on every request:

- If `Origin` starts with `chrome-extension://` (i.e. the request is from our extension's service worker), the server emits CORS approval headers.
- If `Origin` is unset (same-origin request — e.g., the widget HTML loaded from `127.0.0.1:8765` polling `/now-playing` on the same origin, or a non-browser local client), no CORS headers are emitted; the browser/runtime doesn't need them.
- If `Origin` is anything else (any public HTTPS site the user happens to have open), no CORS headers are emitted, and an OPTIONS preflight for `/ingest` or `/heartbeat` returns **403 Forbidden**. This closes the cross-site exfil hole that an earlier `Access-Control-Allow-Origin: *` policy would have left open.

For extension-origin preflights, the response headers are:

| Response header | Value |
|---|---|
| `Access-Control-Allow-Origin` | echoes the request's `Origin` (a specific `chrome-extension://<id>` URL) |
| `Vary` | `Origin` (so caches don't reuse this response for other origins) |
| `Access-Control-Allow-Methods` | `POST, OPTIONS` |
| `Access-Control-Allow-Headers` | `Content-Type` |
| `Access-Control-Allow-Private-Network` | `true` |
| `Access-Control-Max-Age` | `600` (cache preflight for 10 minutes) |

`GET /now-playing` and `GET /widget.html` are designed to be served same-origin (the widget HTML is loaded from the same `http://127.0.0.1:8765` origin it polls). They emit `Access-Control-Allow-Origin` only when the request comes from a `chrome-extension://` origin; for same-origin browser requests no CORS headers are needed.

**Why the service worker is required even with these headers.** Content scripts in MV3 run in the *page's* network context, not the extension's, so their fetches are treated as coming from `https://tidal.com` for PNA purposes. The exemption Chrome grants to extensions with declared `host_permissions` only applies to fetches that originate from the **extension's origin** (`chrome-extension://...`), which means background contexts: service workers, popups, options pages. By routing all fetches through `service_worker.js`, the requests inherit the extension origin and get the PNA exemption — which combined with the server-side `Access-Control-Allow-Private-Network: true` makes the round-trip succeed.

### Request size limits

- `POST /ingest` body capped at **16 KB**. Larger bodies → 413, no state change. (Real payloads are ~500 bytes; the cap is purely a DoS-resistance backstop.)
- `POST /heartbeat` body is read but discarded; capped at **0 bytes** (any non-empty body → 400). Heartbeats are pure liveness pings by contract.

### Server state machine

Single in-memory `current: TrackInfo` + `lastIngestAt: DateTime`, behind a lock (same pattern as today's SMTC path).

- **`POST /ingest` with valid body:** update `current` fields, refresh `lastIngestAt = now`. If `art_url` differs from the last seen URL, immediately set `current.Art = ""` and kick off an async art fetch (see *Art handling*).
- **`POST /ingest` with malformed body:** clear `current` to empty TrackInfo, refresh `lastIngestAt = now`, return `400`. Rationale: we did hear from the extension (it's alive) but we cannot trust what it told us about the current track — blank is the honest answer.
- **`POST /heartbeat`:** refresh `lastIngestAt = now`. Do not touch `current`. Return `204`.
- **Idle timer** (1Hz background task): if `now - lastIngestAt > 10s`, reset `current` to empty TrackInfo. Catches browser crashes and network failures that prevent an explicit clear.
- **`GET /now-playing`:** serialize `current` to JSON. No changes.

### Art handling

- `ArtFetcher` keeps a single-entry cache: `{ lastUrl, lastBase64 }`. Same `art_url` → cache hit, no refetch (so 5s heartbeats and unchanged tracks don't hammer the CDN).
- On `art_url` change in an `/ingest` POST: server immediately sets `current.Art = ""` (no stale art is ever served).
- Fetch is async, with retry schedule **250 ms / 1 s / 3 s** (3 attempts).
- On any success: swap `current.Art` to the resulting base64 data URL.
- On total failure after 3 attempts: leave `current.Art` empty. Widget renders the dark placeholder slot (`#1a1a1e` background, already in `widget.html`). Track text (title/artist/album) remains visible.
- Bytes flow purely in memory — never written to disk. Same lifecycle as the existing SMTC `Thumbnail` handling.

**SSRF hardening:** the server fetches `art_url` from arbitrary local POSTs, which (under our localhost-only-unauthenticated threat model) means any local process could ask it to fetch anywhere. To avoid turning the widget server into a fetch proxy for an attacker:

- **Scheme allowlist:** only `https://` URLs are fetched. `file://`, `http://`, `ftp://`, etc. are rejected at validation time (treated as if `art_url` were empty — track text still renders, art slot stays blank). This is the load-bearing mitigation.
- **Response size cap:** abort the fetch after **5 MB**. Real Tidal album art is well under 200 KB.
- **Timeout:** **5 s** per fetch attempt.
- **Single in-flight fetch:** an in-progress fetch for a different `art_url` is cancelled when a new `art_url` arrives, so a slow CDN can't pile up parallel work.

No host allowlist (would require chasing Tidal's CDN hostname if they ever migrate). Scheme + size + timeout cover the worst cases; you accepted the residual risk in design review.

### Pause vs idle semantics

- **Paused** (extension alive, last `/ingest` had `is_playing: false`): widget shows the track metadata in a paused state (animation disabled). Heartbeats keep it pinned to this state indefinitely.
- **Idle** (no POST of any kind for >10s): server clears `current`. Widget hides entirely.

## Build & CI

Two new jobs are added to the **existing `.github/workflows/build.yml`** (not a separate workflow file — single run, single artifact page). The existing `build` matrix job is left untouched, so the three SMTC artifacts continue to be produced exactly as today.

### New csproj — `src/Browser/TidalNowPlaying.Browser.csproj`

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
    <None Include="..\..\widget.html" Link="widget.html">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
    </None>
  </ItemGroup>
</Project>
```

The `<None Include="..\..\widget.html">` block copies `widget.html` into the build/publish output directory next to the binary. Without this, `dotnet run` and `dotnet build` produce a binary that can't find `widget.html` (the server's `Path.Combine(AppContext.BaseDirectory, "widget.html")` resolves to the bin directory). The CI workflow's `cp widget.html publish/browser/.../widget.html` step is now redundant but kept for explicitness.

No `RuntimeIdentifier` baked in — passed at publish time via `-r`. The existing csproj is amended in the same direction: `<RuntimeIdentifier>` is dropped from the file and `-r win-x64` is passed on the CLI from `build.yml`. Symmetry between the two projects.

**Critical existing-csproj edit:** the `Microsoft.NET.Sdk` default `<Compile>` glob picks up `**/*.cs` under the project's directory, which would pull `src/Browser/**/*.cs` into the Windows build and cause a duplicate `Program` entry-point conflict. The existing csproj must explicitly exclude the Browser subtree:

```xml
<ItemGroup>
  <Compile Remove="Browser/**/*.cs" />
</ItemGroup>
```

The new `Browser/TidalNowPlaying.Browser.csproj` does not need a symmetric exclude — its default glob is rooted at `src/Browser/` and naturally doesn't see the Windows-only files.

### New jobs in `build.yml`

```yaml
build-browser-server:
  runs-on: ubuntu-latest
  strategy:
    matrix:
      rid: [win-x64, osx-arm64, linux-x64]
  steps:
    - uses: actions/checkout@v4
    - uses: actions/setup-dotnet@v4
      with: { dotnet-version: '8.0.x' }
    - name: Publish ${{ matrix.rid }}
      run: |
        dotnet publish src/Browser/TidalNowPlaying.Browser.csproj \
          -c Release -r ${{ matrix.rid }} \
          --self-contained true -p:PublishSingleFile=true \
          -o publish/browser/${{ matrix.rid }}
    - name: Bundle widget.html
      run: cp widget.html publish/browser/${{ matrix.rid }}/widget.html
    - uses: actions/upload-artifact@v4
      with:
        name: TidalNowPlaying-Browser-${{ matrix.rid }}
        path: publish/browser/${{ matrix.rid }}/

package-extension:
  runs-on: ubuntu-latest
  steps:
    - uses: actions/checkout@v4
    - name: Zip extension
      run: cd extension && zip -r ../tidal-extension.zip .
    - uses: actions/upload-artifact@v4
      with:
        name: tidal-extension
        path: tidal-extension.zip

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
    - uses: actions/download-artifact@v4
      with: { path: artifacts }
    - name: Stage release zips
      run: |
        mkdir -p release
        for dir in artifacts/*/; do
          name=$(basename "$dir")
          if [ "$name" = "tidal-extension" ]; then
            cp "$dir"*.zip "release/${name}.zip"
          else
            # actions/download-artifact@v4 does not preserve POSIX exec bits.
            # Restore +x on non-Windows binaries before re-zipping so users
            # don't have to chmod after download.
            case "$name" in
              *Browser-osx-*|*Browser-linux-*)
                find "$dir" -maxdepth 1 -type f ! -name '*.html' -exec chmod +x {} \;
                ;;
            esac
            (cd "$dir" && zip -r "../../release/${name}.zip" .)
          fi
        done
    - uses: softprops/action-gh-release@v2
      with:
        tag_name: latest
        name: Latest build
        prerelease: false
        make_latest: true
        files: release/*.zip
```

### Release strategy

- Rolling **`latest`** tag, updated on every push to `main`.
- **Per-pass uploads** via `if: always()` on the `release` job. Individual artifact zips refresh independently — a flaky runner on one platform doesn't block fresh artifacts for other platforms. `softprops/action-gh-release` adds/overrides files individually, so untouched artifacts keep their prior version.
- **Concurrency control:** `concurrency: { group: release-${{ github.ref }}, cancel-in-progress: true }` on the release job. Rapid back-to-back pushes don't race to upload — the newer push supersedes the older one in flight, so `latest` always reflects the most recent commit instead of an interleaved mix.
- Stable download URLs: `https://github.com/<owner>/<repo>/releases/latest/download/<artifact>.zip`.
- Workflow-artifact retention stays at 30 days as a debugging fallback. Releases never expire.

### Artifacts produced per push

| Release file | Contents | New? |
|---|---|---|
| `TidalNowPlaying.zip` | Windows SMTC build (Tidal filter) + widget.html | existing |
| `SpotifyNowPlaying.zip` | Windows SMTC build (Spotify filter) + widget.html | existing |
| `AnyNowPlaying.zip` | Windows SMTC build (any app) + widget.html | existing |
| `TidalNowPlaying-Browser-win-x64.zip` | new Windows server exe + widget.html | new |
| `TidalNowPlaying-Browser-osx-arm64.zip` | macOS arm64 server binary + widget.html | new |
| `TidalNowPlaying-Browser-linux-x64.zip` | Linux server binary + widget.html | new |
| `tidal-extension.zip` | extension folder (manifest.json, content.js, icons/) | new |

## Error Handling

| Failure | Behavior |
|---|---|
| **Art fetch fails** (CDN slow/down/404) | On any `art_url` change, server immediately clears `current.Art = ""` so no stale art is served. Async fetch retries on a 250ms / 1s / 3s schedule (3 attempts total). On any success, swap in the base64. On total failure, `current.Art` stays empty — widget renders the dark placeholder slot. Track text remains visible. |
| **Malformed `/ingest` body** | Server clears `current` to empty TrackInfo, refreshes `lastIngestAt`, returns `400`. Extension can see the error in its console for debugging. |
| **Malformed `/heartbeat`** | Not possible by construction (zero-byte body). Any POST to `/heartbeat` refreshes `lastIngestAt` and returns `204`. |
| **Port 8765 already in use** | `HttpListener.Start()` throws on startup. Server prints a clear message ("port 8765 is already in use — close the other Now Playing instance, or stop the process bound to that port") and exits non-zero. Same failure mode as today's SMTC build. |
| **Server not yet running** when extension POSTs | `fetch()` rejects with a network error. Content script catches and ignores. The next event or heartbeat retries. No queueing, no backoff state in the extension. |
| **Server restarts mid-stream** | Same as above. First successful POST after restart re-establishes state. |
| **`navigator.mediaSession.metadata` is null at page load** (no track has ever played in this tab) | Content script holds `lastSentState = 'none'` and skips the `/ingest` POST. Heartbeats continue. Once metadata populates, the next 500ms poll catches it and POSTs. |
| **Metadata transitions populated → null** (user closed the Tidal player but kept the tab open, navigated to a non-player page within Tidal, etc.) | Content script detects `lastSentState == 'track'` with a current state of `'none'` and sends a cleared ingest message to the service worker (empty fields, `is_playing: false`). Widget hides immediately. Without this, heartbeats would keep `lastIngestAt` fresh and the idle timeout would never fire, leaving the widget pinned to the last track. |
| **Tidal tab closed cleanly** | Content script's `pagehide` handler sends a cleared ingest message to the service worker. This is **best-effort**: `chrome.runtime.sendMessage` is asynchronous and the content-script process may be torn down before the SW actually fires the fetch. Observed behavior is the widget hides via the 10 s idle timeout rather than immediately. (Earlier drafts used `navigator.sendBeacon`, but sendBeacon to loopback hits the same PNA wall as `fetch`, so the message-passing path is uniform — there is no faster reliable mechanism in MV3 without adding `chrome.tabs.onRemoved` + persistent tab tracking, which is deferred.) |
| **Browser crash / kill -9 / extension disabled mid-session** | No clean POST possible (`pagehide` doesn't fire). Idle timeout fires after 10s; widget hides. |
| **Multiple Tidal tabs open and playing** | Both content scripts POST their state; last write wins on the server. Documented as a known limitation in README. |
| **Tidal mediaSession schema changes** | Content script field reads are wrapped in try/catch with sensible fallbacks (e.g., missing `artwork` → `art_url: ""`). Logged to console for debugging. |

**Logging**: server logs each `/ingest` that actually changes state with a short line (e.g. `◀  Hardwell – Spaceman [playing]`), mirroring the existing SMTC `▶` line. Repeat `/ingest` POSTs with identical fields and all `/heartbeat` POSTs are silent (no console output) to keep the console readable during long playback sessions. Errors (malformed bodies, art fetch failures) are always logged.

## Testing Approach

### Existing SMTC path

Behavior is unchanged. The only edit is extracting `SmtcPoller.cs` from `Program.cs`; the polling logic itself is moved verbatim. Validation is a smoke test on Windows: play something in Tidal, watch the widget appear.

### Browser path — unit tests

A new xUnit test project (`net8.0`) covering:

| Test target | What it covers |
|---|---|
| `IngestHandler` — valid POST | Mutates `current`, refreshes `lastIngestAt`, returns 200. |
| `IngestHandler` — malformed JSON | Clears `current`, refreshes `lastIngestAt`, returns 400. |
| `IngestHandler` — `art_url` unchanged across POSTs | No refetch triggered (cache hit). |
| `IngestHandler` — `art_url` changed | `current.Art` cleared immediately; fetch kicked off. |
| `IngestHandler` — body exceeds 16 KB | Returns 413, no state change, no `lastIngestAt` refresh. |
| `HeartbeatHandler` — empty body | Refreshes `lastIngestAt`, does not touch `current`, returns 204. |
| `HeartbeatHandler` — non-empty body | Returns 400, does not touch `current`, does not refresh `lastIngestAt`. |
| CORS preflight from extension origin — `OPTIONS /ingest`, `OPTIONS /heartbeat` with `Origin: chrome-extension://...` | Responds 204 with `Access-Control-Allow-Origin` echoing the origin, plus `Allow-Methods: POST, OPTIONS`, `Allow-Headers: Content-Type`, `Allow-Private-Network: true`, and `Max-Age: 600`. |
| CORS preflight from public origin — `OPTIONS /ingest`, `OPTIONS /heartbeat` with `Origin: https://evil.example.com` | Responds 403 with **no** `Access-Control-Allow-Origin` or `Access-Control-Allow-Private-Network` headers. |
| `ArtFetcher` — `https://` URL, fetch succeeds first try | Returns base64 data URL; cache populated. |
| `ArtFetcher` — `https://` URL, fetch fails first, succeeds on retry | Final result populated; retry schedule (250ms / 1s / 3s) respected. |
| `ArtFetcher` — `https://` URL, all retries fail | Returns empty; cache not poisoned with bad data. |
| `ArtFetcher` — non-`https://` URL (`http://`, `file://`, `ftp://`) | Rejected at validation time; no fetch attempted; returns empty. |
| `ArtFetcher` — response exceeds 5 MB size cap | Fetch aborted; returns empty. |
| `ArtFetcher` — fetch exceeds 5 s timeout | Fetch aborted; counts as a failed attempt; retry schedule continues. |
| `ArtFetcher` — new `art_url` arrives while previous fetch in flight | Previous fetch cancelled; new fetch proceeds. |
| Idle timeout | With injected clock, advance >10s with no POSTs → `current` cleared, `is_playing=false`. |
| `WidgetServer` — `/now-playing` JSON shape | Serializes `current` to the expected keys/values. Catches accidental schema drift. |

`ArtFetcher` takes an `HttpMessageHandler` (or `HttpClient`) by constructor so tests inject a fake — no live CDN hits in CI.

Tests run as a new `test-browser` job on `ubuntu-latest` in `build.yml`, blocking `build-browser-server`. The SMTC build path gets no new tests (no behavior change).

### Browser path — manual end-to-end

Documented in README:

1. Start `TidalNowPlaying-Browser` server.
2. Load unpacked extension from `extension/`.
3. Open `http://127.0.0.1:8765/widget.html` in a browser tab (or as an OBS Browser Source).
4. Play a track in `listen.tidal.com`. Widget populates within ~1s.
5. Pause → widget shows paused state. Play → resumes.
6. Skip track → new metadata + new art appear together; no flicker of stale art.
7. Navigate within the Tidal tab to a non-player page (e.g., settings) so the player unmounts → widget hides immediately (metadata populated → null transition triggers a cleared `/ingest`).
8. Close Tidal tab → widget hides after ~10 s (idle timeout). The `pagehide` → service-worker-message path is best-effort and typically does not complete in time during a clean tab close.
9. Kill browser (or disable the extension) without closing the tab → widget hides ~10s later (idle timeout fallback).

No E2E automation in CI — Tidal accounts and a real browser aren't worth the complexity. The manual checklist is the release gate.

## Future Considerations

These are explicitly **out of scope for v1** but documented so the implementation doesn't preclude them:

- **Track position / progress bar.** Add `position_ms` and `duration_ms` to the JSON contract; the natural transport for live position is a WebSocket (`ws://127.0.0.1:8765/ws`) rather than polled HTTP, since it scales better than sending a heartbeat every 250ms. The heartbeat endpoint would be the natural place to attach position payload if a polled approach were ever preferred.
- **Multiple browser music services.** The current Spotify and "any" SMTC matrix entries could be mirrored with additional Chrome extensions (Spotify Web, YT Music) or a generic mediaSession extension. Server-side is already source-agnostic — no changes needed there.
- **Chrome Web Store publication.** Would require a Google developer account, manifest review, and a stable icon set. v1 ships as load-unpacked.
- **Firefox / Safari ports.** The extension API surface used is all MV3-portable.
- **Multi-tab deduplication.** If two Tidal tabs are playing, decide a tiebreaker (most-recently-active, or merge state). v1 documents this and accepts last-write-wins.
- **Faster tab-close clear.** The current `pagehide → runtime.sendMessage` path is best-effort and typically loses the race with tab teardown, falling back to the 10 s idle timeout. A `chrome.tabs.onRemoved` listener in the service worker, combined with `chrome.storage.session` to track which tabIds are Tidal tabs, would give immediate clears at the cost of MV3 service-worker-lifecycle complexity (the `tabs.onRemoved` handler must rehydrate the tab-tracking map every time the SW wakes up). Deferred.

## Implementation Notes — divergences from original design

Two design assumptions did not survive contact with the real Chrome / Tidal stack and were revised during integration testing. They are reflected in the sections above; this addendum records the *why* so future maintainers don't try to "fix" the deviations.

1. **Service worker is required, not optional.** The original design omitted a background service worker, putting all extension logic in the content script and using `navigator.sendBeacon` for tab-close clears. Chrome's Private Network Access policy blocks any fetch (and sendBeacon) from the page's network context (`https://tidal.com`, treated as "public") to a private address space (`127.0.0.1`), even when the manifest declares `host_permissions: ["http://127.0.0.1/*"]`. The host_permissions exemption applies only to fetches initiated from the extension's own origin (`chrome-extension://...`). Routing through a service worker is the only way to access loopback from a public-page extension without triggering PNA. The original "no service worker" decision was based on simplifying tab-tracking; tab-tracking is still not used, but the SW exists purely as a network proxy.

2. **`mediaSession.metadata` alone is the "track loaded" signal — not `audio.src`.** The original design gated `state = 'track'` on `mediaSession.metadata` *and* an `<audio>` element with a `src` attribute. In practice, Tidal's web player streams via Media Source Extensions: the audio element has no `src` (the source is attached via `srcObject`/`MediaSource`), or there may be no plain `<audio>` element at all. Gating on `audio.src` meant `/ingest` never fired on Tidal even though `mediaSession` was fully populated. The content script now trusts `mediaSession.metadata` exclusively for the track-loaded gate, with `mediaSession.playbackState` (and `audio.paused` as a fallback) for the play/pause flag.
