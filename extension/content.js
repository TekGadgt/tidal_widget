// Tidal Now Playing — content script.
// Reads navigator.mediaSession.metadata + the page's <audio> element.
// Cannot fetch loopback directly from the page's network context (Chrome's
// Private Network Access policy blocks it even with host_permissions), so
// all network I/O is delegated to the service worker via runtime.sendMessage.

const POLL_MS = 500;
const HEARTBEAT_MS = 5000;

let lastSent = { state: 'none', title: '', artist: '', album: '', isPlaying: false, artUrl: '' };

function findAudio() {
  // Tidal uses Media Source Extensions — the <audio> element may exist with
  // no `src` attribute, or be a <video> element. Check both.
  return document.querySelector('audio, video');
}

function isCurrentlyPlaying(audio) {
  // Prefer the mediaSession playbackState the site explicitly sets, since
  // Tidal may keep an <audio> element around in `paused` state during track
  // load even though playback is occurring.
  const ps = navigator.mediaSession && navigator.mediaSession.playbackState;
  if (ps === 'playing') return true;
  if (ps === 'paused') return false;
  // Fall back to the audio element's own state if available.
  if (audio && typeof audio.paused === 'boolean') return !audio.paused;
  // Default: if metadata is populated but nothing else tells us, assume playing.
  return true;
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
    // mediaSession.metadata is the authoritative "track loaded" signal —
    // Tidal sets it via MSE before the <audio> element gets a `src` attribute.
    if (!md) {
      return { state: 'none', title: '', artist: '', album: '', isPlaying: false, artUrl: '' };
    }
    const audio = findAudio();
    return {
      state: 'track',
      title: md.title || '',
      artist: md.artist || '',
      album: md.album || '',
      isPlaying: isCurrentlyPlaying(audio),
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

function sendIngest(payload) {
  try {
    chrome.runtime.sendMessage({ type: 'ingest', payload }, () => {
      // Swallow runtime.lastError noise — fire-and-forget; next event/heartbeat retries.
      void chrome.runtime.lastError;
    });
  } catch (e) {
    // Service worker may be tearing down during extension reload; ignore.
  }
}

function sendHeartbeat() {
  try {
    chrome.runtime.sendMessage({ type: 'heartbeat' }, () => {
      void chrome.runtime.lastError;
    });
  } catch (e) {
    // ignore
  }
}

function tick() {
  const cur = snapshot();

  if (cur.state === 'track') {
    if (snapshotsDiffer(cur, lastSent)) {
      sendIngest(payloadFromSnapshot(cur));
      lastSent = cur;
    }
  } else {
    if (lastSent.state === 'track') {
      sendIngest(CLEARED_PAYLOAD);
      lastSent = cur;
    }
  }
}

// Polling loop.
setInterval(tick, POLL_MS);

// Heartbeat loop.
setInterval(sendHeartbeat, HEARTBEAT_MS);

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
// (sendBeacon to loopback is blocked by PNA the same way fetch is, so we use
// runtime.sendMessage too — best-effort during pagehide. The server's 10 s
// idle timeout is the reliable fallback if this doesn't make it through.)
window.addEventListener('pagehide', () => {
  try {
    chrome.runtime.sendMessage({ type: 'ingest', payload: CLEARED_PAYLOAD }, () => {
      void chrome.runtime.lastError;
    });
  } catch (e) { /* ignore */ }
});

console.log('[TidalNowPlaying] content script loaded');
