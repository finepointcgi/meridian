using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Scripting;

namespace Meridian.Web.Services;

/// <summary>
/// Scripting console and plugin configuration.
/// <list type="bullet">
/// <item>Local/desktop mode: the console is on unless <c>Meridian:Scripting:Enabled=false</c>; plugins load from <c>{DataPath}/plugins</c>.</item>
/// <item>Server mode: the console is OFF unless <c>Meridian:Scripting:Enabled=true</c>, and then only server administrators may run
/// scripts (every user owns the projects they create, so project ownership alone is not a trust boundary); set
/// <c>Meridian:Scripting:AllowProjectOwners=true</c> to also let project owners run them. Plugins load only from <c>Meridian:Plugins:Path</c>.</item>
/// </list>
/// Scripts are arbitrary .NET code in the server process; see docs/SCRIPTING.md.
/// </summary>
public sealed record ScriptingSettings(bool ConsoleEnabled, bool LocalMode, bool Unrestricted, TimeSpan Timeout, string? PluginsPath, bool AllowProjectOwners = false)
{
    public static ScriptingSettings From(IConfiguration c, MeridianHostInfo host)
    {
        var flag = c["Meridian:Scripting:Enabled"];
        var enabled = host.LocalMode
            ? !string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase)
            : string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
        var timeout = double.TryParse(c["Meridian:Scripting:TimeoutSeconds"], System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 30;
        var pluginsOn = !string.Equals(c["Meridian:Plugins:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
        var path = c["Meridian:Plugins:Path"] is { Length: > 0 } p ? p : host.LocalMode ? Path.Combine(host.DataPath, "plugins") : null;
        return new ScriptingSettings(enabled, host.LocalMode,
            string.Equals(c["Meridian:Scripting:Unrestricted"], "true", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(timeout), pluginsOn ? path : null,
            string.Equals(c["Meridian:Scripting:AllowProjectOwners"], "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Why this user may not run scripts in this workspace, or null when they may.</summary>
    public string? DenialReason(Workspace ws)
    {
        if (!ConsoleEnabled)
            return LocalMode
                ? "The script console is disabled (Meridian:Scripting:Enabled=false)."
                : "The script console is disabled on this server. Scripts run arbitrary .NET code in the server process, so an administrator must opt in with Meridian:Scripting:Enabled=true.";
        if (ws.Session is null && !LocalMode) return "Open a project first.";
        if (!LocalMode && !ws.IsServerAdmin && !(AllowProjectOwners && ws.Role == ProjectRole.Owner))
            return AllowProjectOwners
                ? "Only server administrators and project owners can run scripts on this server."
                : "Only server administrators can run scripts on this server (scripts run arbitrary .NET code in the server process).";
        return null;
    }
}

public static class ScriptingServiceExtensions
{
    /// <summary>Registers the script engine and the plugin manager (plugins load on first use, errors go to the Output dock).</summary>
    public static IServiceCollection AddMeridianScripting(this IServiceCollection services, IConfiguration config, MeridianHostInfo host)
    {
        var settings = ScriptingSettings.From(config, host);
        services.AddSingleton(settings);
        services.AddSingleton(new ScriptEngine(new ScriptEngineOptions { Timeout = settings.Timeout, Unrestricted = settings.Unrestricted }));
        services.AddScoped<ScriptConsole>();
        services.AddSingleton(_ =>
        {
            var pm = new PluginManager(host.LocalMode);
            if (settings.PluginsPath is { } path)
            {
                try { pm.LoadFrom(path); }
                catch (Exception e) { pm.Add(new FailedFolder(e.Message), path); }
            }
            return pm;
        });
        services.AddHostedService<PluginStartup>();
        return services;
    }

    /// <summary>Loads plugins at start-up (so their validation rules apply from the first request) and echoes results to the server log.</summary>
    private sealed class PluginStartup(PluginManager plugins, ILogger<PluginManager> log) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            foreach (var m in plugins.Messages)
                if (m.Contains("ERROR", StringComparison.Ordinal)) log.LogWarning("{Message}", m); else log.LogInformation("{Message}", m);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailedFolder(string error) : IMeridianPlugin
    {
        public string Name => "Plugins folder";
        public string Version => "-";
        public void Initialize(IPluginHost host) => throw new InvalidOperationException(error);
    }
}

/// <summary>Connects <see cref="ModelApi"/> to a circuit's <see cref="Workspace"/>: model access is marshalled onto the circuit.</summary>
public sealed class WorkspaceBridge(Workspace ws, Func<string, string, byte[], Task>? download = null) : IWorkbenchBridge
{
    public Action<string>? OnLog { get; set; }

    public T Invoke<T>(Func<T> work)
    {
        T result = default!;
        // Inline when already on the circuit; otherwise the circuit is free (the console awaits) and runs it promptly.
        ws.Dispatch(() => result = work()).GetAwaiter().GetResult();
        return result;
    }

    public Element? Selected => ws.Selected;
    public Diagram? ActiveDiagram => ws.Active?.Diagram;
    public void Select(Element? element) => ws.Select(element);
    public void Open(Diagram diagram) => ws.Open(diagram);

    public void Log(string message)
    {
        ws.Log(message);
        OnLog?.Invoke(message);
    }

    public void Status(string message) => ws.SetStatus(message);

    public void Download(string fileName, string mimeType, byte[] content)
    {
        if (download is null) { ws.Log($"(download of {fileName} is not available here)"); return; }
        _ = download(fileName, mimeType, content);
        ws.Log($"Downloaded {fileName}");
    }
}

/// <summary>Runs plugin contributions against a workspace: one undo step each, errors to the Output dock.</summary>
public static class PluginRunner
{
    public static bool RunCommand(Workspace ws, IWorkbenchBridge bridge, PluginCommand command) =>
        Guarded(ws, bridge, $"Plugin: {command.Title}", command.Plugin, api => command.Run(api));

    public static bool Import(Workspace ws, IWorkbenchBridge bridge, PluginImporter importer, byte[] content, string fileName) =>
        Guarded(ws, bridge, $"Import {importer.Name}", importer.Plugin, api => importer.Import(api, content, fileName));

    public static byte[]? Export(Workspace ws, IWorkbenchBridge bridge, PluginExporter exporter)
    {
        try { return exporter.Export(new ModelApi(ws.Model, bridge, readOnly: true)); }
        catch (Exception e)
        {
            Fail(ws, exporter.Plugin, exporter.Name, e);
            return null;
        }
    }

    private static bool Guarded(Workspace ws, IWorkbenchBridge bridge, string step, string plugin, Action<IModelApi> run)
    {
        var api = new ModelApi(ws.Model, bridge, readOnly: ws.ReadOnly);
        var ok = true;
        using (api.Batch(step))
        {
            try { run(api); }
            catch (Exception e)
            {
                ok = false;
                Fail(ws, plugin, step, e);
            }
        }
        if (!ok && api.EditCount > 0 && ws.Model.History.UndoDescription == step) ws.Model.History.Undo();
        ws.Bump();
        return ok;
    }

    private static void Fail(Workspace ws, string plugin, string what, Exception e)
    {
        ws.Log($"[{plugin}] {what} failed: {e.GetType().Name}: {e.Message}");
        ws.SetStatus($"{what} failed — see Output");
    }
}
