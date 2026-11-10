using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Web;
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

    [STAThread]
    private static void Main(string[] args)
    {
        _settings = DesktopSettings.Load();
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
        else if (serverArg != null) _window.Load(serverArg);
        else if (_settings.AutoOpen && _settings.LastMode == "local") _window.LoadRawString(StartPage.Html(_settings, busy: true)).RegisterWindowCreatedHandler((_, _) => OpenLocal());
        else if (_settings.AutoOpen && _settings.LastMode == "server" && _settings.Servers.FirstOrDefault() is { } last) _window.Load(last);
        else _window.LoadRawString(StartPage.Html(_settings, update: CachedUpdate()));

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
        _window.Invoke(() => _window.LoadRawString(StartPage.Html(_settings, error: error, update: _update ?? CachedUpdate())));

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
                Post(new { type = "desktop-settings", autoOpen = _settings.AutoOpen, checkForUpdates = _settings.CheckForUpdates, recentFiles = _settings.RecentFiles.Count, lastMode = _settings.LastMode });
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
                // A page finished loading: tell it about a pending update so the web app can show its notice.
                if ((_update ?? CachedUpdate()) is { } u) Post(new { type = "update", version = u.Version, url = u.PageUrl });
                break;
            case "update":
                _ = InstallUpdateAsync();
                break;
            case "open-url":
                OpenUrl((string?)msg!["url"]);
                break;
        }
    }

    private static void Post(object message)
    {
        var json = JsonSerializer.Serialize(message);
        try { _window.Invoke(() => _window.SendWebMessage(json)); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { /* window not ready or closing */ }
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
                    [("Models (.meridian, .sysml, .xmi, .qea, .mdzip, .mdxml)", LocalFileImport.Extensions.Select(e => "*" + e).ToArray())]));
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

    private static UpdateInfo? CachedUpdate() =>
        _settings.AvailableUpdate is { } u && Updater.IsNewer(u.Version, Updater.CurrentVersion()) ? u : null;

    private static async Task CheckForUpdatesAsync()
    {
        if (!_settings.CheckForUpdates || Environment.GetEnvironmentVariable("MERIDIAN_NO_UPDATE_CHECK") == "1") return;
        if (CachedUpdate() is { } cached) { _update = cached; Post(new { type = "update", version = cached.Version, url = cached.PageUrl }); }
        if (!Updater.IsCheckDue(_settings.LastUpdateCheck, DateTimeOffset.Now)) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var found = await new Updater(http, Updater.CurrentVersion(), Log).CheckAsync();
        _settings.LastUpdateCheck = DateTimeOffset.Now;
        _settings.AvailableUpdate = found;
        _settings.Save();
        _update = found;
        if (found != null)
        {
            Log($"Update available: {found.Tag} ({found.Asset.Name}).");
            Post(new { type = "update", version = found.Version, url = found.PageUrl });
        }
    }

    /// <summary>Downloads and verifies the installer with progress, opens it, and quits so it can replace this app.</summary>
    private static async Task InstallUpdateAsync()
    {
        if ((_update ?? CachedUpdate()) is not { } update || Interlocked.Exchange(ref _updating, 1) == 1) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            var lastPercent = -1;
            var progress = new Progress<double>(p =>
            {
                var percent = (int)(p * 100);
                if (percent == lastPercent) return;
                lastPercent = percent;
                Post(new { type = "update-progress", percent });
            });
            var path = await new Updater(http, Updater.CurrentVersion(), Log).DownloadAsync(update, progress);
            Log($"Verified {path}; launching the installer.");
            Updater.Launch(path);
            _window.Invoke(() => _window.Close());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Log("Update failed: " + ex);
            Post(new { type = "update-error", message = ex.Message });
        }
        finally { Interlocked.Exchange(ref _updating, 0); }
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
        Console.Error.WriteLine(text);
    }

    // ------------------------------------------------------------------ files

    private static async Task SaveFileAsync(string name, string base64)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); } catch (FormatException) { return; }
        var ext = Path.GetExtension(name).TrimStart('.');
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var tcs = new TaskCompletionSource<string?>();
        _window.Invoke(async () =>
        {
            try
            {
                var path = await _window.ShowSaveFileAsync("Save " + name, Path.Combine(Directory.Exists(downloads) ? downloads : "", name),
                    ext.Length > 0 ? [(ext.ToUpperInvariant() + " file", [ext])] : null);
                tcs.TrySetResult(path);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        var target = await tcs.Task;
        if (string.IsNullOrEmpty(target)) return;
        if (ext.Length > 0 && !Path.HasExtension(target)) target += "." + ext;
        await File.WriteAllBytesAsync(target, bytes);
    }
}
