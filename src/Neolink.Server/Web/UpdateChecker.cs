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

    private readonly System.Version _current;
    private volatile string? _latest;

    public UpdateChecker(string currentVersion)
    {
        System.Version.TryParse(currentVersion, out var v);
        _current = v ?? new System.Version(0, 0);
    }

    /// <summary>The newest available version, only when strictly newer than the running one.</summary>
    public string? Latest => _latest;

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
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"neolink.net/{_current}"); // GitHub requires a UA

        var tag = await FetchTagAsync(http, ct).ConfigureAwait(false);
        if (tag == null) return;

        if (System.Version.TryParse(tag.TrimStart('v', 'V'), out var latest) && latest > _current)
        {
            if (_latest != tag)
                Log.Info($"Update available: {tag} (running {_current}) — {RepoUrl}");
            _latest = tag;
        }
    }

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
