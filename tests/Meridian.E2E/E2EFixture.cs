using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Playwright;

namespace Meridian.E2E;

/// <summary>A registered test account. The password is generated at runtime and never leaves the test process.</summary>
public sealed record TestUser(string Username, string DisplayName, string Password)
{
    internal string? StorageState { get; set; }
}

/// <summary>
/// One Meridian server (server mode, SQLite in a temp folder) plus one headless Chromium, shared by every test in the
/// "server" collection. Three accounts are registered through the real sign-up form: the first one becomes the server admin.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public MeridianApp App { get; private set; } = default!;
    public IPlaywright Playwright { get; private set; } = default!;
    public IBrowser Browser { get; private set; } = default!;
    public TestUser Admin { get; } = NewUser("admin", "Ada Admin");
    public TestUser Alice { get; } = NewUser("alice", "Alice Smith");
    public TestUser Bob { get; } = NewUser("bob", "Bob Jones");

    private static TestUser NewUser(string name, string display) =>
        new($"{name}{RandomNumberGenerator.GetHexString(4, lowercase: true)}", display, "Pw-" + RandomNumberGenerator.GetHexString(20, lowercase: true));

    public async Task InitializeAsync()
    {
        if (BrowserSupport.MissingReason != null && !BrowserSupport.Required) return;   // every test skips anyway
        App = await MeridianApp.StartAsync(localMode: false, services: CodeSyncE2E.Register);   // scripted code-sync mapper (CodeSyncTests)
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new() { Headless = Environment.GetEnvironmentVariable("HEADED") != "1" });
        foreach (var u in new[] { Admin, Alice, Bob }) await RegisterAsync(u);
    }

    /// <summary>Signs up through /login?mode=register and keeps the signed-in cookies for later contexts.</summary>
    private async Task RegisterAsync(TestUser user)
    {
        await using var s = await BrowserSession.CreateAsync(this, null);
        await s.Page.GotoAsync(App.BaseUrl + "/login?mode=register");
        await s.Page.GetByLabel("Username").FillAsync(user.Username);
        await s.Page.GetByLabel("Display name").FillAsync(user.DisplayName);
        await s.Page.GetByLabel("Password").FillAsync(user.Password);
        await s.Page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await s.Page.WaitForURLAsync(App.BaseUrl + "/");
        await s.WaitForWorkbenchAsync();
        user.StorageState = await s.Context.StorageStateAsync();
        s.AssertNoErrors();
    }

    public async Task DisposeAsync()
    {
        if (Browser != null) await Browser.CloseAsync();
        Playwright?.Dispose();
        if (App != null) await App.DisposeAsync();
    }
}

[CollectionDefinition("server")]
public sealed class ServerCollection : ICollectionFixture<ServerFixture> { }

/// <summary>A browser context (own cookies + storage) with one page, collecting console errors and uncaught exceptions.</summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> _errors = new();
    public IBrowserContext Context { get; }
    public IPage Page { get; }
    public string BaseUrl { get; }
    public IReadOnlyCollection<string> Errors => _errors;

    /// <summary>Console errors that are expected for a given test (e.g. a deliberate 403).</summary>
    public List<Func<string, bool>> IgnoreErrors { get; } = [];

    private BrowserSession(IBrowserContext context, IPage page, string baseUrl)
    {
        Context = context; Page = page; BaseUrl = baseUrl;
    }

    public static Task<BrowserSession> CreateAsync(ServerFixture f, TestUser? user) =>
        CreateAsync(f.Browser, f.App.BaseUrl, user?.StorageState);

    public static async Task<BrowserSession> CreateAsync(IBrowser browser, string baseUrl, string? storageState)
    {
        var context = await browser.NewContextAsync(new()
        {
            BaseURL = baseUrl,
            StorageState = storageState,
            ViewportSize = new() { Width = 1600, Height = 1000 },
            AcceptDownloads = true,
            Permissions = ["clipboard-read", "clipboard-write"],
        });
        context.SetDefaultTimeout(15_000);
        // No first-run tour or What's new card in tests (TourTests opens them explicitly), and no internet: Google Fonts get an empty stylesheet.
        await context.AddInitScriptAsync("try { localStorage.setItem('meridian.tour.v1', '1'); localStorage.setItem('meridian.whatsnew', '1'); } catch (e) {}");
        await context.RouteAsync(url => !url.StartsWith(baseUrl, StringComparison.Ordinal) && (url.StartsWith("http://") || url.StartsWith("https://")),
            route => route.FulfillAsync(new() { Status = 200, ContentType = route.Request.ResourceType == "stylesheet" ? "text/css" : "text/plain", Body = "" }));
        var page = await context.NewPageAsync();
        var session = new BrowserSession(context, page, baseUrl);
        context.Page += (_, p) => session.Watch(p);
        session.Watch(page);
        return session;
    }

    private void Watch(IPage page)
    {
        page.Console += (_, msg) =>
        {
            if (msg.Type == "error") _errors.Enqueue($"console.error @ {page.Url}: {msg.Text}");
        };
        page.PageError += (_, error) => _errors.Enqueue($"uncaught @ {page.Url}: {error}");
    }

    /// <summary>Waits until the interactive workbench has opened a project (the circuit is live and the title bar shows it).</summary>
    public async Task WaitForWorkbenchAsync(string? projectName = null)
    {
        var title = Page.Locator("header.titlebar .project span").First;
        if (projectName != null) await Assertions.Expect(title).ToHaveTextAsync(projectName, new() { Timeout = 20_000 });
        else await Assertions.Expect(title).Not.ToHaveTextAsync("No project", new() { Timeout = 20_000 });
    }

    public void AssertNoErrors()
    {
        var errors = _errors.Where(e => !IgnoreErrors.Any(i => i(e))).ToList();
        if (errors.Count > 0) throw new Xunit.Sdk.XunitException("Browser reported errors:\n" + string.Join("\n", errors));
    }

    public async ValueTask DisposeAsync() => await Context.CloseAsync();
}

/// <summary>Base class: opens sessions on the shared server and fails the test when any page logged an error.</summary>
[Collection("server")]
public abstract class E2ETest(ServerFixture fixture) : IAsyncLifetime
{
    protected ServerFixture F { get; } = fixture;
    private readonly List<BrowserSession> _sessions = [];
    private string _test = "";

    /// <summary>Where the last screen of every page is saved after each test (uploaded by CI for debugging).</summary>
    public static string ScreenshotDir => Environment.GetEnvironmentVariable("MERIDIAN_E2E_SCREENSHOTS") is { Length: > 0 } d ? d
        : Path.Combine(AppContext.BaseDirectory, "e2e-screenshots");

    /// <summary>Opens a signed-in browser for <paramref name="user"/> at <paramref name="path"/> and waits for the workbench.</summary>
    protected async Task<BrowserSession> OpenAsync(TestUser? user, string path = "/", bool waitForWorkbench = true,
        [System.Runtime.CompilerServices.CallerMemberName] string test = "")
    {
        _test = test;
        var s = await BrowserSession.CreateAsync(F, user);
        _sessions.Add(s);
        await s.Page.GotoAsync(path);
        if (waitForWorkbench) await s.WaitForWorkbenchAsync();
        return s;
    }

    private int _serverErrorsBefore;

    public Task InitializeAsync()
    {
        if (F.App != null) _serverErrorsBefore = F.App.ServerErrors.Count;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // Give late errors (e.g. a circuit failing after the last click) a moment to arrive.
        if (_sessions.Count > 0) await Task.Delay(300);
        var failures = new List<string>();
        for (var i = 0; i < _sessions.Count; i++)
        {
            var s = _sessions[i];
            try { Directory.CreateDirectory(ScreenshotDir); await s.Page.ScreenshotAsync(new() { Path = Path.Combine(ScreenshotDir, $"{GetType().Name}.{_test}.{i}.png"), Timeout = 5000 }); }
            catch (PlaywrightException) { }
            try { s.AssertNoErrors(); } catch (Exception ex) { failures.Add(ex.Message); }
            await s.DisposeAsync();
        }
        if (F.App != null && F.App.ServerErrors.Entries.Skip(_serverErrorsBefore).ToList() is { Count: > 0 } server)
            failures.Add("Server logged errors:\n" + string.Join("\n", server));
        if (failures.Count > 0) throw new Xunit.Sdk.XunitException(string.Join("\n", failures));
    }
}
