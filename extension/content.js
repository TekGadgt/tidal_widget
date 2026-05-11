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
