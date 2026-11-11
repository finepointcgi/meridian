using Meridian.Core.Model;
using Meridian.Serialization.Reports;

namespace Meridian.Benchmarks;

/// <summary>Report scenarios: generated straight into a counting sink (what a file or HTTP response would receive).</summary>
internal static class Bench
{
    public static long HtmlReport(ModelRepository m)
    {
        var sink = new CountingStream();
        Meridian.Serialization.HtmlReport.Generate(m, sink);
        return sink.Count;
    }

    public static long Template(ModelRepository m, DocTemplate t, ReportFormat f, ResolveOptions? options = null)
    {
        var sink = new CountingStream();
        ReportEngine.Generate(m, t, f, sink, options: options);
        return sink.Count;
    }

    /// <summary>A forward-only stream that only counts bytes.</summary>
    private sealed class CountingStream : Stream
    {
        public long Count { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Count += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
    }
}
