// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Net.Http.Json;

namespace Neolink.WebClient;

/// <summary>
/// The detection zones the live object boxes obey, per camera, for every surface
/// that draws them — the wall's single-camera view, the event pop-up and the
/// events page. Scoped, so it lives exactly as long as the browser's own copy of
/// the grids: one circuit, one page load, one set of pushes.
///
/// The grids come off the camera itself and cost it a round trip, so they are read
/// once per camera and only for the box groups actually being outlined.
/// </summary>
public sealed class DetectZones
{
    /// <summary>Box group to the camera's own name for that kind of detection.
    /// "other" has no counterpart and follows the shared motion zone.</summary>
    private static readonly (string Group, string Type)[] AiTypes =
        { ("people", "people"), ("vehicles", "vehicle"), ("animals", "dog_cat") };

    private readonly Dictionary<string, List<DetectZoneGrid>> _zones = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pushed = new(StringComparer.OrdinalIgnoreCase);
    private string? _fetching;
    private DateTime _retryAt = DateTime.MinValue;

    /// <summary>Whether this camera's zones are known. False means the boxes must
    /// wait: one frame outlined in a part of the view the user told the camera to
    /// ignore is one too many.</summary>
    public bool Known(string? camera) => camera != null && _zones.ContainsKey(camera);

    /// <summary>The grids to hand the browser, once per camera. Null when there is
    /// nothing new to send.</summary>
    public object? TakePush(string? camera)
    {
        if (camera == null || _pushed.Contains(camera) || !_zones.TryGetValue(camera, out var grids))
            return null;
        _pushed.Add(camera);
        return new { camera, grids };
    }

    /// <summary>A zone the user has just edited is no longer the one being obeyed.</summary>
    public void Forget(string camera)
    {
        _zones.Remove(camera);
        _pushed.Remove(camera);
    }

    /// <summary>Which zones are worth reading depends on which groups are outlined:
    /// a changed group list means the answers on hand were for a different question.</summary>
    public void ForgetAll()
    {
        _zones.Clear();
        _pushed.Clear();
    }

    /// <summary>Reads the camera's zones unless they are known, already being read,
    /// or were unreadable a moment ago. Returns true when something changed and the
    /// caller should re-render. Safe to call from a render path — it does nothing
    /// on all but the first call per camera.</summary>
    public async Task<bool> EnsureAsync(string camera, HttpClient http, string apiBase,
        string? token, IReadOnlyList<string> groups)
    {
        if (_zones.ContainsKey(camera) || _fetching != null || DateTime.UtcNow < _retryAt)
            return false;
        _fetching = camera;
        try
        {
            if (await ReadAsync(camera, http, apiBase, token, groups) is { } grids)
            {
                _zones[camera] = grids;
                _pushed.Remove(camera);
                return true;
            }
            // A camera that could not be asked must not be recorded as one without
            // zones, which would drop the mask for the rest of the session. The
            // wait keeps the retry off the render loop and off the camera.
            _retryAt = DateTime.UtcNow.AddSeconds(20);
            return false;
        }
        finally
        {
            _fetching = null;
        }
    }

    /// <summary>Null means "ask again"; an empty list means the camera genuinely has
    /// no zone, so nothing constrains the boxes.</summary>
    private static async Task<List<DetectZoneGrid>?> ReadAsync(string camera, HttpClient http,
        string apiBase, string? token, IReadOnlyList<string> groups)
    {
        var (shared, retry) = await ReadOneAsync(camera, "md", http, apiBase, token);
        if (retry) return null;
        var grids = new List<DetectZoneGrid>();
        var onShared = new List<string>();
        foreach (var group in groups)
        {
            // A camera with a per-type grid is answering about THIS kind of thing;
            // one without falls back to the zone that governs everything.
            var type = AiTypes.FirstOrDefault(t => t.Group == group).Type;
            var own = type == null ? null : (await ReadOneAsync(camera, type, http, apiBase, token)).Zone;
            if (own != null)
                grids.Add(new DetectZoneGrid(new List<string> { group }, own.Cols, own.Rows, own.Table));
            else if (shared != null)
                onShared.Add(group);
        }
        if (onShared.Count > 0 && shared != null)
            grids.Add(new DetectZoneGrid(onShared, shared.Cols, shared.Rows, shared.Table));
        return grids;
    }

    /// <summary>One zone read. 404 is the camera saying it has no such grid (no HTTP
    /// API, or no zone for that type) and is final; anything else is a camera that
    /// could not be asked just now.</summary>
    private static async Task<(ApiDetectionZone? Zone, bool Retry)> ReadOneAsync(string camera, string type,
        HttpClient http, string apiBase, string? token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{apiBase}/api/cameras/{Uri.EscapeDataString(camera)}/detectionzone?type={Uri.EscapeDataString(type)}");
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var res = await http.SendAsync(req);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, false);
            if (!res.IsSuccessStatusCode) return (null, true);
            return (await res.Content.ReadFromJsonAsync<ApiDetectionZone>(), false);
        }
        catch
        {
            return (null, true);
        }
    }
}
