# Tidal Now Playing — OBS Widget

A lightweight Windows app that reads the currently playing Tidal track via the Windows media session (SMTC) and serves it as an OBS Browser Source widget.

## Usage

1. Run `TidalNowPlaying.exe` (keep it running while streaming)
2. In OBS, add a **Browser Source**:
   - URL: `http://localhost:8765/widget.html`
   - Width: `420`, Height: `110`
   - Check "Shutdown source when not visible"
3. Play something in Tidal — the widget appears automatically

## Files

Both files must stay in the same folder:
- `TidalNowPlaying.exe` — the server
- `widget.html` — the OBS overlay

## Endpoints

- `http://localhost:8765/widget.html` — the visual overlay
- `http://localhost:8765/now-playing` — raw JSON feed

## Building from source

Push to GitHub and the Actions workflow builds the `.exe` automatically.
Download the artifact from the Actions tab after the build completes.
