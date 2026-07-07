namespace Neolink.Recording;

/// <summary>
/// Process-wide storage write counters, fed by every <see cref="ClipWriter"/>
/// (event clips, previews and continuous segments alike) and sampled by the
/// monitor page. Cumulative on purpose: the sampler turns deltas into rates.
/// </summary>
public static class StorageMetrics
{
    private static long _bytes;
    private static long _files;

    /// <summary>Bytes handed to the recordings disk so far.</summary>
    public static long BytesWritten => Interlocked.Read(ref _bytes);

    /// <summary>Recording files completed (finalized) so far.</summary>
    public static long FilesCompleted => Interlocked.Read(ref _files);

    public static void AddBytes(long count) => Interlocked.Add(ref _bytes, count);
    public static void FileCompleted() => Interlocked.Increment(ref _files);
}
