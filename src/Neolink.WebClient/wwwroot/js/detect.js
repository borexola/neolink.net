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

    const FONT = 'ui-sans-serif, system-ui, -apple-system, sans-serif';

    // Familiar things get a glyph instead of a word: a wall of tiles is read at a
    // glance, and "person" spelt out on every box is noise where a figure is not.
    // Drawn from stroke paths in the UI's own icon style, not the platform's
    // emoji: the same figure on every device, and no colour of its own on a
    // coloured pill. Animals of every kind share the paw.
    const GLYPHS = {
        person: 'M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2 M16 7a4 4 0 1 1-8 0 4 4 0 0 1 8 0',
        car: 'M5 17H3v-4l2-5a2 2 0 0 1 1.9-1.3h10.2A2 2 0 0 1 19 8l2 5v4h-2 M3 13h18 M9 17a2 2 0 1 1-4 0 2 2 0 0 1 4 0 M19 17a2 2 0 1 1-4 0 2 2 0 0 1 4 0',
        truck: 'M1 3h15v13H1z M16 8h4l3 3v5h-7z M8 18.5a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0 M21 18.5a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0',
        bus: 'M6 3h12a2 2 0 0 1 2 2v12H4V5a2 2 0 0 1 2-2z M4 11h16 M7 20v-3 M17 20v-3 M8 14.5h.01 M16 14.5h.01',
        bicycle: 'M9 17.5a3.5 3.5 0 1 1-7 0 3.5 3.5 0 0 1 7 0 M22 17.5a3.5 3.5 0 1 1-7 0 3.5 3.5 0 0 1 7 0 M5.5 17.5L9 9h5l4.5 8.5 M9 9l3 8.5H5.5 M12 17.5L15 9 M13 5h3',
        paw: 'M12 20c-3 0-5.5-1.8-5.5-4 0-1.5 1-2.5 2-3.5s1.5-2.5 3.5-2.5 2.5 1.5 3.5 2.5 2 2 2 3.5c0 2.2-2.5 4-5.5 4z M7.6 9a1.6 1.6 0 1 1-3.2 0 1.6 1.6 0 0 1 3.2 0 M11.1 5.5a1.6 1.6 0 1 1-3.2 0 1.6 1.6 0 0 1 3.2 0 M16.1 5.5a1.6 1.6 0 1 1-3.2 0 1.6 1.6 0 0 1 3.2 0 M19.6 9a1.6 1.6 0 1 1-3.2 0 1.6 1.6 0 0 1 3.2 0',
    };
    const GLYPH_FOR = { person: 'person', car: 'car', truck: 'truck', bus: 'bus', bicycle: 'bicycle', motorcycle: 'bicycle' };
    const PATHS = {};
    function glyphFor(label) {
        const g = GLYPH_FOR[label] || (GROUPS[label] === 'animals' ? 'paw' : null);
        if (g && !PATHS[g]) PATHS[g] = new Path2D(GLYPHS[g]);
        return g;
    }

    // Nothing is drawn INSIDE a box: no tint, no glow, no blur over the picture.
    // What is in the box is a face or a number plate, and the outline's whole job
    // is to point at it. Legibility over snow or headlights comes from a crisp
    // dark line under the coloured one instead.
    const UNDER = 'rgba(0, 0, 0, 0.65)';

    function roundRect(ctx, x, y, w, h, r) {
        ctx.beginPath();
        ctx.moveTo(x + r, y);
        ctx.arcTo(x + w, y, x + w, y + h, r);
        ctx.arcTo(x + w, y + h, x, y + h, r);
        ctx.arcTo(x, y + h, x, y, r);
        ctx.arcTo(x, y, x + w, y, r);
        ctx.closePath();
    }

    const INPUT = 640;       // the model's fixed input square
    const MAX_BOXES = 300;   // what YOLOv10's end-to-end head emits
    const PAD = '#727272';   // the grey the model was trained to letterbox with
    // A zoomed-in slice is magnified to fill the input square, which adds no
    // detail past a point: below this the slice is widened instead.
    const MIN_CROP = INPUT / 2;

    // Boxes are matched to the previous pass so they can be steadied and held.
    const MATCH_IOU = 0.2;    // overlap that means "the same thing"
    const MATCH_REACH = 0.6;  // ...or, for a fast mover, this much of a box away
    const CONFIRM = 2;        // passes before a new box is drawn
    const SURE = 0.15;        // ...unless it is this far above the threshold
    const HOLD = 2;           // passes a lost box stays on screen
    const MAX_TRACKS = 64;

    // A detection describes the frame the model was given, which by the time the
    // box is drawn is already old — one pass of work, plus the wait until the next
    // one. At a few looks a second that is most of a second's worth of walking, and
    // a box pinned to where something WAS reads as lag. Each track carries its own
    // speed instead, and the overlay redraws every animation frame from it.
    const JITTER = 0.02;      // movement under this share of the box is noise
    const VSMOOTH = 0.5;      // weight of the newest speed reading
    const LEAD = 0.9;         // how much of that speed to carry forward
    const MAX_LEAD_MS = 500;  // never guess further ahead than this

    // How much of a box must fall inside the camera's watched cells before it is
    // drawn. Not zero: a person standing on the line of their own driveway zone
    // must still be outlined. Not high either: something mostly out of the zone
    // is exactly what the zone was drawn to ignore.
    const ZONE_COVER = 0.25;
    // Room left around the zone when the frame is cropped to it. Someone standing
    // on the line has to arrive whole: a box cut off at the crop edge would both
    // look wrong and measure as entirely inside.
    const ZONE_PAD = 0.1;

    // One session and one scratch canvas for the whole page: only ever one tile
    // is being watched, and a second WebGPU context would cost real memory.
    let ortPromise = null;
    let sessionPromise = null;
    let sessionBase = null;
    let engine = null;
    let model = 'standard';
    // A device that asked for the detailed model and cannot keep up with it drops
    // back for the rest of the page: three passes over budget is not a blip.
    let tooSlow = 0;
    let scratch = null;
    let scratchCtx = null;
    let input = null;

    // camera name -> the detection-zone grids the server read off that camera.
    // Pushed separately from the per-render config: a grid is a few thousand
    // characters, and the config crosses the circuit on every render.
    const zones = Object.create(null);

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

    /// The model to actually load. The detailed one is three times the work: on a
    /// machine with no usable GPU that is not a slower overlay, it is a pegged core,
    /// so those devices quietly stay on the small model.
    async function pickModel(file) {
        if (file !== 'yolov10s.onnx') return file;
        try {
            if (navigator.gpu && await navigator.gpu.requestAdapter()) return file;
        } catch { }
        return 'yolov10n.onnx';
    }

    async function loadSession(base, want) {
        const file = await pickModel(want || 'yolov10n.onnx');
        const key = base + file;
        if (sessionPromise && sessionBase === key) return sessionPromise;
        sessionBase = key;
        sessionPromise = (async () => {
            const ort = await loadOrt(base);
            // WebGPU first, wasm as the fallback — the same file serves both, so a
            // machine without WebGPU still gets boxes, just slower.
            // Asked for BEFORE the session, and reported as what the badge says:
            // navigator.gpu existing is not the same as a usable adapter, and a
            // badge claiming a GPU that never ran is worse than no badge.
            let adapter = null;
            try { adapter = navigator.gpu ? await navigator.gpu.requestAdapter() : null; } catch { }
            const session = await ort.InferenceSession.create(base + file, {
                executionProviders: adapter ? ['webgpu', 'wasm'] : ['wasm'],
                graphOptimizationLevel: 'all',
            });
            engine = adapter ? 'webgpu' : 'cpu';
            model = file === 'yolov10s.onnx' ? 'detailed' : 'standard';
            return session;
        })().catch((e) => { sessionPromise = null; throw e; });
        return sessionPromise;
    }

    function ensureScratch() {
        if (scratch) return;
        scratch = document.createElement('canvas');
        scratch.width = scratch.height = INPUT;
        scratchCtx = scratch.getContext('2d', { willReadFrequently: true });
        // A camera frame is several times the model's square, and the cheap
        // downscale drops a distant figure into aliasing before the model ever
        // sees it. This is the whole difference between spotting someone at the
        // end of a drive and not.
        scratchCtx.imageSmoothingEnabled = true;
        scratchCtx.imageSmoothingQuality = 'high';
        input = new Float32Array(3 * INPUT * INPUT);
    }

    /// The digital-zoom transform the zoom helper writes on the video
    /// ("translate(tx, ty) scale(z)", origin 0 0), or the identity.
    function transformOf(video) {
        const t = video.style.transform || '';
        const s = /scale\(([\d.]+)\)/.exec(t);
        const p = /translate\((-?[\d.]+)px,\s*(-?[\d.]+)px\)/.exec(t);
        return {
            z: Math.max(1, s ? parseFloat(s[1]) || 1 : 1),
            tx: p ? parseFloat(p[1]) || 0 : 0,
            ty: p ? parseFloat(p[2]) || 0 : 0,
        };
    }

    function zoomOf(video) {
        return transformOf(video).z;
    }

    /// Widens a slice that would have to be magnified too far, around its own
    /// centre: past a point the extra pixels are invention, not detail.
    function grow(rect, vw, vh) {
        const g = MIN_CROP / Math.max(rect.sw, rect.sh);
        if (g <= 1) return rect;
        const cx = rect.sx + rect.sw / 2, cy = rect.sy + rect.sh / 2;
        const sw = Math.min(vw, rect.sw * g), sh = Math.min(vh, rect.sh * g);
        return {
            sx: Math.min(Math.max(0, cx - sw / 2), vw - sw),
            sy: Math.min(Math.max(0, cy - sh / 2), vh - sh),
            sw, sh,
        };
    }

    // The slice of the frame the viewer can see, in camera pixels. Zoomed in that
    // is a sub-rectangle: the model always gets a 640 square, so feeding it the
    // visible quarter of the frame instead of all of it is four times the detail
    // on whatever they zoomed in to look at, for the same work.
    function viewOf(video) {
        const vw = video.videoWidth, vh = video.videoHeight;
        const full = { sx: 0, sy: 0, sw: vw, sh: vh };
        const { z, tx, ty } = transformOf(video);
        const bw = video.clientWidth, bh = video.clientHeight;
        if (z <= 1.001 || !bw || !bh) return full;
        // The frame sits letterboxed inside the element box (object-fit: contain);
        // undo that, then the transform, to land back in camera pixels.
        const fit = Math.min(bw / vw, bh / vh);
        const ox = (bw - vw * fit) / 2, oy = (bh - vh * fit) / 2;
        let x0 = ((0 - tx) / z - ox) / fit, x1 = ((bw - tx) / z - ox) / fit;
        let y0 = ((0 - ty) / z - oy) / fit, y1 = ((bh - ty) / z - oy) / fit;
        x0 = Math.max(0, x0); y0 = Math.max(0, y0);
        x1 = Math.min(vw, x1); y1 = Math.min(vh, y1);
        const sw = x1 - x0, sh = y1 - y0;
        if (!(sw > 1) || !(sh > 1)) return full;
        return grow({ sx: x0, sy: y0, sw, sh }, vw, vh);
    }

    // The slice as the model wants it: letterboxed into the top-left of a 640
    // square (aspect kept, the rest padded), RGB planes, 0..1.
    function frameTensor(ort, video, view) {
        ensureScratch();
        const scale = INPUT / Math.max(view.sw, view.sh);
        const w = Math.round(view.sw * scale);
        const h = Math.round(view.sh * scale);
        scratchCtx.fillStyle = PAD;
        scratchCtx.fillRect(0, 0, INPUT, INPUT);
        scratchCtx.drawImage(video, view.sx, view.sy, view.sw, view.sh, 0, 0, w, h);
        const px = scratchCtx.getImageData(0, 0, INPUT, INPUT).data;
        const area = INPUT * INPUT;
        for (let i = 0; i < area; i++) {
            const p = i * 4;
            input[i] = px[p] / 255;
            input[area + i] = px[p + 1] / 255;
            input[2 * area + i] = px[p + 2] / 255;
        }
        return {
            tensor: new ort.Tensor('float32', input, [1, 3, INPUT, INPUT]),
            scale, ox: view.sx, oy: view.sy,
        };
    }

    // Output rows are [x1, y1, x2, y2, score, class] in the 640 square — undo the
    // letterbox and the crop and they are back in the camera's own pixels, which
    // is the one coordinate space everything downstream (tracking, zones, paint)
    // shares whatever the viewer has zoomed to.
    function readBoxes(out, geom, cfg) {
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
                x: geom.ox + d[o] / geom.scale, y: geom.oy + d[o + 1] / geom.scale,
                w: (d[o + 2] - d[o]) / geom.scale, h: (d[o + 3] - d[o + 1]) / geom.scale,
                label, group, score,
            });
        }
        return found;
    }

    // ---------------------------------------------------------------- tracking

    // Each pass is independent, which on its own looks like it: a box shivers on a
    // parked car, a one-frame mistake flashes up, and someone the model loses for
    // a single pass blinks out. Carrying boxes across passes fixes all three, and
    // costs nothing — no second look at the picture.

    const cx = (b) => b.x + b.w / 2;
    const cy = (b) => b.y + b.h / 2;

    function affinity(t, b) {
        const ix = Math.max(0, Math.min(t.x + t.w, b.x + b.w) - Math.max(t.x, b.x));
        const iy = Math.max(0, Math.min(t.y + t.h, b.y + b.h) - Math.max(t.y, b.y));
        const inter = ix * iy;
        const union = t.w * t.h + b.w * b.h - inter;
        const iou = union > 0 ? inter / union : 0;
        if (iou >= MATCH_IOU) return 1 + iou;
        // A car crossing the view clears its own box between passes; distance
        // still says "that is the same thing, further along".
        const reach = Math.max(t.w, t.h, b.w, b.h) * MATCH_REACH;
        const d = Math.hypot(cx(t) - cx(b), cy(t) - cy(b));
        return reach > 0 && d <= reach ? 1 - d / reach : 0;
    }

    function merge(t, b, cfg, now) {
        const step = Math.hypot(cx(b) - cx(t), cy(b) - cy(t));
        if (step <= Math.max(t.w, t.h) * JITTER) {
            // Smaller than the model's own wobble: the thing is standing still, so
            // the box holds its place rather than shivering, and its speed decays.
            t.vx *= 0.5;
            t.vy *= 0.5;
        } else {
            // Believe the measurement, and learn how fast it is going from it. The
            // reading is averaged because a jittery speed throws the box about —
            // except the first one, which is all there is to go on and would
            // otherwise leave the box trailing for the next second.
            const dt = Math.max(1, now - t.at);
            const w = t.vx === 0 && t.vy === 0 ? 1 : VSMOOTH;
            t.vx += ((b.x - t.x) / dt - t.vx) * w;
            t.vy += ((b.y - t.y) / dt - t.vy) * w;
            t.x = b.x;
            t.y = b.y;
        }
        // Size is averaged: it wobbles more than position and matters less.
        t.w += (b.w - t.w) * 0.5;
        t.h += (b.h - t.h) * 0.5;
        t.at = now;
        t.label = b.label;
        t.score = b.score;
        t.hits++;
        t.misses = 0;
        t.seen = true;
        if (!t.shown && (t.hits >= CONFIRM || t.score >= cfg.minConfidence + SURE))
            t.shown = true;
    }

    /// Where a track is NOW rather than where the model last saw it. Only for
    /// something the model can still see (a lost box must not sail off on its last
    /// known speed) and only while the picture is actually moving.
    function predict(t, now, moving) {
        if (!moving || t.misses > 0) return t;
        const dt = Math.min(now - t.at, MAX_LEAD_MS);
        if (!(dt > 0)) return t;
        return { ...t, x: t.x + t.vx * dt * LEAD, y: t.y + t.vy * dt * LEAD };
    }

    function advance(state, video, dets, now) {
        // Boxes are in decoded pixels, so a stream that changes resolution under
        // the tile invalidates every one of them.
        if (state.frameW !== video.videoWidth || state.frameH !== video.videoHeight) {
            state.frameW = video.videoWidth;
            state.frameH = video.videoHeight;
            state.tracks = [];
        }
        const tracks = state.tracks || (state.tracks = []);
        for (const t of tracks) t.seen = false;
        for (const b of dets.slice().sort((p, q) => q.score - p.score)) {
            let best = null, bestScore = 0;
            for (const t of tracks) {
                if (t.seen || t.group !== b.group) continue;
                // Match against where the track should have got to, not where it
                // was last seen: a car clears its own box between looks, and
                // pairing it with its own past is what loses it.
                const dt = Math.min(now - t.at, MAX_LEAD_MS);
                const a = affinity(dt > 0
                    ? { x: t.x + t.vx * dt, y: t.y + t.vy * dt, w: t.w, h: t.h } : t, b);
                if (a > bestScore) { bestScore = a; best = t; }
            }
            if (best) {
                merge(best, b, state.cfg, now);
            } else {
                const t = { ...b, vx: 0, vy: 0, at: now, hits: 1, misses: 0, seen: true, shown: false };
                if (t.score >= state.cfg.minConfidence + SURE) t.shown = true;
                tracks.push(t);
            }
        }
        const alive = [];
        for (const t of tracks) {
            if (!t.seen && ++t.misses > HOLD) continue;
            alive.push(t);
        }
        alive.sort((p, q) => q.score - p.score);
        state.tracks = alive.slice(0, MAX_TRACKS);
        return state.tracks.filter((t) => t.shown);
    }

    // ------------------------------------------------------------------- zones

    function compileGrid(g) {
        const cols = g.cols | 0, rows = g.rows | 0, table = g.table || '';
        if (cols <= 0 || rows <= 0 || table.length !== cols * rows) return null;
        const cells = new Uint8Array(cols * rows);
        let watched = 0;
        // The watched cells' extent, as a fraction of the frame: what the model is
        // pointed at, so nothing outside the zone is even looked at.
        let c0 = cols, r0 = rows, c1 = -1, r1 = -1;
        for (let i = 0; i < cells.length; i++) {
            if (table.charCodeAt(i) !== 49) continue;
            cells[i] = 1;
            watched++;
            const c = i % cols, r = (i / cols) | 0;
            if (c < c0) c0 = c;
            if (c > c1) c1 = c;
            if (r < r0) r0 = r;
            if (r > r1) r1 = r;
        }
        return {
            cols, rows, cells,
            all: watched === cells.length,
            box: watched === 0 ? null
                : { u0: c0 / cols, v0: r0 / rows, u1: (c1 + 1) / cols, v1: (r1 + 1) / rows },
        };
    }

    /// The part of the frame that could hold a drawable box, as fractions of it:
    /// every grid in play, merged. Null means nothing constrains the search — an
    /// outlined group with no grid of its own may be found anywhere.
    function zoneBox(zone, groups) {
        if (!zone) return null;
        let u0 = 1, v0 = 1, u1 = 0, v1 = 0, any = false;
        for (const g of groups) {
            const grid = zone.byGroup[g];
            if (!grid || grid.all) return null;
            if (!grid.box) continue;
            any = true;
            u0 = Math.min(u0, grid.box.u0); v0 = Math.min(v0, grid.box.v0);
            u1 = Math.max(u1, grid.box.u1); v1 = Math.max(v1, grid.box.v1);
        }
        return any ? { u0, v0, u1, v1 } : { u0: 0, v0: 0, u1: 0, v1: 0 };
    }

    /// What to put through the model: the watched part of the frame, narrowed to
    /// what the viewer can see. Null when the two do not meet — a view holding no
    /// watched cells cannot produce a box, so the pass is skipped entirely.
    /// Zone-shaped rather than frame-shaped input is also free detail: a zone over
    /// a third of the view arrives at three times the size the whole frame would.
    function lookAt(video, zone, groups) {
        const view = viewOf(video);
        const box = zoneBox(zone, groups);
        if (!box) return view;
        const vw = video.videoWidth, vh = video.videoHeight;
        const pad = Math.max((box.u1 - box.u0) * vw, (box.v1 - box.v0) * vh) * ZONE_PAD;
        const sx = Math.max(view.sx, box.u0 * vw - pad);
        const sy = Math.max(view.sy, box.v0 * vh - pad);
        const sw = Math.min(view.sx + view.sw, box.u1 * vw + pad) - sx;
        const sh = Math.min(view.sy + view.sh, box.v1 * vh + pad) - sy;
        if (!(sw > 1) || !(sh > 1)) return null;
        return grow({ sx, sy, sw, sh }, vw, vh);
    }

    /// Whether enough of a box sits in cells the camera is set to watch. The grid
    /// spans the whole frame, so this is in normalised frame coordinates and holds
    /// however far the viewer has zoomed in.
    function inZone(grid, b, vw, vh) {
        if (!grid || grid.all) return true;
        const cw = vw / grid.cols, ch = vh / grid.rows;
        const x0 = Math.max(0, b.x), y0 = Math.max(0, b.y);
        const x1 = Math.min(vw, b.x + b.w), y1 = Math.min(vh, b.y + b.h);
        const area = (x1 - x0) * (y1 - y0);
        if (!(area > 0)) return false;
        const c0 = Math.max(0, Math.floor(x0 / cw)), c1 = Math.min(grid.cols - 1, Math.floor((x1 - 1e-6) / cw));
        const r0 = Math.max(0, Math.floor(y0 / ch)), r1 = Math.min(grid.rows - 1, Math.floor((y1 - 1e-6) / ch));
        let seen = 0;
        for (let r = r0; r <= r1; r++) {
            for (let c = c0; c <= c1; c++) {
                if (!grid.cells[r * grid.cols + c]) continue;
                const ow = Math.min(x1, (c + 1) * cw) - Math.max(x0, c * cw);
                const oh = Math.min(y1, (r + 1) * ch) - Math.max(y0, r * ch);
                if (ow > 0 && oh > 0) seen += ow * oh;
            }
        }
        return seen / area >= ZONE_COVER;
    }

    // :scope, so this only ever finds THIS surface's overlay — a plain descendant
    // search matches one belonging to any player on the page.
    const overlayIn = (host) => host && host.querySelector(':scope > .tile-detect');

    function overlayFor(video) {
        let canvas = overlayIn(video.parentElement);
        if (!canvas) {
            canvas = document.createElement('canvas');
            canvas.className = 'tile-detect';
            video.parentElement.appendChild(canvas);
        }
        return canvas;
    }

    // A <video> made fullscreen on its own shows ITS pixels and nothing else: the
    // overlay is a sibling, so the boxes (and every other on-video control) are
    // left behind on the page underneath. The player's own fullscreen button does
    // exactly that. Handing fullscreen to the element that holds them both keeps
    // everything together — and is what the wall's own button already does.
    document.addEventListener('fullscreenchange', () => {
        const el = document.fullscreenElement;
        if (!el || el.tagName !== 'VIDEO') return;
        const host = el.parentElement;
        if (!overlayIn(host)) return;
        host.requestFullscreen?.().catch(() => { });
    });

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
        const px = (n) => n / zoom;
        const left = ox, right = ox + vw * fit;
        ctx.textBaseline = 'middle';
        ctx.lineJoin = ctx.lineCap = 'round';
        for (const b of boxes) {
            const x = ox + b.x * fit, y = oy + b.y * fit;
            const w = b.w * fit, h = b.h * fit;
            const color = COLORS[b.group] || COLORS.other;
            const r = Math.max(0, Math.min(px(12), w / 4, h / 4));
            ctx.save();
            // A box whose object the model lost for a pass fades instead of
            // blinking out: someone stepping behind a pillar is still there.
            ctx.globalAlpha = b.misses > 0 ? 0.45 : 1;

            roundRect(ctx, x, y, w, h, r);
            // A hairline of dark on each side of the colour, not a band: enough to
            // separate the line from a bright wall, invisible against a dark one.
            ctx.strokeStyle = UNDER;
            ctx.lineWidth = px(2.6);
            ctx.stroke();
            ctx.strokeStyle = color;
            ctx.lineWidth = px(1.6);
            ctx.stroke();

            // Weighted corners read as a frame drawn around something, while the
            // sides stay thin enough to watch through. Only where there is room:
            // on a distant figure the corners would be the whole box.
            const arm = Math.min(w, h) * 0.22;
            if (Math.min(w, h) > px(40)) {
                ctx.lineWidth = px(3);
                ctx.beginPath();
                for (const [ax, sx] of [[x, 1], [x + w, -1]]) {
                    for (const [ay, sy] of [[y, 1], [y + h, -1]]) {
                        ctx.moveTo(ax + sx * r, ay);
                        ctx.lineTo(ax + sx * (r + arm), ay);
                        ctx.moveTo(ax, ay + sy * r);
                        ctx.lineTo(ax, ay + sy * (r + arm));
                    }
                }
                ctx.stroke();
            }

            // The label sits above the box, or tucks inside when the box is against
            // the top edge, and never hangs off the side of the picture.
            const fs = px(11), gs = px(13);
            const glyph = glyphFor(b.label);
            const pct = Math.round(b.score * 100) + '%';
            ctx.font = '600 ' + fs + 'px ' + FONT;
            const nameW = glyph ? gs : ctx.measureText(b.label).width;
            ctx.font = '500 ' + fs * 0.85 + 'px ' + FONT;
            const pctW = ctx.measureText(pct).width;
            const padX = px(7), gap = px(5), ph = px(17);
            const pw = nameW + gap + pctW + padX * 2;
            const lx = Math.min(Math.max(left, x - px(1)), Math.max(left, right - pw));
            const ly = y - ph - px(5) < oy ? y + px(5) : y - ph - px(5);
            roundRect(ctx, lx, ly, pw, ph, ph / 2);
            ctx.fillStyle = color;
            ctx.fill();
            ctx.fillStyle = '#0b0d12';
            if (glyph) {
                ctx.save();
                ctx.translate(lx + padX, ly + (ph - gs) / 2);
                ctx.scale(gs / 24, gs / 24);
                ctx.strokeStyle = '#0b0d12';
                ctx.lineWidth = 2.4;
                ctx.lineCap = 'round';
                ctx.lineJoin = 'round';
                ctx.stroke(PATHS[glyph]);
                ctx.restore();
            } else {
                ctx.font = '600 ' + fs + 'px ' + FONT;
                ctx.fillText(b.label, lx + padX, ly + ph / 2);
            }
            // The score is a footnote to the label, not a second heading.
            ctx.globalAlpha *= 0.65;
            ctx.font = '500 ' + fs * 0.85 + 'px ' + FONT;
            ctx.fillText(pct, lx + padX + nameW + gap, ly + ph / 2);
            ctx.restore();
        }
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
        if (state.raf != null) cancelAnimationFrame(state.raf);
        state.raf = null;
        state.canvas?.remove();
        state.badge?.remove();
    }

    /// The overlay repaints on every animation frame, not on every detection: the
    /// model looks a few times a second, but the boxes have to move with the
    /// picture in between or they read as lag. Free of the model — it is a handful
    /// of strokes — and it stops by itself when the tab is hidden.
    function ensurePainter(state) {
        if (state.raf != null) return;
        const frame = () => {
            state.raf = null;
            if (state.stopped) return;
            const video = document.getElementById(state.videoId);
            if (video && state.canvas && state.drawn) {
                const now = performance.now();
                const moving = !video.paused && !video.ended;
                paint(state, video, state.drawn.map((t) => predict(t, now, moving)));
            }
            state.raf = requestAnimationFrame(frame);
        };
        state.raf = requestAnimationFrame(frame);
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
            state.tracks = [];
            state.drawn = null;
            again(500);
            return;
        }
        state.canvas = state.canvas || overlayFor(video);
        ensurePainter(state);
        // A paused clip is one still frame and the model has already answered for
        // it — the painter keeps the boxes on screen, and looking again would cost
        // everything for nothing. Seeking moves currentTime and the work resumes.
        if (video.paused && state.drawn && state.frameTime === video.currentTime) {
            again(250);
            return;
        }
        try {
            const session = await loadSession(state.cfg.base, state.cfg.model);
            if (state.stopped) return;
            const ort = window.ort;
            const started = performance.now();
            const zone = zones[state.cfg.camera];
            const look = lookAt(video, zone, state.cfg.groups);
            if (!look) {
                // Nothing the camera watches is on screen: there is no box to find.
                state.tracks = [];
                state.drawn = [];
                badge(state, 'nothing watched in view');
                again(Math.max(0, (1000 / state.cfg.fps) - (performance.now() - started)));
                return;
            }
            const geom = frameTensor(ort, video, look);
            const out = await session.run({ images: geom.tensor });
            if (state.stopped) return;
            const tracks = advance(state, video,
                readBoxes(out[session.outputNames[0]], geom, state.cfg), performance.now());
            // The crop points the model at the zone; this is what holds the line.
            // Filtered after tracking, so an object crossing the line appears and
            // disappears at the line instead of re-earning its box.
            const boxes = zone
                ? tracks.filter((b) => inZone(zone.byGroup[b.group], b, video.videoWidth, video.videoHeight))
                : tracks;
            badge(state, boxes.length + (boxes.length === 1 ? ' object · ' : ' objects · ') + engine
                + (model === 'detailed' ? ' · detailed' : '')
                + (zone && zone.masked ? ' · zone' : ''));
            state.drawn = boxes;
            state.frameTime = video.currentTime;
            state.failures = 0;
            // Pace from the END of the pass: a slow device simply detects less
            // often instead of queueing work it can never catch up with.
            const took = performance.now() - started;
            // ...and a device that asked for the detailed model but cannot run it at
            // anything like the asked-for rate goes back to the small one rather than
            // sit at one look a second forever.
            if (model === 'detailed' && took > 2000 / state.cfg.fps && ++tooSlow >= 3) {
                console.warn('neolink: the detailed model is too slow here — using the standard one');
                sessionPromise = null;
                sessionBase = null;
                state.cfg = { ...state.cfg, model: 'yolov10n.onnx' };
                tooSlow = 0;
            }
            again(Math.max(0, (1000 / state.cfg.fps) - took));
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
        /// The camera's detection zones, as the server read them off the camera:
        /// one entry per grid, naming the box groups it governs. Pushed before the
        /// config that starts a detector on that camera, so no box is ever drawn
        /// before the zone that might exclude it is known.
        zones(payload) {
            if (!payload || !payload.camera) return;
            const byGroup = Object.create(null);
            let masked = false;
            for (const g of payload.grids || []) {
                const grid = compileGrid(g);
                if (!grid) continue;
                masked = masked || !grid.all;
                for (const name of g.groups || []) byGroup[name] = grid;
            }
            zones[payload.camera] = { byGroup, masked };
        },

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
