# Tidal Now Playing — OBS Widget

A lightweight app that reads the currently playing Tidal track and serves it as an OBS Browser Source widget. Two data sources are supported:

- **SMTC (Windows)** — original path. Reads the Windows media session, so it works with the Tidal desktop app, Spotify desktop, or any media player that publishes SMTC metadata.
- **Browser extension (cross-platform)** — new. A Chrome MV3 extension scrapes `listen.tidal.com` and forwards state to a small cross-platform server. Works on Windows / macOS / Linux.

Both produce the same `widget.html` and JSON feed, so the OBS Browser Source setup is identical for either deployment.

## Usage — SMTC build (Windows only)

1. Download `TidalNowPlaying.zip` (or `SpotifyNowPlaying.zip` / `NowPlaying.zip` for the any-app variant) from the latest [Release](https://github.com/TekGadgt/tidal_widget/releases/latest). Unzip.
2. Run `TidalNowPlaying.exe` (keep it running while streaming).
3. In OBS, add a **Browser Source**:
   - URL: `http://127.0.0.1:8765/widget.html`
   - Width: `420`, Height: `110`
   - Check "Shutdown source when not visible"
4. Play something in the Tidal desktop app — the widget appears automatically.

Both files must stay in the same folder:
- `TidalNowPlaying.exe` — the server
- `widget.html` — the OBS overlay

## Usage — Browser source (Windows / macOS / Linux)

Use this if you listen to Tidal in a browser tab instead of the desktop app.

1. From the latest [Release](https://github.com/TekGadgt/tidal_widget/releases/latest), download:
   - `TidalNowPlaying-Browser-<your-platform>.zip` — server binary + `widget.html`. Available for `win-x64`, `osx-arm64`, and `linux-x64`.
   - `tidal-extension.zip` — the Chrome MV3 extension.
2. Unzip the server build. Run `TidalNowPlaying-Browser` (or `TidalNowPlaying-Browser.exe` on Windows). Keep it running while streaming.
3. Unzip the extension. In Chrome (or any Chromium browser — Edge, Brave, etc.), open `chrome://extensions`, enable **Developer mode**, click **Load unpacked**, and select the unzipped `extension/` folder.
4. Configure OBS the same way as the SMTC build — point a Browser Source at `http://127.0.0.1:8765/widget.html`.
5. Play something in your `listen.tidal.com` tab — the widget appears automatically.

Direct download URLs (always point at the latest `main` build):
- https://github.com/TekGadgt/tidal_widget/releases/latest/download/TidalNowPlaying-Browser-win-x64.zip
- https://github.com/TekGadgt/tidal_widget/releases/latest/download/TidalNowPlaying-Browser-osx-arm64.zip
- https://github.com/TekGadgt/tidal_widget/releases/latest/download/TidalNowPlaying-Browser-linux-x64.zip
- https://github.com/TekGadgt/tidal_widget/releases/latest/download/tidal-extension.zip

Known limitations:
- Single Tidal tab at a time — multiple playing tabs result in last-write-wins on the server.
- macOS / Linux binaries are not code-signed. On macOS, first launch may be blocked by Gatekeeper; clear the quarantine attribute with `xattr -d com.apple.quarantine TidalNowPlaying-Browser` then retry.

## Endpoints (both builds)

- `http://127.0.0.1:8765/widget.html` — the visual overlay
- `http://127.0.0.1:8765/now-playing` — raw JSON feed (`title`, `artist`, `album`, `is_playing`, `art`)

## Building from source

Push to GitHub and the Actions workflow builds all variants automatically. The `latest` GitHub Release is updated on every push to `main` with seven artifacts:

- `TidalNowPlaying.zip`, `SpotifyNowPlaying.zip`, `NowPlaying.zip` — Windows + SMTC builds.
- `TidalNowPlaying-Browser-win-x64.zip`, `TidalNowPlaying-Browser-osx-arm64.zip`, `TidalNowPlaying-Browser-linux-x64.zip` — cross-platform browser-source server builds.
- `tidal-extension.zip` — the Chrome MV3 extension.

Each release file is a self-contained zip — unzip and run.
