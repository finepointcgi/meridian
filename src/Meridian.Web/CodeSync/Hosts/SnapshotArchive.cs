using System.Collections;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Meridian.Core.CodeSync;

namespace Meridian.Web.CodeSync.Hosts;

/// <summary>Hard limits on what a repository archive may contain. Tests shrink them.</summary>
public sealed record SnapshotLimits
{
    /// <summary>Compressed archive as downloaded.</summary>
    public long MaxArchiveBytes { get; init; } = 200L * 1024 * 1024;
    /// <summary>The whole archive once decompressed, including files that are not watched (bounds gzip bombs).</summary>
    public long MaxUncompressedBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    /// <summary>Entries of any kind in the archive.</summary>
    public int MaxEntries { get; init; } = 500_000;
    /// <summary>Files kept for the reader.</summary>
    public int MaxFiles { get; init; } = 20_000;
    /// <summary>A kept file larger than this is skipped (generated code, data).</summary>
    public long MaxFileBytes { get; init; } = 2L * 1024 * 1024;
    /// <summary>All kept files together.</summary>
    public long MaxTotalBytes { get; init; } = 500L * 1024 * 1024;

    public static readonly SnapshotLimits Default = new();
}

/// <summary>Every watched source file of a commit, plus files that were skipped (too large, not text).</summary>
public sealed class CodeSnapshot(IReadOnlyList<CodeFile> files, IReadOnlyList<string> skipped) : IReadOnlyList<CodeFile>
{
    public IReadOnlyList<CodeFile> Files { get; } = files;
    public IReadOnlyList<string> Skipped { get; } = skipped;
    public CodeFile this[int index] => Files[index];
    public int Count => Files.Count;
    public IEnumerator<CodeFile> GetEnumerator() => Files.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Streams a repository .tar.gz (GitHub tarball, GitLab archive.tar.gz) into a <see cref="CodeSnapshot"/>. Nothing is
/// written to disk; the archive is read once, front to back, and every limit is enforced on bytes actually read
/// (the same approach as <see cref="Meridian.Serialization.SafeZip"/>) rather than on sizes the archive claims.
/// </summary>
public static class SnapshotArchive
{
    /// <summary>
    /// Reads a .tar.gz whose entries sit under a single top-level folder (stripped). Keeps regular files inside
    /// <paramref name="paths"/> accepted by <paramref name="include"/>; ignores directories, symlinks and hard links.
    /// Throws <see cref="GitHostException"/> when a limit is exceeded or an entry path is unsafe.
    /// </summary>
    public static async Task<CodeSnapshot> ReadTarGzAsync(Stream archive, IReadOnlyList<string> paths, Func<string, bool> include, SnapshotLimits limits, CancellationToken ct = default)
    {
        var files = new List<CodeFile>();
        var skipped = new List<string>();
        long total = 0;
        var entries = 0;
        try
        {
            await using var compressed = new BoundedStream(archive, limits.MaxArchiveBytes, $"The repository archive is larger than {Mb(limits.MaxArchiveBytes)}.");
            await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            await using var tar = new BoundedStream(gzip, limits.MaxUncompressedBytes, $"The repository is larger than {Mb(limits.MaxUncompressedBytes)} when unpacked.");
            await using var reader = new TarReader(tar);
            while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                if (++entries > limits.MaxEntries) throw new GitHostException($"The archive has more than {limits.MaxEntries:N0} entries.");
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile)) continue;
                var path = RelativePath(entry.Name);
                if (path is null) continue;
                if (!CodeRepoConfig.InPaths(path, paths) || !include(path)) continue;
                if (entry.Length > limits.MaxFileBytes) { skipped.Add($"{path} (larger than {Mb(limits.MaxFileBytes)})"); continue; }
                if (files.Count >= limits.MaxFiles) throw new GitHostException($"More than {limits.MaxFiles:N0} source files in the watched folders. Narrow the folders to sync.");
                total += entry.Length;
                if (total > limits.MaxTotalBytes) throw new GitHostException($"The watched source files add up to more than {Mb(limits.MaxTotalBytes)}. Narrow the folders to sync.");
                if (entry.DataStream is null) { files.Add(new CodeFile(path, "")); continue; }
                var bytes = new byte[entry.Length];   // ≤ MaxFileBytes, checked above
                await entry.DataStream.ReadExactlyAsync(bytes, ct);
                if (Array.IndexOf(bytes, (byte)0) >= 0) { skipped.Add($"{path} (binary)"); continue; }
                files.Add(new CodeFile(path, Decode(bytes)));
            }
        }
        catch (InvalidDataException ex)
        {
            throw new GitHostException($"The repository archive could not be read: {ex.Message}");
        }
        catch (EndOfStreamException)
        {
            throw new GitHostException("The repository archive ended unexpectedly.");
        }
        catch (FormatException ex)
        {
            throw new GitHostException($"The repository archive could not be read: {ex.Message}");
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new CodeSnapshot(files, skipped);
    }

    private static string Mb(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes:N0} bytes";

    /// <summary>
    /// The repository-relative path of an archive entry with the top-level folder removed; null for the folder itself.
    /// Absolute paths, ".." segments, drive letters and NUL characters make the whole archive invalid (nothing is
    /// written to disk, but such a name has no business in a repository archive).
    /// </summary>
    public static string? RelativePath(string name)
    {
        var n = name.Replace('\\', '/');
        if (n.StartsWith('/') || n.Contains('\0') || (n.Length >= 2 && n[1] == ':'))
            throw new GitHostException($"The archive contains an unsafe path: {Printable(name)}");
        var segments = n.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
            throw new GitHostException($"The archive contains an unsafe path: {Printable(name)}");
        segments = segments.Where(s => s != ".").ToArray();
        return segments.Length < 2 ? null : string.Join('/', segments.Skip(1));
    }

    private static string Printable(string s) => new(s.Take(200).Select(c => char.IsControl(c) ? '?' : c).ToArray());

    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Read-only pass-through that throws <see cref="GitHostException"/> once more than a fixed number of bytes were read.</summary>
    internal sealed class BoundedStream(Stream inner, long maxBytes, string message) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int n)
        {
            _read += n;
            if (_read > maxBytes) throw new GitHostException(message);
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
