namespace Meridian.Desktop.Tests;

public class StartPageTests
{
    [Fact]
    public void Shows_recent_files_encoded_and_the_update_banner()
    {
        var settings = new DesktopSettings();
        settings.Remember("/tmp/<script>.meridian", Guid.NewGuid(), DateTime.UtcNow);
        var update = new UpdateInfo("0.2.0", "v0.2.0", "https://example.invalid", new ReleaseAsset("Meridian-0.2.0-macos-arm64.dmg", "u", 1), null);
        var html = StartPage.Html(settings, update: update);
        Assert.Contains("Recent files", html);
        Assert.Contains("&lt;script&gt;.meridian", html);
        Assert.DoesNotContain("<script>.meridian", html);
        Assert.Contains("Meridian 0.2.0 is available.", html);
        Assert.Contains("class=\"update show\"", html);
        Assert.Contains("id=\"updates\" checked", html);
    }

    [Fact]
    public void Keeps_ten_recent_files_most_recent_first()
    {
        var settings = new DesktopSettings();
        for (var i = 0; i < 12; i++) settings.Remember($"/m/{i}.meridian", null, DateTime.UtcNow);
        settings.Remember("/m/3.meridian", null, DateTime.UtcNow);
        Assert.Equal("/m/3.meridian", settings.RecentFiles[0].Path);
        Assert.Single(settings.RecentFiles, r => r.Path == "/m/3.meridian");
    }
}
