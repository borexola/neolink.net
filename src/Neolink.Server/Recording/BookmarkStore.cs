// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;

namespace Neolink.Recording;

/// <summary>One saved timeline bookmark: a named stretch of one day's footage
/// ("My son's first walk", 10:00:00–10:08:00). Times are seconds-of-day in the
/// server's local time, exactly like the timeline's own cursor math.</summary>
public sealed class Bookmark
{
    public required string Id { get; init; }
    /// <summary>The day, "yyyy-MM-dd" (local, matching the recording folders).</summary>
    public required string Date { get; init; }
    public required double From { get; init; }
    public required double To { get; init; }
    public required string Name { get; init; }
    public DateTime CreatedUtc { get; init; }
}

/// <summary>
/// Timeline bookmarks — one bookmarks.json living IN the recordings root, so the
/// notes travel with the footage they describe (a backup of the recordings
/// directory keeps them; pointing a new server at old storage finds them).
/// Same zero-dependency file pattern as the other state stores: in-memory list,
/// atomic replace on every change. Bookmarks are shared by every user of the
/// server, like the recordings themselves. A bookmark whose footage has aged
/// out of retention still lists (the note may outlive the pixels) — the
/// timeline simply shows "no footage" there.
/// </summary>
public sealed class BookmarkStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Sanity caps: a name is a title, not an essay; the list is a
    /// shelf of moments, not a second event database.</summary>
    public const int MaxNameLength = 80;
    public const int MaxBookmarks = 1000;
    private const double DaySeconds = 24 * 3600;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<Bookmark> _bookmarks;

    public BookmarkStore(string recordingsRoot)
    {
        _path = Path.Combine(recordingsRoot, "bookmarks.json");
        _bookmarks = Load(_path);
    }

    private static List<Bookmark> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(path), Json);
                if (parsed != null)
                    return parsed;
            }
        }
        catch (Exception ex)
        {
            // A corrupt file must not take the server down — but silently starting
            // an empty list would OVERWRITE the damaged file (and every bookmark in
            // it) on the next save. Set it aside instead: the list starts fresh and
            // the damaged original stays recoverable by hand.
            Log.Warn($"bookmarks.json unreadable ({ex.Message}) — set aside as bookmarks.json.corrupt, starting empty");
            try { File.Move(path, path + ".corrupt", overwrite: true); } catch { }
        }
        return new List<Bookmark>();
    }

    /// <summary>All bookmarks, newest day first, then by start time within the day.</summary>
    public IReadOnlyList<Bookmark> List()
    {
        lock (_gate)
        {
            return _bookmarks
                .OrderByDescending(b => b.Date, StringComparer.Ordinal)
                .ThenBy(b => b.From)
                .ToList();
        }
    }

    /// <summary>Validates and saves a new bookmark. Throws FormatException with a
    /// user-facing message on bad input (the API surfaces it as a 400).</summary>
    public Bookmark Add(string? date, double from, double to, string? name)
    {
        if (date == null || !DateTime.TryParseExact(date, "yyyy-MM-dd", null,
                System.Globalization.DateTimeStyles.None, out _))
            throw new FormatException("date must be yyyy-MM-dd");
        if (double.IsNaN(from) || double.IsNaN(to)
            || from < 0 || to > DaySeconds || from >= to)
            throw new FormatException("the range needs From before To, inside one day");
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            throw new FormatException("the bookmark needs a name");
        if (trimmed.Length > MaxNameLength)
            throw new FormatException($"the name is capped at {MaxNameLength} characters");

        var bookmark = new Bookmark
        {
            Id = Convert.ToHexString(Guid.NewGuid().ToByteArray()[..5]).ToLowerInvariant(),
            Date = date,
            From = Math.Round(from, 1),
            To = Math.Round(to, 1),
            Name = trimmed,
            CreatedUtc = DateTime.UtcNow,
        };
        lock (_gate)
        {
            if (_bookmarks.Count >= MaxBookmarks)
                throw new FormatException($"bookmark limit reached ({MaxBookmarks}) — delete some first");
            _bookmarks.Add(bookmark);
            Save();
        }
        return bookmark;
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            int removed = _bookmarks.RemoveAll(b => b.Id == id);
            if (removed > 0) Save();
            return removed > 0;
        }
    }

    private void Save()
    {
        // Atomic replace, like the other state files: a crash mid-write must not
        // leave a truncated JSON behind.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_bookmarks, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
