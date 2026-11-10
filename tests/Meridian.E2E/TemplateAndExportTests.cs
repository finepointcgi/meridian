using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

public sealed class TemplateAndExportTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Every_project_template_creates_a_project_that_opens_cleanly()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;

        // Read the gallery's template names (and check their thumbnails rendered).
        await p.ProjectMenuAsync("New project…");
        var dlg = p.MudDialog();
        await Expect(dlg.Locator(".tpl-card").First).ToBeVisibleAsync();
        var names = (await dlg.Locator(".tpl-card .tpl-name").AllAsync()).ToList();
        var templates = new List<string>();
        foreach (var n in names) templates.Add((await n.EvaluateAsync<string>("e => e.firstChild.textContent")).Trim());
        Assert.True(templates.Count >= 8, "templates: " + string.Join(", ", templates));
        foreach (var img in await dlg.Locator(".tpl-thumb img").AllAsync())
        {
            await img.ScrollIntoViewIfNeededAsync();
            await Ui.EventuallyAsync(async () => await img.EvaluateAsync<bool>("i => i.complete && i.naturalWidth > 0"), "template thumbnail should load");
        }
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();

        foreach (var template in templates)
        {
            var name = $"{template} {Guid.NewGuid().ToString("N")[..4]}";
            await p.NewProjectAsync(template, name);
            var counts = await p.CountsAsync();
            Assert.True(counts.Diagrams >= 1, $"{template}: no diagram ({counts})");
            Assert.True(counts.Elements >= 1, $"{template}: empty model ({counts})");
            await Expect(p.Tree().Locator(".row").First).ToBeVisibleAsync();
            // Open the first diagram and check it draws.
            var firstDiagram = p.Tree().Locator(".row").Filter(new() { Has = p.Locator(".meta") }).First;
            await firstDiagram.DblClickAsync();
            await Expect(p.Locator(".tabstrip .tab.active")).ToBeVisibleAsync();
            await Expect(p.Locator(".diagram-host svg.main")).ToBeVisibleAsync();
            s.AssertNoErrors();
        }
    }

    [E2EFact]
    public async Task Diagram_exports_download_svg_and_png()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Export " + Guid.NewGuid().ToString("N")[..6]);
        await p.Tree().GetByText("Vehicle Structure", new() { Exact = true }).DblClickAsync();
        await Expect(p.Locator(".tabstrip .tab.active")).ToContainTextAsync("Vehicle Structure");

        var svg = await p.RunAndWaitForDownloadAsync(() => p.ProjectMenuAsync("Export diagram as SVG"));
        Assert.Equal("Vehicle Structure.svg", svg.SuggestedFilename);
        var svgText = await File.ReadAllTextAsync(await svg.PathAsync());
        Assert.StartsWith("<svg", svgText.TrimStart());
        Assert.Contains("Engine", svgText);

        await p.ProjectMenuAsync("Export diagram as PNG");
        var dlg = p.MudDialog();
        var png = await p.RunAndWaitForDownloadAsync(() => dlg.GetByRole(AriaRole.Button, new() { Name = "Export" }).ClickAsync());
        Assert.EndsWith(".png", png.SuggestedFilename);
        var bytes = await File.ReadAllBytesAsync(await png.PathAsync());
        Assert.True(bytes.Length > 1000, $"PNG too small: {bytes.Length} bytes");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
    }
}
