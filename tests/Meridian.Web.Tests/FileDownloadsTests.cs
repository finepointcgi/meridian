using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Web.Tests;

public class FileDownloadsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "meridian-dl-test-" + Guid.NewGuid().ToString("N"));
    private readonly FileDownloads _downloads;

    public FileDownloadsTests() => _downloads = new FileDownloads(_dir);

    public void Dispose() => _downloads.Dispose();

    private string Staged(string text)
    {
        using var f = _downloads.CreateFile();
        f.Write(System.Text.Encoding.UTF8.GetBytes(text));
        return f.Name;
    }

    [Fact]
    public void Url_Works_Once_And_The_File_Is_Deleted_After_Serving()
    {
        var path = Staged("hello");
        var url = _downloads.Publish(path, "a.html", "text/html");
        Assert.StartsWith("_dl/", url);
        var token = url["_dl/".Length..];
        Assert.Equal(64, token.Length);

        var taken = _downloads.Take(token);
        Assert.NotNull(taken);
        Assert.Equal("a.html", taken.Value.Name);
        using (var reader = new StreamReader(taken.Value.Stream)) Assert.Equal("hello", reader.ReadToEnd());
        Assert.False(File.Exists(path));
        Assert.Null(_downloads.Take(token));
    }

    [Fact]
    public void Unclaimed_Files_Expire()
    {
        var path = Staged("x");
        var token = _downloads.Publish(path, "a.pdf", "application/pdf")["_dl/".Length..];
        _downloads.Sweep(DateTime.UtcNow + FileDownloads.Lifetime + TimeSpan.FromSeconds(1));
        Assert.False(File.Exists(path));
        Assert.Null(_downloads.Take(token));
        Assert.Null(_downloads.Take("not-a-token"));
    }

    [Fact]
    public async Task Small_Files_Go_Inline_Large_Ones_By_Url()
    {
        var js = new Bunit.TestContext().JSInterop;
        js.Mode = Bunit.JSRuntimeMode.Loose;
        var services = new ServiceCollection();
        services.AddSingleton(new FileDownloads(_dir + "-2") { InlineLimit = 10 });
        using var sp = services.BuildServiceProvider();

        var small = await GeneratedDownload.DeliverAsync(sp, js.JSRuntime, "s.txt", "text/plain", s => s.WriteAsync("tiny"u8.ToArray()).AsTask());
        Assert.Equal(4, small);
        var inline = Assert.Single(js.Invocations, i => i.Identifier == "meridian.download");
        Assert.Equal(Convert.ToBase64String("tiny"u8.ToArray()), inline.Arguments[2]);

        var big = await GeneratedDownload.DeliverAsync(sp, js.JSRuntime, "b.txt", "text/plain", s => s.WriteAsync(new byte[1000]).AsTask());
        Assert.Equal(1000, big);
        var byUrl = Assert.Single(js.Invocations, i => i.Identifier == "meridian.downloadUrl");
        Assert.Equal("b.txt", byUrl.Arguments[0]);
        Assert.StartsWith("_dl/", (string)byUrl.Arguments[1]!);
        Assert.Single(Directory.GetFiles(_dir + "-2"));   // waiting to be fetched
        sp.GetRequiredService<FileDownloads>().Dispose();
    }
}
