// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;

namespace Neolink.Web;

/// <summary>
/// Once a day, asks GitHub whether a newer release of Neolink.NET exists.
/// Best-effort by design: offline/LAN-only boxes just never see the banner.
/// Only the latest version STRING is exposed — the UI links to the repo, no
/// code or artifacts are ever fetched.
/// </summary>
public sealed class UpdateChecker
{
    public const string RepoUrl = "https://github.com/borexola/neolink.net";
    private const string ApiLatest = "https://api.github.com/repos/borexola/neolink.net/releases/latest";
    private const string ApiTags = "https://api.github.com/repos/borexola/neolink.net/tags";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private static readonly TimeSpan NudgeFloor = TimeSpan.FromHours(6);

    private readonly System.Version _current;
    private volatile string? _latest;
    private long _lastAttemptMs;   // Environment.TickCount64 when a check last STARTED
    private int _checking;         // one check in flight, ever

    public UpdateChecker(string currentVersion)
    {
        // Only the numeric prefix counts: "0.8.5-events-test" compares as 0.8.5.
        // System.Version can't parse suffixed versions, and falling back to 0.0
        // made every test build see every release as an "update" (a downgrade
        // banner on 0.8.5-test when 0.8.4 was the latest release).
        var numeric = currentVersion.Split('-', '+')[0];
        System.Version.TryParse(numeric, out var v);
        _current = v ?? new System.Version(0, 0);
    }

    /// <summary>The newest available version, only when strictly newer than the running one.</summary>
    public string? Latest => _latest;

    /// <summary>A page load can pull the next daily check forward: /api/features
    /// calls this, so a full refresh means "check now" instead of "wait for the
    /// 24-hour timer" — a release landing hours after the daily poll used to be
    /// invisible until the next one. Throttled hard: at most one check per six
    /// hours and one in flight, whoever asks — GitHub's unauthenticated rate
    /// limit is per-IP and not ours to spend on every request.</summary>
    public void Nudge()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _lastAttemptMs) < NudgeFloor.TotalMilliseconds)
            return;
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await CheckAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug($"Update check (page-load nudge) failed: {Log.Flatten(ex)}");
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
            }
        });
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug($"Update check failed: {Log.Flatten(ex)}");
            }
            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        // Stamped at the attempt, success or not: an offline box must not retry
        // GitHub on every page load just because its checks keep failing.
        Interlocked.Exchange(ref _lastAttemptMs, Environment.TickCount64);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"neolink.net/{_current}"); // GitHub requires a UA

        var tag = await FetchTagAsync(http, ct).ConfigureAwait(false);
        if (tag == null) return;

        if (IsNewer(tag))
        {
            if (_latest != tag)
                Log.Info($"Update available: {tag} (running {_current}) — {RepoUrl}");
            _latest = tag;
        }
    }

    /// <summary>True when the tag (with or without a leading v) parses and is strictly newer than the running version.</summary>
    internal bool IsNewer(string tag) =>
        System.Version.TryParse(tag.TrimStart('v', 'V'), out var latest) && latest > _current;

    private static async Task<string?> FetchTagAsync(HttpClient http, CancellationToken ct)
    {
        using var res = await http.GetAsync(ApiLatest, ct).ConfigureAwait(false);
        if (res.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        }
        if (res.StatusCode != System.Net.HttpStatusCode.NotFound)
            return null;

        // No formal releases yet: fall back to the newest tag.
        using var tagsRes = await http.GetAsync(ApiTags, ct).ConfigureAwait(false);
        if (!tagsRes.IsSuccessStatusCode) return null;
        using var tags = JsonDocument.Parse(await tagsRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return tags.RootElement.ValueKind == JsonValueKind.Array && tags.RootElement.GetArrayLength() > 0
            ? tags.RootElement[0].GetProperty("name").GetString()
            : null;
    }
}
