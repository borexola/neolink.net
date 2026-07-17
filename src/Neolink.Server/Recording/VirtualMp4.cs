// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Collections.Concurrent;

namespace Neolink.Recording;

/// <summary>
/// Serves OLD-FORMAT (fragmented) recordings as if they were classic indexed
/// MP4s — without touching a byte on disk. New recordings are finalized into
/// the classic layout when they close, but installs upgraded from earlier
/// versions may sit on terabytes of per-frame-fragmented footage, and a file
/// still being recorded is fragmented by design. Rewriting archives at that
/// scale is a migration nobody should need: instead, the one-time header walk
/// that finalization does on disk (~50 ms for a 256 MB segment, cached) is
/// done in memory here, and the file is presented byte-mapped as
///   ftyp · free-span (the retired header + all fragments) · synthesized moov
/// so players seek by byte offset exactly like they do on new files.
///
/// <see cref="Open"/> returns a plain FileStream for anything that is not an
/// old-format file (already classic, foreign, unparseable) — serving then
/// behaves exactly as before, making this a pure fast-path with a safe
/// fallback. Growing (still-recording) files work too: each request gets a
/// consistent snapshot covering the samples complete at open time.
/// </summary>
public static class VirtualMp4
{
    /// <summary>Synthesized moovs by path, keyed on length+mtime so a grown or
    /// replaced file never reuses a stale index. Closed files hit this forever;
    /// a growing file naturally misses and re-scans (~tens of ms).</summary>
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();
    private const int CacheLimit = 32;

    private sealed record CacheEntry(long Length, DateTime MTime, byte[] Moov, long FragEnd, long MoovOff)
    {
        public long LastUse;
    }

    /// <summary>
    /// Opens a recording for HTTP serving: the file itself when it is already
    /// seekable, otherwise a read-only virtual view with the classic index
    /// synthesized at the end. The returned stream is seekable and reports the
    /// VIRTUAL length; hand it to range-processing file serving as-is.
    /// </summary>
    public static Stream Open(string path)
    {
        // The vault sniffs the format: encrypted footage decrypts transparently,
        // plaintext gets a big sequential-read buffer as before.
        var file = FootageVault.OpenRead(path);
        try
        {
            if (!ClipWriter.IsFragmented(file))
            {
                file.Seek(0, SeekOrigin.Begin);
                return file; // classic already — serve raw
            }

            var info = new FileInfo(path);
            var key = path;
            if (!Cache.TryGetValue(key, out var entry)
                || entry.Length != info.Length || entry.MTime != info.LastWriteTimeUtc)
            {
                var scan = ClipWriter.ScanFragmented(file);
                entry = new CacheEntry(info.Length, info.LastWriteTimeUtc,
                    ClipWriter.BuildClassicMoov(scan.Init, scan.Layout, scan.Samples, scan.AudioRate),
                    scan.FragEnd, scan.Layout.MoovOff);
                Cache[key] = entry;
                TrimCache(key);
            }
            entry.LastUse = Environment.TickCount64;
            return new VirtualClassicStream(file, entry.MoovOff, entry.FragEnd, entry.Moov);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static void TrimCache(string keep)
    {
        if (Cache.Count <= CacheLimit) return;
        foreach (var stale in Cache.OrderBy(kv => kv.Value.LastUse)
                     .Take(Cache.Count - CacheLimit).Select(kv => kv.Key))
        {
            if (stale != keep)
                Cache.TryRemove(stale, out _);
        }
    }

    /// <summary>
    /// The byte-mapped view: bytes [0, FragEnd) come from the file — except the
    /// 8/16 bytes at the retired header, replaced by a free-box head spanning
    /// the whole fragment region — and the synthesized moov follows at FragEnd.
    /// </summary>
    private sealed class VirtualClassicStream : Stream
    {
        private readonly Stream _file;
        private readonly byte[] _moov;
        private readonly long _fragEnd;
        private readonly byte[] _patch;
        private readonly long _patchPos;
        private long _pos;

        public VirtualClassicStream(Stream file, long moovOff, long fragEnd, byte[] moov)
        {
            _file = file;
            _fragEnd = fragEnd;
            _moov = moov;
            _patch = ClipWriter.FreeHeader(fragEnd - moovOff);
            _patchPos = moovOff;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _fragEnd + _moov.Length;

        public override long Position
        {
            get => _pos;
            set => _pos = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_pos >= Length || buffer.Length == 0) return 0;

            int n;
            if (_pos < _fragEnd)
            {
                int want = (int)Math.Min(buffer.Length, _fragEnd - _pos);
                _file.Seek(_pos, SeekOrigin.Begin);
                n = _file.Read(buffer[..want]);
                if (n <= 0) return 0; // file shrank underneath us — treat as EOF
                // Overlay the free-box head over whatever the disk still says.
                long overlayStart = Math.Max(_pos, _patchPos);
                long overlayEnd = Math.Min(_pos + n, _patchPos + _patch.Length);
                for (long p = overlayStart; p < overlayEnd; p++)
                    buffer[(int)(p - _pos)] = _patch[p - _patchPos];
            }
            else
            {
                int start = (int)(_pos - _fragEnd);
                n = Math.Min(buffer.Length, _moov.Length - start);
                _moov.AsSpan(start, n).CopyTo(buffer);
            }
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                _ => Length + offset,
            };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _file.Dispose();
            base.Dispose(disposing);
        }
    }
}
