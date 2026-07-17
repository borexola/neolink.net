// Neolink.NET service worker.
//
// Deliberately caches NOTHING: this is a live-camera UI rendered server-side,
// so a stale cached shell is strictly worse than no shell. The worker exists
// for two reasons only:
//   1. it makes the app installable as a PWA in every Chromium version
//      (newer ones no longer require a worker, older ones do), and
//   2. when the server is unreachable it replaces the browser's error page
//      with a branded screen that retries by itself.
// Only top-level navigations are intercepted; API calls, media segments and
// the Blazor SignalR traffic pass through untouched (WebSockets never route
// through a service worker at all).

const OFFLINE_HTML = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<meta name="theme-color" content="#0b0d12">
<title>Neolink.NET — server unreachable</title>
<style>
  html, body { margin: 0; height: 100%; background: #0b0d12; color: #e7ebf4;
    font: 14px/1.5 "Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, "Helvetica Neue", sans-serif;
    -webkit-font-smoothing: antialiased; }
  .wrap { height: 100%; display: flex; flex-direction: column; align-items: center;
    justify-content: center; gap: 14px; text-align: center; padding: 24px; box-sizing: border-box; }
  .logo { width: 72px; height: 72px; }
  h1 { font-size: 17px; font-weight: 600; margin: 4px 0 0; }
  p { margin: 0; color: #8b94a7; max-width: 34em; }
  button { margin-top: 10px; padding: 8px 18px; border-radius: 999px; border: 1px solid #222839;
    background: rgba(91, 157, 255, 0.14); color: #5b9dff; font: inherit; font-weight: 600; cursor: pointer; }
  button:hover { background: rgba(91, 157, 255, 0.22); }
</style>
</head>
<body>
<div class="wrap">
  <svg class="logo" viewBox="0 0 256 256" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
    <defs>
      <linearGradient id="tile" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#3B82F6"/><stop offset="1" stop-color="#1D4ED8"/>
      </linearGradient>
    </defs>
    <rect x="16" y="16" width="224" height="224" rx="52" fill="url(#tile)"/>
    <g fill="#ffffff">
      <rect x="72" y="70" width="26" height="116" rx="4"/>
      <rect x="150" y="70" width="26" height="116" rx="4"/>
      <polygon points="72,70 98,70 176,186 150,186"/>
    </g>
    <circle cx="196" cy="188" r="13" fill="#38BDF8"/>
  </svg>
  <h1>Can't reach your Neolink.NET server</h1>
  <p>Live view and recordings will be back as soon as the server is reachable again.
     This page retries automatically.</p>
  <button onclick="location.reload()">Retry now</button>
</div>
<script>
  // Retry quietly in the background: probe the app root and only reload once
  // the server actually answers, so the user never sees a reload flicker-loop.
  // GET, not HEAD — the Blazor endpoint answers 405 to HEAD, which would keep
  // this page up forever. One small page fetch per probe, only while down.
  window.addEventListener('online', () => location.reload());
  setInterval(() => {
    fetch('./', { cache: 'no-store' })
      .then(r => { if (r.ok || r.status === 401 || r.status === 403) location.reload(); })
      .catch(() => {});
  }, 5000);
</script>
</body>
</html>`;

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));

self.addEventListener('fetch', e => {
  if (e.request.mode !== 'navigate') return; // everything else goes straight to the network
  e.respondWith(fetch(e.request).catch(() =>
    new Response(OFFLINE_HTML, {
      status: 503,
      headers: { 'Content-Type': 'text/html; charset=utf-8' },
    })));
});

// Browser alerts: clicking a notification lands on the exact clip. Focus an
// existing window when one is open (and steer it to the event), otherwise open
// a fresh one — the standard PWA notification-click dance.
self.addEventListener('notificationclick', e => {
  e.notification.close();
  const url = e.notification.data && e.notification.data.url;
  e.waitUntil((async () => {
    const wins = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const c of wins) {
      if ('focus' in c) {
        await c.focus();
        if (url && 'navigate' in c) { try { await c.navigate(url); } catch { } }
        return;
      }
    }
    if (url) await self.clients.openWindow(url);
  })());
});
