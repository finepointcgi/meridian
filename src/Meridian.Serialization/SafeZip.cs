using System.IO.Compression;
using System.Text;

namespace Meridian.Serialization;

/// <summary>
/// Bounded reading of uploaded archives (.fmu, .reqifz, zipped sources). A small archive can inflate to gigabytes
/// ("zip bomb"), so every entry is read through a stream that stops at a size limit instead of trusting the header.
/// </summary>
public static class SafeZip
{
    /// <summary>Default limit for one decompressed entry.</summary>
    public const long MaxEntryBytes = 64L * 1024 * 1024;

    /// <summary>Opens an entry for reading; reading more than <paramref name="maxBytes"/> throws <see cref="InvalidDataException"/>.</summary>
    public static Stream Open(ZipArchiveEntry entry, long maxBytes = MaxEntryBytes)
    {
        if (entry.Length > maxBytes) throw TooLarge(entry, maxBytes);
        return new LimitedStream(entry.Open(), maxBytes, entry.FullName);
    }

    /// <summary>Reads an entry as UTF-8 text, at most <paramref name="maxBytes"/> decompressed bytes.</summary>
    public static string ReadText(ZipArchiveEntry entry, long maxBytes = MaxEntryBytes)
    {
        using var s = Open(entry, maxBytes);
        using var reader = new StreamReader(s, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static InvalidDataException TooLarge(ZipArchiveEntry entry, long max) =>
        new($"{entry.FullName} is larger than {max / (1024 * 1024)} MB when unpacked.");

    /// <summary>A read-only wrapper that fails once more than a fixed number of bytes have been read.</summary>
    public sealed class LimitedStream(Stream inner, long maxBytes, string name) : Stream
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
            if (_read > maxBytes) throw new InvalidDataException($"{name} is larger than {maxBytes / (1024 * 1024)} MB when unpacked.");
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
