using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meridian.Web;
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

        // --local / --server <url> skip the start screen (handy for shortcuts and scripts).
        var serverArg = Array.IndexOf(args, "--server") is >= 0 and var si && si + 1 < args.Length ? args[si + 1] : null;
        if (args.Contains("--local")) _window.LoadRawString(StartPage.Html(_settings, busy: true)).RegisterWindowCreatedHandler((_, _) => OpenLocal());
        else if (serverArg != null) _window.Load(serverArg);
        else if (_settings.AutoOpen && _settings.LastMode == "local") _window.LoadRawString(StartPage.Html(_settings, busy: true)).RegisterWindowCreatedHandler((_, _) => OpenLocal());
        else if (_settings.AutoOpen && _settings.LastMode == "server" && _settings.Servers.FirstOrDefault() is { } last) _window.Load(last);
        else _window.LoadRawString(StartPage.Html(_settings));

        _window.WaitForClose();
        StopLocal();
    }

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
                _window.LoadRawString(StartPage.Html(_settings));
                break;
            case "switch":
                _settings.AutoOpen = false;
                _settings.Save();
                _window.LoadRawString(StartPage.Html(_settings));
                break;
            case "save":
                _ = SaveFileAsync((string?)msg!["name"] ?? "download", (string?)msg["data"] ?? "");
                break;
            case "open-url":
                OpenUrl((string?)msg!["url"]);
                break;
        }
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
        if (_localUrl != null) { _window.Load(_localUrl); return; }
        _ = Task.Run(async () =>
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
                _localUrl = $"http://127.0.0.1:{port}/";
                _window.Invoke(() => _window.Load($"{_localUrl}?launch={token}"));
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                _local = null;
                _window.Invoke(() => _window.LoadRawString(StartPage.Html(_settings, error: $"Could not start the local workspace: {ex.Message}")));
            }
        });
    }

    private static void StopLocal()
    {
        var app = Interlocked.Exchange(ref _local, null);
        if (app == null) return;
        try { app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).GetAwaiter().GetResult(); } catch { /* shutting down */ }
        _localUrl = null;
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

    // ------------------------------------------------------------------ server mode

    private static async Task OpenServerAsync(string url)
    {
        url = url.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            _window.Invoke(() => _window.LoadRawString(StartPage.Html(_settings, error: "Enter a server address such as https://meridian.example.com")));
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
            _window.Invoke(() => _window.LoadRawString(StartPage.Html(_settings, error: $"Could not reach {uri.Host}: {ex.Message}")));
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
