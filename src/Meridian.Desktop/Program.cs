using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Web;
using Meridian.Web.Hosting;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Photino.NET;

namespace Meridian.Desktop;

/// <summary>
/// Meridian for Windows, macOS and Linux: a native window (Photino: WebView2 / WKWebView / WebKitGTK) around the
/// Meridian web app. It either runs a private server in-process ("this computer": SQLite, single user, offline)
/// or connects to a team server (PostgreSQL, accounts, live collaboration) by URL.
/// </summary>
internal static class Program
{
    private static PhotinoWindow _window = default!;
    private static DesktopSettings _settings = default!;
    private static WebApplication? _local;
    private static string? _localUrl;
    private static string? _localToken;
    private static Task<bool>? _localStart;
    private static readonly object _gate = new();
    private static UpdateInfo? _update;
    private static int _updating;
    /// <summary>A server opened with --server (not necessarily in the settings).</summary>
    private static string? _serverArg;
    private static int _crashOffered;

    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack first: its installer and updater start Meridian with hook arguments while installing, updating or
        // uninstalling, and Run() handles those and exits. A normal start returns at once.
        Velopack.VelopackApp.Build().Run();
        // Crash files go to the data folder; nothing is sent anywhere (the next start offers a prefilled GitHub issue).
        CrashReports.Configure(CrashFolder, "desktop");
        CrashReports.InstallProcessHandlers();
        _settings = DesktopSettings.Load();
        // A portable update leaves the staging folder and the previous version next to the install: remove them now.
        _ = Task.Run(() =>
        {
            if (InstallLocation.Detect(AppContext.BaseDirectory, UpdatePlatform.CurrentOs) is { } install) PortableInstaller.CleanupLeftovers(install, Log);
        });
        _window = new PhotinoWindow()
            .SetTitle("Meridian")
            .SetUseOsDefaultSize(false)
            .SetSize(1480, 940)
            .SetMinSize(1000, 640)
            .Center()
            .SetResizable(true)
            .SetContextMenuEnabled(true)
            .SetDevToolsEnabled(Environment.GetEnvironmentVariable("MERIDIAN_DEVTOOLS") == "1")
            .RegisterWebMessageReceivedHandler((_, message) => OnMessage(message))
            .RegisterWindowClosingHandler((_, _) => { StopLocal(); return false; });

        var icon = Path.Combine(AppContext.BaseDirectory, "meridian.png");
        if (File.Exists(icon)) _window.SetIconFile(icon);

        // Documents opened while running (and, on macOS, at launch) arrive as Apple Events rather than arguments.
        MacOpenFiles.Install(paths => _ = OpenFilesAsync(paths));
        _window.RegisterWindowCreatedHandler((_, _) =>
        {
            MacOpenFiles.Install(paths => _ = OpenFilesAsync(paths));
            _ = CheckForUpdatesAsync();
        });

        // A document path on the command line (Windows/Linux file associations, "Open with") opens it in the local workspace.
        var files = DocumentArgs(args);
        // --local / --server <url> skip the start screen (handy for shortcuts and scripts).
        var serverArg = Array.IndexOf(args, "--server") is >= 0 and var si && si + 1 < args.Length ? args[si + 1] : null;
        if (files.Length > 0) _window.LoadRawString(StartPage.Html(_settings, busy: true, update: _update)).RegisterWindowCreatedHandler((_, _) => _ = OpenFilesAsync(files));
        else if (args.Contains("--local")) _window.LoadRawString(StartPage.Html(_settings, busy: true)).RegisterWindowCreatedHandler((_, _) => OpenLocal());
        else if (serverArg != null) _window.Load(_serverArg = serverArg);
        else if (_settings.AutoOpen && _settings.LastMode == "local") _window.LoadRawString(StartPage.Html(_settings, busy: true)).RegisterWindowCreatedHandler((_, _) => OpenLocal());
        else if (_settings.AutoOpen && _settings.LastMode == "server" && _settings.Servers.FirstOrDefault() is { } last) _window.Load(last);
        else _window.LoadRawString(StartPage.Html(_settings, update: CachedUpdate(), crash: OfferCrash()));

        _window.WaitForClose();
        StopLocal();
    }

    /// <summary>Command-line arguments that name model files (paths or file:// URIs); flags and their values are skipped.</summary>
    internal static string[] DocumentArgs(string[] args)
    {
        var list = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--server") { i++; continue; }
            if (a.StartsWith('-')) continue; // --local, macOS -psn_…
            if (Uri.TryCreate(a, UriKind.Absolute, out var u) && u.IsFile) a = u.LocalPath;
            if (LocalFileImport.IsSupported(a)) list.Add(Path.GetFullPath(a));
        }
        return list.ToArray();
    }

    private static void ShowStart(string? error = null) =>
        _window.Invoke(() => _window.LoadRawString(StartPage.Html(_settings, error: error, update: _update ?? CachedUpdate(), crash: OfferCrash())));

    private static void OnMessage(string raw)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(raw) as JsonObject; } catch (JsonException) { return; }
        switch ((string?)msg?["command"])
        {
            case "local":
                _settings.AutoOpen = (bool?)msg!["remember"] ?? false;
                OpenLocal();
                break;
            case "server":
                _settings.AutoOpen = (bool?)msg!["remember"] ?? false;
                _ = OpenServerAsync((string?)msg["url"] ?? "");
                break;
            case "forget":
                _settings.Servers.Remove((string?)msg!["url"] ?? "");
                _settings.Save();
                ShowStart();
                break;
            case "switch":
                _settings.AutoOpen = false;
                _settings.Save();
                ShowStart();
                break;
            case "save":
                _ = SaveFileAsync((string?)msg!["name"] ?? "download", (string?)msg["data"] ?? "");
                break;
            case "save-url":
                _ = SaveUrlAsync((string?)msg!["name"] ?? "download", (string?)msg["url"] ?? "", (bool?)msg["open"] ?? false);
                break;
            case "openFile":
                _ = PickAndOpenAsync();
                break;
            case "openRecent":
                // Only paths already in the recent list: a page cannot make the host read arbitrary files.
                if (_settings.RecentFiles.FirstOrDefault(r => r.Path == (string?)msg!["path"]) is { } recent) _ = OpenFilesAsync([recent.Path]);
                break;
            case "forgetRecent":
                _settings.RecentFiles.RemoveAll(r => r.Path == (string?)msg!["path"]);
                _settings.Save();
                ShowStart();
                break;
            case "updates":
                _settings.CheckForUpdates = (bool?)msg!["enabled"] ?? true;
                _settings.Save();
                if (_settings.CheckForUpdates) _ = CheckForUpdatesAsync();
                break;
            // Settings ▸ Desktop in the web app.
            case "desktopSettings":
                Post(DesktopState());
                break;
            case "checkUpdates":
                _ = CheckForUpdatesAsync(force: true);
                break;
            case "updateSource":
                SetUpdateSource(msg!);
                break;
            case "updateToken":
                if ((string?)msg!["provider"] == "GitLab") SetGitLabToken((string?)msg["token"]);
                else SetUpdateToken((string?)msg["token"]);
                break;
            case "autoOpen":
                _settings.AutoOpen = (bool?)msg!["enabled"] ?? false;
                _settings.Save();
                break;
            case "clearRecent":
                _settings.RecentFiles.Clear();
                _settings.Save();
                break;
            case "hello":
                // A page finished loading: deliver waiting messages, and tell it about a pending update so the web app can show its notice.
                PageReady();
                if ((_update ?? CachedUpdate()) is { } u) Post(new { type = "update", version = u.Version, url = u.PageUrl });
                if (OfferCrash() is { } crash) Post(new { type = "crash", summary = crash.Summary, time = crash.Time.ToString("O") });
                break;
            // Problem reports (Help ▸ Report a problem, the crash notice): open a prefilled GitHub issue in the browser.
            case "reportCrash":
            case "reportProblem":
                ReportProblem();
                break;
            case "dismissCrash":
                CrashReports.MarkHandled(CrashReports.Pending(CrashFolder).Select(p => p.Path));
                Post(DesktopState());
                break;
            case "openCrashFolder":
                OpenCrashFolder();
                break;
            case "crashPrompt":
                _settings.OfferCrashReports = (bool?)msg!["enabled"] ?? true;
                _settings.Save();
                Post(DesktopState());
                break;
            case "update":
                _ = InstallUpdateAsync();
                break;
            case "open-url":
                OpenUrl((string?)msg!["url"]);
                break;
        }
    }

    // Messages for the page wait here until a page has said "hello": before that the web view may not exist yet, and on
    // Windows sending to WebView2 too early throws (an update check at start-up crashed the app this way).
    private static readonly List<string> _pending = new();
    private static bool _pageReady;

    // ------------------------------------------------------------------ problem reports

    internal static string CrashFolder => Path.Combine(DesktopSettings.Folder, "crashes");

    /// <summary>The newest unreported crash, once per run, unless the prompt is turned off in Settings ▸ Desktop.</summary>
    private static CrashReport? OfferCrash()
    {
        if (!_settings.OfferCrashReports || Volatile.Read(ref _crashOffered) == 1) return null;
        if (CrashReports.Pending(CrashFolder) is not [var (_, latest), ..]) return null;
        return Interlocked.Exchange(ref _crashOffered, 1) == 0 ? latest : null;
    }

    /// <summary>
    /// Opens a new GitHub issue in the browser, prefilled with the newest unreported crash (or just the version and OS).
    /// The user reviews and submits it there; the crash is marked handled so it is not offered again.
    /// </summary>
    private static void ReportProblem()
    {
        var pending = CrashReports.Pending(CrashFolder);
        var url = pending is [var (_, latest), ..]
            ? IssueLinks.ForCrash(latest)
            : IssueLinks.ForProblem(Updater.CurrentVersion(), $"{CrashReports.ThisOs()}, desktop app");
        OpenUrl(url);
        CrashReports.MarkHandled(pending.Select(p => p.Path));
        Post(DesktopState());
        Log(pending.Count > 0 ? $"Opened a problem report for crash {pending[0].Report.Id} in the browser." : "Opened a problem report in the browser.");
    }

    /// <summary>Shows the crash files in Finder / Explorer / the file manager (the path never goes to the page).</summary>
    private static void OpenCrashFolder()
    {
        try
        {
            Directory.CreateDirectory(CrashFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(CrashFolder) { UseShellExecute = true });
        }
        catch (Exception ex) { Log($"Could not open the crash folder: {ex.Message}"); }
    }

    private static void Post(object message)
    {
        var json = JsonSerializer.Serialize(message);
        lock (_pending)
        {
            if (!_pageReady) { if (_pending.Count < 50) _pending.Add(json); return; }
        }
        Send(json);
    }

    private static void Send(string json)
    {
        try { _window.Invoke(() => { try { _window.SendWebMessage(json); } catch (Exception ex) { Log($"Message to the page dropped: {ex.Message}"); } }); }
        catch (Exception ex) { Log($"Message to the page dropped: {ex.Message}"); }   // window not ready or closing
    }

    /// <summary>A page finished loading: deliver what was waiting.</summary>
    private static void PageReady()
    {
        List<string> waiting;
        lock (_pending)
        {
            _pageReady = true;
            waiting = [.. _pending];
            _pending.Clear();
        }
        foreach (var json in waiting) Send(json);
    }

    /// <summary>Opens a diagram hyperlink in the system browser. Only http(s) addresses are accepted.</summary>
    private static void OpenUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Log($"open-url failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ local mode

    private static void OpenLocal()
    {
        _settings.LastMode = "local";
        _settings.Save();
        _ = Task.Run(async () =>
        {
            if (await EnsureLocalAsync()) _window.Invoke(() => _window.Load($"{_localUrl}?launch={_localToken}"));
        });
    }

    /// <summary>Starts the in-process server once; later callers share the same start.</summary>
    private static Task<bool> EnsureLocalAsync()
    {
        lock (_gate) return _localStart ??= StartLocalAsync();
    }

    private static async Task<bool> StartLocalAsync()
    {
        try
        {
            var port = PickPort(47817);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var dataPath = DesktopSettings.Folder;
            _local = await MeridianServer.CreateAsync([], b =>
            {
                b.Configuration["Meridian:Mode"] = "local";
                b.Configuration["Meridian:DataPath"] = dataPath;
                b.Configuration["Meridian:LaunchToken"] = token;
                b.Configuration["AllowedHosts"] = "127.0.0.1;localhost";
                b.WebHost.UseUrls($"http://127.0.0.1:{port}");
            }, new WebApplicationOptions
            {
                // The web app's static assets and manifest are published next to this executable.
                ApplicationName = "Meridian.Web",
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                EnvironmentName = "Production",
            });
            await _local.StartAsync();
            _localToken = token;
            _localUrl = $"http://127.0.0.1:{port}/";
            return true;
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            _local = null;
            lock (_gate) _localStart = null;
            ShowStart($"Could not start the local workspace: {ex.Message}");
            return false;
        }
    }

    private static void StopLocal()
    {
        var app = Interlocked.Exchange(ref _local, null);
        if (app == null) return;
        try { app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).GetAwaiter().GetResult(); } catch { /* shutting down */ }
        _localUrl = null;
        lock (_gate) _localStart = null;
    }

    /// <summary>A stable port keeps per-origin browser storage (theme, favourites) across launches; falls back to any free port.</summary>
    private static int PickPort(int preferred)
    {
        foreach (var p in new[] { preferred, 0 })
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, p);
                l.Start();
                var port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("No free local port");
    }

    // ------------------------------------------------------------------ opening files

    private static async Task PickAndOpenAsync()
    {
        var tcs = new TaskCompletionSource<string[]?>();
        _window.Invoke(async () =>
        {
            try
            {
                tcs.TrySetResult(await _window.ShowOpenFileAsync("Open a model", null, false,
                    [("Models (.meridian, .sysml, .xmi, .qea, .mdzip, .mdxml, Mermaid .mmd / .md, draw.io, Gliffy, SQL DDL / .dacpac)", LocalFileImport.Extensions.Select(e => "*" + e).ToArray())]));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        try
        {
            if (await tcs.Task is { Length: > 0 } picked) await OpenFilesAsync(picked);
        }
        catch (Exception ex) { Log("Open dialog failed: " + ex.Message); }
    }

    /// <summary>
    /// Opens model files in the local workspace: each new or changed file is imported as a project by the in-process server
    /// (token-guarded endpoint), an unchanged recent file reopens its project, and the window shows the last one.
    /// </summary>
    private static async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        var files = paths.Select(p => Uri.TryCreate(p, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : p)
            .Where(LocalFileImport.IsSupported).Select(Path.GetFullPath).Distinct().ToList();
        if (files.Count == 0) return;
        _settings.LastMode = "local";
        _window.Invoke(() => _window.LoadRawString(StartPage.Html(_settings, busy: true)));
        if (!await EnsureLocalAsync()) return;

        Guid? open = null;
        var errors = new List<string>();
        foreach (var path in files)
        {
            var info = new FileInfo(path);
            if (!info.Exists) { errors.Add($"{info.Name} was not found."); continue; }
            var known = _settings.RecentFiles.FirstOrDefault(r => r.Path == path);
            if (known?.ProjectId is { } existing && known.FileTimeUtc == info.LastWriteTimeUtc)
            {
                _settings.Remember(path, existing, info.LastWriteTimeUtc);
                open = existing;
                continue;
            }
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_localUrl!), LocalFileImport.Route.TrimStart('/')))
                {
                    Content = JsonContent.Create(new LocalFileImport.Request(path)),
                };
                req.Headers.Add(LocalFileImport.TokenHeader, _localToken);
                using var res = await http.SendAsync(req);
                if (!res.IsSuccessStatusCode)
                {
                    var body = await res.Content.ReadAsStringAsync();
                    string? message = null;
                    try { message = (string?)JsonNode.Parse(body)?["error"]; } catch (JsonException) { }
                    errors.Add(message ?? $"Could not open {info.Name} ({(int)res.StatusCode}).");
                    continue;
                }
                var result = await res.Content.ReadFromJsonAsync<LocalFileImport.Response>(JsonSerializerOptions.Web);
                _settings.Remember(path, result?.ProjectId, info.LastWriteTimeUtc);
                open = result?.ProjectId;
                Log($"Opened {path} as project {open}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                errors.Add($"Could not open {info.Name}: {ex.Message}");
            }
        }
        _settings.Save();
        foreach (var e in errors) Log(e);
        if (open is { } id) _window.Invoke(() => _window.Load($"{_localUrl}?project={id}&launch={_localToken}"));
        else ShowStart(string.Join(" ", errors));
    }

    // ------------------------------------------------------------------ updates

    private static UpdateTokenProtector? _tokens;
    private static int _checking;

    private static bool ChecksDisabled => Environment.GetEnvironmentVariable("MERIDIAN_NO_UPDATE_CHECK") == "1";

    private static UpdateTokenProtector? Tokens()
    {
        try { return _tokens ??= new UpdateTokenProtector(UpdateTokenProtector.DefaultKeysFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Log("Token encryption unavailable: " + ex.Message);
            return null;
        }
    }

    private static UpdateInfo? CachedUpdate() =>
        _settings.AvailableUpdate is { } u && Updater.IsNewer(u.Version, Updater.CurrentVersion()) ? u : null;

    /// <summary>Settings ▸ Desktop: version, update source and the last check (never the token itself).</summary>
    private static object DesktopState() => new
    {
        type = "desktop-settings",
        autoOpen = _settings.AutoOpen,
        checkForUpdates = _settings.CheckForUpdates,
        recentFiles = _settings.RecentFiles.Count,
        lastMode = _settings.LastMode,
        version = Updater.CurrentVersion(),
        lastCheck = _settings.LastUpdateCheck?.ToString("O"),
        lastMessage = _settings.LastUpdateMessage,
        lastStatus = _settings.LastUpdateStatus?.ToString(),
        available = CachedUpdate()?.Version,
        updateSource = _settings.UpdateSource.ToString(),
        manifestUrl = _settings.UpdateManifestUrl,
        hasToken = !string.IsNullOrEmpty(_settings.ProtectedGitHubToken),
        gitLabUrl = _settings.UpdateGitLabUrl,
        gitLabProject = _settings.UpdateGitLabProject,
        hasGitLabToken = !string.IsNullOrEmpty(_settings.ProtectedGitLabToken),
        checksDisabled = ChecksDisabled,
        crashPrompt = _settings.OfferCrashReports,
        pendingCrashes = CrashReports.Pending(CrashFolder).Count,
    };

    private static void PostStatus(string status, string message) =>
        Post(new { type = "update-status", status, message, lastCheck = _settings.LastUpdateCheck?.ToString("O"), available = (_update ?? CachedUpdate())?.Version });

    /// <summary>Checks the configured source: once a day when automatic checks are on, or now (<paramref name="force"/>, the "Check for updates" button).</summary>
    private static async Task CheckForUpdatesAsync(bool force = false)
    {
        if (ChecksDisabled)
        {
            if (force) PostStatus("disabled", "Update checks are turned off on this computer (MERIDIAN_NO_UPDATE_CHECK=1).");
            return;
        }
        if (!force)
        {
            if (!_settings.CheckForUpdates) return;
            if (CachedUpdate() is { } cached) { _update = cached; Post(new { type = "update", version = cached.Version, url = cached.PageUrl }); }
            if (!Updater.IsCheckDue(_settings.LastUpdateCheck, DateTimeOffset.Now)) return;
        }
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            PostStatus("checking", "Checking for updates…");
            using var http = Updater.CreateHttpClient(TimeSpan.FromSeconds(20));
            var result = await new Updater(http, Updater.CurrentVersion(), Log, source: _settings.ResolveUpdateSource(Tokens())).CheckAsync();
            Log($"Update check: {result.Status}: {result.Message}");
            _settings.LastUpdateCheck = DateTimeOffset.Now;
            _settings.LastUpdateStatus = result.Status;
            _settings.LastUpdateMessage = result.Message;
            // Keep a found update when the network is down; replace it with whatever a successful check says.
            if (result.Status is not (UpdateStatus.Unreachable or UpdateStatus.Invalid)) _settings.AvailableUpdate = result.Update;
            _settings.Save();
            _update = result.Update ?? CachedUpdate();
            PostStatus(result.Status.ToString(), result.Message);
            if (result.Update is { } found)
            {
                Log($"Update available: {found.Tag} ({found.Asset.Name}, {found.Kind}).");
                Post(new { type = "update", version = found.Version, url = found.PageUrl });
            }
        }
        catch (Exception ex)
        {
            // An update check must never take the app down (a 404 from a private repository used to on Windows).
            Log($"Update check failed: {ex}");
            try { PostStatus("Unreachable", "Couldn't check for updates: " + ex.Message); } catch { /* reporting is best effort */ }
        }
        finally { Interlocked.Exchange(ref _checking, 0); }
    }

    /// <summary>Settings ▸ Desktop ▸ Update source: GitHub, GitHub with a token, or a manifest address.</summary>
    private static void SetUpdateSource(JsonObject msg)
    {
        var source = (string?)msg["source"];
        if (!Enum.TryParse<UpdateSourceKind>(source, ignoreCase: true, out var kind)) return;
        if (kind == UpdateSourceKind.Manifest)
        {
            var url = (string?)msg["url"];
            if (UpdateSource.ParseManifestUrl(url, out var uri) is { } error) { PostStatus("invalid-source", error); return; }
            _settings.UpdateManifestUrl = uri!.AbsoluteUri;
        }
        if (kind == UpdateSourceKind.GitLab)
        {
            var url = (string?)msg["url"];
            var project = (string?)msg["project"];
            if (UpdateSource.ParseGitLabUrl(url, out var uri) is { } error) { PostStatus("invalid-source", error); return; }
            if (UpdateSource.ValidateGitLabProject(project) is { } projectError) { PostStatus("invalid-source", projectError); return; }
            var host = uri!.AbsoluteUri.TrimEnd('/');
            // A token saved for another GitLab instance must never be sent to this one.
            if (_settings.UpdateGitLabUrl is { } previous && !string.Equals(previous, host, StringComparison.OrdinalIgnoreCase)) _settings.ProtectedGitLabToken = null;
            _settings.UpdateGitLabUrl = host;
            _settings.UpdateGitLabProject = project!.Trim().Trim('/');
        }
        if (_settings.UpdateSource != kind || kind is UpdateSourceKind.Manifest or UpdateSourceKind.GitLab)
        {
            _settings.AvailableUpdate = null;
            _settings.LastUpdateMessage = null;
            _settings.LastUpdateStatus = null;
            _update = null;
        }
        _settings.UpdateSource = kind;
        _settings.Save();
        Post(DesktopState());
        PostStatus("saved", kind switch
        {
            UpdateSourceKind.GitHubToken when string.IsNullOrEmpty(_settings.ProtectedGitHubToken) => "Saved. Paste a GitHub token to check the private repository.",
            UpdateSourceKind.Manifest => $"Saved. Updates come from {_settings.UpdateManifestUrl}.",
            UpdateSourceKind.GitLab => $"Saved. Updates come from the releases of {_settings.UpdateGitLabProject} on {new Uri(_settings.UpdateGitLabUrl!).Host}"
                + (string.IsNullOrEmpty(_settings.ProtectedGitLabToken) ? " (add a token if the project is private)." : "."),
            _ => "Saved.",
        });
    }

    /// <summary>Saves (or clears) the GitLab token, encrypted. It is sent only to the configured GitLab instance.</summary>
    private static void SetGitLabToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            _settings.ProtectedGitLabToken = null;
            _settings.Save();
            Post(DesktopState());
            PostStatus("saved", "GitLab token removed.");
            return;
        }
        if (UpdateTokenProtector.Validate(token, "GitLab") is { } error) { PostStatus("invalid-source", error); return; }
        if (Tokens() is not { } tokens) { PostStatus("invalid-source", "The token could not be stored encrypted on this computer."); return; }
        _settings.ProtectedGitLabToken = tokens.Protect(token.Trim());
        _settings.Save();
        Post(DesktopState());
        if (_settings.UpdateSource == UpdateSourceKind.GitLab)
        {
            PostStatus("saved", "GitLab token saved (encrypted on this computer). Checking…");
            _ = CheckForUpdatesAsync(force: true);
        }
        else PostStatus("saved", "GitLab token saved (encrypted on this computer). Choose GitLab releases and Use this source.");
    }

    /// <summary>Saves (or clears) the GitHub token, encrypted. The token comes straight from the page's input to this process.</summary>
    private static void SetUpdateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            _settings.ProtectedGitHubToken = null;
            _settings.Save();
            Post(DesktopState());
            PostStatus("saved", "Token removed.");
            return;
        }
        if (UpdateTokenProtector.Validate(token) is { } error) { PostStatus("invalid-source", error); return; }
        if (Tokens() is not { } tokens) { PostStatus("invalid-source", "The token could not be stored encrypted on this computer."); return; }
        _settings.ProtectedGitHubToken = tokens.Protect(token.Trim());
        _settings.UpdateSource = UpdateSourceKind.GitHubToken;
        _settings.Save();
        Post(DesktopState());
        PostStatus("saved", "Token saved (encrypted on this computer). Checking…");
        _ = CheckForUpdatesAsync(force: true);
    }

    /// <summary>
    /// Downloads and verifies the package with progress. An installer is opened and the app quits; a portable archive is unpacked
    /// next to the install and a script swaps it in after the app quits, then starts the new version. If the install folder
    /// cannot be written, the release page opens instead.
    /// </summary>
    private static async Task InstallUpdateAsync()
    {
        if ((_update ?? CachedUpdate()) is not { } update || Interlocked.Exchange(ref _updating, 1) == 1) return;
        string? downloaded = null;
        try
        {
            if (await TryVelopackUpdateAsync(update)) return;

            InstallLocation? location = null;
            if (update.Kind == PackageKind.Portable)
            {
                location = InstallLocation.Detect(AppContext.BaseDirectory, UpdatePlatform.CurrentOs);
                var problem = location is null
                    ? "This copy of Meridian was not installed from a release package (a development build?), so it can't replace itself."
                    : PortableInstaller.CheckWritable(location, DesktopSettings.Folder);
                if (problem != null) { OpenReleasePage(update, problem); return; }
            }

            using var http = Updater.CreateHttpClient(TimeSpan.FromMinutes(30));
            var lastPercent = -1;
            var progress = new Progress<double>(p =>
            {
                var percent = (int)(p * 100);
                if (percent == lastPercent) return;
                lastPercent = percent;
                Post(new { type = "update-progress", percent });
            });
            var updater = new Updater(http, Updater.CurrentVersion(), Log, source: _settings.ResolveUpdateSource(Tokens()));
            downloaded = await updater.DownloadAsync(update, progress);
            Log($"Verified {downloaded}.");

            if (location is null)
            {
                Log("Launching the installer.");
                Updater.Launch(downloaded);
                downloaded = null;   // the installer is still running from there
            }
            else
            {
                PostStatus("installing", "Unpacking the update…");
                var newRoot = await PortableInstaller.StageAsync(downloaded, location);
                var script = PortableInstaller.WriteScript(location, newRoot, Environment.ProcessId, Path.Combine(DesktopSettings.Folder, "desktop.log"), DesktopSettings.Folder);
                Log($"Staged {update.Version} in {newRoot}; {Path.GetFileName(script)} swaps it in after Meridian quits.");
                System.Diagnostics.Process.Start(PortableInstaller.ScriptStartInfo(script, UpdatePlatform.CurrentOs));
                PostStatus("restarting", $"Restarting into Meridian {update.Version}…");
            }
            _window.Invoke(() => _window.Close());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            Log("Update failed: " + ex);
            Post(new { type = "update-error", message = ex.Message });
            PostStatus("failed", "Update failed: " + ex.Message);
        }
        finally
        {
            if (downloaded != null) try { Directory.Delete(Path.GetDirectoryName(downloaded)!, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Interlocked.Exchange(ref _updating, 0);
        }
    }

    /// <summary>
    /// A copy installed from a Velopack package updates itself: download (a delta when possible), verify, then quit and let
    /// Velopack swap the files and start the new version. False when this copy isn't such an install or the feed has no
    /// Velopack release for this computer (older releases, a hand-written manifest): the classic updater takes over.
    /// </summary>
    private static async Task<bool> TryVelopackUpdateAsync(UpdateInfo update)
    {
        if (!VelopackUpdates.IsInstalled()) return false;
        if (VelopackUpdates.SourceFor(_settings.ResolveUpdateSource(Tokens())) is not { } source) return false;
        (Velopack.UpdateManager Manager, Velopack.VelopackAsset Release)? ready;
        try
        {
            var lastPercent = -1;
            ready = await VelopackUpdates.DownloadAsync(source, percent =>
            {
                if (percent == lastPercent) return;
                lastPercent = percent;
                Post(new { type = "update-progress", percent });
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"Velopack update from {source.GetType().Name} failed, using the classic updater: {ex.Message}");
            return false;
        }
        if (ready is not { } r)
        {
            Log($"The update feed has no Velopack release newer than this one for {VelopackUpdates.Channel(UpdatePlatform.Current())}; using the classic updater for {update.Version}.");
            return false;
        }
        Log($"Downloaded and verified Meridian {r.Release.Version}; restarting into it.");
        PostStatus("restarting", $"Restarting into Meridian {r.Release.Version}…");
        VelopackUpdates.ApplyAfterExit(r.Manager, r.Release);
        _window.Invoke(() => _window.Close());
        return true;
    }

    /// <summary>The install folder can't be replaced: say why and open the release page (or the manifest's notes) to download by hand.</summary>
    private static void OpenReleasePage(UpdateInfo update, string problem)
    {
        var page = update.PageUrl is { Length: > 0 } p ? p : update.Source switch
        {
            UpdateSourceKind.Manifest => null,
            UpdateSourceKind.GitLab => _settings.UpdateGitLabUrl is { } gl && _settings.UpdateGitLabProject is { } proj && !proj.All(char.IsAsciiDigit)
                ? $"{gl.TrimEnd('/')}/{proj}/-/releases" : null,
            _ => $"https://github.com/{Updater.Repository}/releases",
        };
        var message = page != null
            ? $"{problem} Opening the release page so you can download {update.Version} yourself."
            : $"{problem} Download {update.Version} from your update server and install it by hand.";
        Log(message);
        Post(new { type = "update-error", message });
        PostStatus("manual", message);
        if (page != null) OpenUrl(page);
    }

    // ------------------------------------------------------------------ server mode

    private static async Task OpenServerAsync(string url)
    {
        url = url.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            ShowStart("Enter a server address such as https://meridian.example.com");
            return;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await http.GetAsync(new Uri(uri, "/login"));
            if ((int)response.StatusCode >= 500) throw new HttpRequestException($"the server answered {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ShowStart($"Could not reach {uri.Host}: {ex.Message}");
            return;
        }
        var origin = uri.GetLeftPart(UriPartial.Authority) + "/";
        _settings.LastMode = "server";
        _settings.Servers.Remove(origin);
        _settings.Servers.Insert(0, origin);
        _settings.Save();
        _window.Invoke(() => _window.Load(origin));
    }

    /// <summary>Appends to desktop.log in the data folder (GUI apps have no console).</summary>
    internal static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(DesktopSettings.Folder);
            File.AppendAllText(Path.Combine(DesktopSettings.Folder, "desktop.log"), $"[{DateTime.Now:O}] {text}{Environment.NewLine}");
        }
        catch (IOException) { }
        CrashReports.Record(text);
        Console.Error.WriteLine(text);
    }

    // ------------------------------------------------------------------ files

    private static async Task SaveFileAsync(string name, string base64)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); } catch (FormatException) { return; }
        try
        {
            var target = await PickSaveTargetAsync(name);
            Log($"Save {name} ({bytes.Length} bytes): dialog returned '{target}'.");
            if (target == null) return;
            await File.WriteAllBytesAsync(target, bytes);
        }
        catch (Exception ex) { Log($"Save {name} failed: {ex}"); }
    }

    private static readonly HttpClient _downloads = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>A file the server streams (a generated report): pick the target, then copy the response to it in chunks.</summary>
    /// <param name="open">Open the saved file afterwards (a printed PDF, see <see cref="DesktopDownloads.OpensAfterSave"/>).</param>
    private static async Task SaveUrlAsync(string name, string url, bool open = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !DesktopDownloads.IsAllowed(uri, [_localUrl, _serverArg, .. _settings.Servers]))
        {
            Log($"Save {name}: refused a download from '{url}'.");
            return;
        }
        try
        {
            var target = await PickSaveTargetAsync(name);
            Log($"Save {name} (streamed): dialog returned '{target}'.");
            if (target == null) return;
            var bytes = await DesktopDownloads.SaveAsync(_downloads, uri, target);
            Log($"Saved {name}: {bytes:N0} bytes.");
            if (open && DesktopDownloads.OpensAfterSave(target))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Log($"Save {name} failed: {ex}"); }
    }

    /// <summary>The native save dialog (null when cancelled); the file's extension is added when the user leaves it off.</summary>
    private static async Task<string?> PickSaveTargetAsync(string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.');
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var tcs = new TaskCompletionSource<string?>();
        // Leave the web-message callback first: a modal panel run from inside it can stall the web view.
        await Task.Run(() => _window.Invoke(async () =>
        {
            try
            {
                // Photino's macOS dialog drops the file name (it shows "Untitled"), so macOS uses NSSavePanel directly.
                var path = OperatingSystem.IsMacOS()
                    ? MacSavePanel.Show("Save " + name, downloads, name)
                    : await _window.ShowSaveFileAsync("Save " + name, Path.Combine(Directory.Exists(downloads) ? downloads : "", name),
                        ext.Length > 0 ? [(ext.ToUpperInvariant() + " file", [ext])] : null);
                tcs.TrySetResult(path);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }));
        var target = await tcs.Task;
        if (string.IsNullOrEmpty(target)) return null;
        if (ext.Length > 0 && !Path.HasExtension(target)) target += "." + ext;
        return target;
    }
}
