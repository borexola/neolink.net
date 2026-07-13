// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using Neolink.Config;

namespace Neolink.Recording;

/// <summary>Which tier a storage location serves.</summary>
public enum StorageRole
{
    /// <summary>The bulk recordings volume — continuous footage, and the default for everything.</summary>
    Main,
    /// <summary>Optional fast tier holding event clips (SSD).</summary>
    Clips,
    /// <summary>Optional cold tier that aged footage is moved to instead of deleted.</summary>
    Archive,
}

/// <summary>One configured storage location and its role.</summary>
public sealed record StorageLocation(StorageRole Role, string Path)
{
    public string Label => Role switch
    {
        StorageRole.Main => "Recordings",
        StorageRole.Clips => "Clips (fast)",
        StorageRole.Archive => "Archive",
        _ => Role.ToString(),
    };
}

/// <summary>A point-in-time capacity reading of one location's volume.</summary>
public sealed record StorageStatus(
    StorageRole Role, string Label, string Path, long TotalBytes, long FreeBytes, bool Online)
{
    public double UsedPercent => TotalBytes > 0 ? (TotalBytes - FreeBytes) * 100.0 / TotalBytes : 0;

    /// <summary>Near-capacity: the UI shows an amber "getting full" banner.</summary>
    public bool Warn => Online && TotalBytes > 0 && UsedPercent >= StorageLocations.WarnPercent;

    /// <summary>Effectively full: recording to this tier halts and the UI shows a red banner.</summary>
    public bool Full => Online && TotalBytes > 0 && FreeBytes < StorageLocations.MinFreeBytes;
}

/// <summary>
/// Resolves the configured storage tiers (main, optional clips, optional archive)
/// and reports each distinct volume's capacity. All tiers are optional: an unset
/// clips/archive path resolves back to the main path, so a plain single-folder
/// install behaves exactly as before. The volume probe is injectable so the
/// resolution and threshold logic can be tested without real drives.
/// </summary>
public sealed class StorageLocations
{
    /// <summary>Percent-used at which a location is flagged as getting full (amber).</summary>
    public const double WarnPercent = 90.0;

    /// <summary>Headroom kept free; below this a tier is "full" and recording halts.
    /// One gibibyte comfortably covers a rolling segment plus filesystem slack.</summary>
    public const long MinFreeBytes = 1L * 1024 * 1024 * 1024;

    private readonly string _main;
    private readonly string _clips;   // == _main when not separately configured
    private readonly string _archive; // "" when not configured
    private readonly Func<string, (long Total, long Free)?> _probe;

    /// <param name="probe">Volume sampler for a path (total, free), or null when the
    /// volume can't be read; defaults to <see cref="DriveInfo"/>.</param>
    public StorageLocations(RecordingConfig cfg, Func<string, (long, long)?>? probe = null)
    {
        _main = Full(cfg.Path);
        _clips = string.IsNullOrWhiteSpace(cfg.ClipsPath) ? _main : Full(cfg.ClipsPath);
        _archive = string.IsNullOrWhiteSpace(cfg.ArchivePath) ? "" : Full(cfg.ArchivePath);
        _probe = probe ?? DefaultProbe;

        // Create every distinct tier up front so writers never race on mkdir.
        foreach (var loc in Locations)
        {
            try { Directory.CreateDirectory(loc.Path); }
            catch (Exception ex) { Log.Warn($"Storage: cannot create {loc.Label} path '{loc.Path}': {ex.Message}"); }
        }
    }

    private static string Full(string p) => Path.GetFullPath(p);

    /// <summary>The bulk/main recordings root (continuous footage; default for all).</summary>
    public string MainRoot => _main;

    /// <summary>Where new event clips are written — the clips tier when set, else main.</summary>
    public string ClipsRoot => _clips;

    /// <summary>The archive root, or null when archiving is off.</summary>
    public string? ArchiveRoot => _archive.Length == 0 ? null : _archive;

    /// <summary>True when a separate SSD clips tier is configured (distinct from main).</summary>
    public bool HasClipsTier => !PathEquals(_clips, _main);

    /// <summary>True when an archive tier is configured.</summary>
    public bool HasArchiveTier => _archive.Length > 0;

    /// <summary>Every DISTINCT configured location (by path), main first. Two roles on
    /// the same folder collapse to one entry (main wins).</summary>
    public IReadOnlyList<StorageLocation> Locations
    {
        get
        {
            var list = new List<StorageLocation> { new(StorageRole.Main, _main) };
            if (HasClipsTier) list.Add(new(StorageRole.Clips, _clips));
            if (HasArchiveTier && !PathEquals(_archive, _main) && !PathEquals(_archive, _clips))
                list.Add(new(StorageRole.Archive, _archive));
            return list;
        }
    }

    /// <summary>Configured-distinct tiers that report byte-identical capacity — a
    /// strong hint they actually resolve to the SAME filesystem. The usual cause
    /// is a Docker bind mount that never attached to the intended drive (the drive
    /// wasn't mounted when the container started, or the container wasn't
    /// recreated), so the path sits on the container's root overlay instead.
    /// One human-readable warning per colliding group; empty when all is well.</summary>
    public IEnumerable<string> SharedVolumeWarnings() =>
        Sample()
            .Where(s => s.Online && s.TotalBytes > 0)
            .GroupBy(s => (s.TotalBytes, s.FreeBytes))
            .Where(g => g.Count() > 1)
            .Select(g =>
                $"{string.Join(" and ", g.Select(s => $"{s.Label} ({s.Path})"))} report identical " +
                "capacity — they are probably on the SAME filesystem, not the separate drives you " +
                "configured. In Docker, recreate the container (docker compose up -d --force-recreate) " +
                "and make sure the drives are mounted before Docker starts.");

    /// <summary>Capacity reading of every distinct location.</summary>
    public List<StorageStatus> Sample() =>
        Locations.Select(l =>
        {
            var v = SafeProbe(l.Path);
            return new StorageStatus(l.Role, l.Label, l.Path,
                v?.Total ?? 0, v?.Free ?? 0, v.HasValue);
        }).ToList();

    /// <summary>Is there room to keep writing to the tier that backs this role?
    /// Unknown/unreadable volumes are treated as writable (fail open — never block
    /// recording just because a stat call hiccuped).</summary>
    public bool HasRoom(StorageRole role)
    {
        var path = role switch
        {
            StorageRole.Clips => _clips,
            StorageRole.Archive => _archive.Length == 0 ? _main : _archive,
            _ => _main,
        };
        var v = SafeProbe(path);
        return v is not { } vol || vol.Free >= MinFreeBytes;
    }

    private (long Total, long Free)? SafeProbe(string path)
    {
        try { return _probe(path); }
        catch { return null; }
    }

    private static (long, long)? DefaultProbe(string path)
    {
        try
        {
            var d = new DriveInfo(Path.GetFullPath(path));
            return (d.TotalSize, d.AvailableFreeSpace);
        }
        catch { return null; }
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
