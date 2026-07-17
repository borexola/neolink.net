// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Neolink.Recording;

/// <summary>
/// Footage encryption at rest (beta, opt-in via recording.encrypt): every read
/// and write of clip/segment/thumbnail files goes through this vault. Writes
/// produce chunked AES-256-GCM files when encryption is on; reads SNIFF the
/// header, so plaintext footage from before the switch (or after turning it
/// back off) keeps playing forever, side by side with encrypted files.
///
/// Threat model: protects footage against a stolen/decommissioned disk, a NAS
/// share mounted elsewhere, and backups — NOT against an attacker with full
/// access to the running host, who can read the key like the server does. The
/// master key derives from the same secret as <see cref="Neolink.Notifications.SecretProtector"/>
/// (NEOLINK_SECRET_KEY env var, or the state dir's secret.key): lose that key,
/// lose the encrypted footage — back it up, and ideally keep the state dir on
/// a different disk than the recordings.
///
/// The vault is configured once at startup and consulted from static call
/// sites (ClipWriter recovery, VirtualMp4) as well as instance ones — a
/// process-wide setting, like the storage metrics.
/// </summary>
public static class FootageVault
{
    private static byte[]? _master;
    private static bool _encryptNew;

    /// <summary>Wires the vault: <paramref name="masterKey"/> enables DECRYPTION of
    /// existing encrypted footage (always pass it when recording runs, so footage
    /// recorded while encryption WAS on outlives the toggle); <paramref name="encryptNew"/>
    /// additionally encrypts newly written files.</summary>
    public static void Configure(byte[]? masterKey, bool encryptNew)
    {
        if (masterKey is { Length: not 32 })
            throw new ArgumentException("master key must be 32 bytes", nameof(masterKey));
        _master = masterKey;
        _encryptNew = encryptNew && masterKey != null;
    }

    /// <summary>New footage files will be written encrypted.</summary>
    public static bool EncryptingNew => _encryptNew;

    /// <summary>Creates a footage file for writing (replacing any existing file) —
    /// encrypted when the vault says so, plain otherwise. The returned stream is
    /// seekable and readable either way, exactly like the FileStream it replaces.</summary>
    public static Stream Create(string path)
    {
        if (!_encryptNew || _master == null)
            // Large buffer: fewer, bigger writes are what HDDs want.
            return new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        // The encrypting layer already writes in whole chunks, so no extra buffer.
        var inner = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1);
        return EncryptedFootageStream.Create(inner, _master);
    }

    /// <summary>Opens footage for reading, sniffing the format: encrypted files
    /// decrypt transparently, everything else is served raw (the pre-existing
    /// behavior). Seekable both ways, so HTTP range serving just works.</summary>
    public static Stream OpenRead(string path) => Open(path, writable: false);

    /// <summary>Opens footage for in-place repair (crash recovery), sniffing the
    /// format like <see cref="OpenRead"/>.</summary>
    public static Stream OpenReadWrite(string path) => Open(path, writable: true);

    private static Stream Open(string path, bool writable)
    {
        // Sniff with a positional read on the raw handle: a buffered FileStream
        // would fill its whole buffer (a 1 MB read at the file head) just to
        // answer an 8-byte question, then discard it when the caller seeks — a
        // wasted megabyte and a disk seek on EVERY range request, which is what
        // high-speed playback is made of. RandomAccess reads exactly 8 bytes and
        // moves no file pointer; the same handle then backs the real stream.
        var access = writable ? FileAccess.ReadWrite : FileAccess.Read;
        var handle = File.OpenHandle(path, FileMode.Open, access,
            FileShare.ReadWrite | FileShare.Delete);
        try
        {
            Span<byte> head = stackalloc byte[8];
            int got = RandomAccess.Read(handle, head, 0);
            bool encrypted = got == 8 && EncryptedFootageStream.IsMagic(head)
                && RandomAccess.GetLength(handle) >= EncryptedFootageStream.HeaderLen;
            if (!encrypted)
                // Large buffer: fewer, bigger reads are what serving video wants.
                return new FileStream(handle, access, bufferSize: writable ? 1 : 1 << 20);
            if (_master == null)
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)} is encrypted footage but no key is available — " +
                    "is the state dir (secret.key) or NEOLINK_SECRET_KEY intact?");
            // Unbuffered: the encrypting layer does its own slot-sized IO.
            var raw = new FileStream(handle, access, bufferSize: 1);
            return EncryptedFootageStream.Open(raw, _master, writable);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Writes a small artifact (a thumbnail) in one go, encrypted when the
    /// vault says so — the drop-in for File.WriteAllBytes on footage artifacts.</summary>
    public static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        await using var s = Create(path);
        await s.WriteAsync(bytes, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// A seekable random-access stream over a chunked AES-256-GCM file. Layout:
///
///   header (32 B): magic "NLNKENC\x01" | chunk size (u32 LE) | file id (16 B) | reserved (4 B)
///   slot i (at 32 + i·(chunk+28)): nonce (12 B) | tag (16 B) | ciphertext (≤ chunk)
///
/// Every slot is sealed independently with a FRESH random nonce each time it is
/// (re)written, under a per-file key: HKDF-SHA256(master, salt: file id). The
/// GCM tag authenticates the slot's position via AAD (file id + slot index), so
/// slots cannot be reordered or transplanted between files. Only the final slot
/// may be short; that is also why it can be rewritten in place as the recorder
/// appends — its slot offset never moves. Integrity is per-slot: any in-place
/// tamper is detected on read; a truncated tail behaves like a crash-truncated
/// recording, which every consumer already copes with (that trade is what lets
/// a live file grow and be watched at the same time).
///
/// Position, Length and all offsets are in PLAINTEXT coordinates — callers seek
/// and patch exactly as they would a plain file. One slot is cached; sequential
/// IO touches each slot once, and a range request decrypts only what it covers.
/// AES-GCM is hardware-accelerated, so throughput dwarfs footage bitrates.
/// </summary>
public sealed class EncryptedFootageStream : Stream
{
    private static readonly byte[] Magic = { (byte)'N', (byte)'L', (byte)'N', (byte)'K',
                                             (byte)'E', (byte)'N', (byte)'C', 1 };
    public const int HeaderLen = 32;
    public const int SlotOverhead = 12 + 16; // nonce + tag
    public const int DefaultChunk = 64 * 1024;

    private readonly Stream _inner;
    private readonly AesGcm _aes;
    private readonly byte[] _fileId;
    private readonly int _chunk;
    private readonly bool _writable;

    private long _pos;              // plaintext position
    private long _plainLen;         // write mode: the source of truth
    private long _slotIdx = -1;     // cached slot (-1 = none)
    private readonly byte[] _buf;   // cached slot plaintext
    private int _bufLen;            // valid bytes in _buf
    private bool _dirty;
    private readonly byte[] _slotBuf; // scratch: one slot as stored on disk

    private int SlotSize => _chunk + SlotOverhead;

    private EncryptedFootageStream(Stream inner, byte[] master, byte[] fileId, int chunk, bool writable)
    {
        _inner = inner;
        _fileId = fileId;
        _chunk = chunk;
        _writable = writable;
        _buf = new byte[chunk];
        _slotBuf = new byte[SlotSize];
        var fileKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: fileId,
            info: "neolink-footage-v1"u8.ToArray());
        _aes = new AesGcm(fileKey, tagSizeInBytes: 16);
        CryptographicOperations.ZeroMemory(fileKey);
        _plainLen = DiskPlainLength();
    }

    /// <summary>Starts a fresh encrypted file (writes the header immediately).</summary>
    internal static EncryptedFootageStream Create(Stream inner, byte[] master)
    {
        var fileId = RandomNumberGenerator.GetBytes(16);
        var header = new byte[HeaderLen];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), DefaultChunk);
        fileId.CopyTo(header, 12);
        inner.Write(header);
        return new EncryptedFootageStream(inner, master, fileId, DefaultChunk, writable: true);
    }

    /// <summary>Opens an existing encrypted file (the caller already sniffed it).</summary>
    internal static EncryptedFootageStream Open(Stream inner, byte[] master, bool writable)
    {
        var header = new byte[HeaderLen];
        inner.Seek(0, SeekOrigin.Begin);
        inner.ReadExactly(header);
        if (header[7] != Magic[7])
            throw new InvalidDataException(
                $"encrypted footage format v{header[7]} — this Neolink version reads v{Magic[7]}; " +
                "upgrade the server to play this file");
        var chunk = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        if (chunk is < 4096 or > (1 << 24))
            throw new InvalidDataException("encrypted footage header is corrupt (chunk size)");
        return new EncryptedFootageStream(inner, master, header.AsSpan(12, 16).ToArray(), chunk, writable);
    }

    /// <summary>Does this 8-byte header carry the footage magic? Matches ANY
    /// format version — an unknown (future) version must fail loudly in
    /// <see cref="Open"/>, never fall through and be served as plaintext video.</summary>
    internal static bool IsMagic(ReadOnlySpan<byte> head) =>
        head.Length >= 7 && head[..7].SequenceEqual(Magic.AsSpan(0, 7));

    // ------------------------------------------------------------------ slot IO

    private void Aad(long slot, Span<byte> aad)
    {
        _fileId.CopyTo(aad);
        BinaryPrimitives.WriteInt64LittleEndian(aad[16..], slot);
    }

    /// <summary>Brings the given slot into the cache (flushing the previous one).</summary>
    private void LoadSlot(long idx)
    {
        if (_slotIdx == idx) return;
        FlushSlot();
        _slotIdx = idx;
        _bufLen = ReadSlot(idx, _buf);
    }

    /// <summary>Reads and decrypts one slot from disk into <paramref name="plain"/>;
    /// returns its plaintext length (0 = beyond EOF or an unreadable torn tail).</summary>
    private int ReadSlot(long idx, byte[] plain)
    {
        Span<byte> aad = stackalloc byte[24];
        Aad(idx, aad);
        for (int attempt = 0; ; attempt++)
        {
            _inner.Seek(HeaderLen + idx * (long)SlotSize, SeekOrigin.Begin);
            int got = ReadUpTo(_inner, _slotBuf);
            if (got <= SlotOverhead) return 0; // beyond EOF, or a tail torn mid-write
            int cipherLen = Math.Min(got - SlotOverhead, _chunk);
            try
            {
                _aes.Decrypt(_slotBuf.AsSpan(0, 12), _slotBuf.AsSpan(SlotOverhead, cipherLen),
                    _slotBuf.AsSpan(12, 16), plain.AsSpan(0, cipherLen), aad);
                return cipherLen;
            }
            catch (CryptographicException)
            {
                // A slot being rewritten right now (the recorder appending, or a
                // finalize patch) reads torn — retry once; a final slot that still
                // fails IS the growing tail, so treat it as not-there-yet.
                if (attempt == 0) continue;
                bool last = HeaderLen + (idx + 1) * (long)SlotSize >= _inner.Length;
                if (last) return 0;
                throw new InvalidDataException(
                    "encrypted footage failed authentication (tampered file or wrong key)");
            }
        }
    }

    private static int ReadUpTo(Stream s, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = s.Read(buf, total, buf.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>Seals and writes the cached slot with a fresh nonce.</summary>
    private void FlushSlot()
    {
        if (!_dirty || _slotIdx < 0) return;
        long offset = HeaderLen + _slotIdx * (long)SlotSize;
        if (offset > _inner.Length)
            throw new NotSupportedException("sparse writes are not supported in encrypted footage");
        RandomNumberGenerator.Fill(_slotBuf.AsSpan(0, 12));
        Span<byte> aad = stackalloc byte[24];
        Aad(_slotIdx, aad);
        _aes.Encrypt(_slotBuf.AsSpan(0, 12), _buf.AsSpan(0, _bufLen),
            _slotBuf.AsSpan(SlotOverhead, _bufLen), _slotBuf.AsSpan(12, 16), aad);
        _inner.Seek(offset, SeekOrigin.Begin);
        _inner.Write(_slotBuf, 0, SlotOverhead + _bufLen);
        _dirty = false;
    }

    /// <summary>Plaintext length as derivable from the bytes on disk — every slot
    /// is full except a shorter final one, so the math is exact.</summary>
    private long DiskPlainLength()
    {
        long payload = _inner.Length - HeaderLen;
        if (payload <= 0) return 0;
        long full = payload / SlotSize;
        long rem = payload % SlotSize;
        return full * _chunk + Math.Max(0, rem - SlotOverhead);
    }

    // ------------------------------------------------------------------ Stream

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => _writable;

    /// <summary>Read mode recomputes from disk so a growing live file keeps
    /// reporting its current size, exactly like a plain FileStream would.</summary>
    public override long Length => _writable ? _plainLen : Math.Max(DiskPlainLength(), _plainLen);

    public override long Position
    {
        get => _pos;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("cannot seek before the beginning");
        _pos = target; // the slot loads lazily on the next read/write
        return _pos;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (buffer.Length > 0)
        {
            LoadSlot(_pos / _chunk);
            int inSlot = (int)(_pos % _chunk);
            int n = Math.Min(buffer.Length, _bufLen - inSlot);
            if (n <= 0) break; // EOF (or the live tail hasn't landed yet)
            _buf.AsSpan(inSlot, n).CopyTo(buffer);
            buffer = buffer[n..];
            _pos += n;
            total += n;
        }
        return total;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!_writable) throw new NotSupportedException("stream is read-only");
        while (buffer.Length > 0)
        {
            LoadSlot(_pos / _chunk);
            int inSlot = (int)(_pos % _chunk);
            if (inSlot > _bufLen) // seek past the tail inside the slot: zero-fill
                _buf.AsSpan(_bufLen, inSlot - _bufLen).Clear();
            int n = Math.Min(buffer.Length, _chunk - inSlot);
            buffer[..n].CopyTo(_buf.AsSpan(inSlot));
            _bufLen = Math.Max(_bufLen, inSlot + n);
            _dirty = true;
            _pos += n;
            _plainLen = Math.Max(_plainLen, _pos);
            buffer = buffer[n..];
            if (_bufLen == _chunk) FlushSlot(); // stream full slots to disk as we go
        }
    }

    public override void Flush()
    {
        FlushSlot();
        _inner.Flush();
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException("encrypted footage cannot be truncated in place");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { FlushSlot(); } catch { /* the file is already toast; keep disposing */ }
            _aes.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
