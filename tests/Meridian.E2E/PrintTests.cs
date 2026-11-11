using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Print / PDF…: page setup and preview, then a multi-page PDF streamed from the report download endpoint.</summary>
public sealed class PrintTests(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Vehicle_structure_prints_on_two_A4_landscape_pages()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        await p.NewProjectAsync("Vehicle sample", "Print " + Guid.NewGuid().ToString("N")[..6]);
        await p.Tree().GetByText("Vehicle Structure", new() { Exact = true }).DblClickAsync();
        await Expect(p.Locator(".tabstrip .tab.active")).ToContainTextAsync("Vehicle Structure");
        await Expect(p.Locator(".diagram-host svg.main")).ToBeVisibleAsync();

        // ⌘P / Ctrl+P opens the page setup (the browser's own print dialog is not shown).
        await p.Keyboard.PressAsync("Control+p");
        var dlg = p.MudDialog();
        await Expect(dlg.GetByText("Print “Vehicle Structure”")).ToBeVisibleAsync();
        await Expect(dlg.Locator(".print-preview img")).ToBeVisibleAsync();

        await dlg.Locator("select.print-paper").SelectOptionAsync("A4");
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Landscape", Exact = true }).ClickAsync();
        await dlg.GetByRole(AriaRole.Button, new() { Name = "Fit to pages", Exact = true }).ClickAsync();
        var across = dlg.Locator("input[aria-label='Pages across']");
        await across.FillAsync("2");
        await across.PressAsync("Tab");
        await dlg.Locator("input[aria-label='Pages down']").FillAsync("1");
        await dlg.Locator("input[aria-label='Pages down']").PressAsync("Tab");
        await Expect(dlg.Locator(".print-summary")).ToContainTextAsync("2 × 1 = 2 pages");
        await Expect(dlg.Locator(".print-thumb")).ToHaveCountAsync(2);

        var pdf = await p.RunAndWaitForDownloadAsync(() => dlg.GetByRole(AriaRole.Button, new() { Name = "Save PDF" }).ClickAsync());
        Assert.Equal("Vehicle Structure.pdf", pdf.SuggestedFilename);
        Assert.Contains("/api/reports/", pdf.Url);
        var bytes = await File.ReadAllBytesAsync(await pdf.PathAsync());
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Equal(2, Regex.Matches(text, @"/Type\s*/Page\b").Count);
        // A4 landscape: 841.89 × 595.28 pt.
        var box = Regex.Match(text, @"/MediaBox\s*\[([^\]]+)\]");
        Assert.True(box.Success, "no MediaBox");
        var size = box.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(841.89, size[2] - size[0], 0.5);
        Assert.Equal(595.28, size[3] - size[1], 0.5);
        s.AssertNoErrors();
    }
}
