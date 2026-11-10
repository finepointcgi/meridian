using System.Text.Json;
using Microsoft.Playwright;

namespace Meridian.Web.Tests;

/// <summary>Runs only when MERIDIAN_E2E_URL points at a running Meridian (e.g. local mode: http://127.0.0.1:5199/?launch=TOKEN).</summary>
public sealed class E2ETheoryAttribute : TheoryAttribute
{
    public E2ETheoryAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MERIDIAN_E2E_URL")))
            Skip = "Set MERIDIAN_E2E_URL to a running Meridian to run the browser layout checks (Playwright Chromium).";
    }
}

/// <summary>
/// Phone and tablet layout in a real browser (Chromium with iPhone 13 / iPad device emulation): no horizontal page
/// scroll, 44px touch targets, every phone view reachable, tablet drawers and ribbon overflow. Set MERIDIAN_E2E_SHOTS to a
/// folder to keep screenshots.
/// </summary>
public class MobileLayoutE2ETests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("MERIDIAN_E2E_URL");
    private static readonly string? Shots = Environment.GetEnvironmentVariable("MERIDIAN_E2E_SHOTS");

    private const string OverflowScript = """
        () => {
          const w = window.innerWidth;
          const skip = el => el.closest('.canvas, .ribbon, .tabstrip, .dock-tabs, .m-chips, .mud-popover-provider, .notif-pop');
          const bad = [];
          for (const el of document.querySelectorAll('body *')) {
            if (skip(el)) continue;
            const s = getComputedStyle(el);
            if (s.display === 'none' || s.visibility === 'hidden' || s.position === 'fixed' && el.closest('.mud-overlay')) continue;
            const r = el.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) continue;
            // Clipped by a hidden-overflow ancestor (e.g. ellipsis text) is fine.
            let clipped = false;
            for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
              const ps = getComputedStyle(p);
              if (ps.overflowX !== 'visible') { const pr = p.getBoundingClientRect(); if (pr.right <= w + 1) { clipped = true; break; } }
            }
            if (!clipped && (r.right > w + 1 || r.left < -1)) bad.push((el.className?.baseVal ?? el.className ?? el.tagName) + ' ' + Math.round(r.left) + '..' + Math.round(r.right));
          }
          return { scroll: Math.max(document.documentElement.scrollWidth, document.body.scrollWidth) - w, bad: bad.slice(0, 10) };
        }
        """;

    private const string SmallTargetsScript = """
        (sel) => [...document.querySelectorAll(sel)].filter(b => b.offsetParent !== null).map(b => {
            const r = b.getBoundingClientRect();
            return { name: b.getAttribute('aria-label') || b.textContent.trim().slice(0, 30), w: r.width, h: r.height };
          }).filter(t => t.w < 44 || t.h < 44)
        """;

    public sealed record OverflowReport(double Scroll, string[] Bad);
    public sealed record Target(string Name, double W, double H);

    [E2ETheory]
    [InlineData("phone-375", "iPhone 13", 375, 812)]
    [InlineData("phone-768", "iPad Mini", 768, 1024)]
    [InlineData("tablet-1024", "iPad Mini landscape", 1024, 768)]
    public async Task Layout_Fits_The_Screen_And_The_Main_Views_Are_Reachable(string name, string device, int width, int height)
    {
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        var options = pw.Devices[device];
        options.ViewportSize = new() { Width = width, Height = height };
        await using var context = await browser.NewContextAsync(options);
        var page = await context.NewPageAsync();
        await page.GotoAsync(Url!);
        await page.WaitForSelectorAsync(".app", new() { Timeout = 30000 });
        await OpenSampleIfAsked(page);
        await page.WaitForSelectorAsync(".diagram-host .canvas", new() { Timeout = 30000 });
        await page.WaitForTimeoutAsync(800);
        var skipTour = page.Locator(".tour-skip");
        if (await skipTour.IsVisibleAsync()) await skipTour.ClickAsync();   // desktop/tablet first-run tour (hidden on phones)

        await AssertNoOverflow(page, name);
        if (width < 900) await Phone(page, name);
        else await Tablet(page, name);
    }

    private static async Task OpenSampleIfAsked(IPage page)
    {
        var sample = page.GetByRole(AriaRole.Button, new() { Name = "From sample" });
        try { await sample.WaitForAsync(new() { Timeout = 6000 }); }
        catch (TimeoutException) { return; }
        await sample.ClickAsync();
    }

    private static async Task Phone(IPage page, string name)
    {
        Assert.True(await page.IsVisibleAsync(".m-topbar"));
        Assert.True(await page.IsVisibleAsync(".m-bottomnav"));
        Assert.False(await page.IsVisibleAsync(".titlebar"));
        Assert.False(await page.IsVisibleAsync(".ribbon"));
        Assert.False(await page.IsVisibleAsync(".workbench > section.card:first-child"));
        await Shot(page, name + "-diagram");
        await AssertTargets(page, ".m-topbar button, .m-bottomnav button, .m-diagram-bar button");

        // Safe areas: the bars reserve env(safe-area-inset-*) (0 in a desktop browser, so just check they are pinned to the edges).
        var nav = await page.Locator(".m-bottomnav").BoundingBoxAsync();
        Assert.Equal(page.ViewportSize!.Height, nav!.Y + nav.Height, 1);

        // Tap a shape → bottom sheet; long-press → action menu.
        await TapSomeShape(page);
        if (await page.IsVisibleAsync(".m-sheet"))
        {
            await Shot(page, name + "-sheet");
            await AssertNoOverflow(page, name + " sheet");
            await AssertTargets(page, ".m-sheet .m-action, .m-sheet .m-icon-btn");
            await page.Locator(".m-sheet [aria-label=Close]").ClickAsync();
        }

        // Pinch out with two fingers (CDP touch events) zooms the canvas.
        var before = await Zoom(page);
        await Pinch(page);
        await page.WaitForTimeoutAsync(500);
        Assert.True(await Zoom(page) > before * 1.2, $"pinch should zoom in (was {before})");

        foreach (var view in new[] { "Model", "Requirements", "Reviews", "Comments" })
        {
            await page.Locator($".m-tab[data-view={view}]").ClickAsync();
            await page.WaitForSelectorAsync(".m-view");
            Assert.True(await page.IsVisibleAsync(".m-view"), view);
            await page.WaitForTimeoutAsync(250);
            await Shot(page, $"{name}-{view.ToLowerInvariant()}");
            await AssertNoOverflow(page, $"{name} {view}");
        }

        await page.Locator(".m-tab[data-view=Model]").ClickAsync();
        await page.WaitForSelectorAsync(".m-list .m-row");
        await AssertTargets(page, ".m-view .m-row-main, .m-view .m-icon-btn");
        await page.Locator(".m-tab[data-view=Requirements]").ClickAsync();
        await page.WaitForSelectorAsync(".m-card");
        await AssertTargets(page, ".m-view .m-chip, .m-view .m-card-main");
    }

    private static async Task Tablet(IPage page, string name)
    {
        Assert.True(await page.IsVisibleAsync(".titlebar"));
        Assert.True(await page.IsVisibleAsync(".ribbon .rtabs"));
        Assert.False(await page.IsVisibleAsync(".m-bottomnav"));
        Assert.False(await page.IsVisibleAsync(".workbench > section.card:first-child"));   // drawers start closed
        // Tabbed ribbon: every tab and the showing group fit without scrolling sideways.
        Assert.True(await page.EvaluateAsync<bool>("() => { const r = document.querySelector('.ribbon'); return r.scrollWidth <= r.clientWidth + 1 && [...r.querySelectorAll('.rtab, .rgroup.active .rbtn')].every(e => e.getBoundingClientRect().right <= innerWidth + 1); }"));
        await Shot(page, name);

        await page.Locator(".t-drawer-btn").First.ClickAsync();
        await page.WaitForTimeoutAsync(400);
        var tree = page.Locator(".workbench > section.card:first-child");
        Assert.True(await tree.IsVisibleAsync());
        var box = await tree.BoundingBoxAsync();
        Assert.True(box!.X >= 0 && box.X + box.Width <= page.ViewportSize!.Width);
        await Shot(page, name + "-left-drawer");
        await page.Locator(".t-scrim").ClickAsync(new() { Position = new() { X = page.ViewportSize.Width - 60, Y = 300 } });
        await page.WaitForTimeoutAsync(400);
        Assert.False(await tree.IsVisibleAsync());

        await page.Locator(".t-drawer-btn").Nth(1).ClickAsync();
        await page.WaitForTimeoutAsync(400);
        Assert.True(await page.Locator(".workbench > aside.stack").IsVisibleAsync());
        await Shot(page, name + "-right-drawer");
        await AssertNoOverflow(page, name + " drawers");
    }

    private static async Task AssertNoOverflow(IPage page, string where)
    {
        var json = await page.EvaluateAsync<JsonElement>(OverflowScript);
        var report = json.Deserialize<OverflowReport>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(report.Scroll <= 0, $"{where}: page scrolls horizontally by {report.Scroll}px");
        Assert.True(report.Bad.Length == 0, $"{where}: elements past the right edge: {string.Join("; ", report.Bad)}");
    }

    private static async Task AssertTargets(IPage page, string selector)
    {
        var json = await page.EvaluateAsync<JsonElement>(SmallTargetsScript, selector);
        var small = json.Deserialize<Target[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(small.Length == 0, "touch targets under 44px: " + string.Join(", ", small.Select(t => $"{t.Name} {t.W:0}×{t.H:0}")));
    }

    private static async Task TapSomeShape(IPage page)
    {
        var canvas = (await page.Locator(".diagram-host .canvas").BoundingBoxAsync())!;
        var rects = page.Locator(".diagram-host svg.main rect");
        var n = Math.Min(await rects.CountAsync(), 30);
        for (var i = 0; i < n && !await page.IsVisibleAsync(".m-sheet"); i++)
        {
            var b = await rects.Nth(i).BoundingBoxAsync();
            if (b is null || b.Width < 30 || b.Height < 20) continue;
            double x = b.X + b.Width / 2, y = b.Y + Math.Min(14, b.Height / 2);
            if (x < canvas.X + 10 || y < canvas.Y + 70 || x > canvas.X + canvas.Width - 10 || y > canvas.Y + canvas.Height - 10) continue;
            await page.Touchscreen.TapAsync((float)x, (float)y);
            await page.WaitForTimeoutAsync(400);
        }
    }

    private static Task<double> Zoom(IPage page) => page.EvaluateAsync<double>(
        "() => { const t = document.querySelector('.diagram-host svg.main > g[transform]')?.getAttribute('transform') || ''; const m = /scale\\(([-\\d.e]+)\\)/.exec(t); return m ? parseFloat(m[1]) : 1; }");

    private static async Task Pinch(IPage page)
    {
        var canvas = (await page.Locator(".diagram-host .canvas").BoundingBoxAsync())!;
        double cx = canvas.X + canvas.Width / 2, cy = canvas.Y + canvas.Height / 2;
        var cdp = await page.Context.NewCDPSessionAsync(page);
        object Points(double d) => new[] { new { x = cx - d, y = cy, id = 1 }, new { x = cx + d, y = cy, id = 2 } };
        await cdp.SendAsync("Input.dispatchTouchEvent", new() { ["type"] = "touchStart", ["touchPoints"] = Points(30) });
        for (var d = 40; d <= 110; d += 10)
            await cdp.SendAsync("Input.dispatchTouchEvent", new() { ["type"] = "touchMove", ["touchPoints"] = Points(d) });
        await cdp.SendAsync("Input.dispatchTouchEvent", new() { ["type"] = "touchEnd", ["touchPoints"] = Array.Empty<object>() });
    }

    private static async Task Shot(IPage page, string name)
    {
        if (string.IsNullOrEmpty(Shots)) return;
        Directory.CreateDirectory(Shots);
        await page.ScreenshotAsync(new() { Path = Path.Combine(Shots, name + ".png") });
    }
}
