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
┌─────────────────────────┐
│  Chrome tab (tidal.com) │
│  ┌───────────────────┐  │
│  │ content.js        │──┼── POST /ingest ──┐
│  │  - reads media    │  │  POST /heartbeat │
│  │    session +      │  │                  │
│  │    <audio> elem   │  │                  ▼
│  └───────────────────┘  │  ┌────────────────────────────┐
│  ┌───────────────────┐  │  │ TidalNowPlaying.Browser    │
│  │ service_worker.js │──┼─▶│  HttpListener :8765        │
│  │  - tab close →    │  │  │   /ingest    (POST)        │
│  │    POST clear     │  │  │   /heartbeat (POST)        │
│  └───────────────────┘  │  │   /now-playing (GET, JSON) │
└─────────────────────────┘  │   /widget.html (GET)       │
                             │                            │
                             │  - holds current TrackInfo │
                             │  - fetches art URL → bytes │
                             │    → base64 → in-memory    │
                             │  - 10s idle timeout clears │
                             │    current TrackInfo       │
                             └────────────────────────────┘
                                          │
                                          │ GET /widget.html
                                          ▼
                             ┌────────────────────────────┐
                             │ OBS Browser Source         │
                             │  polls /now-playing        │
                             └────────────────────────────┘
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
    ├── manifest.json                    (MV3, host_permissions: tidal.com + 127.0.0.1:8765)
    ├── content.js                       (reads mediaSession + <audio>; POSTs ingest + heartbeat)
    ├── service_worker.js                (chrome.tabs.onRemoved → POST cleared ingest)
    └── icons/                           (16/48/128 png — placeholder ok for v1)
```

### Responsibility split

- **`Shared/WidgetServer.cs`** — owns the HttpListener loop and the two read-only routes (`/now-playing`, `/widget.html`). Both binaries reuse it so the widget URL and JSON contract cannot drift.
- **`Shared/TrackInfo.cs`** — shared record. Same JSON serialization for both paths.
- **Windows side** — `Program.cs` becomes a tiny wire-up file; SMTC polling moves into `SmtcPoller.cs`. Outward behavior identical to today.
- **Browser side** — `Program.cs` wires up the ingest + heartbeat handlers, the `ArtFetcher`, the `WidgetServer`, and the 10-second idle timer. Handlers are separated by responsibility.
- **Extension** — `content.js` does the actual reading and POSTing. `service_worker.js` exists only for the tab-close clear signal (MV3 service workers go idle, so they can't hold polling state — that lives in the content script).

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

`content.js` (runs on `listen.tidal.com` and `tidal.com`):

- Polls `navigator.mediaSession.metadata` every 500ms. On any field change (title/artist/album/artwork) → `POST /ingest` immediately.
- Listens to the page's `<audio>` element `play`/`pause` events → `POST /ingest` immediately with updated `is_playing`.
- `setInterval(5000)` → `POST /heartbeat`.
- POSTs are fire-and-forget. Failures (server down, network error) are silently ignored; the next event or heartbeat retries.

`service_worker.js`:

- Listens to `chrome.tabs.onRemoved`. When the closed tab was a Tidal tab, → `POST /ingest` with cleared body.

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
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="..\Shared\**\*.cs" />
  </ItemGroup>
</Project>
```

No `RuntimeIdentifier` baked in — passed at publish time via `-r`. The existing csproj is amended in the same direction: `<RuntimeIdentifier>` is dropped from the file and `-r win-x64` is passed on the CLI from `build.yml`. Symmetry between the two projects.

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
- Stable download URLs: `https://github.com/<owner>/<repo>/releases/latest/download/<artifact>.zip`.
- Workflow-artifact retention stays at 30 days as a debugging fallback. Releases never expire.

### Artifacts produced per push

| Release file | Contents | New? |
|---|---|---|
| `TidalNowPlaying.zip` | Windows SMTC build (Tidal filter) + widget.html | existing |
| `SpotifyNowPlaying.zip` | Windows SMTC build (Spotify filter) + widget.html | existing |
| `NowPlaying.zip` | Windows SMTC build (any app) + widget.html | existing |
| `TidalNowPlaying-Browser-win-x64.zip` | new Windows server exe + widget.html | new |
| `TidalNowPlaying-Browser-osx-arm64.zip` | macOS arm64 server binary + widget.html | new |
| `TidalNowPlaying-Browser-linux-x64.zip` | Linux server binary + widget.html | new |
| `tidal-extension.zip` | extension folder (manifest.json, content.js, service_worker.js, icons/) | new |

## Error Handling

| Failure | Behavior |
|---|---|
| **Art fetch fails** (CDN slow/down/404) | On any `art_url` change, server immediately clears `current.Art = ""` so no stale art is served. Async fetch retries on a 250ms / 1s / 3s schedule (3 attempts total). On any success, swap in the base64. On total failure, `current.Art` stays empty — widget renders the dark placeholder slot. Track text remains visible. |
| **Malformed `/ingest` body** | Server clears `current` to empty TrackInfo, refreshes `lastIngestAt`, returns `400`. Extension can see the error in its console for debugging. |
| **Malformed `/heartbeat`** | Not possible by construction (zero-byte body). Any POST to `/heartbeat` refreshes `lastIngestAt` and returns `204`. |
| **Port 8765 already in use** | `HttpListener.Start()` throws on startup. Server prints a clear message ("port 8765 is already in use — close the other Now Playing instance, or stop the process bound to that port") and exits non-zero. Same failure mode as today's SMTC build. |
| **Server not yet running** when extension POSTs | `fetch()` rejects with a network error. Content script catches and ignores. The next event or heartbeat retries. No queueing, no backoff state in the extension. |
| **Server restarts mid-stream** | Same as above. First successful POST after restart re-establishes state. |
| **`navigator.mediaSession.metadata` is null** (page just loaded, Tidal hasn't populated yet) | Content script treats it as "no track" and skips the `/ingest` POST. Heartbeats continue. Once metadata populates, the next 500ms poll catches it and POSTs. |
| **Tidal tab closed cleanly** | `chrome.tabs.onRemoved` in service worker fires a cleared `/ingest`. Widget hides immediately. |
| **Browser crash / kill -9** | No clean POST possible. Idle timeout fires after 10s; widget hides. |
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
| `IngestHandler` — `art_url` unchanged | No refetch triggered (cache hit). |
| `IngestHandler` — `art_url` changed | `current.Art` cleared immediately; fetch kicked off. |
| `HeartbeatHandler` | Refreshes `lastIngestAt`, does not touch `current`, returns 204. |
| `ArtFetcher` — fetch succeeds first try | Returns base64 data URL; cache populated. |
| `ArtFetcher` — fetch fails first, succeeds on retry | Final result populated; retry schedule respected. |
| `ArtFetcher` — all retries fail | Returns empty; cache not poisoned with bad data. |
| Idle timeout | With injected clock, advance >10s with no POSTs → `current` cleared, `is_playing=false`. |
| `WidgetServer` — `/now-playing` JSON shape | Serializes `current` to the expected keys/values. Catches accidental schema drift. |

`ArtFetcher` takes an `HttpMessageHandler` (or `HttpClient`) by constructor so tests inject a fake — no live CDN hits in CI.

Tests run as a new `test-browser` job on `ubuntu-latest` in `build.yml`, blocking `build-browser-server`. The SMTC build path gets no new tests (no behavior change).

### Browser path — manual end-to-end

Documented in README:

1. Start `TidalNowPlaying-Browser` server.
2. Load unpacked extension from `extension/`.
3. Open `http://localhost:8765/widget.html` in a browser tab (or as an OBS Browser Source).
4. Play a track in `listen.tidal.com`. Widget populates within ~1s.
5. Pause → widget shows paused state. Play → resumes.
6. Skip track → new metadata + new art appear together; no flicker of stale art.
7. Close Tidal tab → widget hides immediately.
8. Kill browser (or disable the extension) without closing the tab → widget hides ~10s later.

No E2E automation in CI — Tidal accounts and a real browser aren't worth the complexity. The manual checklist is the release gate.

## Future Considerations

These are explicitly **out of scope for v1** but documented so the implementation doesn't preclude them:

- **Track position / progress bar.** Add `position_ms` and `duration_ms` to the JSON contract; the natural transport for live position is a WebSocket (`ws://127.0.0.1:8765/ws`) rather than polled HTTP, since it scales better than sending a heartbeat every 250ms. The heartbeat endpoint would be the natural place to attach position payload if a polled approach were ever preferred.
- **Multiple browser music services.** The current Spotify and "any" SMTC matrix entries could be mirrored with additional Chrome extensions (Spotify Web, YT Music) or a generic mediaSession extension. Server-side is already source-agnostic — no changes needed there.
- **Chrome Web Store publication.** Would require a Google developer account, manifest review, and a stable icon set. v1 ships as load-unpacked.
- **Firefox / Safari ports.** The extension API surface used is all MV3-portable.
- **Multi-tab deduplication.** If two Tidal tabs are playing, decide a tiebreaker (most-recently-active, or merge state). v1 documents this and accepts last-write-wins.
