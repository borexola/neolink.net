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
            // Tell the UI whether this stream carries audio — the speaker toggle
            // only shows for cameras that actually have some.
            if (meta.audio) this.video.dataset.audio = '1';
            else delete this.video.dataset.audio;
            window.neolink.audioSync();
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

    // Flat speaker SVGs (built once — audioSync runs on every render). Emoji
    // render inconsistently across platforms, so these are inline SVG.
    const _spk = (body) => '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" '
        + 'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" '
        + 'aria-hidden="true"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/>' + body + '</svg>';
    const VOL_ON = _spk('<path d="M15.54 8.46a5 5 0 0 1 0 7.07"/><path d="M19.07 4.93a10 10 0 0 1 0 14.14"/>');
    const VOL_OFF = _spk('<line x1="23" y1="9" x2="17" y2="15"/><line x1="17" y1="9" x2="23" y2="15"/>');

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
        // In-memory geometry is the truth; localStorage is only persistence.
        // Re-applying straight from localStorage raced the debounced save: a
        // Blazor render right after a drag re-read the OLD positions (snap-back),
        // which then "corrected themselves" once the 250ms write landed.
        geo: null,
        applying: false, // programmatic layout must not echo back into capture
        load() {
            if (!this.geo) {
                try { this.geo = JSON.parse(localStorage.getItem(this.key)) || {}; } catch { this.geo = {}; }
            }
            return this.geo;
        },
        save(geo) {
            this.geo = geo;
            clearTimeout(this.saveTimer);
            this.saveTimer = setTimeout(() => localStorage.setItem(this.key, JSON.stringify(geo)), 250);
        },
        // Geometry is stored as FRACTIONS of the stage, so freeform windows scale
        // with the browser instead of being stranded at fixed pixel spots.
        capture(stage) {
            if (this.applying) return;
            const W = stage.clientWidth, H = stage.clientHeight;
            if (!W || !H) return;
            const geo = this.load();
            stage.querySelectorAll('.tile').forEach(tile => {
                if (!tile.style.width) return;
                geo[tile.dataset.slot] = {
                    x: tile.offsetLeft / W, y: tile.offsetTop / H,
                    w: tile.offsetWidth / W, h: tile.offsetHeight / H,
                };
            });
            this.save(geo);
        },
        // Lays every (non-dragging) tile out from its stored fractions at the
        // stage's CURRENT size — also runs whenever the stage resizes.
        apply(stage) {
            const W = stage.clientWidth, H = stage.clientHeight;
            if (!W || !H) return;
            const geo = this.load();
            this.applying = true;
            let migrated = false;
            stage.querySelectorAll('.tile').forEach((tile, idx) => {
                if (tile.dataset.dragging) return;
                const slot = tile.dataset.slot ?? String(idx);
                let g = geo[slot] || {
                    x: (24 + 32 * idx) / W, y: (24 + 32 * idx) / H,
                    w: Math.min(0.55, 440 / W), h: Math.min(0.5, 280 / H),
                };
                // Legacy pixel geometry (pre-fractions): convert once and WRITE IT
                // BACK — re-normalizing the same pixels at a different stage size
                // would corrupt the layout.
                if (g.w > 1.5 || g.h > 1.5) {
                    g = { x: g.x / W, y: g.y / H, w: g.w / W, h: g.h / H };
                    geo[slot] = g;
                    migrated = true;
                }
                const w = Math.min(Math.max(g.w, 0.08), 1);
                const h = Math.min(Math.max(g.h, 0.08), 1);
                const x = Math.min(Math.max(g.x, 0), 1 - w);
                const y = Math.min(Math.max(g.y, 0), 1 - h);
                tile.style.left = Math.round(x * W) + 'px';
                tile.style.top = Math.round(y * H) + 'px';
                tile.style.width = Math.round(w * W) + 'px';
                tile.style.height = Math.round(h * H) + 'px';
            });
            if (migrated) this.save(geo);
            setTimeout(() => { this.applying = false; }, 80); // outlive the ResizeObserver echo
        },
    };

    function freeInit(stageId) {
        const stage = document.getElementById(stageId);
        if (!stage || !stage.classList.contains('mode-free')) return;

        free.apply(stage);

        // Follow the browser: when the stage resizes, re-lay the windows out
        // from their fractions so they grow/shrink with it.
        if (!stage.dataset.freeResize) {
            stage.dataset.freeResize = '1';
            new ResizeObserver(() => {
                if (stage.classList.contains('mode-free')) free.apply(stage);
            }).observe(stage);
        }

        stage.querySelectorAll('.tile').forEach((tile, idx) => {
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

    // ---------- monitor page (browser-side FPS/heap sampling state) ----------
    const perf = { raf: 0, frames: 0, windowStart: 0, fps: 0 };

    // ---------- monitor page (live server log tails) ----------
    const logTails = {};

    window.neolink = {
        freeInit,

        // Freeform geometry accessors for Blazor (saved layouts). They talk to
        // the LIVE in-memory state — a plain localStorage read could be a
        // debounce-interval behind what's on screen.
        freeGeometry() {
            return JSON.stringify(free.load());
        },
        freeSetGeometry(json) {
            try { free.geo = JSON.parse(json) || {}; } catch { free.geo = {}; }
            clearTimeout(free.saveTimer);
            localStorage.setItem(free.key, JSON.stringify(free.geo));
        },

        // Briefly flash a tile's border red — used when a camera the user picked
        // is already on screen (so we don't duplicate or clobber another tile).
        flashTile(id) {
            const el = document.getElementById(id);
            if (!el) return;
            el.classList.remove('tile-flash');
            void el.offsetWidth; // reflow so a repeat click restarts the animation
            el.classList.add('tile-flash');
            setTimeout(() => el.classList.remove('tile-flash'), 950);
        },

        // Ambient event previews (review strip): ensure real muting, fast-forward
        // playback rate and looped playback. Idempotent, called per render.
        trickle(speed) {
            document.querySelectorAll('video[data-trickle]').forEach(v => {
                v.muted = true;
                v.defaultPlaybackRate = v.playbackRate = speed || 4;
                if (v.paused) v.play().catch(() => { });
            });
        },

        // Drives the event player: points the <video> at `url` (full clip at 1×,
        // low-res preview when fast-forwarding), preserving the current position
        // across a source swap, and applies the playback rate. Idempotent — a
        // same-url call only updates the rate. Fast-forwarding the light preview
        // avoids the decoder stall that froze 2K/4K (H.265) clips at 2×/4×.
        eventPlayer(videoId, url, rate) {
            const v = document.getElementById(videoId);
            if (!v || !url) return;
            if (v.dataset.evUrl !== url) {
                const at = (v.currentTime && isFinite(v.currentTime)) ? v.currentTime : 0;
                v.dataset.evUrl = url;
                v.src = url;
                const onMeta = () => {
                    v.removeEventListener('loadedmetadata', onMeta);
                    try { if (at > 0 && at < v.duration) v.currentTime = at; } catch { }
                    v.defaultPlaybackRate = v.playbackRate = rate;
                    v.play().catch(() => { });
                };
                v.addEventListener('loadedmetadata', onMeta);
                try { v.load(); } catch { }
            } else {
                v.defaultPlaybackRate = v.playbackRate = rate;
            }
        },

        // Event-strip edge arrows: shown only while that side actually has more
        // to scroll, clicking pages the strip by most of a viewport. All state
        // lives here (classes on the wrapper) — no circuit traffic per scroll.
        stripNavInit(wrapId) {
            const wrap = document.getElementById(wrapId);
            if (!wrap || wrap.dataset.navInit) return;
            wrap.dataset.navInit = '1';
            const scroll = wrap.querySelector('.events-bar-scroll');
            if (!scroll) return;
            const update = () => {
                wrap.classList.toggle('can-left', scroll.scrollLeft > 2);
                wrap.classList.toggle('can-right',
                    scroll.scrollLeft + scroll.clientWidth < scroll.scrollWidth - 2);
            };
            scroll.addEventListener('scroll', update, { passive: true });
            new ResizeObserver(update).observe(scroll);            // strip/window resized
            new MutationObserver(update).observe(scroll, { childList: true }); // cards came/went
            const page = (dir) => scroll.scrollBy({ left: dir * scroll.clientWidth * 0.8, behavior: 'smooth' });
            wrap.querySelector('.strip-nav-left')?.addEventListener('click', () => page(-1));
            wrap.querySelector('.strip-nav-right')?.addEventListener('click', () => page(1));

            // Swipe/drag panning (mouse only): press anywhere on the strip and drag
            // to scroll it. Touch is left alone — the browser already pans natively.
            // A real drag (>5px) swallows the click on release so the card under
            // the cursor doesn't open, but a plain click still plays its event.
            const drag = { active: false, moved: false, startX: 0, startLeft: 0 };
            scroll.addEventListener('pointerdown', (e) => {
                if (e.pointerType !== 'mouse' || e.button !== 0) return;
                drag.active = true;
                drag.moved = false;
                drag.startX = e.clientX;
                drag.startLeft = scroll.scrollLeft;
            });
            scroll.addEventListener('pointermove', (e) => {
                if (!drag.active) return;
                const dx = e.clientX - drag.startX;
                if (!drag.moved) {
                    if (Math.abs(dx) < 5) return; // still just a (wobbly) click
                    drag.moved = true;
                    scroll.classList.add('strip-dragging');
                    try { scroll.setPointerCapture(e.pointerId); } catch { }
                }
                scroll.scrollLeft = drag.startLeft - dx;
            });
            const dragEnd = (e) => {
                if (!drag.active) return;
                drag.active = false;
                scroll.classList.remove('strip-dragging');
                try { scroll.releasePointerCapture(e.pointerId); } catch { }
            };
            scroll.addEventListener('pointerup', dragEnd);
            scroll.addEventListener('pointercancel', dragEnd);
            scroll.addEventListener('click', (e) => {
                if (!drag.moved) return;
                drag.moved = false; // this click ends the pan, nothing more
                e.stopPropagation();
                e.preventDefault();
            }, { capture: true });
            // Thumbnails are draggable by default; that would hijack the pan.
            scroll.addEventListener('dragstart', (e) => e.preventDefault());

            update();
        },

        // Esc dismisses the top-most pop-up (event player / quick view). The key
        // routes through the dialog's own Close button, so every Blazor-side
        // close effect (mark-reviewed on close, player teardown) runs exactly
        // as if it was clicked.
        escInit() {
            if (document.body.dataset.escInit) return;
            document.body.dataset.escInit = '1';
            document.addEventListener('keydown', (e) => {
                if (e.key !== 'Escape') return;
                // The tile context menu closes first (its backdrop is the closer).
                const ctx = document.querySelector('.ctx-backdrop');
                if (ctx) {
                    e.preventDefault();
                    ctx.click();
                    return;
                }
                const dialogs = document.querySelectorAll('.event-player, .quick-view');
                const btn = dialogs.length
                    ? dialogs[dialogs.length - 1].querySelector('.icon-btn[title="Close"]')
                    : null;
                if (btn) {
                    e.preventDefault();
                    btn.click();
                }
            });
        },

        // HTML5 drag sources must put SOMETHING in dataTransfer or Firefox never
        // starts the drag; the real drag state lives Blazor-side. Idempotent.
        dndInit() {
            if (document.body.dataset.dndInit) return;
            document.body.dataset.dndInit = '1';
            document.addEventListener('dragstart', (e) => {
                const src = e.target instanceof Element ? e.target.closest('[draggable="true"]') : null;
                if (src && e.dataTransfer) {
                    e.dataTransfer.setData('text/plain', src.id || 'neolink');
                    e.dataTransfer.effectAllowed = 'move';
                }
            });
        },

        // Grid-mode corner grips: dragging resizes a tile in whole grid-cell
        // steps (live inline preview; the span is committed to Blazor on release
        // and persisted with the view). All motion stays off the circuit.
        tileResizeInit(dotnetRef) {
            if (document.body.dataset.tileResizeInit) return;
            document.body.dataset.tileResizeInit = '1';
            document.addEventListener('pointerdown', (e) => {
                const handle = e.target instanceof Element ? e.target.closest('.tile-size-handle') : null;
                if (!handle) return;
                const tile = handle.closest('.tile');
                const grid = document.getElementById('stage-grid');
                if (!tile || !grid) return;
                e.preventDefault();
                e.stopPropagation();
                tile.draggable = false; // the grip resizes — it must not start a tile drag

                const colCount = getComputedStyle(grid).gridTemplateColumns.split(' ').length;
                const gap = parseFloat(getComputedStyle(grid).gap) || 8;
                const start = tile.getBoundingClientRect();
                const c0 = parseInt(tile.dataset.cols || '1');
                const r0 = parseInt(tile.dataset.rows || '1');
                const unitW = (start.width - gap * (c0 - 1)) / c0;
                const unitH = (start.height - gap * (r0 - 1)) / r0;
                let cols = c0, rows = r0;

                const move = (ev) => {
                    cols = Math.max(1, Math.min(colCount,
                        Math.round((ev.clientX - start.left + gap / 2) / (unitW + gap)) + 0));
                    rows = Math.max(1, Math.round((ev.clientY - start.top + gap / 2) / (unitH + gap)));
                    tile.style.gridColumn = `span ${cols}`;
                    tile.style.gridRow = `span ${rows}`;
                };
                const up = () => {
                    handle.removeEventListener('pointermove', move);
                    handle.removeEventListener('pointerup', up);
                    handle.removeEventListener('pointercancel', up);
                    dotnetRef.invokeMethodAsync('OnTileSpan', parseInt(tile.dataset.slot), cols, rows)
                        .catch(() => { });
                };
                try { handle.setPointerCapture(e.pointerId); } catch { }
                handle.addEventListener('pointermove', move);
                handle.addEventListener('pointerup', up);
                handle.addEventListener('pointercancel', up);
            });
        },

        // Live-audio mute toggles: cameras with AAC audio expose a speaker button
        // (tile bar / quick view). Videos start muted so autoplay always works;
        // clicking the toggle is the user gesture that lets sound through. JS owns
        // the state — audioSync re-stamps the glyphs after every Blazor render.
        audioInit() {
            if (!document.body.dataset.audioInit) {
                document.body.dataset.audioInit = '1';
                document.addEventListener('click', (e) => {
                    const btn = e.target instanceof Element ? e.target.closest('[data-audio-toggle]') : null;
                    if (!btn) return;
                    e.preventDefault();
                    e.stopPropagation(); // the tile underneath must not react
                    const video = btn.closest('.tile, .quick-view')?.querySelector('video');
                    if (!video) return;
                    video.muted = !video.muted;
                    if (!video.muted) video.volume = 1;
                    this.audioSync();
                }, { capture: true });
            }
            this.audioSync();
        },

        // Un-mutes one player (talk sessions auto-enable the camera's audio so the
        // conversation is two-way). Safe under autoplay rules: it only ever runs
        // from a user gesture (the mic button click started the session).
        unmute(videoId) {
            const v = document.getElementById(videoId);
            if (!v || !v.muted) return;
            v.muted = false;
            v.volume = 1;
            this.audioSync();
        },

        audioSync() {
            document.querySelectorAll('[data-audio-toggle]').forEach(btn => {
                const video = btn.closest('.tile, .quick-view')?.querySelector('video');
                const has = !!(video && video.dataset.audio);
                btn.style.display = has ? '' : 'none';
                if (!has) return;
                // Only touch the DOM when the muted state actually changed.
                const state = video.muted ? 'off' : 'on';
                if (btn.dataset.spk === state) return;
                btn.dataset.spk = state;
                btn.innerHTML = video.muted ? VOL_OFF : VOL_ON;
                btn.title = video.muted ? 'Unmute — this camera has audio' : 'Mute';
            });
        },

        // Digital zoom on live video (theater/maximized tiles, browser fullscreen,
        // and the quick-view pop-up): the mouse wheel zooms around the cursor so
        // the spot under it stays put, dragging pans while zoomed, the HUD pill
        // steps in/out and restores 1:1, and a double-click snaps back to 1:1.
        // All state lives DOM-side — nothing touches the Blazor circuit.
        zoomInit() {
            if (document.body.dataset.zoomInit) { this._zoomSweep?.(); return; }
            document.body.dataset.zoomInit = '1';

            const MAXZ = 8, STEP = 1.25;
            const states = new WeakMap();  // container -> { z, tx, ty }
            const zoomedBoxes = new Set(); // containers currently zoomed in (class
                                           // alone won't do: Blazor re-renders clobber it)
            const boxOf = (t) => (t instanceof Element ? t : null)
                ?.closest?.('.tile, .quick-view-media, .event-player-media');
            const eligible = (box) => !!box && !!box.querySelector('video') &&
                (box.classList.contains('zoom-on') || box === document.fullscreenElement);
            const stOf = (box) => {
                let s = states.get(box);
                if (!s) { s = { z: 1, tx: 0, ty: 0 }; states.set(box, s); }
                return s;
            };

            // Clamp the pan so the frame always covers the container, then paint.
            const apply = (box) => {
                const st = stOf(box);
                const v = box.querySelector('video');
                if (!v) return;
                st.z = Math.min(MAXZ, Math.max(1, st.z));
                st.tx = Math.min(0, Math.max(box.clientWidth * (1 - st.z), st.tx));
                st.ty = Math.min(0, Math.max(box.clientHeight * (1 - st.z), st.ty));
                if (st.z <= 1.001) {
                    st.z = 1; st.tx = 0; st.ty = 0;
                    v.style.transform = '';
                } else {
                    v.style.transformOrigin = '0 0';
                    v.style.transform = `translate(${st.tx}px, ${st.ty}px) scale(${st.z})`;
                }
                box.classList.toggle('zoomed', st.z > 1);
                if (st.z > 1) zoomedBoxes.add(box); else zoomedBoxes.delete(box);
                // The event player's native controls would scale with the frame and
                // fight the pan gesture — hide them while zoomed, restore at 1:1.
                if (box.classList.contains('event-player-media'))
                    v.controls = st.z <= 1;
                const badge = box.querySelector('[data-zoom-badge]');
                if (badge) badge.textContent = Math.round(st.z * 100) + '%';
            };
            // Rescale keeping the container point (px,py) fixed on screen.
            const zoomAt = (box, factor, px, py) => {
                const st = stOf(box);
                const z0 = st.z;
                st.z = Math.min(MAXZ, Math.max(1, z0 * factor));
                const k = st.z / z0;
                st.tx = px - k * (px - st.tx);
                st.ty = py - k * (py - st.ty);
                apply(box);
            };
            const reset = (box) => { const st = stOf(box); st.z = 1; st.tx = 0; st.ty = 0; apply(box); };
            // Layout changed under us (unmaximized, left fullscreen): back to 1:1.
            this._zoomSweep = () => [...zoomedBoxes]
                .forEach(b => { if (!b.isConnected || !eligible(b)) reset(b); });

            document.addEventListener('wheel', (e) => {
                const box = boxOf(e.target);
                if (!eligible(box)) return;
                e.preventDefault(); // the wheel is zoom here, never page scroll
                const r = box.getBoundingClientRect();
                zoomAt(box, e.deltaY < 0 ? STEP : 1 / STEP, e.clientX - r.left, e.clientY - r.top);
            }, { passive: false, capture: true });

            // HUD buttons — capture phase so the tile underneath never sees the click.
            document.addEventListener('click', (e) => {
                const btn = e.target instanceof Element ? e.target.closest('[data-zoom]') : null;
                if (!btn) return;
                const box = boxOf(btn);
                if (!box) return;
                e.preventDefault();
                e.stopPropagation();
                if (btn.dataset.zoom === 'reset') reset(box);
                else zoomAt(box, btn.dataset.zoom === 'in' ? STEP : 1 / STEP,
                    box.clientWidth / 2, box.clientHeight / 2);
            }, { capture: true });

            // Drag pans while zoomed; the click on release is swallowed so the
            // tile doesn't react to it.
            const pan = { box: null, moved: false, x: 0, y: 0 };
            document.addEventListener('pointerdown', (e) => {
                const box = boxOf(e.target);
                if (!eligible(box) || stOf(box).z <= 1) return;
                if (e.target.closest('.tile-bar, .zoom-hud, button, select')) return;
                pan.box = box; pan.moved = false; pan.x = e.clientX; pan.y = e.clientY;
                // While zoomed, dragging PANS — it must not start an HTML5 tile
                // drag (Blazor re-stamps draggable on the next render).
                if (box instanceof HTMLElement) box.draggable = false;
            }, { capture: true });
            document.addEventListener('pointermove', (e) => {
                if (!pan.box) return;
                const dx = e.clientX - pan.x, dy = e.clientY - pan.y;
                if (!pan.moved && Math.hypot(dx, dy) < 4) return; // still just a click
                pan.moved = true;
                pan.box.classList.add('zoom-panning');
                const st = stOf(pan.box);
                st.tx += dx; st.ty += dy;
                pan.x = e.clientX; pan.y = e.clientY;
                apply(pan.box);
            }, { capture: true });
            const panEnd = () => { pan.box?.classList.remove('zoom-panning'); pan.box = null; };
            document.addEventListener('pointerup', panEnd, { capture: true });
            document.addEventListener('pointercancel', panEnd, { capture: true });
            document.addEventListener('click', (e) => {
                if (!pan.moved) return;
                pan.moved = false; // the pan is over; this click is not a tile click
                e.stopPropagation();
                e.preventDefault();
            }, { capture: true });

            // Double-click zoomed video: back to 1:1 (swallowed so the tile's own
            // double-click — maximize/restore — only fires from an unzoomed state).
            document.addEventListener('dblclick', (e) => {
                const box = boxOf(e.target);
                if (!box || stOf(box).z <= 1) return;
                e.stopPropagation();
                e.preventDefault();
                reset(box);
            }, { capture: true });

            document.addEventListener('fullscreenchange', () => setTimeout(this._zoomSweep, 0));
            this._zoomSweep();
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
        // Batched sync: one interop call per clock tick instead of one per
        // camera. entries = [{id, url, offset, playing, rate}]; returns the
        // worst HEALTHY lag across all players (broken media excluded).
        tlSyncAll(entries) {
            let maxLag = 0;
            for (const e of entries || []) {
                const lag = this.tlSync(e.id, e.url, e.offset, e.playing, e.rate);
                if (lag > maxLag) maxLag = lag;
            }
            return maxLag;
        },

        // Returns the video's LAG: how many seconds the picture is behind the
        // timeline cursor (0 = keeping up / paused, -1 = broken media). Blazor
        // uses the worst healthy lag as feedback to slow the cursor down when
        // the server or the decoder can't sustain the chosen speed.
        tlSync(videoId, url, offset, playing, rate) {
            const v = document.getElementById(videoId);
            if (!v) return 0;
            // Busy veil: while the player has no decodable picture under the
            // cursor (first load, a seek into an unbuffered range, a stalled
            // fetch), the tile carries .tl-loading and CSS shows a spinner —
            // otherwise a slow segment just looks frozen.
            const tile = v.closest('.tl-tile');
            const busy = (b) => { if (tile) tile.classList.toggle('tl-loading', b); };
            v.muted = true;
            if (!url) {
                busy(false);
                delete v.dataset.tlUrl;
                if (v.getAttribute('src')) { v.removeAttribute('src'); try { v.load(); } catch { } }
                return 0;
            }
            const r = Math.max(0.25, Math.min(16, rate || 1));
            if (v.dataset.tlUrl !== url) {
                v.dataset.tlUrl = url;
                v.src = url;
                try { v.load(); } catch { }
                v.currentTime = offset;
            } else {
                // The clock ticks every 500 ms, so timer jitter scales with the
                // playback rate — widen the drift tolerance accordingly or fast
                // playback degrades into a seek-storm. (The adaptive clock keeps
                // real lag below this while playing; seeks stay exceptional.)
                const tolerance = playing ? Math.max(1.5, r * 0.75) : 0.35;
                if (Math.abs(v.currentTime - offset) > tolerance) v.currentTime = offset;
            }
            // AFTER the src/load branch: load() resets playbackRate to the default,
            // so set both — a new segment then starts at the chosen speed.
            try {
                if (v.defaultPlaybackRate !== r) v.defaultPlaybackRate = r;
                if (v.playbackRate !== r) v.playbackRate = r;
            } catch { }
            if (playing) { if (v.paused) v.play().catch(() => { }); }
            else if (!v.paused) v.pause();
            if (v.error) { busy(false); return -1; } // dead media must not hold the timeline hostage
            // Playing needs future data to keep moving; a paused scrub only needs
            // the current frame to be showing something.
            busy(v.seeking || v.readyState < (playing ? 3 : 2));
            return playing ? Math.max(0, offset - (v.currentTime || 0)) : 0;
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
        // Tells Blazor when browser fullscreen ends — Esc, the toggle button or
        // the browser's own UI all land here — so the fullscreen sub→main
        // stream switch can be reverted. Idempotent.
        fsWatch(dotnetRef) {
            if (document.body.dataset.fsWatch) return;
            document.body.dataset.fsWatch = '1';
            document.addEventListener('fullscreenchange', () => {
                if (!document.fullscreenElement)
                    dotnetRef.invokeMethodAsync('OnFullscreenExit').catch(() => { });
            });
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
        // ---------- two-way talk: microphone → camera speaker ----------
        // One session at a time. Opens the /api/talk WebSocket, sends a JSON
        // hello with the AudioContext's real sample rate (the server resamples
        // to whatever the camera wants), then streams Int16 LE PCM chunks.
        // State flows back to Blazor via OnTalkState("live"|"off"|"error").
        async talkStart(wsUrl, dotnetRef) {
            this.talkStop();
            const report = (state, detail) => {
                try { dotnetRef.invokeMethodAsync('OnTalkState', state, detail || null).catch(() => { }); } catch { }
            };
            if (!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia)) {
                report('error', 'microphone access needs HTTPS (or localhost)');
                return;
            }
            const s = { ws: null, ctx: null, stream: null, src: null, proc: null, report };
            this._talk = s;
            let stream;
            try {
                stream = await navigator.mediaDevices.getUserMedia({
                    audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true }
                });
            } catch (err) {
                if (this._talk === s) this._talk = null;
                report('error', err && err.name === 'NotAllowedError'
                    ? 'microphone permission denied'
                    : 'microphone unavailable');
                return;
            }
            if (this._talk !== s) { stream.getTracks().forEach(t => t.stop()); return; } // stopped meanwhile
            s.stream = stream;
            const ctx = new (window.AudioContext || window.webkitAudioContext)();
            s.ctx = ctx;
            let ws;
            try { ws = new WebSocket(wsUrl); } catch {
                this._talkTeardown(false);
                report('error', 'cannot open the talk connection');
                return;
            }
            s.ws = ws;
            ws.binaryType = 'arraybuffer';
            ws.onopen = () => {
                if (this._talk !== s) return;
                ws.send(JSON.stringify({ sampleRate: ctx.sampleRate }));
                // ScriptProcessor keeps this dependency-free; 2048 samples ≈ 43 ms
                // per chunk at 48 kHz, plenty tight for voice.
                const src = ctx.createMediaStreamSource(stream);
                const proc = ctx.createScriptProcessor(2048, 1, 1);
                proc.onaudioprocess = ev => {
                    if (ws.readyState !== WebSocket.OPEN) return;
                    const f32 = ev.inputBuffer.getChannelData(0);
                    const i16 = new Int16Array(f32.length);
                    for (let i = 0; i < f32.length; i++) {
                        const v = Math.max(-1, Math.min(1, f32[i]));
                        i16[i] = v < 0 ? v * 0x8000 : v * 0x7fff;
                    }
                    ws.send(i16.buffer);
                };
                src.connect(proc);
                proc.connect(ctx.destination); // processing needs a sink; output stays silent
                s.src = src;
                s.proc = proc;
                report('live');
            };
            ws.onclose = ev => {
                const t = this._talkTeardown(false);
                if (!t) return; // already stopped locally
                const reason = ev.reason || '';
                if (reason && reason !== 'bye') t.report('error', reason);
                else t.report('off');
            };
            ws.onerror = () => { /* onclose follows with the close reason */ };
        },
        talkStop() { this._talkTeardown(true); },
        _talkTeardown(reportOff) {
            const s = this._talk;
            if (!s) return null;
            this._talk = null;
            try { if (s.proc) { s.proc.onaudioprocess = null; s.proc.disconnect(); } } catch { }
            try { if (s.src) s.src.disconnect(); } catch { }
            try { if (s.stream) s.stream.getTracks().forEach(t => t.stop()); } catch { }
            try { if (s.ctx) s.ctx.close(); } catch { }
            try { if (s.ws && s.ws.readyState <= 1) s.ws.close(1000, 'bye'); } catch { }
            if (reportOff) s.report('off');
            return s;
        },

        // ---------- monitor page: browser-side resource sampling ----------
        // FPS is counted with a requestAnimationFrame loop (started on demand,
        // stopped when the page stops sampling); JS heap comes from the
        // non-standard performance.memory (Chromium only — -1 elsewhere).
        perfStart() {
            if (perf.raf) return;
            perf.frames = 0;
            perf.windowStart = performance.now();
            perf.fps = 0;
            const loop = (t) => {
                perf.frames++;
                if (t - perf.windowStart >= 1000) {
                    perf.fps = Math.round(perf.frames * 1000 / (t - perf.windowStart));
                    perf.frames = 0;
                    perf.windowStart = t;
                }
                perf.raf = requestAnimationFrame(loop);
            };
            perf.raf = requestAnimationFrame(loop);
        },
        perfStop() {
            if (perf.raf) cancelAnimationFrame(perf.raf);
            perf.raf = 0;
        },
        perfSample() {
            const m = performance.memory; // Chromium-only
            return {
                heap: m ? m.usedJSHeapSize : -1,
                heapLimit: m ? m.jsHeapSizeLimit : -1,
                fps: perf.raf ? perf.fps : -1,
                domNodes: document.getElementsByTagName('*').length,
            };
        },
        // No-op whose only purpose is to be awaited: the caller times the full
        // Blazor circuit round-trip (browser → SignalR → server → back).
        perfPing() { return 0; },

        // ---------- monitor page: chart hover tooltip ----------
        // Each MonChart body carries its series as JSON in data-hover; one
        // delegated mousemove drives a shared tooltip + per-chart crosshair, so
        // hovering costs zero circuit traffic. The crosshair snaps to the
        // nearest sample of the primary series.
        monHoverInit() {
            if (document.body.dataset.monHover) return;
            document.body.dataset.monHover = '1';
            let tip = null, cross = null, curBody = null;
            const hide = () => {
                if (tip) tip.style.display = 'none';
                if (cross) cross.remove();
                cross = null;
                curBody = null;
            };
            document.addEventListener('mousemove', (e) => {
                const body = e.target instanceof Element
                    ? e.target.closest('.mon-chart-body[data-hover]') : null;
                if (!body) { hide(); return; }
                // Re-parse only when the payload string actually changed (2s beat).
                const raw = body.dataset.hover;
                let data = body._monHover && body._monHover.raw === raw ? body._monHover.data : null;
                if (!data) {
                    try { data = JSON.parse(raw); } catch { hide(); return; }
                    body._monHover = { raw, data };
                }
                if (!data.s || !data.s.length || !(data.b > data.a)) { hide(); return; }
                const rect = body.getBoundingClientRect();
                const frac = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
                const t = data.a + frac * (data.b - data.a);
                let snapT = null;
                const rows = [];
                for (const s of data.s) {
                    let best = null, bd = Infinity;
                    for (const p of s.p) {
                        const d = Math.abs(p[0] - t);
                        if (d < bd) { bd = d; best = p; }
                    }
                    if (!best) continue;
                    if (snapT === null) snapT = best[0]; // primary series sets the crosshair
                    rows.push({ l: s.l, c: s.c, v: best[1] });
                }
                if (!rows.length || snapT === null) { hide(); return; }
                if (!tip) {
                    tip = document.createElement('div');
                    tip.className = 'mon-tip';
                    document.body.appendChild(tip);
                }
                const when = new Date(snapT).toLocaleTimeString(undefined, { hour12: false });
                // Labels/units/colors are app constants, never user input.
                tip.innerHTML = '<div class="mon-tip-time">' + when + '</div>' +
                    rows.map(r => '<div class="mon-tip-row"><i style="background:' + r.c + '"></i>' +
                        r.l + '<b>' + (Math.round(r.v * 100) / 100) + (data.u || '') + '</b></div>').join('');
                tip.style.display = 'block';
                let x = e.clientX + 14;
                if (x + tip.offsetWidth > innerWidth - 8) x = e.clientX - tip.offsetWidth - 14;
                let y = e.clientY - tip.offsetHeight - 12;
                if (y < 8) y = e.clientY + 16;
                tip.style.left = x + 'px';
                tip.style.top = y + 'px';
                // Crosshair lives inside the chart body; Blazor re-renders can
                // drop it, so recreate whenever it's gone.
                if (curBody !== body && cross) { cross.remove(); cross = null; }
                curBody = body;
                if (!cross || !cross.isConnected) {
                    cross = document.createElement('div');
                    cross.className = 'mon-cross';
                    body.appendChild(cross);
                }
                cross.style.left = ((snapT - data.a) / (data.b - data.a) * rect.width).toFixed(1) + 'px';
            }, { passive: true });
            document.addEventListener('mouseleave', hide);
            document.addEventListener('scroll', hide, { passive: true, capture: true });
        },

        // ---------- live server log tail (admin) ----------
        // Lines are appended straight into the DOM here — routing every log line
        // through the Blazor circuit would re-render the page per line. The view
        // auto-follows while scrolled to the bottom; scrolling up freezes it.
        logsAttach(containerId, httpUrl) {
            this.logsDetach(containerId);
            const el = document.getElementById(containerId);
            if (!el) return;
            const state = { alive: true, ws: null, timer: 0, lastSeq: 0 };
            logTails[containerId] = state;

            const append = (e) => {
                if (!e || e.seq <= state.lastSeq) return; // reconnect overlap: already shown
                state.lastSeq = e.seq;
                const pinned = el.scrollHeight - el.scrollTop - el.clientHeight < 48;
                const line = document.createElement('div');
                line.className = 'log-line log-' + String(e.lvl || 'inf').toLowerCase();
                const time = document.createElement('span');
                time.className = 'log-time';
                time.textContent = new Date(e.t).toLocaleTimeString(undefined, { hour12: false });
                const lvl = document.createElement('span');
                lvl.className = 'log-lvl';
                lvl.textContent = e.lvl;
                const msg = document.createElement('span');
                msg.className = 'log-msg';
                msg.textContent = e.msg; // textContent: log content can never become markup
                line.append(time, lvl, msg);
                el.appendChild(line);
                while (el.childElementCount > 2000) el.firstElementChild.remove();
                if (pinned) el.scrollTop = el.scrollHeight;
            };
            const retry = () => {
                if (!state.alive) return;
                clearTimeout(state.timer);
                state.timer = setTimeout(connect, 3000);
            };
            const connect = () => {
                if (!state.alive) return;
                try { state.ws = new WebSocket(httpUrl.replace(/^http/, 'ws')); }
                catch { retry(); return; }
                state.ws.onmessage = (ev) => {
                    try {
                        const data = JSON.parse(ev.data); // backlog = array, live = single entry
                        if (Array.isArray(data)) { data.forEach(append); el.scrollTop = el.scrollHeight; }
                        else append(data);
                    } catch { }
                };
                state.ws.onclose = () => retry();
                state.ws.onerror = () => { try { state.ws.close(); } catch { } };
            };
            connect();
        },
        logsDetach(containerId) {
            const s = logTails[containerId];
            if (!s) return;
            s.alive = false;
            clearTimeout(s.timer);
            try { s.ws && s.ws.close(); } catch { }
            delete logTails[containerId];
        },
        logsClear(containerId) {
            document.getElementById(containerId)?.replaceChildren();
        },

        defaultServer() {
            // The UI is served by Neolink.Server itself: API is on the same origin.
            return location.origin;
        },
        lsGet(key) { return localStorage.getItem(key); },
        lsSet(key, value) { localStorage.setItem(key, value); },
        // Session-scoped flags: things that must NOT survive to the next visit
        // (e.g. dismissing the "no sign-in" security banner).
        ssGet(key) { return sessionStorage.getItem(key); },
        ssSet(key, value) { sessionStorage.setItem(key, value); },
    };
})();
