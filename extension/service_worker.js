// Tidal Now Playing — service worker.
// Performs the actual fetches to the local companion server.
// Content script (running in the page's network context, treated as "public" by
// Chrome's Private Network Access policy) cannot fetch loopback directly even
// with host_permissions — the exemption only applies to extension-origin
// network requests. The service worker runs in chrome-extension://... origin
// and IS exempt, so we route all fetches through it via runtime.sendMessage.

const SERVER = 'http://127.0.0.1:8765';

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg && msg.type === 'ingest') {
    fetch(`${SERVER}/ingest`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(msg.payload),
    })
      .then(r => sendResponse({ ok: r.ok, status: r.status }))
      .catch(e => sendResponse({ ok: false, error: String(e) }));
    return true; // keep the message channel open so the SW stays alive until fetch resolves
  }
  if (msg && msg.type === 'heartbeat') {
    fetch(`${SERVER}/heartbeat`, { method: 'POST' })
      .then(r => sendResponse({ ok: r.ok, status: r.status }))
      .catch(e => sendResponse({ ok: false, error: String(e) }));
    return true;
  }
  return false;
});
