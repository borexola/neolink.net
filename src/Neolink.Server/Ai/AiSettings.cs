// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;
using System.Text.Json.Serialization;
using Neolink.Notifications;

namespace Neolink.Ai;

/// <summary>
/// User-configured settings for AI event descriptions: an OpenAI-style
/// chat-completions endpoint (LM Studio, Ollama, llama.cpp server, or a hosted
/// API) that receives a burst of low-res frames from each detection event and
/// answers with a short description. Persisted as ai.json in the state dir.
/// The API key is stored ONLY as an AES-GCM token (<see cref="ApiKeyEnc"/>) via
/// <see cref="SecretProtector"/> — never in plaintext, never returned to the UI.
/// </summary>
public sealed class AiSettings
{
    /// <summary>The frame budget's hard ceiling — one frame per second, so this
    /// also caps how long an event is sampled. Payload size and prompt-processing
    /// time grow with every frame; 20 low-res images is already a big ask for a
    /// local vision model.</summary>
    public const int MaxCaptureSeconds = 20;

    public const string DefaultPrompt =
        "You are the eyes of a home security system. You receive frames from one " +
        "surveillance camera, captured one second apart during a detection event, oldest first. " +
        "In 1-3 short sentences, describe what happens across the frames: who or what is " +
        "visible, what they are doing, and anything unusual. Be concrete and factual, do not " +
        "speculate beyond what the images show, and answer with the description only.";

    /// <summary>
    /// The threat-classification contract, appended to the (user-editable) prompt
    /// on every event request — separate from it so a custom prompt cannot break
    /// the parsing. The answer's first line carries the level; SplitLevel in the
    /// describer peels it off.
    /// </summary>
    public const string LevelProtocol =
        "IMPORTANT — answer format (this overrides any earlier instruction to answer with " +
        "the description only): the FIRST line of your answer must be exactly one word — " +
        "GREEN, YELLOW or RED — the threat level of what the frames show. GREEN = routine, " +
        "expected activity (residents, deliveries, pets, passing vehicles). YELLOW = unusual " +
        "or suspicious activity worth reviewing (an unfamiliar person lingering or looking " +
        "into windows or car handles, a deliberately concealed face, an animal damaging " +
        "property). RED = immediate danger (a visible weapon, fighting or violence, a " +
        "break-in attempt, fire or smoke). The description follows from the second line.";

    /// <summary>Master opt-in. Off = no frames are captured and nothing is ever sent.</summary>
    public bool Enabled { get; set; }

    /// <summary>The active backend: "openai" (LM Studio, llama.cpp server, hosted
    /// APIs — the /v1 chat-completions shape), "ollama" (Ollama's native
    /// /api/chat) or "anthropic" (the Messages API — Claude, or any proxy that
    /// speaks it). Every backend keeps its own endpoint/model settings, so
    /// switching back and forth loses nothing.</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>OpenAI-style API base, e.g. "http://127.0.0.1:1234/v1" (LM Studio).
    /// "/chat/completions" is appended unless already present.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Model name; blank lets the server pick (LM Studio uses the loaded model).</summary>
    public string Model { get; set; } = "";

    /// <summary>Ollama base, e.g. "http://127.0.0.1:11434"; "/api/chat" is appended
    /// unless already present.</summary>
    public string OllamaEndpoint { get; set; } = "";

    /// <summary>Ollama model name (required — Ollama has no "loaded model" default);
    /// must be a vision-capable model.</summary>
    public string OllamaModel { get; set; } = "";

    /// <summary>Anthropic-style base; blank = https://api.anthropic.com (the API
    /// itself). "/v1/messages" is appended unless already present — a proxy that
    /// speaks the Messages API works by pointing this at it.</summary>
    public string AnthropicEndpoint { get; set; } = "";

    /// <summary>Anthropic model name (required); must be a vision-capable model.</summary>
    public string AnthropicModel { get; set; } = "";

    /// <summary>AES-GCM token of the Anthropic API key (write-only, like the others).</summary>
    public string AnthropicApiKeyEnc { get; set; } = "";

    /// <summary>AES-GCM token of the API key (see SecretProtector); "" = none.
    /// Local servers usually need none.</summary>
    public string ApiKeyEnc { get; set; } = "";

    /// <summary>The system prompt; blank falls back to <see cref="DefaultPrompt"/>.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Ask reasoning models to skip their thinking phase (appends
    /// "/no_think", the convention Qwen-style models honor). Responses have
    /// &lt;think&gt; blocks stripped either way; this saves the time they take.</summary>
    public bool NoThink { get; set; } = true;

    /// <summary>The frame budget per event (1..<see cref="MaxCaptureSeconds"/>).
    /// Sampling starts at one frame per second and spreads: whenever the budget
    /// fills, every other frame is dropped and the interval doubles, so the set
    /// always spans the WHOLE event, however long it runs. (The JSON name is
    /// historic — it began life as "seconds sampled".)</summary>
    public int CaptureSeconds { get; set; } = 10;

    /// <summary>How long one completion may take before the job is abandoned.
    /// Local models on modest hardware can genuinely need minutes.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    [JsonIgnore] // derived — keep ai.json to what the user actually set
    public string EffectivePrompt => string.IsNullOrWhiteSpace(Prompt) ? DefaultPrompt : Prompt;

    public AiSettings Clone() => new()
    {
        Enabled = Enabled,
        Provider = Provider,
        Endpoint = Endpoint,
        Model = Model,
        OllamaEndpoint = OllamaEndpoint,
        OllamaModel = OllamaModel,
        AnthropicEndpoint = AnthropicEndpoint,
        AnthropicModel = AnthropicModel,
        AnthropicApiKeyEnc = AnthropicApiKeyEnc,
        ApiKeyEnc = ApiKeyEnc,
        Prompt = Prompt,
        NoThink = NoThink,
        CaptureSeconds = CaptureSeconds,
        TimeoutSeconds = TimeoutSeconds,
    };

    /// <summary>True while Ollama's native API is the active backend.</summary>
    [JsonIgnore]
    public bool UsesOllama => string.Equals(Provider, "ollama", StringComparison.OrdinalIgnoreCase);

    /// <summary>True while the Anthropic Messages API is the active backend.</summary>
    [JsonIgnore]
    public bool UsesAnthropic => string.Equals(Provider, "anthropic", StringComparison.OrdinalIgnoreCase);

    /// <summary>The URL completions are POSTed to; null when the endpoint is unusable.</summary>
    public Uri? CompletionsUrl()
    {
        var e = Endpoint.Trim().TrimEnd('/');
        if (e.Length == 0) return null;
        if (!e.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            // A bare host ("http://10.1.1.5:1234") means an OpenAI-style server
            // rooted at /v1 — the universal convention (LM Studio, Ollama,
            // llama.cpp server). LM Studio answers the UNprefixed path with a
            // 200 that is not a completion at all (seen live), so guessing
            // /v1 here is strictly better than passing the mistake through.
            // Any explicit path (a proxy, a gateway) is respected as typed.
            if (Uri.TryCreate(e, UriKind.Absolute, out var probe)
                && probe.AbsolutePath is "/" or "")
                e += "/v1";
            e += "/chat/completions";
        }
        return Uri.TryCreate(e, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https" ? uri : null;
    }

    /// <summary>The URL Ollama chats are POSTed to; null when unusable.</summary>
    public Uri? OllamaUrl()
    {
        var e = OllamaEndpoint.Trim().TrimEnd('/');
        if (e.Length == 0) return null;
        if (!e.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase))
            e += "/api/chat";
        return Uri.TryCreate(e, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https" ? uri : null;
    }

    /// <summary>The URL Messages-API requests are POSTed to. Unlike the local
    /// backends there is a well-known default, so blank means the real API.</summary>
    public Uri? AnthropicUrl()
    {
        var e = AnthropicEndpoint.Trim().TrimEnd('/');
        if (e.Length == 0) e = "https://api.anthropic.com";
        if (!e.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
            e += e.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "/messages" : "/v1/messages";
        return Uri.TryCreate(e, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https" ? uri : null;
    }

    /// <summary>The active backend's request URL (per <see cref="Provider"/>).</summary>
    public Uri? ActiveUrl() =>
        UsesOllama ? OllamaUrl() : UsesAnthropic ? AnthropicUrl() : CompletionsUrl();
}

/// <summary>
/// Loads/saves <see cref="AiSettings"/> (ai.json next to the other UI state,
/// owner-only). The plaintext API key never leaves this class: encrypted on the
/// way in, decrypted only for the request that needs it.
/// </summary>
public sealed class AiStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _file;
    private readonly SecretProtector _protector;
    private readonly object _gate = new();
    private AiSettings _settings = new();

    public AiStore(string stateDir, SecretProtector protector)
    {
        _file = Path.Combine(stateDir, "ai.json");
        _protector = protector;
        try
        {
            if (File.Exists(_file))
                _settings = JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(_file), JsonOpts) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"AI settings unreadable ({ex.Message}); AI descriptions start disabled.");
        }
    }

    /// <summary>A private copy of the current settings (API key stays encrypted).</summary>
    public AiSettings Snapshot()
    {
        lock (_gate) return _settings.Clone();
    }

    /// <summary>True while the feature is globally switched on.</summary>
    public bool Enabled
    {
        get { lock (_gate) return _settings.Enabled; }
    }

    /// <summary>The decrypted API key for the request; "" when none/unreadable.</summary>
    public string ApiKey()
    {
        string enc;
        lock (_gate) enc = _settings.ApiKeyEnc;
        return _protector.Unprotect(enc) ?? "";
    }

    /// <summary>The decrypted Anthropic API key; "" when none/unreadable.</summary>
    public string AnthropicApiKey()
    {
        string enc;
        lock (_gate) enc = _settings.AnthropicApiKeyEnc;
        return _protector.Unprotect(enc) ?? "";
    }

    /// <summary>The decrypted key for the ACTIVE backend of these settings.</summary>
    public string ActiveApiKey(AiSettings cfg) => cfg.UsesAnthropic ? AnthropicApiKey() : ApiKey();

    /// <summary>True once a key has been stored (so the UI can show "set").</summary>
    public bool HasApiKey
    {
        get { lock (_gate) return _settings.ApiKeyEnc.Length > 0; }
    }

    /// <summary>Same signal for the Anthropic backend's own key.</summary>
    public bool HasAnthropicKey
    {
        get { lock (_gate) return _settings.AnthropicApiKeyEnc.Length > 0; }
    }

    /// <summary>Replaces the settings. The key parameters are write-only:
    /// null keeps the stored key, non-null re-encrypts (""=clear it).</summary>
    public void Save(AiSettings incoming, string? newApiKey, string? newAnthropicKey = null)
    {
        lock (_gate)
        {
            incoming.ApiKeyEnc = newApiKey switch
            {
                null => _settings.ApiKeyEnc,   // unchanged
                "" => "",                       // cleared
                _ => _protector.Protect(newApiKey),
            };
            incoming.AnthropicApiKeyEnc = newAnthropicKey switch
            {
                null => _settings.AnthropicApiKeyEnc,
                "" => "",
                _ => _protector.Protect(newAnthropicKey),
            };
            incoming.Provider = incoming.UsesOllama ? "ollama"
                : incoming.UsesAnthropic ? "anthropic" : "openai"; // normalize unknowns
            incoming.CaptureSeconds = Math.Clamp(incoming.CaptureSeconds, 1, AiSettings.MaxCaptureSeconds);
            incoming.TimeoutSeconds = Math.Clamp(incoming.TimeoutSeconds, 5, 600);
            _settings = incoming;
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_settings, JsonOpts));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot persist AI settings: {ex.Message}");
        }
    }
}
