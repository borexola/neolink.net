// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
//
// Live object boxes (preview): outlines what the model recognises on the camera
// you are watching, drawn on a canvas over the tile.
//
// It runs HERE, in the browser, and not on the server — a Reolink camera reports
// WHAT it detected and never where, so a box has to come from looking at pixels,
// and the server deliberately never decodes video. One camera at a time: the
// single-camera view (/cameras/{name}) is the only surface that asks for it, so
// one detector, one model session, one loop.
//
// This file is fetched only when the feature is switched on, so an install that
// leaves it off pays nothing. ONNX Runtime and the model come from the server's
// own /detect/asset/ (downloaded there once, checksum-pinned), never from a CDN
// at view time — a camera wall must keep working with no internet.
(function () {
    'use strict';

    // COCO-80, in the model's own class order.
    const LABELS = [
        'person', 'bicycle', 'car', 'motorcycle', 'airplane', 'bus', 'train', 'truck', 'boat',
        'traffic light', 'fire hydrant', 'stop sign', 'parking meter', 'bench', 'bird', 'cat',
        'dog', 'horse', 'sheep', 'cow', 'elephant', 'bear', 'zebra', 'giraffe', 'backpack',
        'umbrella', 'handbag', 'tie', 'suitcase', 'frisbee', 'skis', 'snowboard', 'sports ball',
        'kite', 'baseball bat', 'baseball glove', 'skateboard', 'surfboard', 'tennis racket',
        'bottle', 'wine glass', 'cup', 'fork', 'knife', 'spoon', 'bowl', 'banana', 'apple',
        'sandwich', 'orange', 'broccoli', 'carrot', 'hot dog', 'pizza', 'donut', 'cake', 'chair',
        'couch', 'potted plant', 'bed', 'dining table', 'toilet', 'tv', 'laptop', 'mouse',
        'remote', 'keyboard', 'cell phone', 'microwave', 'oven', 'toaster', 'sink', 'refrigerator',
        'book', 'clock', 'vase', 'scissors', 'teddy bear', 'hair drier', 'toothbrush',
    ];

    // The model knows 80 everyday objects; a camera view cares about four kinds of
    // thing. Anything unlisted falls into "other", which is off by default — a
    // driveway does not need its wheelie bin outlined as a "suitcase".
    const GROUPS = {
        person: 'people',
        bicycle: 'vehicles', car: 'vehicles', motorcycle: 'vehicles', bus: 'vehicles',
        train: 'vehicles', truck: 'vehicles', boat: 'vehicles', airplane: 'vehicles',
        bird: 'animals', cat: 'animals', dog: 'animals', horse: 'animals', sheep: 'animals',
        cow: 'animals', elephant: 'animals', bear: 'animals', zebra: 'animals', giraffe: 'animals',
    };

    const COLORS = {
        people: '#35d392', vehicles: '#5b9dff', animals: '#c084fc', other: '#94a3b8',
    };

    const INPUT = 640;       // the model's fixed input square
    const MAX_BOXES = 300;   // what YOLOv10's end-to-end head emits

    // One session and one scratch canvas for the whole page: only ever one tile
    // is being watched, and a second WebGPU context would cost real memory.
    let ortPromise = null;
    let sessionPromise = null;
    let sessionBase = null;
    let engine = null;
    let scratch = null;
    let scratchCtx = null;
    let input = null;

    // The running detector, or null. Held in one place so a re-sync can compare
    // what is wanted against what is actually running.
    let active = null;

    const loadScript = (src) => new Promise((resolve, reject) => {
        const el = document.createElement('script');
        el.src = src;
        el.onload = () => resolve();
        el.onerror = () => reject(new Error('could not load ' + src));
        document.head.appendChild(el);
    });

    async function loadOrt(base) {
        if (ortPromise) return ortPromise;
        ortPromise = (async () => {
            if (!window.ort) await loadScript(base + 'ort.webgpu.min.js');
            if (!window.ort) throw new Error('ONNX Runtime did not register itself');
            // The runtime fetches its own .mjs/.wasm — point it at the same folder.
            window.ort.env.wasm.wasmPaths = base;
            // Multi-threaded wasm needs cross-origin isolation (COOP/COEP), which a
            // LAN camera server has no business demanding of its own page: one
            // thread, and let WebGPU do the work where it exists.
            window.ort.env.wasm.numThreads = 1;
            window.ort.env.logLevel = 'error';
            return window.ort;
        })().catch((e) => { ortPromise = null; throw e; });
        return ortPromise;
    }

    async function loadSession(base) {
        if (sessionPromise && sessionBase === base) return sessionPromise;
        sessionBase = base;
        sessionPromise = (async () => {
            const ort = await loadOrt(base);
            // WebGPU first, wasm as the fallback — the same file serves both, so a
            // machine without WebGPU still gets boxes, just slower.
            // Asked for BEFORE the session, and reported as what the badge says:
            // navigator.gpu existing is not the same as a usable adapter, and a
            // badge claiming a GPU that never ran is worse than no badge.
            let adapter = null;
            try { adapter = navigator.gpu ? await navigator.gpu.requestAdapter() : null; } catch { }
            const session = await ort.InferenceSession.create(base + 'yolov10n.onnx', {
                executionProviders: adapter ? ['webgpu', 'wasm'] : ['wasm'],
                graphOptimizationLevel: 'all',
            });
            engine = adapter ? 'webgpu' : 'cpu';
            return session;
        })().catch((e) => { sessionPromise = null; throw e; });
        return sessionPromise;
    }

    function ensureScratch() {
        if (scratch) return;
        scratch = document.createElement('canvas');
        scratch.width = scratch.height = INPUT;
        scratchCtx = scratch.getContext('2d', { willReadFrequently: true });
        input = new Float32Array(3 * INPUT * INPUT);
    }

    // The frame as the model wants it: letterboxed into the top-left of a 640
    // square (aspect kept, the rest left black), RGB planes, 0..1.
    function frameTensor(ort, video) {
        ensureScratch();
        const scale = INPUT / Math.max(video.videoWidth, video.videoHeight);
        const w = Math.round(video.videoWidth * scale);
        const h = Math.round(video.videoHeight * scale);
        scratchCtx.fillStyle = '#000';
        scratchCtx.fillRect(0, 0, INPUT, INPUT);
        scratchCtx.drawImage(video, 0, 0, w, h);
        const px = scratchCtx.getImageData(0, 0, INPUT, INPUT).data;
        const area = INPUT * INPUT;
        for (let i = 0; i < area; i++) {
            const p = i * 4;
            input[i] = px[p] / 255;
            input[area + i] = px[p + 1] / 255;
            input[2 * area + i] = px[p + 2] / 255;
        }
        return { tensor: new ort.Tensor('float32', input, [1, 3, INPUT, INPUT]), scale };
    }

    // Output rows are [x1, y1, x2, y2, score, class] in the 640 square — divide by
    // the letterbox scale and they are back in the camera's own pixels.
    function readBoxes(out, scale, cfg) {
        const d = out.data;
        const rows = Math.min(MAX_BOXES, Math.floor(d.length / 6));
        const found = [];
        for (let i = 0; i < rows; i++) {
            const o = i * 6;
            const score = d[o + 4];
            if (score < cfg.minConfidence) continue;
            const label = LABELS[d[o + 5] | 0] || 'object';
            const group = GROUPS[label] || 'other';
            if (cfg.groups.indexOf(group) < 0) continue;
            found.push({
                x: d[o] / scale, y: d[o + 1] / scale,
                w: (d[o + 2] - d[o]) / scale, h: (d[o + 3] - d[o + 1]) / scale,
                label, group, score,
            });
        }
        return found;
    }

    function overlayFor(video) {
        let canvas = video.parentElement && video.parentElement.querySelector('.tile-detect');
        if (!canvas) {
            canvas = document.createElement('canvas');
            canvas.className = 'tile-detect';
            video.parentElement.appendChild(canvas);
        }
        return canvas;
    }

    // Boxes arrive in camera pixels; the tile shows the frame object-fit: contain,
    // so they need the same letterbox the browser applied. Digital zoom is a
    // transform on the <video>, so the canvas simply wears the same one.
    function paint(state, video, boxes) {
        const canvas = state.canvas;
        if (!canvas.isConnected) {
            // A Blazor re-render can drop our node from the tile.
            if (!video.parentElement) return;
            video.parentElement.appendChild(canvas);
        }
        const dpr = window.devicePixelRatio || 1;
        const bw = video.clientWidth, bh = video.clientHeight;
        if (!bw || !bh) return;
        if (canvas.width !== Math.round(bw * dpr) || canvas.height !== Math.round(bh * dpr)) {
            canvas.width = Math.round(bw * dpr);
            canvas.height = Math.round(bh * dpr);
        }
        canvas.style.transform = video.style.transform || '';
        canvas.style.transformOrigin = video.style.transformOrigin || '';

        const ctx = canvas.getContext('2d');
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, bw, bh);
        const vw = video.videoWidth, vh = video.videoHeight;
        if (!vw || !vh) return;
        const fit = Math.min(bw / vw, bh / vh);
        const ox = (bw - vw * fit) / 2, oy = (bh - vh * fit) / 2;
        // Strokes and text stay the same size on screen however far it is zoomed in.
        const zoom = zoomOf(video);
        ctx.lineWidth = 2 / zoom;
        ctx.font = (11 / zoom) + 'px ui-sans-serif, system-ui, sans-serif';
        ctx.textBaseline = 'bottom';
        for (const b of boxes) {
            const x = ox + b.x * fit, y = oy + b.y * fit;
            const w = b.w * fit, h = b.h * fit;
            const color = COLORS[b.group] || COLORS.other;
            ctx.strokeStyle = color;
            ctx.strokeRect(x, y, w, h);
            const text = b.label + ' ' + Math.round(b.score * 100) + '%';
            const pad = 3 / zoom, th = 14 / zoom;
            const tw = ctx.measureText(text).width + pad * 2;
            // Above the box, or just inside it when the box is against the top edge.
            const ty = y - th < 0 ? y + th : y;
            ctx.fillStyle = color;
            ctx.fillRect(x, ty - th, tw, th);
            ctx.fillStyle = '#0b0d12';
            ctx.fillText(text, x + pad, ty - pad / 2);
        }
    }

    /// The scale factor of the digital-zoom transform the zoom helper wrote on the
    /// video ("translate(...) scale(z)"), or 1 when it is not zoomed.
    function zoomOf(video) {
        const m = /scale\(([\d.]+)\)/.exec(video.style.transform || '');
        return m ? Math.max(1, parseFloat(m[1]) || 1) : 1;
    }

    function badge(state, text) {
        if (!state.badge) {
            state.badge = document.createElement('div');
            state.badge.className = 'tile-detect-badge';
        }
        if (!state.badge.isConnected) {
            const host = document.getElementById(state.videoId)?.parentElement;
            if (!host) return;
            host.appendChild(state.badge);
        }
        if (state.badge.textContent !== text) state.badge.textContent = text;
    }

    function clear(state) {
        state.stopped = true;
        clearTimeout(state.timer);
        state.canvas?.remove();
        state.badge?.remove();
    }

    async function run(state) {
        if (state.stopped) return;
        const again = (ms) => {
            if (!state.stopped) state.timer = setTimeout(() => run(state), ms);
        };
        const video = document.getElementById(state.videoId);
        // No tile, no picture, or nobody looking: the next tick asks again. The
        // hidden-tab check is what keeps a backgrounded wall from burning a GPU.
        if (!video || !video.videoWidth || video.readyState < 2 || document.hidden) {
            if (state.canvas) state.canvas.getContext('2d').clearRect(0, 0, state.canvas.width, state.canvas.height);
            again(500);
            return;
        }
        state.canvas = state.canvas || overlayFor(video);
        try {
            const session = await loadSession(state.cfg.base);
            if (state.stopped) return;
            const ort = window.ort;
            const started = performance.now();
            const { tensor, scale } = frameTensor(ort, video);
            const out = await session.run({ images: tensor });
            if (state.stopped) return;
            const boxes = readBoxes(out[session.outputNames[0]], scale, state.cfg);
            paint(state, video, boxes);
            badge(state, boxes.length + (boxes.length === 1 ? ' object · ' : ' objects · ') + engine);
            state.failures = 0;
            // Pace from the END of the pass: a slow device simply detects less
            // often instead of queueing work it can never catch up with.
            again(Math.max(0, (1000 / state.cfg.fps) - (performance.now() - started)));
        } catch (e) {
            state.failures = (state.failures || 0) + 1;
            if (state.failures === 1) console.warn('neolink: object detection failed —', e);
            badge(state, 'boxes unavailable');
            // Three strikes and this view gives up: an unsupported browser must not
            // sit in a retry loop for as long as the tile is open.
            if (state.failures >= 3) { clear(state); return; }
            again(2000);
        }
    }

    window.neolinkDetect = {
        /// The single call the page makes: `cfg` names the tile to draw on, or is
        /// null when nothing should be detected. Idempotent — it is called on every
        /// render, and only acts when what is wanted actually changed.
        sync(cfg) {
            const want = cfg && cfg.videoId ? cfg : null;
            const same = want && active && active.videoId === want.videoId
                && JSON.stringify(active.cfg) === JSON.stringify(want);
            if (same) return;
            if (active) { clear(active); active = null; }
            if (!want) return;
            active = { videoId: want.videoId, cfg: want, stopped: false, failures: 0 };
            badge(active, 'starting…');
            run(active);
        },
    };
})();
