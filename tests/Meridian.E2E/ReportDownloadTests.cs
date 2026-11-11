using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Generate document → the file streams from GET api/reports/{id}/file into a real browser download.</summary>
public sealed class ReportDownloadTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Generating_a_report_downloads_it_from_the_streaming_endpoint()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Report download " + Guid.NewGuid().ToString("N")[..6]);

        // PDF (the default format) of the SRS (the default template).
        await p.RibbonAsync("Report");
        var dlg = p.MudDialog();
        await Expect(dlg.GetByText("Generate document")).ToBeVisibleAsync();
        await Expect(dlg.Locator(".report-scope")).ToBeVisibleAsync();
        var pdf = await p.RunAndWaitForDownloadAsync(() => dlg.GetByRole(AriaRole.Button, new() { Name = "Generate", Exact = true }).ClickAsync());
        Assert.EndsWith(".pdf", pdf.SuggestedFilename);
        Assert.Contains("/api/reports/", pdf.Url);
        var bytes = await File.ReadAllBytesAsync(await pdf.PathAsync());
        Assert.True(bytes.Length > 10_000, $"{bytes.Length} bytes");
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        await Expect(dlg.GetByText(new System.Text.RegularExpressions.Regex("Generated .+\\.pdf"))).ToBeVisibleAsync();

        // HTML, limited to one package.
        await dlg.GetByText("HTML (single file)").ClickAsync();
        await dlg.Locator(".report-scope").ClickAsync();
        await p.Locator(".mud-popover-open .mud-list-item", new() { HasTextString = "A package…" }).ClickAsync();
        await dlg.Locator(".report-scope-package").ClickAsync();
        await p.Locator(".mud-popover-open .mud-list-item", new() { HasTextString = "Behavior" }).First.ClickAsync();
        var html = await p.RunAndWaitForDownloadAsync(() => dlg.GetByRole(AriaRole.Button, new() { Name = "Generate", Exact = true }).ClickAsync());
        Assert.EndsWith(".html", html.SuggestedFilename);
        var text = await File.ReadAllTextAsync(await html.PathAsync());
        Assert.Contains("System Requirements Specification", text);
        Assert.StartsWith("<!doctype html>", text);

        // The link only works for the signed-in owner.
        using var anonymous = new HttpClient();
        using var response = await anonymous.GetAsync(html.Url);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        s.AssertNoErrors();
    }
}
