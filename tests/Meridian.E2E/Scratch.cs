using static Microsoft.Playwright.Assertions;
namespace Meridian.E2E;
public sealed class Scratch(ServerFixture f) : E2ETest(f)
{
    [E2EFact]
    public async Task Debug()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        var dir = "/private/tmp/claude-501/-Users-finepointcgi-cameo-alt/f23ff5c4-b08d-49b9-846c-de5967f19add/scratchpad/probe/";
        await p.NewProjectAsync("Blank project", "Dbg" + Guid.NewGuid().ToString("N")[..4]);
        await p.RibbonAsync("Requirement");
        await Task.Delay(1000);
        await p.ScreenshotAsync(new() { Path = dir + "1.png" });
        Console.WriteLine("NAMEFIELDS " + await p.Inspector().GetByLabel("Name", new() { Exact = true }).CountAsync());
        Console.WriteLine("FOCUSED " + await p.EvaluateAsync<string>("() => document.activeElement?.outerHTML?.slice(0,200)"));
        var field = p.Inspector().GetByLabel("Name", new() { Exact = true });
        await field.FillAsync("Flow rate");
        await Task.Delay(500);
        await field.PressAsync("Tab");
        await Task.Delay(1000);
        await p.ScreenshotAsync(new() { Path = dir + "2.png" });
    }
}
