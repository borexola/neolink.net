// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Neolink.Recording;
using Neolink.Streaming;

namespace Neolink.Ai;

/// <summary>
/// One event's frame set: while the event records, the camera's own JPEG
/// snapshot command is sampled — the Baichuan snap serves the low-res
/// sub-stream image, so no server-side video decoding is ever needed. Sampling
/// SPREADS across the whole event, however long it runs: it starts at one frame
/// per second, and whenever the frame budget fills, every other stored frame is
/// dropped and the interval doubles. The final set always spans the entire
/// event (a person leaving 30s in tells a different story than the first 10s),
/// stays within budget, and each frame remembers when it was taken so the model
/// can be told the real offsets. Frames live in memory only and die with the
/// capture unless the event completes and the job is submitted.
/// </summary>
public sealed class AiCapture
{
    private readonly ICameraControl _control;
    private readonly int _budget;
    private readonly CancellationTokenSource _stop;
    private volatile bool _discarded;
    private int _disposeArmed;

    internal string Camera { get; }
    internal List<(DateTime Utc, byte[] Jpeg)> Frames { get; } = new();
    internal Task Completion { get; }

    internal AiCapture(string camera, ICameraControl control, int budget, CancellationToken ct)
    {
        Camera = camera;
        _control = control;
        _budget = Math.Max(2, budget); // decimation needs headroom to halve
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Completion = Task.Run(RunAsync, CancellationToken.None);
    }

    /// <summary>Event over: stop sampling, keep what was captured. The loop may
    /// already be done (frame budget spent, or the camera gave up) — the CTS is
    /// only disposed via <see cref="DisposeWhenDone"/>, so Cancel stays legal.</summary>
    internal void Stop() => _stop.Cancel();

    /// <summary>Tentative event discarded: stop and never send anything.</summary>
    public void Cancel()
    {
        _discarded = true;
        _stop.Cancel();
        DisposeWhenDone(); // nobody will submit this capture — clean up here
    }

    internal bool Discarded => _discarded;

    /// <summary>Disposes the linked CTS once the capture loop has fully exited
    /// (its cancellation registration would otherwise outlive the event). Called
    /// exactly once, by whichever path ends this capture's life: the worker after
    /// processing, Cancel(), or a failed submit.</summary>
    internal void DisposeWhenDone()
    {
        if (Interlocked.Exchange(ref _disposeArmed, 1) != 0) return;
        Completion.ContinueWith(static (_, s) => ((CancellationTokenSource)s!).Dispose(),
            _stop, TaskScheduler.Default);
    }

    private async Task RunAsync()
    {
        int failures = 0;
        var interval = TimeSpan.FromSeconds(1); // doubles at every decimation
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var t0 = DateTime.UtcNow;
                try
                {
                    // Per-shot deadline so one hung command can't silently eat the
                    // whole window; SnapshotSmall is the size-limited variant (the
                    // HTTP API scales server-side, Baichuan answers sub-stream).
                    using var shot = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    shot.CancelAfter(TimeSpan.FromSeconds(5));
                    var jpeg = await _control.SnapshotSmallAsync(shot.Token).ConfigureAwait(false);
                    if (jpeg is { Length: > 100 } && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
                    {
                        lock (Frames)
                        {
                            // Budget full: keep every other frame (the survivors
                            // still span the whole event so far) and sample half
                            // as often from here on.
                            if (Frames.Count >= _budget)
                            {
                                for (int k = Frames.Count - 1; k > 0; k -= 2)
                                    Frames.RemoveAt(k);
                                interval += interval;
                            }
                            Frames.Add((t0, jpeg));
                        }
                        failures = 0;
                    }
                    else if (++failures >= 3)
                    {
                        break; // camera has no snapshots — stop asking
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Debug($"{Camera}: AI frame capture miss: {Log.Flatten(ex)}");
                    if (++failures >= 3) break;
                }
                // Pace to the current interval; a slow snapshot yields fewer frames.
                var spent = DateTime.UtcNow - t0;
                if (spent < interval)
                {
                    try { await Task.Delay(interval - spent, _stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (Exception ex)
        {
            // Belt and braces: the loop body already contains its failures, and
            // nothing here may ever surface into the event lifecycle.
            Log.Debug($"{Camera}: AI frame capture aborted: {Log.Flatten(ex)}");
        }
    }

    internal (int Count, long Bytes) Stats()
    {
        lock (Frames) return (Frames.Count, Frames.Sum(f => (long)f.Jpeg.Length));
    }
}

/// <summary>
/// Sends each completed detection event's frame burst to an OpenAI-style
/// chat-completions endpoint (LM Studio and friends) and stores the model's
/// description on the event. Deliberately fire-and-forget from the recorder's
/// point of view: jobs queue on a small bounded channel and one background
/// worker drains it, so a slow or dead LLM can never back-pressure recording,
/// streaming or anything else — when the queue is full, new jobs are dropped
/// (and say so in the log).
/// </summary>
public sealed class AiDescriber
{
    private sealed record Job(AiCapture Capture, EventRecord Record);

    // Small on purpose: each queued job holds its frames in memory, and a queue
    // deeper than this means the model can't keep up anyway.
    private readonly Channel<Job> _jobs = Channel.CreateBounded<Job>(
        new BoundedChannelOptions(8) { SingleReader = true });

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly AiStore _store;
    private readonly EventStore _events;
    private readonly Func<string, bool> _cameraOptIn;
    // Events queued or in flight, so the UI can say "describing…" instead of
    // showing nothing while the model works. Ids only — jobs hold the frames.
    private readonly ConcurrentDictionary<string, byte> _pending = new();

    public AiDescriber(AiStore store, EventStore events, Func<string, bool> cameraOptIn)
    {
        _store = store;
        _events = events;
        _cameraOptIn = cameraOptIn;
    }

    /// <summary>Both gates in one place: the global switch AND this camera's opt-in.
    /// Checked per event (not at wiring), so settings changes apply immediately.</summary>
    public bool WantsCapture(string camera) => _store.Enabled && _cameraOptIn(camera);

    /// <summary>Fires after a description (and threat level) lands on an event and
    /// is persisted — the MQTT bridge mirrors it into the camera's sensors.</summary>
    public event Action<EventRecord>? Described;

    /// <summary>Starts the 1 fps frame burst for a starting event; null when the
    /// feature is off for this camera.</summary>
    public AiCapture? TryBeginCapture(string camera, ICameraControl control, CancellationToken ct)
    {
        if (!WantsCapture(camera)) return null;
        var cfg = _store.Snapshot();
        return new AiCapture(camera, control,
            Math.Clamp(cfg.CaptureSeconds, 1, AiSettings.MaxCaptureSeconds), ct);
    }

    /// <summary>Event closed and saved: stop sampling and queue the description
    /// job. Never blocks — a full queue drops the job with a log line.</summary>
    public void Submit(AiCapture capture, EventRecord rec)
    {
        capture.Stop();
        if (capture.Discarded) return;
        _pending[rec.Id] = 1;
        if (!_jobs.Writer.TryWrite(new Job(capture, rec)))
        {
            Log.Warn($"{capture.Camera}: AI describe queue is full — event {rec.Id} skipped " +
                     "(the model is not keeping up with the event rate)");
            _pending.TryRemove(rec.Id, out _);
            capture.DisposeWhenDone();
        }
    }

    /// <summary>True while the event's description is queued or being generated —
    /// the web UI shows "describing…" instead of silently missing text.</summary>
    public bool IsPending(string eventId) => _pending.ContainsKey(eventId);

    /// <summary>The worker: one job at a time, every failure contained.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(job, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn($"{job.Capture.Camera}: AI describe failed: {Log.Flatten(ex)}");
                }
                finally
                {
                    _pending.TryRemove(job.Record.Id, out _);
                    job.Capture.DisposeWhenDone();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAsync(Job job, CancellationToken ct)
    {
        // The capture normally finished with the event; the bound is a seatbelt.
        try { await job.Capture.Completion.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
        catch (TimeoutException) { }

        var cfg = _store.Snapshot();
        if (!cfg.Enabled || job.Capture.Discarded) return; // switched off since capture
        var (count, bytes) = job.Capture.Stats();
        if (count == 0)
        {
            Log.Info($"{job.Capture.Camera}: AI describe skipped — no frames captured " +
                     "(camera answered no snapshots during the event)");
            return;
        }
        if (cfg.ActiveUrl() is not { } url)
        {
            Log.Warn($"{job.Capture.Camera}: AI describe skipped — the " +
                     $"{(cfg.UsesOllama ? "Ollama" : cfg.UsesAnthropic ? "Anthropic-style" : "OpenAI-style")} " +
                     $"endpoint '{(cfg.UsesOllama ? cfg.OllamaEndpoint : cfg.UsesAnthropic ? cfg.AnthropicEndpoint : cfg.Endpoint)}' " +
                     "is not a usable http(s) URL");
            return;
        }

        var rec = job.Record;
        var modelName = cfg.UsesOllama ? cfg.OllamaModel : cfg.UsesAnthropic ? cfg.AnthropicModel : cfg.Model;
        Log.Info($"{rec.Camera}: 🧠 describing event ({count} frame(s), {bytes / 1024} KB " +
                 $"→ {url.GetLeftPart(UriPartial.Authority)}" +
                 $"{(string.IsNullOrWhiteSpace(modelName) ? "" : $", model {modelName}")})");

        List<(DateTime Utc, byte[] Jpeg)> frames;
        lock (job.Capture.Frames) frames = job.Capture.Frames.ToList();
        var sw = Stopwatch.StartNew();
        var (raw, model, usage) = await CompleteAsync(cfg, _store.ActiveApiKey(cfg), BuildUserText(rec, frames),
            frames.Select(f => f.Jpeg).ToList(), classify: true, ct).ConfigureAwait(false);
        sw.Stop();

        var (level, text) = SplitLevel(raw);
        if (level == null && string.IsNullOrWhiteSpace(text))
        {
            Log.Warn($"{rec.Camera}: AI describe returned an empty answer after {sw.Elapsed.TotalSeconds:0.0}s");
            return;
        }

        // The event may have been deleted (retention, user) while the model
        // thought — Save() on an unknown id is a no-op, but don't log success.
        if (_events.Find(rec.Id) == null)
        {
            Log.Info($"{rec.Camera}: AI description arrived after the event was deleted — discarded");
            return;
        }
        rec.AiDescription = text;
        rec.AiLevel = level;
        rec.AiModel = model;
        rec.AiDescribedUtc = DateTime.UtcNow;
        _events.Save(rec);
        Log.Info($"{rec.Camera}: 🧠 event described in {sw.Elapsed.TotalSeconds:0.0}s" +
                 $"{(level == null ? "" : $" [{level.ToUpperInvariant()}]")}" +
                 $"{(usage == null ? "" : $" ({usage} tokens)")}: \"{text ?? "(no description)"}\"");
        Described?.Invoke(rec);
    }

    /// <summary>
    /// Peels the threat level off the answer per <see cref="AiSettings.LevelProtocol"/>:
    /// the first word (allowing markdown litter and same-line continuation) when it
    /// is GREEN/YELLOW/RED; anything else leaves the level null and the text whole.
    /// </summary>
    internal static (string? Level, string? Text) SplitLevel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        // Accepts the bare word and the "Threat level: X" spelling some models
        // insist on — but ONLY as a prefix, so "a man in a RED jacket" in a
        // level-less answer can never be misread as a verdict.
        var m = Regex.Match(text,
            @"^[\s*#>_`\-]*(?:threat\s*level\s*[:=\-]\s*)?[*_`]*(GREEN|YELLOW|RED)\b[.:,;!\s*_`\-—]*",
            RegexOptions.IgnoreCase);
        if (!m.Success) return (null, text.Trim());
        var rest = text[m.Length..].Trim();
        return (m.Groups[1].Value.ToLowerInvariant(), rest.Length == 0 ? null : rest);
    }

    private static string BuildUserText(EventRecord rec, IReadOnlyList<(DateTime Utc, byte[] Jpeg)> frames)
    {
        var local = rec.StartUtc.ToLocalTime();
        // Real per-frame offsets: sampling spreads across the whole event (the
        // interval grows as it runs), and telling the model where each frame
        // sits lets it narrate "at first … then 30 seconds later …" truthfully.
        var offsets = string.Join(", ",
            frames.Select(f => $"+{Math.Max(0, (int)(f.Utc - rec.StartUtc).TotalSeconds)}s"));
        return $"Camera \"{rec.Camera}\" reported: {string.Join(", ", rec.Labels)}. " +
               $"Event started {local:yyyy-MM-dd HH:mm:ss} (local) and lasted " +
               $"{Math.Max(1, (int)(rec.EndUtc - rec.StartUtc).TotalSeconds)}s. " +
               $"The {frames.Count} frame(s) below span the event, oldest first, " +
               $"taken at {offsets} after its start.";
    }

    /// <summary>One chat request against the active backend (OpenAI-style or
    /// Ollama native). Returns the cleaned answer (level line still attached — the
    /// caller splits it), the model the server says it used, and a token count.</summary>
    private static async Task<(string? Text, string? Model, long? Tokens)> CompleteAsync(
        AiSettings cfg, string apiKey, string userText, IReadOnlyList<byte[]> frames,
        bool classify, CancellationToken ct)
    {
        // NoThink rides the prompt ("/no_think", the Qwen-family convention, which
        // Ollama templates honor too); <think> blocks are stripped either way.
        // Claude models don't use the marker — it would just be prompt noise.
        var system = cfg.EffectivePrompt
                     + (classify ? "\n\n" + AiSettings.LevelProtocol : "")
                     + (cfg.NoThink && !cfg.UsesAnthropic ? " /no_think" : "");
        object payload;
        if (cfg.UsesAnthropic)
        {
            if (string.IsNullOrWhiteSpace(cfg.AnthropicModel))
                throw new InvalidOperationException(
                    "the Anthropic backend needs a vision-capable model name — set one in Settings → AI");
            // Messages API: system is a top-level field, images are base64 source
            // blocks, and max_tokens is REQUIRED.
            var blocks = new List<object> { new { type = "text", text = userText } };
            foreach (var jpeg in frames)
                blocks.Add(new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = "image/jpeg",
                        data = Convert.ToBase64String(jpeg),
                    },
                });
            payload = new Dictionary<string, object>
            {
                ["model"] = cfg.AnthropicModel.Trim(),
                ["max_tokens"] = 1024,
                ["system"] = system,
                ["messages"] = new object[] { new { role = "user", content = blocks } },
                ["temperature"] = 0.2,
            };
        }
        else if (cfg.UsesOllama)
        {
            if (string.IsNullOrWhiteSpace(cfg.OllamaModel))
                throw new InvalidOperationException(
                    "Ollama needs a vision-capable model name (it has no \"currently loaded\" " +
                    "default) — set one in Settings → AI");
            // Native /api/chat: images are plain base64 strings on the user message.
            payload = new Dictionary<string, object>
            {
                ["model"] = cfg.OllamaModel.Trim(),
                ["messages"] = new object[]
                {
                    new { role = "system", content = system },
                    new
                    {
                        role = "user",
                        content = userText,
                        images = frames.Select(Convert.ToBase64String).ToArray(),
                    },
                },
                ["stream"] = false,
                ["options"] = new { temperature = 0.2 },
            };
        }
        else
        {
            var content = new List<object> { new { type = "text", text = userText } };
            foreach (var jpeg in frames)
                content.Add(new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg),
                        detail = "low",
                    },
                });
            // Only fields every OpenAI-compatible server understands — vendor
            // extensions get 400s from strict ones.
            var oai = new Dictionary<string, object>
            {
                ["messages"] = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content },
                },
                ["temperature"] = 0.2,
                ["stream"] = false,
            };
            if (!string.IsNullOrWhiteSpace(cfg.Model))
                oai["model"] = cfg.Model.Trim();
            payload = oai;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.ActiveUrl());
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        if (cfg.UsesAnthropic)
        {
            // The Messages API authenticates with x-api-key, not a Bearer token.
            if (apiKey.Length > 0)
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (apiKey.Length > 0)
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(cfg.TimeoutSeconds, 5, 600)));
        string body;
        try
        {
            using var res = await Http.SendAsync(req, timeout.Token).ConfigureAwait(false);
            body = await res.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"LLM answered {(int)res.StatusCode}: {Excerpt(body)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"LLM did not answer within {cfg.TimeoutSeconds}s");
        }

        Log.Debug($"AI raw response: {Excerpt(body, 2000)}");
        using var doc = JsonDocument.Parse(body);
        return cfg.UsesOllama ? ParseOllama(doc, body)
             : cfg.UsesAnthropic ? ParseAnthropic(doc, body)
             : ParseOpenAi(doc, body);
    }

    private static (string? Text, string? Model, long? Tokens) ParseAnthropic(JsonDocument doc, string body)
    {
        // Messages shape: { model, content: [{type:"text",text}...], usage: {input_tokens, output_tokens} }.
        if (!doc.RootElement.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                "the server answered 200, but not in the Messages-API shape (no content array). " +
                $"Is the endpoint really Anthropic-style? It said: {Excerpt(body)}");
        var text = string.Concat(content.EnumerateArray()
            .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text")
            .Select(b => b.TryGetProperty("text", out var x) ? x.GetString() : null));
        string? model = doc.RootElement.TryGetProperty("model", out var m)
                        && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        long tokens = 0;
        if (doc.RootElement.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("input_tokens", out var i) && i.TryGetInt64(out var iv)) tokens += iv;
            if (u.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var ov)) tokens += ov;
        }
        return (CleanAnswer(text), model, tokens > 0 ? tokens : null);
    }

    private static (string? Text, string? Model, long? Tokens) ParseOpenAi(JsonDocument doc, string body)
    {
        // A 200 without choices is not a completion at all — LM Studio answers a
        // wrong path with exactly that ("Unexpected endpoint ... Returning 200
        // anyway"). Name the real problem instead of "empty completion".
        if (!doc.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidOperationException(
                "the server answered 200, but not with a chat completion (no choices). " +
                "The endpoint is probably not the OpenAI-style API base — it usually ends " +
                $"in /v1. Server said: {Excerpt(body)}");
        string? text = null;
        if (choices[0].TryGetProperty("message", out var msg)
            && msg.TryGetProperty("content", out var c))
            text = c.ValueKind == JsonValueKind.String
                ? c.GetString()
                // Some servers answer content as an array of {type,text} parts.
                : c.ValueKind == JsonValueKind.Array
                    ? string.Concat(c.EnumerateArray().Select(p =>
                        p.TryGetProperty("text", out var t) ? t.GetString() : null))
                    : null;
        string? model = doc.RootElement.TryGetProperty("model", out var m)
                        && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        long? tokens = doc.RootElement.TryGetProperty("usage", out var u)
                       && u.TryGetProperty("total_tokens", out var tt)
                       && tt.TryGetInt64(out var n) ? n : null;
        return (CleanAnswer(text), model, tokens);
    }

    private static (string? Text, string? Model, long? Tokens) ParseOllama(JsonDocument doc, string body)
    {
        // Native shape: { model, message: { content }, prompt_eval_count, eval_count }.
        if (!doc.RootElement.TryGetProperty("message", out var msg))
            throw new InvalidOperationException(
                "the server answered 200, but not in Ollama's /api/chat shape (no message). " +
                $"Is the endpoint really an Ollama server? It said: {Excerpt(body)}");
        string? text = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;
        string? model = doc.RootElement.TryGetProperty("model", out var m)
                        && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        long tokens = 0;
        if (doc.RootElement.TryGetProperty("prompt_eval_count", out var pe) && pe.TryGetInt64(out var p))
            tokens += p;
        if (doc.RootElement.TryGetProperty("eval_count", out var ev) && ev.TryGetInt64(out var e))
            tokens += e;
        return (CleanAnswer(text), model, tokens > 0 ? tokens : null);
    }

    /// <summary>Drops reasoning-model &lt;think&gt; blocks and trims; null when nothing is left.</summary>
    internal static string? CleanAnswer(string? text)
    {
        if (text == null) return null;
        text = Regex.Replace(text, @"(?s)<think(?:ing)?>.*?(</think(?:ing)?>|\z)", "").Trim();
        return text.Length == 0 ? null : text;
    }

    private static string Excerpt(string s, int max = 300) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>Connectivity test for the settings UI: a tiny text-only completion
    /// against (possibly unsaved) settings. Null = OK; otherwise the error text.</summary>
    public static async Task<(string? Error, string? Detail)> TestAsync(
        AiSettings cfg, string apiKey, CancellationToken ct)
    {
        if (cfg.ActiveUrl() == null)
            return (cfg.UsesOllama
                ? "The Ollama endpoint is not a usable http(s) URL. Expected something like http://127.0.0.1:11434"
                : cfg.UsesAnthropic
                    ? "The Anthropic-style endpoint is not a usable http(s) URL — blank means https://api.anthropic.com"
                    : "The endpoint is not a usable http(s) URL. Expected something like http://127.0.0.1:1234/v1", null);
        try
        {
            var sw = Stopwatch.StartNew();
            var (text, model, _) = await CompleteAsync(cfg, apiKey,
                "Connectivity test. Reply with the single word: READY", Array.Empty<byte[]>(),
                classify: false, ct).ConfigureAwait(false);
            return text == null
                ? ("The server answered, but with an empty completion.", null)
                : (null, $"{model ?? "model"} answered in {sw.Elapsed.TotalSeconds:0.0}s: \"{Excerpt(text, 120)}\"");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (Log.Flatten(ex), null);
        }
    }
}
