using System.Net;
using Meridian.Desktop;

namespace Meridian.Desktop.Tests;

/// <summary>The save-file bridge for streamed reports: which URLs the host will fetch, and the chunked copy to disk.</summary>
public sealed class DesktopDownloadsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("meridian-desktop-dl-");

    public void Dispose() => _dir.Delete(true);

    [Theory]
    [InlineData("http://127.0.0.1:5123/api/reports/0b7e/file?ticket=t", true)]
    [InlineData("https://meridian.example.com/api/reports/0b7e/file?ticket=t", true)]
    [InlineData("https://meridian.example.com/team/api/reports/0b7e/file", true)]      // a server under a path base
    [InlineData("https://meridian.example.com:8443/api/reports/0b7e/file", false)]     // another port
    [InlineData("https://evil.example.net/api/reports/0b7e/file", false)]              // another host
    [InlineData("http://meridian.example.com/api/reports/0b7e/file", false)]           // another scheme
    [InlineData("https://meridian.example.com/api/projects", false)]                   // not a report
    [InlineData("file:///etc/passwd", false)]
    public void Only_Report_Downloads_From_Known_Servers_Are_Fetched(string url, bool allowed) =>
        Assert.Equal(allowed, DesktopDownloads.IsAllowed(new Uri(url), ["http://127.0.0.1:5123", null, "https://meridian.example.com/team/"]));

    [Fact]
    public async Task Response_Is_Copied_To_The_File_In_Chunks()
    {
        var payload = new byte[DesktopDownloads.ChunkSize * 5 + 123];
        new Random(4).NextBytes(payload);
        var source = new TrickleStream(payload);
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) }));
        var target = Path.Combine(_dir.FullName, "SDD.pdf");
        var progress = new List<long>();
        var written = await DesktopDownloads.SaveAsync(http, new Uri("http://127.0.0.1:5123/api/reports/x/file"), target, new SyncProgress(progress.Add));
        Assert.Equal(payload.Length, written);
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        Assert.True(progress.Count >= 6, $"{progress.Count} chunks");
        Assert.True(source.LargestRead <= DesktopDownloads.ChunkSize, $"read {source.LargestRead} bytes at once");
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task A_Failed_Download_Leaves_No_File()
    {
        var target = Path.Combine(_dir.FullName, "report.html");
        using (var refused = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))))
            await Assert.ThrowsAsync<HttpRequestException>(() => DesktopDownloads.SaveAsync(refused, new Uri("http://127.0.0.1:1/api/reports/x/file"), target));
        Assert.False(File.Exists(target));

        // The connection drops half-way: the partial file is removed, the target never appears.
        var broken = new TrickleStream(new byte[DesktopDownloads.ChunkSize * 3], failAfter: DesktopDownloads.ChunkSize * 2);
        using (var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(broken) })))
            await Assert.ThrowsAsync<IOException>(() => DesktopDownloads.SaveAsync(http, new Uri("http://127.0.0.1:1/api/reports/x/file"), target));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class SyncProgress(Action<long> a) : IProgress<long>
    {
        public void Report(long value) => a(value);
    }

    /// <summary>A response body that records the biggest read and can fail part-way.</summary>
    private sealed class TrickleStream(byte[] data, int failAfter = -1) : Stream
    {
        private int _position;
        public int LargestRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (failAfter >= 0 && _position >= failAfter) throw new IOException("The connection was reset.");
            var n = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            LargestRead = Math.Max(LargestRead, n);
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
