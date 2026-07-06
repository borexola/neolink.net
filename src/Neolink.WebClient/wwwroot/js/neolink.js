// Neolink web client: MSE player fed by fMP4-over-WebSocket from Neolink.Server.
(function () {
    'use strict';

    // Latency tuning: like VLC, keep a jitter buffer between the live edge and the
    // playhead. Cameras deliver video anywhere from per-frame to whole multi-second
    // GOP batches, so the cushion adapts to the observed delivery cadence: it must
    // always cover the gap between two network deliveries.
    const MIN_LATENCY = 1.0;   // floor for the adaptive target (s)
    const MAX_LATENCY = 12.0;  // ceiling (s)

    class Player {
        constructor(video, wsUrl) {
            this.video = video;
            // Blazor renders the `muted` attribute, but on dynamically created
            // elements that only sets defaultMuted — the live property stays false,
            // and unmuted autoplay is blocked until a user gesture (symptom: frozen
            // first frame + "connecting…" after a page refresh). Mute for real.
            video.muted = true;
            this.wsUrl = wsUrl;
            this.alive = true;
            this.queue = [];
            this.sb = null;
            this.ms = null;
            this.ws = null;
            this.timer = null;
            this.started = false;
            this.latencyTarget = MIN_LATENCY;
            this.msgGaps = [];
            this.lastMsgAt = 0;
            this.msgCount = 0;
            this.onPlaying = () => { this.video.dataset.live = '1'; };
            this.onWaiting = () => this.jumpGap();
            video.addEventListener('playing', this.onPlaying);
            video.addEventListener('waiting', this.onWaiting);
            this.connect();
        }

        connect() {
            if (!this.alive) return;
            try {
                this.ws = new WebSocket(this.wsUrl);
            } catch {
                this.retry();
                return;
            }
            this.ws.binaryType = 'arraybuffer';
            this.ws.onmessage = (e) => {
                if (typeof e.data === 'string') {
                    this.setup(JSON.parse(e.data));
                } else {
                    // Track delivery cadence: the jitter buffer must cover the largest
                    // gap between deliveries (GOP-batching cameras pause for seconds).
                    const now = performance.now() / 1000;
                    if (this.lastMsgAt > 0) {
                        this.msgGaps.push(now - this.lastMsgAt);
                        if (this.msgGaps.length > 10) this.msgGaps.shift();
                        const maxGap = Math.max(...this.msgGaps);
                        this.latencyTarget = Math.min(MAX_LATENCY, Math.max(MIN_LATENCY, maxGap * 1.25 + 0.3));
                    }
                    this.lastMsgAt = now;
                    this.msgCount++;

                    this.queue.push(e.data);
                    if (this.queue.length > 900) this.queue.splice(0, this.queue.length - 300);
                    this.pump();
                }
            };
            this.ws.onclose = () => { this.teardownMse(); this.retry(); };
            this.ws.onerror = () => { try { this.ws.close(); } catch { } };
        }

        retry() {
            if (!this.alive) return;
            delete this.video.dataset.live;
            clearTimeout(this.timer);
            this.timer = setTimeout(() => this.connect(), 3000);
        }

        setup(meta) {
            this.teardownMse();
            if (!('MediaSource' in window) || !MediaSource.isTypeSupported(meta.mime)) {
                // Codec not playable in this browser (typically H.265 without HW support).
                this.setStatus('⚠ ' + meta.codec + ' not supported by this browser — try the sub stream');
                this.alive = false;
                try { this.ws.close(); } catch { }
                return;
            }
            this.setStatus('connecting…');
            this.ms = new MediaSource();
            this.video.src = URL.createObjectURL(this.ms);
            this.ms.addEventListener('sourceopen', () => {
                if (!this.ms) return;
                this.sb = this.ms.addSourceBuffer(meta.mime);
                this.sb.mode = 'segments';
                this.sb.addEventListener('updateend', () => this.pump());
                this.pump();
            }, { once: true });
            // Playback starts from pump() once START_BUFFER is accumulated.
        }

        pump() {
            if (!this.sb || this.sb.updating) return;

            // Trim history so the buffer doesn't grow forever
            try {
                const b = this.sb.buffered;
                if (b.length && this.video.currentTime - b.start(0) > 30) {
                    this.sb.remove(b.start(0), this.video.currentTime - 10);
                    return;
                }
            } catch { }

            const next = this.queue.shift();
            if (next) {
                try {
                    this.sb.appendBuffer(next);
                } catch {
                    // QuotaExceeded or detached buffer: force a clean reconnect
                    try { this.ws.close(); } catch { }
                    return;
                }
            }

            // If the decoder is starved even though data exists ahead, we're at a
            // buffered-range gap: hop over it instead of waiting.
            if (this.started && this.video.readyState < 3) this.jumpGap();

            // Latency management (VLC-style jitter buffer).
            try {
                const b = this.sb.buffered;
                if (!b.length) return;
                const last = b.length - 1;
                const end = b.end(last);
                const t = this.video.currentTime;
                const ahead = end - t;

                if (!this.started) {
                    // Start once the delivery cadence is known (≥1 measured gap) and the
                    // buffer can cover the latency target.
                    const span = end - b.start(last);
                    if (this.msgGaps.length >= 1 && span >= this.latencyTarget) {
                        this.started = true;
                        this.video.currentTime = Math.max(b.start(last), end - this.latencyTarget);
                        // If playback is refused (autoplay policy), clear `started` so the
                        // next delivery retries instead of freezing on the first frame.
                        this.video.play().catch(() => { this.started = false; });
                    }
                    return;
                }

                const inLast = t >= b.start(last) - 0.01;
                if (!inLast) return; // behind a gap: jumpGap handles it

                if (ahead > this.latencyTarget + 5) {
                    this.video.currentTime = end - this.latencyTarget; // hard resync
                    this.video.playbackRate = 1.0;
                } else if (ahead > this.latencyTarget + 1) {
                    this.video.playbackRate = 1.1;  // drift back gently, invisibly
                } else if (this.video.playbackRate !== 1.0) {
                    this.video.playbackRate = 1.0;
                }
            } catch { }
        }

        // Seek over a hole in the buffered ranges (caused by dropped frames upstream).
        jumpGap() {
            if (!this.sb) return;
            try {
                const b = this.sb.buffered;
                const t = this.video.currentTime;
                for (let i = 0; i < b.length; i++) {
                    if (b.start(i) > t + 0.01 && b.start(i) - t < 10) {
                        this.video.currentTime = b.start(i) + 0.05;
                        return;
                    }
                }
            } catch { }
        }

        setStatus(text) {
            const overlay = this.video.parentElement?.querySelector('.tile-status');
            if (overlay) overlay.textContent = text;
        }

        teardownMse() {
            this.sb = null;
            this.queue = [];
            this.started = false;
            try { this.video.playbackRate = 1.0; } catch { }
            delete this.video.dataset.live;
            if (this.video.src) {
                try { URL.revokeObjectURL(this.video.src); } catch { }
                this.video.removeAttribute('src');
                try { this.video.load(); } catch { }
            }
            this.ms = null;
        }

        destroy() {
            this.alive = false;
            clearTimeout(this.timer);
            this.video.removeEventListener('playing', this.onPlaying);
            this.video.removeEventListener('waiting', this.onWaiting);
            try { this.ws && this.ws.close(); } catch { }
            this.teardownMse();
        }
    }

    const players = {};

    // Fullscreen state only exists browser-side; expose it as a body class so
    // CSS can swap the enter/exit glyph on the tile buttons without a server trip.
    document.addEventListener('fullscreenchange', () => {
        document.body.classList.toggle('is-fullscreen', !!document.fullscreenElement);
    });

    // ---------- freeform layout (draggable/resizable tiles, geometry persisted) ----------
    const free = {
        key: 'neolink.freegeo',
        z: 10,
        saveTimer: null,
        load() {
            try { return JSON.parse(localStorage.getItem(this.key)) || {}; } catch { return {}; }
        },
        save(geo) {
            clearTimeout(this.saveTimer);
            this.saveTimer = setTimeout(() => localStorage.setItem(this.key, JSON.stringify(geo)), 250);
        },
        capture(stage) {
            const geo = this.load();
            stage.querySelectorAll('.tile').forEach(tile => {
                if (!tile.style.width) return;
                geo[tile.dataset.slot] = {
                    x: tile.offsetLeft, y: tile.offsetTop,
                    w: tile.offsetWidth, h: tile.offsetHeight,
                };
            });
            this.save(geo);
        },
    };

    function freeInit(stageId) {
        const stage = document.getElementById(stageId);
        if (!stage || !stage.classList.contains('mode-free')) return;
        const geo = free.load();

        stage.querySelectorAll('.tile').forEach((tile, idx) => {
            const slot = tile.dataset.slot ?? String(idx);
            if (!tile.dataset.dragging) {
                const g = geo[slot] || { x: 24 + 32 * idx, y: 24 + 32 * idx, w: 440, h: 280 };
                tile.style.left = g.x + 'px';
                tile.style.top = g.y + 'px';
                tile.style.width = g.w + 'px';
                tile.style.height = g.h + 'px';
            }

            if (tile.dataset.freeInit) return;
            tile.dataset.freeInit = '1';

            tile.addEventListener('pointerdown', () => { tile.style.zIndex = ++free.z; });

            // Drag by the header strip
            const head = tile.querySelector('.tile-head');
            if (head) {
                head.addEventListener('pointerdown', (e) => {
                    if (e.button !== 0) return;
                    e.preventDefault();
                    head.setPointerCapture(e.pointerId);
                    tile.dataset.dragging = '1';
                    const sx = e.clientX, sy = e.clientY;
                    const ox = tile.offsetLeft, oy = tile.offsetTop;
                    const move = (ev) => {
                        tile.style.left = Math.max(0, ox + ev.clientX - sx) + 'px';
                        tile.style.top = Math.max(0, oy + ev.clientY - sy) + 'px';
                    };
                    const up = () => {
                        head.removeEventListener('pointermove', move);
                        head.removeEventListener('pointerup', up);
                        delete tile.dataset.dragging;
                        free.capture(stage);
                    };
                    head.addEventListener('pointermove', move);
                    head.addEventListener('pointerup', up);
                });
            }

            // Persist native CSS resize (resize: both)
            new ResizeObserver(() => {
                if (!tile.isConnected || !tile.style.width) return;
                if (!stage.classList.contains('mode-free')) return;
                free.capture(stage);
            }).observe(tile);
        });
    }

    // ---------- timeline page (recorded footage scrubbing) ----------
    const scrubs = {};

    window.neolink = {
        freeInit,

        // Ambient event previews (review strip): ensure real muting, fast-forward
        // playback rate and looped playback. Idempotent, called per render.
        trickle(speed) {
            document.querySelectorAll('video[data-trickle]').forEach(v => {
                v.muted = true;
                v.defaultPlaybackRate = v.playbackRate = speed || 4;
                if (v.paused) v.play().catch(() => { });
            });
        },

        // Review-strip vertical resizing: drag the handle at the bar's bottom edge.
        // The height is applied live in the DOM (no SignalR churn while dragging)
        // and reported to Blazor once on release for persistence.
        stripResizeInit(id, dotnetRef) {
            const el = document.getElementById(id);
            if (!el || el.dataset.resizeInit) return;
            const handle = el.querySelector('.strip-resize');
            if (!handle) return;
            el.dataset.resizeInit = '1';
            handle.addEventListener('pointerdown', (e) => {
                if (e.button !== 0 && e.pointerType === 'mouse') return;
                e.preventDefault();
                e.stopPropagation();
                try { handle.setPointerCapture(e.pointerId); } catch { }
                const startY = e.clientY;
                const startH = el.getBoundingClientRect().height;
                const move = (ev) => {
                    const h = Math.max(96, Math.min(window.innerHeight * 0.6, startH + ev.clientY - startY));
                    el.style.height = h + 'px';
                };
                const up = () => {
                    handle.removeEventListener('pointermove', move);
                    handle.removeEventListener('pointerup', up);
                    dotnetRef.invokeMethodAsync('OnStripResized', el.getBoundingClientRect().height);
                };
                handle.addEventListener('pointermove', move);
                handle.addEventListener('pointerup', up);
            });
        },

        // Points a <video> at a recording segment and keeps it at the wanted
        // offset. Tolerance while playing avoids constant reseeks (the video
        // advances on its own); paused scrubbing snaps tightly.
        tlSync(videoId, url, offset, playing) {
            const v = document.getElementById(videoId);
            if (!v) return;
            v.muted = true;
            if (!url) {
                delete v.dataset.tlUrl;
                if (v.getAttribute('src')) { v.removeAttribute('src'); try { v.load(); } catch { } }
                return;
            }
            if (v.dataset.tlUrl !== url) {
                v.dataset.tlUrl = url;
                v.src = url;
                try { v.load(); } catch { }
                v.currentTime = offset;
            } else {
                const tolerance = playing ? 1.5 : 0.35;
                if (Math.abs(v.currentTime - offset) > tolerance) v.currentTime = offset;
            }
            if (playing) { if (v.paused) v.play().catch(() => { }); }
            else if (!v.paused) v.pause();
        },

        // Pointer-drag scrubbing on the timeline lanes: reports the horizontal
        // fraction (0..1) to Blazor, throttled so dragging doesn't flood SignalR.
        tlScrubInit(elemId, dotnetRef) {
            this.tlScrubDispose(elemId);
            const el = document.getElementById(elemId);
            if (!el) return;
            let lastSent = 0;
            const send = (e, final) => {
                const now = performance.now();
                if (!final && now - lastSent < 70) return;
                lastSent = now;
                const r = el.getBoundingClientRect();
                const f = Math.min(1, Math.max(0, (e.clientX - r.left) / r.width));
                dotnetRef.invokeMethodAsync('OnTimelineScrub', f, final);
            };
            const down = (e) => {
                if (e.button !== 0 && e.pointerType === 'mouse') return;
                e.preventDefault();
                try { el.setPointerCapture(e.pointerId); } catch { }
                send(e, false);
                const move = (ev) => send(ev, false);
                const up = (ev) => {
                    el.removeEventListener('pointermove', move);
                    el.removeEventListener('pointerup', up);
                    send(ev, true);
                };
                el.addEventListener('pointermove', move);
                el.addEventListener('pointerup', up);
            };
            el.addEventListener('pointerdown', down);
            scrubs[elemId] = { el, down };
        },
        tlScrubDispose(elemId) {
            const s = scrubs[elemId];
            if (s) { s.el.removeEventListener('pointerdown', s.down); delete scrubs[elemId]; }
        },

        attach(videoId, wsUrl) {
            this.detach(videoId);
            const video = document.getElementById(videoId);
            if (video) players[videoId] = new Player(video, wsUrl);
        },
        detach(videoId) {
            const p = players[videoId];
            if (p) { p.destroy(); delete players[videoId]; }
        },
        // Toggle: the same tile button enters and leaves browser fullscreen.
        fullscreen(elementId) {
            if (document.fullscreenElement) {
                document.exitFullscreen().catch(() => { });
                return;
            }
            const el = document.getElementById(elementId);
            if (el?.requestFullscreen) el.requestFullscreen().catch(() => { });
        },
        exitFullscreen() {
            if (document.fullscreenElement) document.exitFullscreen().catch(() => { });
        },
        defaultServer() {
            // The UI is served by Neolink.Server itself: API is on the same origin.
            return location.origin;
        },
        lsGet(key) { return localStorage.getItem(key); },
        lsSet(key, value) { localStorage.setItem(key, value); },
    };
})();
